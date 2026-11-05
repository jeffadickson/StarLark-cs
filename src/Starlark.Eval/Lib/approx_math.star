# Elementary functions in pure Starlark: sin, cos, exp, log, log2 and atan, from series and argument reduction.
#
# Load it with:   load("std/approx_math.star", "sin", "cos", "exp", "log", "log2", "atan", "pi", "e")
#
# Starlark has no math module and no way to read the bits of a float, so everything here is built from + - * / and
# comparisons. The results agree with a C library to within a few units in the last place (about 1e-15 relative error),
# and they behave the same on every host, because no platform math library is involved.
#
# Names that start with an underscore are internal helpers. Starlark does not let `load` export them, and the host's own
# fast versions of these functions (written in C# on top of System.Math) are also named with a leading underscore:
# _sin, _cos, _exp, _log, _log2 and _atan.
#
# Special values follow the C and Go libraries, not Python's: log(0) is -inf, log of a negative number is nan, exp overflows
# to inf, and sin(inf) is nan; none of them is an error. A wrong argument type is an error.
#
# Accuracy limits: sin and cos reduce the argument with a three part value of pi/2, which is exact for |x| below about
# 1.6 million. Beyond that the error grows with |x| (the reduction has to be done in far more digits to stay exact).

pi = 3.141592653589793
e = 2.718281828459045

_INF = float("inf")
_NAN = float("nan")

_LN2_HI = 6.93147180369123816490e-01     # ln 2 split in two so that k * _LN2_HI is exact for |k| < 2048
_LN2_LO = 1.90821492927058770002e-10
_LOG2E = 1.4426950408889634
_SQRT2 = 1.4142135623730951
_HALF_PI = 1.5707963267948966
_TWO_OVER_PI = 0.6366197723675814

# pi/2 in four pieces; the first three have 33 significant bits, so n * piece is exact for |n| < 2**20.
_PIO2_1 = 1.57079632673412561417e+00
_PIO2_2 = 6.07710050630396597660e-11
_PIO2_3 = 2.02226624871116645580e-21
_PIO2_4 = 8.47842766036889956997e-32

_EXP_MAX = 709.782712893384               # exp of anything larger overflows
_EXP_MIN = -745.1332191019412             # exp of anything smaller underflows to 0

# 2**(2**j) for j = 0..9: the greedy steps of the exponent search below.
_BIG = [2.0, 4.0, 16.0, 256.0, 65536.0, 4294967296.0, 18446744073709551616.0,
        3.402823669209385e38, 1.157920892373162e77, 1.3407807929942597e154]

# sin x = x * (1 + s1 x^2 + s2 x^4 + ...), with s_k = (-1)^k / (2k+1)!
def _build_sin():
    out = []
    f = 1.0
    sign = 1.0
    for k in range(0, 11):
        if k > 0:
            f = f * (2 * k) * (2 * k + 1)
            sign = -sign
        out.append(sign / f)
    return out

# cos x = 1 + c1 x^2 + c2 x^4 + ..., with c_k = (-1)^k / (2k)!
def _build_cos():
    out = []
    f = 1.0
    sign = 1.0
    for k in range(0, 11):
        if k > 0:
            f = f * (2 * k - 1) * (2 * k)
            sign = -sign
        out.append(sign / f)
    return out

# exp r = sum r^k / k!
def _build_exp():
    out = []
    f = 1.0
    for k in range(0, 17):
        if k > 0:
            f = f * k
        out.append(1.0 / f)
    return out

_SIN_C = _build_sin()
_COS_C = _build_cos()
_EXP_C = _build_exp()

# Validate an argument: it must be an int or a float, otherwise fail with "name: got TYPE, want float or int". Returns it as a float. Every public function starts with this.
def _check(name, x):
    t = type(x)
    if t != "float" and t != "int":
        fail("%s: got %s, want float or int" % (name, t))
    return float(x)

# NaN test. Starlark orders NaN above every number and makes nan == nan true (unlike C and Python), so `x != x` is never true;
# instead NaN is the one value that is above infinity.
def _is_nan(x):
    return x > _INF

# Largest int not above x, for finite x.
def _floor(x):
    n = int(x)
    if n > x:
        n -= 1
    return n

# Horner evaluation of c[0] + c[1] y + c[2] y^2 + ... where y is the second argument.
def _horner(c, y):
    s = c[len(c) - 1]
    for k in range(len(c) - 2, -1, -1):
        s = s * y + c[k]
    return s

# x * 2**k for any |k| up to about 2000, in two exact halves so that nothing overflows on the way.
def _ldexp(x, k):
    h = k // 2
    return _scale(_scale(x, h), k - h)

# Multiply x by 2**k using the bits of k: one multiplication (or division, for a negative k) per set bit, by the exact table value _BIG[j] = 2**(2**j). Exact, because multiplying by a power of two never rounds.
def _scale(x, k):
    if k >= 0:
        for j in range(0, 10):
            if (k >> j) & 1 == 1:
                x = x * _BIG[j]
    else:
        n = -k
        for j in range(0, 10):
            if (n >> j) & 1 == 1:
                x = x / _BIG[j]
    return x

# Split a positive finite x into (m, e) with 1 <= m < 2 and x = m * 2**e, exactly.
def _frexp(x):
    m = x
    e = 0
    if m < 2.2250738585072014e-308:          # subnormal: scale up by 2**54 first
        m = m * 18014398509481984.0
        e = -54
    if m >= 2.0:
        for j in range(9, -1, -1):
            if m >= _BIG[j]:
                m = m / _BIG[j]
                e += 1 << j
    elif m < 1.0:
        for j in range(9, -1, -1):
            if m * _BIG[j] < 2.0:
                m = m * _BIG[j]
                e -= 1 << j
    return (m, e)

# ---------------------------------------------------------------- exp

def exp(x):
    "e raised to the power x."
    x = _check("exp", x)
    if _is_nan(x):
        return x
    if x > _EXP_MAX:
        return _INF
    if x < _EXP_MIN:
        return 0.0
    k = _floor(x * _LOG2E + 0.5)
    r = (x - k * _LN2_HI) - k * _LN2_LO
    return _ldexp(_horner(_EXP_C, r), k)

# ---------------------------------------------------------------- log

# ln(m) for 1/sqrt(2) <= m <= sqrt(2), as 2 atanh((m-1)/(m+1)).
def _log_core(m):
    z = (m - 1.0) / (m + 1.0)
    z2 = z * z
    s = 1.0 / 27.0
    for k in range(12, 0, -1):
        s = s * z2 + 1.0 / (2 * k + 1)
    return 2.0 * z + 2.0 * z * z2 * s

# (m, e) with x = m * 2**e and m in [1/sqrt(2), sqrt(2)).
def _log_parts(x):
    f = _frexp(x)
    m = f[0]
    k = f[1]
    if m > _SQRT2:
        m = m * 0.5
        k += 1
    return (m, k)

# Natural logarithm of an already checked float.
def _ln(x):
    if _is_nan(x) or x < 0.0:
        return _NAN
    if x == 0.0:
        return -_INF
    if x == _INF:
        return x
    p = _log_parts(x)
    k = p[1]
    return k * _LN2_HI + (_log_core(p[0]) + k * _LN2_LO)

def log(x, base = None):
    "Natural logarithm of x, or the logarithm to the given base."
    x = _check("log", x)
    if base == None:
        return _ln(x)
    b = _check("log", base)
    if b == 1.0:
        fail("log: division by zero")
    return _ln(x) / _ln(b)

def log2(x):
    "Base 2 logarithm of x. Exact for powers of two."
    x = _check("log2", x)
    if _is_nan(x) or x < 0.0:
        return _NAN
    if x == 0.0:
        return -_INF
    if x == _INF:
        return x
    p = _log_parts(x)
    return p[1] + _log_core(p[0]) * _LOG2E

# ---------------------------------------------------------------- sin and cos

# Reduce x to (n, r) with x = n * pi/2 + r and |r| <= pi/4, n an int.
def _reduce(x):
    if x < 0.7853981633974483 and x > -0.7853981633974483:
        return (0, x)
    n = _floor(x * _TWO_OVER_PI + 0.5)
    f = float(n)
    r = x - f * _PIO2_1
    r = r - f * _PIO2_2
    r = r - f * _PIO2_3
    r = r - f * _PIO2_4
    return (n, r)

# The sine of a reduced argument |r| <= pi/4, as r times a polynomial in r*r (the Taylor series, evaluated by Horner's rule).
def _ksin(r):
    return r * _horner(_SIN_C, r * r)

# The cosine of a reduced argument |r| <= pi/4, as a polynomial in r*r.
def _kcos(r):
    return _horner(_COS_C, r * r)

def sin(x):
    "Sine of x, in radians."
    x = _check("sin", x)
    if _is_nan(x) or x == _INF or x == -_INF:
        return _NAN
    if x == 0.0:
        return x
    t = _reduce(x)
    q = t[0] & 3
    r = t[1]
    if q == 0:
        return _ksin(r)
    if q == 1:
        return _kcos(r)
    if q == 2:
        return -_ksin(r)
    return -_kcos(r)

def cos(x):
    "Cosine of x, in radians."
    x = _check("cos", x)
    if _is_nan(x) or x == _INF or x == -_INF:
        return _NAN
    t = _reduce(x)
    q = t[0] & 3
    r = t[1]
    if q == 0:
        return _kcos(r)
    if q == 1:
        return -_ksin(r)
    if q == 2:
        return -_kcos(r)
    return _ksin(r)

# ---------------------------------------------------------------- atan

# sqrt(v) for 1 <= v <= 2 by Newton's method; six steps from 1.2 reach full precision.
def _sqrt12(v):
    g = 1.2
    for _ in range(6):
        g = 0.5 * (g + v / g)
    return g

def atan(x):
    "Arc tangent of x, in radians, between -pi/2 and pi/2."
    x = _check("atan", x)
    if _is_nan(x) or x == 0.0:
        return x
    negative = x < 0.0
    a = -x if negative else x
    if a == _INF:
        return -_HALF_PI if negative else _HALF_PI
    inverted = a > 1.0
    if inverted:
        a = 1.0 / a
    # Three halvings of the angle: tan(t/2) = tan(t) / (1 + sqrt(1 + tan(t)^2)). That leaves |a| < 0.1, where the series converges fast.
    for _ in range(3):
        a = a / (1.0 + _sqrt12(1.0 + a * a))
    # atan a = a * (1 - a^2/3 + a^4/5 - ...), by Horner's rule on a^2; |a| < 0.1 here, so twelve terms are far more than needed.
    a2 = a * a
    t = 0.0
    for k in range(11, -1, -1):
        c = 1.0 / (2 * k + 1)
        if k % 2 == 1:
            c = -c
        t = t * a2 + c
    result = 8.0 * a * t
    if inverted:
        result = _HALF_PI - result
    return -result if negative else result

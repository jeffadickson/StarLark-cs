# Elementary functions in pure Starlark, from series and argument reduction: sin, cos, tan, asin, acos, atan, atan2, exp, log,
# log2, sqrt, pow, hypot, floor, ceil, trunc, round, degrees, radians, isnan and isinf, and the constants pi and e.
#
# Load it with:   load("std/approx_math.star", "sin", "cos", "tan", "exp", "log", "sqrt", "pow", "pi", "e")   (any of the names)
#
# Starlark has no math module and no way to read the bits of a float, so everything here is built from + - * / and
# comparisons. The results agree with a C library to within a few units in the last place (about 1e-15 relative error),
# and they behave the same on every host, because no platform math library is involved.
#
# Names that start with an underscore are internal helpers. Starlark does not let `load` export them. The host also
# predeclares fast versions of these functions with the same names (written in C# on top of System.Math); loading a
# name from this package replaces the predeclared one in the loading file.
#
# Special values follow the C and Go libraries, not Python's: log(0) is -inf, log of a negative number is nan, exp overflows
# to inf, and sin(inf) is nan; none of them is an error. A wrong argument type is an error.
#
# Accuracy: sqrt, floor, ceil and trunc are exact or within one unit in the last place; the trigonometric functions, exp and
# log are within a few. pow is computed as repeated multiplication for a whole exponent up to 64 and otherwise as
# exp(y * log(x)), whose error grows with |y * log(x)|. The native versions in the host use System.Math.
#
# Accuracy limits: sin and cos reduce the argument with a four part value of pi/2 (three exact pieces and a tail), which is exact for |x| below about
# 1.6 million. Beyond that the error grows with |x| (the reduction has to be done in far more digits to stay exact).

pi = 3.141592653589793
e = 2.718281828459045

_INF = float("inf")
_NAN = float("nan")
_PI = 3.141592653589793
_QUARTER_PI = 0.7853981633974483
_THREE_QUARTER_PI = 2.356194490192345
_DEGREES = 57.29577951308232            # degrees per radian
_RADIANS = 0.017453292519943295         # radians per degree

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

# ---------------------------------------------------------------- sign tests

# True for a negative number and for negative zero (the printed form of -0.0 starts with a minus sign).
def _neg(v):
    return v < 0.0 or (v == 0.0 and str(v)[0] == "-")

# ---------------------------------------------------------------- tan, asin, acos, atan2

def tan(x):
    "Tangent of x, in radians."
    x = _check("tan", x)
    if _is_nan(x) or x == _INF or x == -_INF:
        return _NAN
    if x == 0.0:
        return x
    t = _reduce(x)
    r = t[1]
    if t[0] & 1 == 0:
        return _ksin(r) / _kcos(r)
    # x = n * pi/2 + r with n odd: tan x = -cos(r) / sin(r).
    s = _ksin(r)
    if s == 0.0:
        return _INF
    return -_kcos(r) / s

def atan2(y, x):
    "The angle in radians, between -pi and pi, of the point (x, y) measured from the positive x axis. Takes y first, as in C."
    y = _check("atan2", y)
    x = _check("atan2", x)
    if _is_nan(y) or _is_nan(x):
        return _NAN
    ny = _neg(y)
    if y == _INF or y == -_INF:
        if x == _INF:
            a = _QUARTER_PI
        elif x == -_INF:
            a = _THREE_QUARTER_PI
        else:
            a = _HALF_PI
        return -a if ny else a
    if x == _INF:
        return -0.0 if ny else 0.0
    if x == -_INF:
        return -_PI if ny else _PI
    if x == 0.0:
        if y == 0.0:
            return (-_PI if ny else _PI) if _neg(x) else y
        return -_HALF_PI if ny else _HALF_PI
    if y == 0.0:
        if x > 0.0:
            return y
        return -_PI if ny else _PI
    a = atan(y / x)
    if x > 0.0:
        return a
    return a - _PI if ny else a + _PI

def asin(x):
    "Arc sine of x, in radians, between -pi/2 and pi/2. nan when |x| > 1."
    x = _check("asin", x)
    if _is_nan(x) or x > 1.0 or x < -1.0:
        return _NAN
    return atan2(x, sqrt((1.0 - x) * (1.0 + x)))

def acos(x):
    "Arc cosine of x, in radians, between 0 and pi. nan when |x| > 1."
    x = _check("acos", x)
    if _is_nan(x) or x > 1.0 or x < -1.0:
        return _NAN
    return atan2(sqrt((1.0 - x) * (1.0 + x)), x)

# ---------------------------------------------------------------- sqrt, hypot, pow

def sqrt(x):
    "Square root of x; nan for a negative x."
    x = _check("sqrt", x)
    if _is_nan(x) or x == 0.0:
        return x
    if x < 0.0:
        return _NAN
    if x == _INF:
        return x
    # x = m * 2**e with an even e and 1 <= m < 4, so the root is sqrt(m) * 2**(e/2); Newton's method from 1.5 converges in six steps.
    p = _frexp(x)
    m = p[0]
    e = p[1]
    if e & 1 == 1:
        m = m * 2.0
        e -= 1
    g = 1.5
    for _ in range(6):
        g = 0.5 * (g + m / g)
    return _ldexp(g, e // 2)

def hypot(x, y):
    "The length of the vector (x, y), sqrt(x*x + y*y), without overflow or underflow on the way."
    x = _check("hypot", x)
    y = _check("hypot", y)
    if x == _INF or x == -_INF or y == _INF or y == -_INF:
        return _INF
    if _is_nan(x) or _is_nan(y):
        return _NAN
    a = -x if x < 0.0 else x
    b = -y if y < 0.0 else y
    if b > a:
        t = a
        a = b
        b = t
    if a == 0.0:
        return 0.0
    r = b / a
    return a * sqrt(1.0 + r * r)

# x**n for an int n >= 0 below 256, by repeated squaring.
def _ipow(x, n):
    result = 1.0
    base = x
    for _ in range(0, 8):
        if n == 0:
            break
        if n & 1 == 1:
            result = result * base
        base = base * base
        n = n >> 1
    return result

# True if y is a whole number (so that a negative base has a real power).
def _is_whole(y):
    return y < 9007199254740992.0 and y > -9007199254740992.0 and float(int(y)) == y

# True if y is a whole odd number (every float of magnitude 2**53 or more is even).
def _is_odd(y):
    return _is_whole(y) and int(y) & 1 == 1

def pow(x, y):
    "x raised to the power y, following the C library for zeros, infinities and nan; a negative x with a non-integer y gives nan."
    x = _check("pow", x)
    y = _check("pow", y)
    if y == 0.0 or x == 1.0:
        return 1.0
    if _is_nan(x) or _is_nan(y):
        return _NAN
    ax = -x if x < 0.0 else x
    if y == _INF or y == -_INF:
        if ax == 1.0:
            return 1.0
        return _INF if (ax > 1.0) == (y > 0.0) else 0.0
    odd = _is_odd(y)
    if x == 0.0:
        if y < 0.0:
            return -_INF if (odd and _neg(x)) else _INF
        return x if odd else 0.0
    if x == _INF:
        return _INF if y > 0.0 else 0.0
    if x == -_INF:
        if y > 0.0:
            return -_INF if odd else _INF
        return -0.0 if odd else 0.0
    if x < 0.0 and not _is_whole(y):
        return _NAN
    negate = x < 0.0 and odd
    if _is_whole(y) and y <= 64.0 and y >= -64.0:
        n = int(y)
        r = _ipow(ax, n if n >= 0 else -n)
        if n < 0:
            # 1 / r, where an underflowed r (0) means the true power is beyond the largest float.
            r = _INF if r == 0.0 else 1.0 / r
    else:
        r = exp(y * _ln(ax))
    return -r if negate else r

# ---------------------------------------------------------------- rounding to ints, angles, tests

# An int is its own answer; a float must be finite. Returns the argument (an int, or a checked float).
def _round_input(name, x):
    if type(x) == "int":
        return x
    x = _check(name, x)
    if _is_nan(x) or x == _INF or x == -_INF:
        fail("%s: cannot convert %s to int" % (name, str(x)))
    return x

def floor(x):
    "The largest int not above x."
    x = _round_input("floor", x)
    return x if type(x) == "int" else _floor(x)

def ceil(x):
    "The smallest int not below x."
    x = _round_input("ceil", x)
    if type(x) == "int":
        return x
    n = int(x)
    return n + 1 if n < x else n

def trunc(x):
    "x rounded toward zero, as an int."
    x = _round_input("trunc", x)
    return x if type(x) == "int" else int(x)

def round(x):
    "x rounded to the nearest int; a half goes to the even neighbour (2.5 gives 2, 3.5 gives 4), as in Python."
    x = _round_input("round", x)
    if type(x) == "int":
        return x
    n = _floor(x)
    d = x - n
    if d < 0.5:
        return n
    if d > 0.5:
        return n + 1
    return n if n % 2 == 0 else n + 1

def degrees(x):
    "Radians converted to degrees."
    return _check("degrees", x) * _DEGREES

def radians(x):
    "Degrees converted to radians."
    return _check("radians", x) * _RADIANS

def isnan(x):
    "True if x is nan."
    return _is_nan(_check("isnan", x))

def isinf(x):
    "True if x is positive or negative infinity."
    x = _check("isinf", x)
    return x == _INF or x == -_INF

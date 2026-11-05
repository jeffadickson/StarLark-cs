# Numbers for the BASIC interpreter.
#
# TRS-80 Level II BASIC has three numeric types: 16-bit integers, 32-bit single precision and 64-bit double precision.
# Here an integer is a Starlark int, a single is a float that has been rounded to 24 bits of mantissa (to_single), and a
# double is a one-element tuple holding a float, so the three can be told apart by type() alone.
#
# Starlark has no math module and no exceptions, so this file also holds the functions BASIC needs (SQR, EXP, LOG, SIN,
# COS, ATN, ^) written with series, and the number scanner that never fails on bad text.

INT_MIN = -32768
INT_MAX = 32767
SNG_LIMIT = 1.7014118346046923e38      # 2**127: a single at or above this overflows (?OV ERROR)
LN2 = 0.6931471805599453
PI = 3.141592653589793
HALF_PI = 1.5707963267948966
TWO_PI = 6.283185307179586

# Build the table of exact powers of two from 2**-170 to 2**170, indexed by exponent. Multiplying or halving by two is exact in floating point,
# so every entry is exact.
def _make_pow2():
    table = {}
    x = 1.0
    for k in range(0, 171):
        table[k] = x
        x = x * 2.0
    x = 1.0
    for k in range(0, 171):
        table[-k] = x
        x = x / 2.0
    return table

# Exact powers of two, 2**-170 .. 2**170.
POW2 = _make_pow2()

# ---------------------------------------------------------------- kinds

# 0 integer, 1 single, 2 double, 3 string.
def kind_of(v):
    t = type(v)
    if t == "int":
        return 0
    if t == "float":
        return 1
    if t == "tuple":
        return 2
    return 3

# Any BASIC number as a float. A double is stored as a one-item tuple (so it can be told from a single), and this unwraps it.
def to_float(v):
    if type(v) == "tuple":
        return v[0]
    return float(v)

# Largest integer not greater than x, as an int (floor), safe for any finite float (NaN gives 0; an infinity is an error).
def floor_int(x):
    if x != x:
        return 0
    n = int(x)
    if n > x:
        n -= 1
    return n

# The negative of a number, keeping its type. -32768 has no 16-bit negative, so it becomes the single-precision 32768.0.
def negate(v):
    k = kind_of(v)
    if k == 0:
        if v == INT_MIN:
            return 32768.0
        return -v
    if k == 1:
        return -v
    return (-v[0],)

# ---------------------------------------------------------------- single precision

# e such that 2**e <= a < 2**(e+1), for 2**-170 <= a < 2**170.
def binary_exponent(a):
    lo = -170
    hi = 170
    for _ in range(10):
        mid = (lo + hi + 1) // 2
        if POW2[mid] <= a:
            lo = mid
        else:
            hi = mid - 1
    return lo

# Round a float to the nearest 32-bit single (24-bit mantissa), ties to even. Values too small to represent become 0.
# The caller has already checked for overflow.
def to_single(x):
    if x == 0.0 or x != x:
        return 0.0
    a = x
    sign = 1.0
    if a < 0.0:
        a = -a
        sign = -1.0
    if a < POW2[-127]:
        return 0.0
    if a >= SNG_LIMIT:
        return sign * SNG_LIMIT
    e = binary_exponent(a)
    scale = POW2[23 - e]
    q = a * scale
    n = int(q)
    frac = q - n
    if frac > 0.5 or (frac == 0.5 and n % 2 == 1):
        n += 1
    return sign * (n / scale)

# True if a value is too big for single precision, or is not a number.
def overflows(x):
    return x >= SNG_LIMIT or x <= -SNG_LIMIT or x != x

# ---------------------------------------------------------------- printing numbers

# Significant digits of a > 0 as (digit string, decimal exponent of the first digit).
def _sci_digits(a, nd):
    s = ("%." + str(nd - 1) + "e") % a
    parts = s.split("e")
    return (parts[0].replace(".", ""), int(parts[1]))

# A float the way BASIC prints it, without the sign slot: nd significant digits, fixed notation for 0.01 up to 10**nd,
# otherwise d.dddE+xx (D for double precision).
def format_real(x, nd, expc):
    if x == 0.0:
        return "0"
    sign = ""
    a = x
    if x < 0.0:
        sign = "-"
        a = -x
    d = _sci_digits(a, nd)
    digits = d[0].rstrip("0")
    e = d[1]
    if digits == "":
        digits = "0"
    if e >= -2 and e < nd:
        if e < 0:
            body = "." + "0" * (-e - 1) + digits
        elif len(digits) <= e + 1:
            body = digits + "0" * (e + 1 - len(digits))
        else:
            body = digits[:e + 1] + "." + digits[e + 1:]
    else:
        body = digits[0]
        if len(digits) > 1:
            body += "." + digits[1:]
        body += expc + ("-" if e < 0 else "+") + ("%02d" % abs(e))
    return sign + body

# A number as text with no sign slot (what STR$ builds on).
def number_text(v):
    k = kind_of(v)
    if k == 0:
        return str(v)
    if k == 1:
        return format_real(v, 6, "E")
    return format_real(v[0], 16, "D")

# True if a number is below zero, for any of the three numeric types.
def _is_negative(v):
    k = kind_of(v)
    if k == 2:
        return v[0] < 0.0
    return v < 0

# STR$: positive numbers get a leading space where the sign would be.
def str_text(v):
    s = number_text(v)
    if _is_negative(v):
        return s
    return " " + s

# PRINT: a leading space for the sign and a trailing space.
def print_text(v):
    return str_text(v) + " "

# ---------------------------------------------------------------- reading numbers

# The index just after the run of decimal digits that starts at s[i] (i itself if there are none).
def span_digits(s, i):
    j = i
    for k in range(i, len(s)):
        c = s[k]
        if c < "0" or c > "9":
            break
        j = k + 1
    return j

# Scan an unsigned number at s[i] (s already upper case). Returns (value, next index, kind):
# kind 0/1/2 is the type of the value, -1 means there is no number there, -2 means it overflows.
def scan_number(s, i):
    n = len(s)
    a = span_digits(s, i)
    j = a
    has_dot = False
    frac_end = a
    if j < n and s[j] == ".":
        has_dot = True
        frac_end = span_digits(s, j + 1)
        j = frac_end
    ndig = (a - i)
    if has_dot:
        ndig += frac_end - a - 1
    if ndig == 0:
        return (0, i, -1)
    # Only significant digits decide between single and double precision, so leading zeros do not count.
    sig_text = s[i:a]
    if has_dot:
        sig_text += s[a + 1:frac_end]
    sig = len(sig_text.lstrip("0"))
    mant_end = j
    has_exp = False
    dbl = False
    exp_sign = ""
    exp_digits = ""
    if j < n and (s[j] == "E" or s[j] == "D"):
        k = j + 1
        if k < n and (s[k] == "+" or s[k] == "-"):
            exp_sign = s[k]
            k += 1
        e_end = span_digits(s, k)
        if e_end > k:
            has_exp = True
            dbl = s[j] == "D"
            exp_digits = s[k:e_end]
            j = e_end
    text = s[i:mant_end]
    ev = 0
    if has_exp:
        if len(exp_digits) > 6:
            ev = 999999
        else:
            ev = int(exp_digits)
        if exp_sign == "-":
            ev = -ev
    # float() refuses text that is too large, so refuse it first. Leading zeros are not counted.
    int_digits = len(s[i:a].lstrip("0"))
    if int_digits + ev > 40:
        return (0, j, -2)
    value = 0.0
    if not (has_exp and ev < -400):
        value = float(text + ("e" + exp_sign + exp_digits if has_exp else ""))
    suffix = ""
    if j < n and (s[j] == "%" or s[j] == "!" or s[j] == "#"):
        suffix = s[j]
        j += 1
    if value >= SNG_LIMIT:
        return (0, j, -2)
    if suffix == "%":
        iv = floor_int(value)
        if iv > INT_MAX:
            return (0, j, -2)
        return (iv, j, 0)
    if suffix == "!":
        return (to_single(value), j, 1)
    if suffix == "#" or dbl or sig > 7:
        return ((value,), j, 2)
    if has_dot or has_exp:
        return (to_single(value), j, 1)
    iv = int(text)
    if iv <= INT_MAX:
        return (iv, j, 0)
    return (to_single(value), j, 1)

# VAL: the number at the start of s (after spaces), or 0.
def val_text(s):
    u = s.upper()
    n = len(u)
    i = 0
    for k in range(n):
        if u[k] != " ":
            break
        i = k + 1
    neg = False
    if i < n and u[i] == "-":
        neg = True
        i += 1
    elif i < n and u[i] == "+":
        i += 1
    r = scan_number(u, i)
    if r[2] < 0:
        return 0
    if neg:
        return negate(r[0])
    return r[0]

# A whole field of INPUT or DATA as a number: (ok, value). Empty is 0.
def parse_number_text(s):
    u = s.upper().strip()
    if u == "":
        return (True, 0)
    i = 0
    neg = False
    if u[0] == "-":
        neg = True
        i = 1
    elif u[0] == "+":
        i = 1
    r = scan_number(u, i)
    if r[2] < 0 or r[1] != len(u):
        return (False, 0)
    if neg:
        return (True, negate(r[0]))
    return (True, r[0])

# ---------------------------------------------------------------- math

# Square root of x >= 0 by Newton's method from a power-of-two guess.
def f_sqr(x):
    if x <= 0.0:
        return 0.0
    g = POW2[binary_exponent(x) // 2]
    for _ in range(8):
        g = 0.5 * (g + x / g)
    return g

# e**x, or None for x above 88.7 (callers still check the result against the single precision limit), and 0.0 below -100.
def f_exp(x):
    if x > 88.7:
        return None
    if x < -100.0:
        return 0.0
    if x >= 0.0:
        k = int(x / LN2 + 0.5)
    else:
        k = int(x / LN2 - 0.5)
    r = x - k * LN2
    term = 1.0
    s = 1.0
    for i in range(1, 17):
        term = term * r / i
        s += term
    return s * POW2[k]

# Natural logarithm of x > 0.
def f_log(x):
    e = binary_exponent(x)
    m = x / POW2[e]
    if m > 1.4142135623730951:
        m = m / 2.0
        e += 1
    z = (m - 1.0) / (m + 1.0)
    z2 = z * z
    t = z
    s = z
    for i in range(1, 14):
        t = t * z2
        s += t / (2 * i + 1)
    return e * LN2 + 2.0 * s

# Sine by reduction and series: bring x into -pi/2..pi/2 using the periodicity and symmetry of sine, then sum the Taylor series. Good to the six
# or seven digits Level II printed. (The separate package std/approx_math.star is a more accurate, general version.)
def f_sin(x):
    if x >= 0.0:
        k = int(x / TWO_PI + 0.5)
    else:
        k = int(x / TWO_PI - 0.5)
    r = x - k * TWO_PI
    if r > HALF_PI:
        r = PI - r
    elif r < -HALF_PI:
        r = -PI - r
    r2 = r * r
    term = r
    s = r
    for i in range(1, 13):
        term = -term * r2 / ((2 * i) * (2 * i + 1))
        s += term
    return s

# Cosine, as the sine of x plus a quarter turn.
def f_cos(x):
    return f_sin(x + HALF_PI)

# Arc tangent: fold x into 0..1 using atan(-x) = -atan(x) and atan(1/x) = pi/2 - atan(x), halve the angle twice to make the series converge
# fast, sum the series, and undo the folding.
def f_atn(x):
    neg = x < 0.0
    a = x
    if neg:
        a = -x
    inv = a > 1.0
    if inv:
        a = 1.0 / a
    for _ in range(2):
        a = a / (1.0 + f_sqr(1.0 + a * a))
    a2 = a * a
    term = a
    s = a
    for i in range(1, 14):
        term = -term * a2
        s += term / (2 * i + 1)
    s = s * 4.0
    if inv:
        s = HALF_PI - s
    if neg:
        return -s
    return s

# x ** y as (value, error code). Error codes are the BASIC ones: "" none, "FC", "OV", "/0".
def f_pow(x, y):
    if y == 0.0:
        return (1.0, "")
    if x == 0.0:
        if y > 0.0:
            return (0.0, "")
        return (0.0, "/0")
    if y == float(floor_int(y)) and abs(y) <= 2048.0:
        n = floor_int(abs(y))
        result = 1.0
        base = x
        for _ in range(12):
            if n % 2 == 1:
                result = result * base
            n = n // 2
            if n == 0:
                break
            base = base * base
        if y < 0.0:
            if result == 0.0 or overflows(result):
                return (0.0, "OV")
            result = 1.0 / result
        if overflows(result):
            return (0.0, "OV")
        return (result, "")
    if x < 0.0:
        return (0.0, "FC")
    t = y * f_log(x)
    r = f_exp(t)
    if r == None:
        return (0.0, "OV")
    return (r, "")

# Next state of the pseudo random generator (a 31-bit linear congruential generator). RND turns the state into a value in [0, 1) itself.
def rnd_next(seed):
    return (seed * 1103515245 + 12345) % 2147483648

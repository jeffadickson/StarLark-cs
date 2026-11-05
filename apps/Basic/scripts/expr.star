# Expressions for the BASIC interpreter: compile a token list to reverse Polish notation, then evaluate it.
#
# Starlark does not allow recursion, so neither step recurses. The compiler is the shunting-yard algorithm with an operator
# stack, and the evaluator is a loop over the compiled items with an explicit stack of frames for DEF FN calls. Nesting of
# parentheses, of functions and of FN calls is therefore limited by data, never by the host's stack.
#
# Compiled items (the "rpn" list):
#   ("c", value)                 push a constant
#   ("var", name, suffix)        push a scalar variable
#   ("arr", (name, suffix), n)   pop n subscripts, push an array element
#   ("B", op)  ("U", op)         binary and unary operator
#   ("call", NAME, n)            built-in function with n arguments
#   ("ufn", NAME, n)             user function (DEF FN) with n arguments
#
# Every function that can fail returns its error as a two letter BASIC code ("TM", "OV", "FC", "/0", ...), "" for none.

load("numbers.star",
     "INT_MAX", "INT_MIN", "kind_of", "to_float", "floor_int", "negate", "to_single", "overflows", "str_text", "val_text",
     "f_sqr", "f_exp", "f_log", "f_sin", "f_cos", "f_atn", "f_pow", "rnd_next")

# Character -> code (0 to 255), for ASC. Characters outside this table read as 63, "?", since Level II had no such character.
CODES = {"%c" % i: i for i in range(256)}

# The type suffix of a variable name -> its kind: 0 integer (%), 1 single precision (!), 2 double precision (#), 3 string ($).
SUFFIX_KIND = {"%": 0, "!": 1, "#": 2, "$": 3}
# The reverse: kind number -> suffix character. Used to give an untyped name its default type.
KIND_SUFFIX = ["%", "!", "#", "$"]

# Names that are built-in functions. A set, so the compiler can ask "is this word a function?" in one lookup.
FUNCS = {}
for _w in ["SGN", "INT", "ABS", "FRE", "INP", "POS", "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "CINT",
           "CSNG", "CDBL", "FIX", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "INSTR", "STRING$",
           "POINT", "MEM", "INKEY$", "ERL", "ERR"]:
    FUNCS[_w] = True

# Functions written without parentheses (INKEY$, MEM, ERL, ERR).
ZERO_ARG = {"INKEY$": True, "MEM": True, "ERL": True, "ERR": True}

# Operator precedence for the shunting-yard compiler: a higher number binds tighter. "u-" is unary minus. As on Level II, ^ binds tighter than
# unary minus, so -2^2 is -4.
PREC = {"OR": 1, "AND": 2, "NOT": 3, "=": 4, "<>": 4, "<": 4, ">": 4, "<=": 4, ">=": 4,
        "+": 5, "-": 5, "*": 6, "/": 6, "u-": 7, "^": 8}

# The symbolic binary operators. AND and OR are keywords and are handled separately.
BINARY = {"+": True, "-": True, "*": True, "/": True, "^": True,
          "=": True, "<>": True, "<": True, ">": True, "<=": True, ">=": True}

# The longest string BASIC allows; a longer result is the "LS" (string too long) error.
MAX_STRING = 255

# The dictionary key for a variable: its two-character name plus a type suffix. An untyped name takes its type from the DEFINT/DEFSTR table
# for its first letter, so with the default (single precision) A is stored as "A!".
def var_key(st, name, suffix):
    if suffix == "":
        return name + KIND_SUFFIX[st["deftype"][name[0]]]
    return name + suffix

# The value a variable of this kind has before it is assigned: 0, 0.0, a double 0.0, or the empty string.
def zero_of(kind):
    if kind == 0:
        return 0
    if kind == 1:
        return 0.0
    if kind == 2:
        return (0.0,)
    return ""

# The kind (0 to 3) of a variable key, from its suffix.
def key_kind(key):
    return SUFFIX_KIND[key[-1]]

# Convert a value to what a variable of this kind holds. Returns (value, error).
def coerce(kind, v):
    vk = kind_of(v)
    if kind == 3:
        if vk != 3:
            return (None, "TM")
        return (v, "")
    if vk == 3:
        return (None, "TM")
    if kind == vk:
        return (v, "")
    f = to_float(v)
    if kind == 0:
        n = floor_int(f)
        if n < INT_MIN or n > INT_MAX:
            return (None, "OV")
        return (n, "")
    if kind == 1:
        if overflows(f):
            return (None, "OV")
        return (to_single(f), "")
    return ((f,), "")

# ---------------------------------------------------------------- compiling

# Compile the expression that starts at toks[pos]. Stops at the first token that cannot continue it (a comma, semicolon,
# colon, THEN, TO, STEP, ELSE, or an unmatched close parenthesis). Returns (rpn, next position, error).
def compile_expr(toks, pos):
    # The output (the compiled RPN list), the operator stack, and one argument counter per open "(" that belongs to a function call or array.
    out = []
    ops = []
    argc = []
    # `expect` is True when an operand (a value, "(", a function, NOT or a minus sign) must come next, and False after an operand, when an operator
    # or a closing bracket must. It is what tells unary minus from binary minus, which is how "3 +" is caught; "3 4" is left for the statement to reject, since the expression simply stops after the 3.
    expect = True
    # The loop is bounded by the number of tokens (each turn consumes at least one) because Starlark has no `while`.
    n = len(toks)
    i = pos
    depth = 0
    for _ in range(n + 2):
        if i >= n:
            break
        t = toks[i]
        kind = t[0]
        # A number or a string: push it as a constant. Two operands in a row means the expression has ended, so stop and let the caller deal with the rest.
        if kind == "n" or kind == "s":
            if not expect:
                break
            out.append(("c", t[1]))
            expect = False
            i += 1
        # A variable. Followed by "(" it is an array element: mark the open bracket on the operator stack so the subscripts are gathered, and count
        # its first argument. Otherwise it is a plain variable.
        elif kind == "v":
            if not expect:
                break
            if i + 1 < n and toks[i + 1][0] == "o" and toks[i + 1][1] == "(":
                ops.append(("M", "arr", (t[1], t[2])))
                argc.append(1)
                depth += 1
                i += 2
            else:
                out.append(("var", t[1], t[2]))
                expect = False
                i += 1
        # A keyword: a built-in function, FN (a user function), NOT, AND or OR. Any other keyword (THEN, TO, STEP, ELSE...) ends the expression.
        elif kind == "k":
            w = t[1]
            if w in FUNCS:
                if not expect:
                    break
                if i + 1 < n and toks[i + 1][0] == "o" and toks[i + 1][1] == "(":
                    ops.append(("M", "call", w))
                    argc.append(1)
                    depth += 1
                    i += 2
                elif w in ZERO_ARG:
                    out.append(("call", w, 0))
                    expect = False
                    i += 1
                else:
                    return ([], i, "SN")
            elif w == "FN":
                if not expect:
                    break
                if i + 1 >= n or toks[i + 1][0] != "v":
                    return ([], i, "SN")
                name = toks[i + 1][1] + toks[i + 1][2]
                if i + 2 < n and toks[i + 2][0] == "o" and toks[i + 2][1] == "(":
                    ops.append(("M", "ufn", name))
                    argc.append(1)
                    depth += 1
                    i += 3
                else:
                    out.append(("ufn", name, 0))
                    expect = False
                    i += 2
            # NOT is a prefix operator and applies to what follows it, so it just goes on the operator stack.
            elif w == "NOT":
                if not expect:
                    break
                ops.append(("U", "NOT"))
                i += 1
            # Binary operators: first move every operator already on the stack that binds at least as tightly into the output, then push this one.
            elif w == "AND" or w == "OR":
                if expect:
                    return ([], i, "SN")
                p = PREC[w]
                for _ in range(len(ops)):
                    if len(ops) == 0 or ops[-1][0] == "M" or PREC[ops[-1][1]] < p:
                        break
                    out.append(ops.pop())
                ops.append(("B", w))
                expect = True
                i += 1
            else:
                break
        # An operator or punctuation mark.
        elif kind == "o":
            op = t[1]
            if op == "(":
                if not expect:
                    break
                ops.append(("M", "paren", ""))
                depth += 1
                i += 1
            # A closing bracket: move operators to the output back to the matching "(" and drop it. If that bracket belonged to a function, array or FN,
            # emit that item with its argument count.
            elif op == ")":
                if depth == 0:
                    break
                if expect:
                    return ([], i, "SN")
                for _ in range(len(ops) + 1):
                    if ops[-1][0] == "M":
                        break
                    out.append(ops.pop())
                m = ops.pop()
                depth -= 1
                if m[1] == "arr":
                    out.append(("arr", m[2], argc.pop()))
                elif m[1] == "call":
                    out.append(("call", m[2], argc.pop()))
                elif m[1] == "ufn":
                    out.append(("ufn", m[2], argc.pop()))
                expect = False
                i += 1
            # An argument separator inside a function call or subscript list: finish the argument (flush its operators) and count the next one.
            elif op == ",":
                if depth == 0:
                    break
                if expect:
                    return ([], i, "SN")
                for _ in range(len(ops) + 1):
                    if ops[-1][0] == "M":
                        break
                    out.append(ops.pop())
                if ops[-1][1] == "paren":
                    return ([], i, "SN")
                argc[-1] += 1
                expect = True
                i += 1
            # A sign where an operand is expected. A minus becomes the unary operator; a plus does nothing.
            elif expect and (op == "-" or op == "+"):
                if op == "-":
                    ops.append(("U", "u-"))
                i += 1
            # An ordinary binary operator: pop everything that binds tighter or equally, then push this one.
            elif op in BINARY:
                if expect:
                    return ([], i, "SN")
                p = PREC[op]
                for _ in range(len(ops)):
                    if len(ops) == 0 or ops[-1][0] == "M" or PREC[ops[-1][1]] < p:
                        break
                    out.append(ops.pop())
                ops.append(("B", op))
                expect = True
                i += 1
            else:
                break
        else:
            break
    # Ended in the middle of an expression (an operator or "(" still open): a syntax error.
    if expect or depth != 0:
        return ([], i, "SN")
    # End of the expression: move the remaining operators to the output.
    for _ in range(len(ops)):
        if len(ops) == 0:
            break
        out.append(ops.pop())
    return (out, i, "")

# ---------------------------------------------------------------- operators

# A number as a 16-bit integer, rounding down. Returns (value, error): "TM" for a string, "OV" outside -32768 to 32767. Used by AND, OR and NOT,
# which work on 16-bit integers.
def to_int16(v):
    if kind_of(v) == 3:
        return (0, "TM")
    n = floor_int(to_float(v))
    if n < INT_MIN or n > INT_MAX:
        return (0, "OV")
    return (n, "")

# Check a floating-point result and give it the right type: a double stays a double, anything else is rounded to single precision. Returns
# (value, error); a result that overflows (or is not a number) is the "OV" error.
def real_result(r, double):
    if double:
        if r > 1.7976931348623157e308 or r < -1.7976931348623157e308 or r != r:
            return (None, "OV")
        return ((r,), "")
    if overflows(r):
        return (None, "OV")
    return (to_single(r), "")

# Apply a comparison operator. BASIC's true is -1 and false is 0, so the result is -1 or 0.
def compare(op, a, b):
    if op == "=":
        r = a == b
    elif op == "<>":
        r = a != b
    elif op == "<":
        r = a < b
    elif op == ">":
        r = a > b
    elif op == "<=":
        r = a <= b
    else:
        r = a >= b
    if r:
        return (-1, "")
    return (0, "")

# Apply a binary operator to two values. Returns (value, error). Strings may be joined with + and compared; AND/OR work on 16-bit integers;
# integer + - * stay integers while they fit and become single precision when they overflow; mixing types widens to the larger one; division
# by zero is "/0".
def binop(op, a, b):
    ka = kind_of(a)
    kb = kind_of(b)
    if ka == 3 or kb == 3:
        if ka != kb:
            return (None, "TM")
        if op == "+":
            r = a + b
            if len(r) > MAX_STRING:
                return (None, "LS")
            return (r, "")
        if op in ["=", "<>", "<", ">", "<=", ">="]:
            return compare(op, a, b)
        return (None, "TM")
    if op == "AND" or op == "OR":
        x = to_int16(a)
        y = to_int16(b)
        if x[1] != "":
            return (None, x[1])
        if y[1] != "":
            return (None, y[1])
        if op == "AND":
            return (x[0] & y[0], "")
        return (x[0] | y[0], "")
    k = max(ka, kb)
    if op in ["=", "<>", "<", ">", "<=", ">="]:
        return compare(op, to_float(a) if k > 0 else a, to_float(b) if k > 0 else b)
    if k == 0 and (op == "+" or op == "-" or op == "*"):
        if op == "+":
            r = a + b
        elif op == "-":
            r = a - b
        else:
            r = a * b
        if r >= INT_MIN and r <= INT_MAX:
            return (r, "")
        return real_result(float(r), False)
    fa = to_float(a)
    fb = to_float(b)
    double = k == 2
    if op == "+":
        return real_result(fa + fb, double)
    if op == "-":
        return real_result(fa - fb, double)
    if op == "*":
        return real_result(fa * fb, double)
    if op == "/":
        if fb == 0.0:
            return (None, "/0")
        return real_result(fa / fb, double)
    if op == "^":
        r = f_pow(fa, fb)
        if r[1] != "":
            return (None, r[1])
        return real_result(r[0], double)
    return (None, "SN")

# Apply a unary operator: negation, or NOT, which is the 16-bit complement (-x - 1). Returns (value, error).
def unop(op, a):
    if kind_of(a) == 3:
        return (None, "TM")
    if op == "u-":
        return (negate(a), "")
    x = to_int16(a)
    if x[1] != "":
        return (None, x[1])
    return (-x[0] - 1, "")

# ---------------------------------------------------------------- built-in functions

# Give a math function's result the right type (single, or double if the argument was a double). Same as real_result.
def real_of(x, double):
    return real_result(x, double)

# A whole number as BASIC's integer type if it fits in 16 bits, otherwise as a single-precision number.
def int_or_single(n):
    if n >= INT_MIN and n <= INT_MAX:
        return (n, "")
    return real_result(float(n), False)

# True if `v` is a string (kind 3). Used to check function arguments.
def need_string(v):
    return kind_of(v) == 3

# Call built-in `name` with the already evaluated arguments. Returns (value, error).
def call_function(st, name, args):
    n = len(args)
    a = args[0] if n > 0 else None
    if name == "LEN":
        if n != 1 or not need_string(a):
            return (None, "TM")
        return (len(a), "")
    if name == "LEFT$" or name == "RIGHT$":
        if n != 2 or not need_string(a) or kind_of(args[1]) == 3:
            return (None, "TM")
        c = floor_int(to_float(args[1]))
        if c < 0 or c > 255:
            return (None, "FC")
        if name == "LEFT$":
            return (a[:c], "")
        if c >= len(a):
            return (a, "")
        return (a[len(a) - c:], "")
    if name == "MID$":
        if (n != 2 and n != 3) or not need_string(a) or kind_of(args[1]) == 3:
            return (None, "TM")
        start = floor_int(to_float(args[1]))
        if start < 1 or start > 255:
            return (None, "FC")
        if n == 3:
            if kind_of(args[2]) == 3:
                return (None, "TM")
            c = floor_int(to_float(args[2]))
            if c < 0 or c > 255:
                return (None, "FC")
            return (a[start - 1:start - 1 + c], "")
        return (a[start - 1:], "")
    if name == "ASC":
        if n != 1 or not need_string(a):
            return (None, "TM")
        if len(a) == 0:
            return (None, "FC")
        return (CODES.get(a[0], 63), "")
    if name == "CHR$":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        c = floor_int(to_float(a))
        if c < 0 or c > 255:
            return (None, "FC")
        return ("%c" % c, "")
    if name == "STR$":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        return (str_text(a), "")
    if name == "VAL":
        if n != 1 or not need_string(a):
            return (None, "TM")
        return (val_text(a), "")
    if name == "INSTR":
        if n != 2 and n != 3:
            return (None, "SN")
        start = 1
        s = args[0]
        t = args[1]
        if n == 3:
            if kind_of(args[0]) == 3:
                return (None, "TM")
            start = floor_int(to_float(args[0]))
            if start < 1 or start > 255:
                return (None, "FC")
            s = args[1]
            t = args[2]
        if not need_string(s) or not need_string(t):
            return (None, "TM")
        if start > len(s):
            return (0, "")
        if t == "":
            return (start, "")
        return (s.find(t, start - 1) + 1, "")
    if name == "STRING$":
        if n != 2 or kind_of(a) == 3:
            return (None, "TM")
        c = floor_int(to_float(a))
        if c < 0 or c > 255:
            return (None, "FC")
        ch = args[1]
        if need_string(ch):
            if len(ch) == 0:
                return (None, "FC")
            return (ch[0] * c, "")
        code = floor_int(to_float(ch))
        if code < 0 or code > 255:
            return (None, "FC")
        return (("%c" % code) * c, "")
    if name == "INKEY$":
        if len(st["inkey"]) > 0:
            return (st["inkey"].pop(0), "")
        return ("", "")
    if name == "MEM" or name == "FRE":
        return (14000, "")
    if name == "ERL":
        return int_or_single(st["errline"])
    if name == "ERR":
        return (st["errcode"], "")
    if name == "POS":
        return (st["col"], "")
    if name == "POINT":
        if n != 2:
            return (None, "SN")
        x = floor_int(to_float(args[0]))
        y = floor_int(to_float(args[1]))
        if x < 0 or x > 127 or y < 0 or y > 47:
            return (None, "FC")
        c = st["screen"][(y // 3) * 64 + x // 2]
        if c >= 128 and c <= 191 and (c & (1 << ((y % 3) * 2 + x % 2))) != 0:
            return (1, "")
        return (0, "")
    if name == "PEEK" or name == "INP":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        addr = floor_int(to_float(a))
        if addr < 0:
            addr += 65536
        if addr >= 15360 and addr < 16384:
            return (st["screen"][addr - 15360], "")
        return (st["mem"].get(addr, 0), "")
    if n != 1:
        return (None, "SN")
    if kind_of(a) == 3:
        return (None, "TM")
    k = kind_of(a)
    double = k == 2
    x = to_float(a)
    if name == "ABS":
        if k == 0:
            return int_or_single(abs(a))
        return (negate(a) if x < 0.0 else a, "")
    if name == "SGN":
        if x > 0.0:
            return (1, "")
        if x < 0.0:
            return (-1, "")
        return (0, "")
    if name == "INT":
        if k == 0:
            return (a, "")
        f = floor_int(x)
        if double:
            return ((float(f),), "")
        return int_or_single(f)
    if name == "FIX":
        if k == 0:
            return (a, "")
        f = int(x)
        if double:
            return ((float(f),), "")
        return int_or_single(f)
    if name == "CINT":
        f = floor_int(x)
        if f < INT_MIN or f > INT_MAX:
            return (None, "OV")
        return (f, "")
    if name == "CSNG":
        return real_of(x, False)
    if name == "CDBL":
        return ((x,), "")
    if name == "SQR":
        if x < 0.0:
            return (None, "FC")
        return real_of(f_sqr(x), double)
    if name == "EXP":
        r = f_exp(x)
        if r == None:
            return (None, "OV")
        return real_of(r, double)
    if name == "LOG":
        if x <= 0.0:
            return (None, "FC")
        return real_of(f_log(x), double)
    if name == "SIN":
        return real_of(f_sin(x), double)
    if name == "COS":
        return real_of(f_cos(x), double)
    if name == "TAN":
        c = f_cos(x)
        if c == 0.0:
            return (None, "OV")
        return real_of(f_sin(x) / c, double)
    if name == "ATN":
        return real_of(f_atn(x), double)
    if name == "RND":
        if x < 0.0:
            st["seed"] = (floor_int(-x) * 7919 + 13) % 2147483648
        st["seed"] = rnd_next(st["seed"])
        frac = (st["seed"] // 128) / 16777216.0
        if x >= 1.0:
            return int_or_single(int(frac * floor_int(x)) + 1)
        return (to_single(frac), "")
    return (None, "SN")

# ---------------------------------------------------------------- arrays

# Create an array. `dims` holds the largest subscript of each dimension, so the element count is the product of (d + 1). "OM" (out of memory)
# if that is more than 20,000 elements. Every element starts at the zero value of the array's type.
def new_array(st, key, dims):
    total = 1
    for d in dims:
        total *= d + 1
    if total > 20000:
        return "OM"
    st["arrays"][key] = [dims, [zero_of(key_kind(key))] * total]
    return ""

# Flat position of an element, creating the array (with 10 as every bound) on first use. Returns (array, position, error).
def locate(st, key, subs):
    arr = st["arrays"].get(key)
    if arr == None:
        err = new_array(st, key, [10] * len(subs))
        if err != "":
            return (None, 0, err)
        arr = st["arrays"][key]
    dims = arr[0]
    if len(dims) != len(subs):
        return (None, 0, "BS")
    pos = 0
    for j in range(len(dims)):
        s = subs[j]
        if kind_of(s) == 3:
            return (None, 0, "TM")
        s = floor_int(to_float(s))
        if s < 0 or s > dims[j]:
            return (None, 0, "BS")
        pos = pos * (dims[j] + 1) + s
    return (arr, pos, "")

# ---------------------------------------------------------------- evaluating

MAX_FN_DEPTH = 30

# Run a compiled expression. Returns (value, error).
def eval_rpn(st, rpn):
    stack = []
    frames = []
    cur = rpn
    pc = 0
    # A bound, not a limit anyone should reach: Starlark has no `while`, and each turn takes one item.
    for _ in range(100000000):
        # Reached the end of the current code. Either the whole expression is done (no frames left), or a user function has finished: restore the
        # caller's variables, code position and stack, and push the function's value for the caller to use.
        if pc >= len(cur):
            val = stack[-1]
            if len(frames) == 0:
                return (val, "")
            f = frames.pop()
            for pair in f[3]:
                if pair[1] == None:
                    st["vars"].pop(pair[0], None)
                else:
                    st["vars"][pair[0]] = pair[1]
            cur = f[0]
            pc = f[1]
            stack = f[2]
            stack.append(val)
            continue
        item = cur[pc]
        pc += 1
        tag = item[0]
        if tag == "c":
            stack.append(item[1])
        elif tag == "var":
            key = var_key(st, item[1], item[2])
            v = st["vars"].get(key)
            if v == None:
                v = zero_of(key_kind(key))
            stack.append(v)
        # A binary operator: its two operands are the top of the stack (right operand on top).
        elif tag == "B":
            b = stack.pop()
            a = stack.pop()
            r = binop(item[1], a, b)
            if r[1] != "":
                return (None, r[1])
            stack.append(r[0])
        elif tag == "U":
            r = unop(item[1], stack.pop())
            if r[1] != "":
                return (None, r[1])
            stack.append(r[0])
        # A built-in function: take its arguments off the stack, in order, and call it.
        elif tag == "call":
            c = item[2]
            args = stack[len(stack) - c:]
            stack = stack[:len(stack) - c]
            r = call_function(st, item[1], args)
            if r[1] != "":
                return (None, r[1])
            stack.append(r[0])
        # An array element: take the subscripts off the stack and look up the element (creating an array with subscripts 0 to 10 in every dimension on first use, as BASIC does).
        elif tag == "arr":
            c = item[2]
            subs = stack[len(stack) - c:]
            stack = stack[:len(stack) - c]
            key = var_key(st, item[1][0], item[1][1])
            loc = locate(st, key, subs)
            if loc[2] != "":
                return (None, loc[2])
            stack.append(loc[0][1][loc[1]])
        else:
            c = item[2]
            args = stack[len(stack) - c:]
            stack = stack[:len(stack) - c]
            # Anything else is a call to a DEF FN function.
            defn = st["fns"].get(item[1])
            if defn == None:
                return (None, "UF")
            params = defn[0]
            if len(params) != len(args):
                return (None, "SN")
            # Functions calling functions are limited to 30 deep ("OM"), since each level costs a frame.
            if len(frames) >= MAX_FN_DEPTH:
                return (None, "OM")
            # The values the parameters hide are saved so they can be restored afterwards. None means the variable did not exist.
            saved = []
            for j in range(len(params)):
                cv = coerce(key_kind(params[j]), args[j])
                if cv[1] != "":
                    return (None, cv[1])
                saved.append((params[j], st["vars"].get(params[j])))
            for j in range(len(params)):
                st["vars"][params[j]] = coerce(key_kind(params[j]), args[j])[0]
            # Remember where to come back to, then switch to running the function's body.
            frames.append((cur, pc, stack, saved))
            cur = defn[1]
            pc = 0
            stack = []
    return (None, "OM")

# Compile (with a cache for program lines) and evaluate the expression at toks[pos]. Returns (value, next position, error).
def eval_at(st, toks, pos):
    line = st["line"]
    cache_key = line * 4096 + pos
    hit = None
    if line > 0:
        hit = st["rpn"].get(cache_key)
    if hit == None:
        hit = compile_expr(toks, pos)
        if line > 0:
            st["rpn"][cache_key] = hit
    if hit[2] != "":
        return (None, hit[1], hit[2])
    r = eval_rpn(st, hit[0])
    return (r[0], hit[1], r[1])

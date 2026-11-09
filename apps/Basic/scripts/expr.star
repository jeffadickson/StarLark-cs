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

# How values are represented (kind_of in numbers.star tells them apart):
#   int          a BASIC integer (kept within INT_MIN..INT_MAX = -32768..32767 by the code below), kind 0
#   float        a single precision number, already rounded by to_single, kind 1
#   (x,)         a double precision number: a ONE-item tuple holding a float, so it can be told from a single, kind 2
#   str          a BASIC string, kind 3
# Whenever this file says "kind" it means one of those four numbers: 0 integer, 1 single, 2 double, 3 string. Most helpers in this file return a
# pair (value, error): when the error is "" the value is good, and when the error is a two letter code the value is None (or a dummy 0).
# Imported from numbers.star: the integer limits, type tests and conversions (kind_of, to_float, floor_int, negate, to_single, overflows), the text
# conversions for STR$ and VAL (str_text, val_text), and the math routines (f_sqr ... f_pow).
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
# Fill the set from this list. Only membership matters, so every value is True. (The leading underscore marks _w as a throwaway name.)
for _w in ["SGN", "INT", "ABS", "FRE", "INP", "POS", "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "CINT",
           "CSNG", "CDBL", "FIX", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "INSTR", "STRING$",
           "POINT", "MEM", "INKEY$", "ERL", "ERR"]:
    FUNCS[_w] = True

# Functions written without parentheses (INKEY$, MEM, ERL, ERR).
ZERO_ARG = {"INKEY$": True, "MEM": True, "ERL": True, "ERR": True}

# Levels, from loosest to tightest binding:
#   1 OR    2 AND    3 NOT    4 comparisons (= <> < > <= >=)    5 + -    6 * /    7 unary minus    8 ^
# Two consequences of how compile_expr uses the table: every binary operator is LEFT associative (an operator of EQUAL precedence already on the
# stack is popped first), including ^, so 2^3^2 is (2^3)^2; and NOT sits below the comparisons, so NOT A=B means NOT (A=B). The keys "NOT" and "u-"
# only ever name the prefix (unary) operators; "AND" and "OR" are the keyword binary operators.
# Operator precedence for the shunting-yard compiler: a higher number binds tighter. "u-" is unary minus. As on Level II, ^ binds tighter than
# unary minus, so -2^2 is -4.
PREC = {"OR": 1, "AND": 2, "NOT": 3, "=": 4, "<>": 4, "<": 4, ">": 4, "<=": 4, ">=": 4,
        "+": 5, "-": 5, "*": 6, "/": 6, "u-": 7, "^": 8}

# The symbolic binary operators. AND and OR are keywords and are handled separately.
BINARY = {"+": True, "-": True, "*": True, "/": True, "^": True,
          "=": True, "<>": True, "<": True, ">": True, "<=": True, ">=": True}

# The error codes this file produces, and when:
#   TM  type mismatch: a string where a number is needed, a number where a string is needed, or the wrong kind of argument to a function
#   OV  overflow: an integer conversion outside -32768..32767, or a single or double result too large (or not a number)
#   SN  syntax error: the compiler found a malformed expression, a function was given the wrong number of arguments, or an operator is unknown
#   FC  illegal function call: an argument is out of the range the function accepts (a negative LOG, CHR$(300), a POINT outside the screen...)
#   BS  bad subscript: an array subscript out of range, or the wrong number of subscripts
#   /0  division by zero
#   LS  string too long (a joined string of more than MAX_STRING characters)
#   OM  out of memory: an array bigger than the limit, DEF FN calls nested too deeply, or the evaluator's step bound used up
#   UF  undefined user function: no DEF FN of that name has been stored
# A function that fails returns the code; "" means success.
# The longest string BASIC allows; a longer result is the "LS" (string too long) error.
MAX_STRING = 255

# The dictionary key for a variable: its two-character name plus a type suffix. An untyped name takes its type from the DEFINT/DEFSTR table
# for its first letter, so with the default (single precision) A is stored as "A!".
# `name` is the two character variable name from the lexer, `suffix` is "", "%", "!", "#" or "$". The result is the key used in st["vars"] (scalars)
# and st["arrays"] (arrays), so A%, A!, A# and A$ are four different variables, and none of them is the same as the array of that name.
def var_key(st, name, suffix):
    # No suffix was typed. st["deftype"] maps a first letter to a kind number (the DEFINT / DEFSNG / DEFDBL / DEFSTR setting), and KIND_SUFFIX turns
    # that number back into the suffix character.
    if suffix == "":
        return name + KIND_SUFFIX[st["deftype"][name[0]]]
    # A suffix that was typed always wins over the DEF setting.
    return name + suffix

# The value a variable of this kind has before it is assigned: 0, 0.0, a double 0.0, or the empty string.
def zero_of(kind):
    if kind == 0:
        return 0
    # Kind 1 is single precision, which is a plain float.
    if kind == 1:
        return 0.0
    # Kind 2 is double precision: a one-item tuple, to tell it apart from a single.
    if kind == 2:
        return (0.0,)
    # Kind 3 (string): the empty string.
    return ""

# The kind (0 to 3) of a variable key, from its suffix.
def key_kind(key):
    # var_key always puts the type suffix last, so the final character of any key says what kind of variable it is.
    return SUFFIX_KIND[key[-1]]

# How a value is coerced when it is stored (variables, arrays and DEF FN parameters all use this):
#   kind 3 (string)   accepts only a string; a number is "TM"
#   kind 0, 1, 2      accept any number; a string is "TM"; a value of the same kind is kept as it is
#   to integer        the number is rounded down (floor_int); outside -32768..32767 is "OV"
#   to single         a value too big for single precision is "OV"; otherwise it is rounded with to_single
#   to double         the float is wrapped in a one-item tuple, which cannot fail
# Convert a value to what a variable of this kind holds. Returns (value, error).
def coerce(kind, v):
    # `vk` is the kind of the value being stored; `kind` is the kind of the variable it is being stored into.
    vk = kind_of(v)
    # A string variable accepts only strings; anything else is a type mismatch.
    if kind == 3:
        if vk != 3:
            return (None, "TM")
        return (v, "")
    # Here the variable is numeric, so a string value cannot be stored in it.
    if vk == 3:
        return (None, "TM")
    # Same kind on both sides: nothing to convert.
    if kind == vk:
        return (v, "")
    # Different numeric kinds: go through a plain float (a double is unwrapped from its tuple, an integer is widened) and then narrow or widen to the target.
    f = to_float(v)
    if kind == 0:
        # Storing into an integer variable rounds the number down, then it must fit in 16 bits.
        n = floor_int(f)
        if n < INT_MIN or n > INT_MAX:
            return (None, "OV")
        return (n, "")
    # Target is single precision. A double or an integer arrives here as the float `f`.
    if kind == 1:
        if overflows(f):
            return (None, "OV")
        return (to_single(f), "")
    # The only target left is double precision (kind 2): wrap the float in a one-item tuple.
    return ((f,), "")

# THE COMPILER
# compile_expr turns a list of tokens into reverse Polish notation (RPN): a flat list in which every operator comes after its operands, so the
# evaluator needs no precedence rules and no brackets. For example A+B*2 compiles to
#     ("var","A",""), ("var","B",""), ("c",2), ("B","*"), ("B","+")
# and X(I+1)*2 compiles to
#     ("var","I",""), ("c",1), ("B","+"), ("arr",("X",""),1), ("c",2), ("B","*")
# It is the shunting-yard algorithm: operands go straight to the output list; operators wait on a stack until something of lower or equal
# precedence shows up (see PREC). The compiler is a single loop over the tokens that remembers one bit of state, `expect`: whether an operand is
# due next or an operator is. That bit is how unary minus is told from binary minus (a "-" where an operand is due is unary), and it is how
# malformed input such as "3 +" or "()" is caught. Open brackets of every kind are markers on the operator stack, and a counter of the arguments
# seen so far is kept for each marker that opens a function call, an array subscript list or an FN call.
# ---------------------------------------------------------------- compiling

# Compile the expression that starts at toks[pos]. Stops at the first token that cannot continue it (a comma, semicolon,
# colon, THEN, TO, STEP, ELSE, or an unmatched close parenthesis). Returns (rpn, next position, error).
# Token shapes come from lex.star: ("n", number), ("s", text), ("v", name, suffix), ("k", KEYWORD), ("o", operator or punctuation).
# Entries on the operator stack `ops` come in three shapes:
#   ("B", op)                  a binary operator waiting for its right operand ("+", "<=", "AND"...)
#   ("U", op)                  a prefix operator, "u-" (unary minus) or "NOT"
#   ("M", what, payload)       a marker for an open bracket. what is "paren" (plain grouping, payload ""), "arr" (payload (name, suffix)),
#                              "call" (payload the built-in's name) or "ufn" (payload the DEF FN name).
# The ("B", ...) and ("U", ...) entries are exactly the RPN items the evaluator wants, so they are copied to the output unchanged. A marker is never
# copied: the closing bracket discards it and emits the finished ("arr"/"call"/"ufn") item instead.
# Returns the 3-tuple (rpn, next position, error). On success, "next position" is the index of the first token that is not part of the expression;
# on an error the list is empty and the position is the token where the problem was seen.
def compile_expr(toks, pos):
    # The output (the compiled RPN list), the operator stack, and one argument counter per open "(" that belongs to a function call or array.
    # Shapes: `out` holds finished RPN items (listed at the top of the file); `ops` holds the entries described above; `argc` is parallel to the
    # call/array/FN markers on `ops` only (not to plain parentheses): its last item is the argument count of the innermost such open bracket.
    out = []
    ops = []
    argc = []
    # `expect` is True when an operand (a value, "(", a function, NOT or a minus sign) must come next, and False after an operand, when an operator
    # or a closing bracket must. It is what tells unary minus from binary minus, which is how "3 +" is caught; "3 4" is left for the statement to reject, since the expression simply stops after the 3.
    expect = True
    # The loop is bounded by the number of tokens (each turn consumes at least one) because Starlark has no `while`.
    n = len(toks)
    # `i` is the index of the next token to look at.
    i = pos
    # `depth` counts the brackets (of every kind) that are open right now. It tells ")" and "," whether they belong to this expression: with depth 0
    # they belong to whatever contains the expression (a statement), so the expression simply ends there.
    depth = 0
    # Each turn either consumes at least one token or leaves the loop, so n + 2 turns is more than enough.
    for _ in range(n + 2):
        # Out of tokens: stop. The check after the loop decides whether the expression was complete.
        if i >= n:
            break
        t = toks[i]
        # The token type letter: "n" number, "s" string, "v" variable, "k" keyword, "o" operator.
        kind = t[0]
        # A number or a string: push it as a constant. Two operands in a row means the expression has ended, so stop and let the caller deal with the rest.
        if kind == "n" or kind == "s":
            if not expect:
                break
            # t[1] is the value itself: an int, a float, a one-item tuple (double) or a string. After an operand an operator must follow, so `expect` goes False.
            out.append(("c", t[1]))
            expect = False
            i += 1
        # A variable. Followed by "(" it is an array element: mark the open bracket on the operator stack so the subscripts are gathered, and count
        # its first argument. Otherwise it is a plain variable.
        elif kind == "v":
            if not expect:
                break
            if i + 1 < n and toks[i + 1][0] == "o" and toks[i + 1][1] == "(":
                # Variable followed by "(": an array element. The marker keeps the name and suffix; it is replaced by an ("arr", (name, suffix), count) item
                # when the matching ")" is reached. `expect` stays True because the first subscript must follow.
                ops.append(("M", "arr", (t[1], t[2])))
                # Start the argument counter at 1: the first argument is the one about to be read. (An empty list such as "F()" is rejected at the ")" because
                # `expect` is still True there.)
                argc.append(1)
                depth += 1
                # Skip both the name and the "(" token.
                i += 2
            else:
                # A scalar variable: the name and suffix are kept as typed; eval_rpn applies var_key when it runs, because an untyped name's type comes from the
                # DEFINT/DEFSTR table.
                out.append(("var", t[1], t[2]))
                expect = False
                i += 1
        # A keyword: a built-in function, FN (a user function), NOT, AND or OR. Any other keyword (THEN, TO, STEP, ELSE...) ends the expression.
        elif kind == "k":
            # The keyword text, such as "SQR", "FN", "AND".
            w = t[1]
            # A built-in function. It is an operand, so it is only legal where an operand is expected; otherwise the expression has ended.
            if w in FUNCS:
                if not expect:
                    break
                if i + 1 < n and toks[i + 1][0] == "o" and toks[i + 1][1] == "(":
                    # Function followed by "(": push a marker (payload: the function name). The ")" will emit ("call", name, argument count).
                    ops.append(("M", "call", w))
                    argc.append(1)
                    depth += 1
                    i += 2
                # No bracket follows. Only the functions that take no arguments may be written that way; they become a call with 0 arguments at once.
                # Any other function name without "(" is a syntax error (the next branch).
                elif w in ZERO_ARG:
                    out.append(("call", w, 0))
                    expect = False
                    i += 1
                else:
                    return ([], i, "SN")
            # A user function call: FN name or FN name(args). The name token that follows FN is a variable token.
            elif w == "FN":
                if not expect:
                    break
                # FN must be followed by a name; otherwise it is a syntax error.
                if i + 1 >= n or toks[i + 1][0] != "v":
                    return ([], i, "SN")
                # The function's name: the two-character name plus its suffix. DEF FN stores the function under the same string (see st_def in basic.star).
                name = toks[i + 1][1] + toks[i + 1][2]
                if i + 2 < n and toks[i + 2][0] == "o" and toks[i + 2][1] == "(":
                    # FN name( ... ): a marker, to be turned into ("ufn", name, argument count) at the ")". Skip FN, the name and "(" (three tokens).
                    ops.append(("M", "ufn", name))
                    argc.append(1)
                    depth += 1
                    i += 3
                else:
                    # FN name with no bracket: a call with no arguments. Skip FN and the name (two tokens).
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
                # Move operators from the stack to the output while they bind at least as tightly as the new one. The loop can pop at most len(ops) times, which is
                # the bound Starlark (no `while`) needs.
                for _ in range(len(ops)):
                    # Stop when the stack is empty, when the top is a marker (an open bracket is never popped by an operator, so the operators inside a bracket stay
                    # inside it), or when the top binds looser than the new operator. Anything else is popped to the output: equal precedence pops too, which makes the
                    # operators left associative.
                    if len(ops) == 0 or ops[-1][0] == "M" or PREC[ops[-1][1]] < p:
                        break
                    out.append(ops.pop())
                # Push the new operator; its right operand is still to come, so an operand is due next.
                ops.append(("B", w))
                expect = True
                i += 1
            else:
                break
        # An operator or punctuation mark.
        elif kind == "o":
            op = t[1]
            # A "(" that does not follow a function or array name is a grouping bracket. It is an operand start, so it is only allowed where an operand is
            # expected; after an operand the expression just ends.
            if op == "(":
                if not expect:
                    break
                # The "paren" marker emits nothing when it is closed; it only stops operators from moving out through it.
                ops.append(("M", "paren", ""))
                depth += 1
                i += 1
            # A closing bracket: move operators to the output back to the matching "(" and drop it. If that bracket belonged to a function, array or FN,
            # emit that item with its argument count.
            elif op == ")":
                # No bracket is open, so this ")" belongs to something outside the expression: stop here and leave it for the caller.
                if depth == 0:
                    break
                # A ")" where an operand is due, as in "(" followed straight away by ")" or "3 + )": a syntax error.
                if expect:
                    return ([], i, "SN")
                # Flush the pending operators to the output, down to the nearest marker. depth is above 0, so a marker is always on the stack and this loop always
                # reaches it; the bound is only the stack size, plus one for safety.
                for _ in range(len(ops) + 1):
                    if ops[-1][0] == "M":
                        break
                    out.append(ops.pop())
                # Take the marker off the stack. Its second item says what kind of bracket it was.
                m = ops.pop()
                depth -= 1
                # Array, built-in function and FN markers become an RPN item carrying the number of arguments counted (argc.pop() is that bracket's counter). A
                # plain "paren" marker needs no item: the operators inside it are already in the output.
                if m[1] == "arr":
                    out.append(("arr", m[2], argc.pop()))
                elif m[1] == "call":
                    out.append(("call", m[2], argc.pop()))
                elif m[1] == "ufn":
                    out.append(("ufn", m[2], argc.pop()))
                # A closed bracket ends an operand, so an operator (or another closing bracket) is due next.
                expect = False
                i += 1
            # An argument separator inside a function call or subscript list: finish the argument (flush its operators) and count the next one.
            elif op == ",":
                if depth == 0:
                    break
                # A "," where an operand is due, as in "F(,2)" or "F(1,,2)": a syntax error.
                if expect:
                    return ([], i, "SN")
                # Finish the argument that just ended: flush its pending operators down to the nearest marker (same loop as for ")").
                for _ in range(len(ops) + 1):
                    if ops[-1][0] == "M":
                        break
                    out.append(ops.pop())
                # The nearest open bracket is a plain grouping bracket, such as "(1,2)". Commas are only allowed in a call, an array or an FN list, so this is a
                # syntax error.
                if ops[-1][1] == "paren":
                    return ([], i, "SN")
                # One more argument seen in the innermost call, array or FN. The marker stays on the stack; the next argument starts, so an operand is due.
                argc[-1] += 1
                expect = True
                i += 1
            # A sign where an operand is expected. A minus becomes the unary operator; a plus does nothing.
            elif expect and (op == "-" or op == "+"):
                # This branch is tried before the binary-operator one, so a "-" or "+" that arrives where an operand is due is a sign, and the same character after
                # an operand falls through to the binary operators below. A minus becomes the prefix item ("U", "u-") that waits on the stack (nothing is popped,
                # because a prefix operator has no left operand). `expect` stays True: the operand it applies to is still to come.
                if op == "-":
                    ops.append(("U", "u-"))
                i += 1
            # An ordinary binary operator: pop everything that binds tighter or equally, then push this one.
            elif op in BINARY:
                if expect:
                    return ([], i, "SN")
                # Same pop-while-tighter loop as for AND and OR above: pop while the top is not a marker and binds at least as tightly, then push this operator.
                p = PREC[op]
                for _ in range(len(ops)):
                    if len(ops) == 0 or ops[-1][0] == "M" or PREC[ops[-1][1]] < p:
                        break
                    out.append(ops.pop())
                # The binary operator is pushed; an operand must follow it, so `expect` is set True on the next line.
                ops.append(("B", op))
                expect = True
                i += 1
            else:
                break
        else:
            break
    # Ended in the middle of an expression (an operator or "(" still open): a syntax error.
    # The tokens ran out (or something ended the expression) while an operand was still due, as in "3 +", or while a bracket was still open.
    if expect or depth != 0:
        return ([], i, "SN")
    # End of the expression: move the remaining operators to the output.
    # Every remaining entry is a ("B", ...) or ("U", ...) item here, because depth is 0 and so no marker is left. They go out from the top of the
    # stack down, which is the right order: the tightest binding operator is on top and is applied first.
    for _ in range(len(ops)):
        if len(ops) == 0:
            break
        out.append(ops.pop())
    # Success: the RPN list, the index of the first token left over (the expression may be followed by a comma, THEN, a colon...), and no error.
    return (out, i, "")

# OPERATORS
# Rules for arithmetic, shared by the evaluator:
#   * Any number operand with a string operand is "TM" (both must be strings or both numbers). Two strings may be joined with + (result "LS" when
#     longer than 255) or compared; every other operator on strings is "TM".
#   * AND, OR and NOT first turn their operands into 16-bit integers (to_int16: rounded down, "OV" if outside -32768..32767).
#   * Comparisons give -1 for true and 0 for false.
#   * When both operands are integers, + - * give an integer if the result still fits in 16 bits, otherwise a single. "/" and "^" always work in
#     floating point.
#   * In all other cases the result kind is the wider of the two operands (single beats integer, double beats both). The arithmetic itself is done in
#     ordinary floats and then checked and rounded by real_result: "OV" on overflow, a one-item tuple for double, to_single for single.
#   * Division by zero is "/0".
# ---------------------------------------------------------------- operators

# A number as a 16-bit integer, rounding down. Returns (value, error): "TM" for a string, "OV" outside -32768 to 32767. Used by AND, OR and NOT,
# which work on 16-bit integers.
def to_int16(v):
    # A string is not a number.
    if kind_of(v) == 3:
        return (0, "TM")
    # Round down, whatever the kind (a double is unwrapped by to_float first).
    n = floor_int(to_float(v))
    if n < INT_MIN or n > INT_MAX:
        return (0, "OV")
    return (n, "")

# Check a floating-point result and give it the right type: a double stays a double, anything else is rounded to single precision. Returns
# (value, error); a result that overflows (or is not a number) is the "OV" error.
def real_result(r, double):
    # A double result is checked against the largest finite double, and "r != r" is true only for NaN (not a number). Either is an overflow.
    if double:
        if r > 1.7976931348623157e308 or r < -1.7976931348623157e308 or r != r:
            return (None, "OV")
        return ((r,), "")
    # Single precision: too big (or NaN) is an overflow; anything else is rounded to the nearest single.
    if overflows(r):
        return (None, "OV")
    return (to_single(r), "")

# Apply a comparison operator. BASIC's true is -1 and false is 0, so the result is -1 or 0.
def compare(op, a, b):
    if op == "=":
        # The six comparisons just use the host comparison on the two values. The caller has already put the operands in a comparable form (both
        # strings, both ints, or both floats).
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
    # BASIC's true is -1 (all bits set), false is 0.
    if r:
        return (-1, "")
    return (0, "")

# Apply a binary operator to two values. Returns (value, error). Strings may be joined with + and compared; AND/OR work on 16-bit integers;
# integer + - * stay integers while they fit and become single precision when they overflow; mixing types widens to the larger one; division
# by zero is "/0".
def binop(op, a, b):
    # Kinds of the left and right operand (0 integer, 1 single, 2 double, 3 string).
    ka = kind_of(a)
    kb = kind_of(b)
    # At least one operand is a string.
    if ka == 3 or kb == 3:
        # One string and one number: always a type mismatch.
        if ka != kb:
            return (None, "TM")
        # Two strings: + joins them.
        if op == "+":
            r = a + b
            # The joined string is longer than 255 characters.
            if len(r) > MAX_STRING:
                return (None, "LS")
            return (r, "")
        # Two strings can be compared. The comparison uses the host's string ordering and returns (-1 or 0, "") from compare.
        if op in ["=", "<>", "<", ">", "<=", ">="]:
            return compare(op, a, b)
        # Any other operator on two strings (- * / ^ AND OR) is a type mismatch.
        return (None, "TM")
    # Both operands are numbers now. AND and OR are bitwise operations on 16-bit integers; the left operand's error is reported before the right's.
    if op == "AND" or op == "OR":
        x = to_int16(a)
        y = to_int16(b)
        if x[1] != "":
            return (None, x[1])
        if y[1] != "":
            return (None, y[1])
        if op == "AND":
            # Bitwise AND / OR of two values in -32768..32767 stays in that range, so no overflow check is needed. -1 (true) AND x gives x, which is why true is -1.
            return (x[0] & y[0], "")
        return (x[0] | y[0], "")
    # The wider kind of the two numbers: 0 both integers, 1 at least one single, 2 at least one double.
    k = max(ka, kb)
    # Numeric comparison: two integers are compared as they are; any other mix is compared as floats (to_float unwraps doubles).
    if op in ["=", "<>", "<", ">", "<=", ">="]:
        return compare(op, to_float(a) if k > 0 else a, to_float(b) if k > 0 else b)
    # Integer arithmetic: both operands are integers.
    if k == 0 and (op == "+" or op == "-" or op == "*"):
        if op == "+":
            r = a + b
        elif op == "-":
            r = a - b
        else:
            r = a * b
        # The result still fits in 16 bits, so it stays an integer.
        if r >= INT_MIN and r <= INT_MAX:
            return (r, "")
        # Too big for an integer: the result becomes a single precision number instead of an error.
        return real_result(float(r), False)
    # Everything left is floating point arithmetic: both operands as floats, with the result kind decided by `double`.
    fa = to_float(a)
    fb = to_float(b)
    # The result is a double only when at least one operand was a double; integer and single operands give a single result.
    double = k == 2
    if op == "+":
        return real_result(fa + fb, double)
    if op == "-":
        return real_result(fa - fb, double)
    if op == "*":
        return real_result(fa * fb, double)
    # Division is always done in floating point, even for two integers.
    if op == "/":
        # Division by zero is "/0" (checked before dividing).
        if fb == 0.0:
            return (None, "/0")
        return real_result(fa / fb, double)
    # Exponentiation is done by f_pow from numbers.star, which returns (value, error); its errors ("FC", "OV", "/0") are passed on unchanged.
    if op == "^":
        r = f_pow(fa, fb)
        if r[1] != "":
            return (None, r[1])
        return real_result(r[0], double)
    # An operator this function does not know (none of the cases above matched).
    return (None, "SN")

# Apply a unary operator: negation, or NOT, which is the 16-bit complement (-x - 1). Returns (value, error).
def unop(op, a):
    # Both unary operators need a number.
    if kind_of(a) == 3:
        return (None, "TM")
    # Unary minus: negate keeps the kind of its operand (and turns the one integer that cannot be negated, -32768, into a single).
    if op == "u-":
        return (negate(a), "")
    # The only other prefix operator is NOT, which works on a 16-bit integer.
    x = to_int16(a)
    if x[1] != "":
        return (None, x[1])
    # NOT is the bitwise complement: -x - 1 (NOT 0 is -1, NOT -1 is 0).
    return (-x[0] - 1, "")

# BUILT-IN FUNCTIONS
# call_function receives the function's name and the list of already evaluated arguments, and returns (value, error). It is one long chain of
# tests on the name: first the string functions, then the functions that read the machine (INKEY$, MEM, ERL, ERR, POS, POINT, PEEK), and last
# the group of mathematical functions that all take exactly one number. Each case checks the argument count and kinds itself. In the string
# functions the count and the kinds are tested together, so a wrong count is reported as "TM" there; POINT, INSTR and the math group report a wrong count as "SN".
# ---------------------------------------------------------------- built-in functions

# Give a math function's result the right type (single, or double if the argument was a double). Same as real_result.
def real_of(x, double):
    return real_result(x, double)

# A whole number as BASIC's integer type if it fits in 16 bits, otherwise as a single-precision number.
# `n` is a whole number (an int) of any size, possibly outside the 16-bit range.
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
    # `a` is the first argument, for convenience (None when there are no arguments; every use is guarded by a count test first).
    a = args[0] if n > 0 else None
    # LEN(string): the number of characters.
    if name == "LEN":
        if n != 1 or not need_string(a):
            return (None, "TM")
        return (len(a), "")
    # LEFT$(string, count) and RIGHT$(string, count). The second argument must be a number.
    if name == "LEFT$" or name == "RIGHT$":
        if n != 2 or not need_string(a) or kind_of(args[1]) == 3:
            return (None, "TM")
        # The count, rounded down; it must be 0 to 255.
        c = floor_int(to_float(args[1]))
        if c < 0 or c > 255:
            return (None, "FC")
        if name == "LEFT$":
            return (a[:c], "")
        # Asking for at least the whole string gives the whole string (the slice below would start at a negative position otherwise).
        if c >= len(a):
            return (a, "")
        return (a[len(a) - c:], "")
    # MID$(string, start) or MID$(string, start, length). `start` counts from 1.
    if name == "MID$":
        if (n != 2 and n != 3) or not need_string(a) or kind_of(args[1]) == 3:
            return (None, "TM")
        start = floor_int(to_float(args[1]))
        if start < 1 or start > 255:
            return (None, "FC")
        # With a length: take `c` characters from position `start` (the slice stops at the end of the string if it is shorter).
        if n == 3:
            if kind_of(args[2]) == 3:
                return (None, "TM")
            c = floor_int(to_float(args[2]))
            if c < 0 or c > 255:
                return (None, "FC")
            return (a[start - 1:start - 1 + c], "")
        # Without a length: everything from `start` on (empty if start is past the end).
        return (a[start - 1:], "")
    # ASC(string): the character code of the first character. The empty string is an illegal function call.
    if name == "ASC":
        if n != 1 or not need_string(a):
            return (None, "TM")
        if len(a) == 0:
            return (None, "FC")
        # A character that is not in the table (code above 255) reads as 63, "?".
        return (CODES.get(a[0], 63), "")
    # CHR$(code): the one-character string with that code (0 to 255).
    if name == "CHR$":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        c = floor_int(to_float(a))
        if c < 0 or c > 255:
            return (None, "FC")
        # "%c" turns an integer into the character with that code.
        return ("%c" % c, "")
    # STR$(number) and VAL(string) do the number/text conversion in numbers.star.
    if name == "STR$":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        return (str_text(a), "")
    if name == "VAL":
        if n != 1 or not need_string(a):
            return (None, "TM")
        return (val_text(a), "")
    # INSTR(string, pattern) or INSTR(start, string, pattern). The search is for a position, counted from 1; 0 means not found.
    if name == "INSTR":
        if n != 2 and n != 3:
            return (None, "SN")
        # The default start is the first character. With two arguments the string is args[0] and the pattern args[1].
        start = 1
        s = args[0]
        t = args[1]
        # Three arguments: the first is the start position (a number, 1 to 255), and the string and pattern are the second and third.
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
        # Starting beyond the end of the string finds nothing.
        if start > len(s):
            return (0, "")
        # An empty pattern is found at the start position.
        if t == "":
            return (start, "")
        # find counts from 0 and gives -1 for "not found"; adding 1 turns that into BASIC's 1-based position and 0 for not found.
        return (s.find(t, start - 1) + 1, "")
    # STRING$(count, character): the character repeated count times. The character is given either as a string (its first character is used) or as a
    # character code.
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
    # INKEY$: st["inkey"] is the queue of keys the host has pressed (filled by press_keys in basic.star). Take the oldest one, or give "" if none.
    if name == "INKEY$":
        if len(st["inkey"]) > 0:
            return (st["inkey"].pop(0), "")
        return ("", "")
    # A fixed number: this machine does not track free memory. Any arguments are ignored.
    if name == "MEM" or name == "FRE":
        return (14000, "")
    # ERL: the line number where the last error happened (st["errline"]). Line numbers above 32767 do not fit an integer, hence int_or_single.
    if name == "ERL":
        return int_or_single(st["errline"])
    # ERR: the error code st["errcode"] saved by the error handler in basic.star when an error last occurred.
    if name == "ERR":
        return (st["errcode"], "")
    # POS: the cursor column, 0 to 63 (st["col"]). Any argument is ignored.
    if name == "POS":
        return (st["col"], "")
    # POINT(x, y): x is the column 0 to 127 and y the row 0 to 47 of the 128 x 48 block graphics.
    if name == "POINT":
        if n != 2:
            return (None, "SN")
        # A string for either coordinate is a type mismatch (without this check to_float would fail on it).
        if need_string(args[0]) or need_string(args[1]):
            return (None, "TM")
        x = floor_int(to_float(args[0]))
        y = floor_int(to_float(args[1]))
        if x < 0 or x > 127 or y < 0 or y > 47:
            return (None, "FC")
        # st["screen"] is the list of 1024 character codes (16 rows of 64). One screen character covers a block of 2 columns by 3 rows, so the character
        # is at row y // 3, column x // 2.
        c = st["screen"][(y // 3) * 64 + x // 2]
        # Codes 128 to 191 are block graphics cells: 128 plus one bit per block. The bit for this point is numbered (y % 3) * 2 + x % 2 (row in the cell
        # times 2, plus column in the cell). 1 if that bit is set; a text character, or a clear bit, gives 0.
        if c >= 128 and c <= 191 and (c & (1 << ((y % 3) * 2 + x % 2))) != 0:
            return (1, "")
        return (0, "")
    # PEEK(address). INP is treated the same way, as a read of the same memory. The address is rounded down.
    if name == "PEEK" or name == "INP":
        if n != 1 or kind_of(a) == 3:
            return (None, "TM")
        addr = floor_int(to_float(a))
        # Negative addresses count back from 65536 (-1 is 65535).
        if addr < 0:
            addr += 65536
        # 15360 to 16383 is the video memory, which is the screen list itself; the address minus 15360 is the cell.
        if addr >= 15360 and addr < 16384:
            return (st["screen"][addr - 15360], "")
        # Anywhere else: the value POKE stored in st["mem"], or 0 if that address was never written.
        return (st["mem"].get(addr, 0), "")
    # Everything from here on takes exactly one number.
    if n != 1:
        return (None, "SN")
    if kind_of(a) == 3:
        return (None, "TM")
    # `k` is the kind of the argument (0 to 2 here, the string case is already rejected) and `x` is the argument as a float.
    k = kind_of(a)
    # `double` decides whether results are given as double precision (through real_of) or single precision.
    double = k == 2
    x = to_float(a)
    # ABS: an integer stays an integer unless it was -32768; other kinds keep their kind.
    if name == "ABS":
        if k == 0:
            return int_or_single(abs(a))
        return (negate(a) if x < 0.0 else a, "")
    # SGN: 1, -1 or 0 as an integer.
    if name == "SGN":
        if x > 0.0:
            return (1, "")
        if x < 0.0:
            return (-1, "")
        return (0, "")
    # INT: rounds down to the whole number. An integer is already whole. The result of a double stays a double; otherwise it is an integer if it fits.
    if name == "INT":
        if k == 0:
            return (a, "")
        f = floor_int(x)
        if double:
            return ((float(f),), "")
        return int_or_single(f)
    # FIX: drops the fraction (rounds toward zero, as the host int() does), otherwise like INT.
    if name == "FIX":
        if k == 0:
            return (a, "")
        f = int(x)
        if double:
            return ((float(f),), "")
        return int_or_single(f)
    # CINT: convert to an integer (rounded down), "OV" outside the 16-bit range.
    if name == "CINT":
        f = floor_int(x)
        if f < INT_MIN or f > INT_MAX:
            return (None, "OV")
        return (f, "")
    # CSNG: convert to single precision (real_of with double False). CDBL: the number as a double.
    if name == "CSNG":
        return real_of(x, False)
    if name == "CDBL":
        return ((x,), "")
    # SQR: negative arguments are an illegal function call.
    if name == "SQR":
        if x < 0.0:
            return (None, "FC")
        return real_of(f_sqr(x), double)
    # EXP: f_exp gives None when the result would be too large.
    if name == "EXP":
        r = f_exp(x)
        if r == None:
            return (None, "OV")
        return real_of(r, double)
    # LOG: zero and negative arguments are an illegal function call.
    if name == "LOG":
        if x <= 0.0:
            return (None, "FC")
        return real_of(f_log(x), double)
    if name == "SIN":
        return real_of(f_sin(x), double)
    if name == "COS":
        return real_of(f_cos(x), double)
    # TAN is sin / cos; a zero cosine would divide by zero, and is reported as an overflow.
    if name == "TAN":
        c = f_cos(x)
        if c == 0.0:
            return (None, "OV")
        return real_of(f_sin(x) / c, double)
    if name == "ATN":
        return real_of(f_atn(x), double)
    # RND(x). A negative x first reseeds the generator from x. Every call then advances the seed (st["seed"], a 31-bit number) with rnd_next.
    if name == "RND":
        if x < 0.0:
            st["seed"] = (floor_int(-x) * 7919 + 13) % 2147483648
        st["seed"] = rnd_next(st["seed"])
        # The upper 24 bits of the 31-bit seed (seed // 128), divided by 2 to the power 24: a fraction from 0 up to, but not including, 1.
        frac = (st["seed"] // 128) / 16777216.0
        # RND(n) for n of 1 or more: a whole number from 1 to the integer part of n.
        if x >= 1.0:
            return int_or_single(int(frac * floor_int(x)) + 1)
        # RND(0) and negative or fractional arguments: the fraction itself, as a single.
        return (to_single(frac), "")
    # A name that none of the cases above handles.
    return (None, "SN")

# ARRAYS
# st["arrays"] maps a variable key (name plus suffix, see var_key) to [dims, flat]. `dims` is a list holding the LARGEST subscript of each
# dimension, so a dimension of 10 has the 11 subscripts 0 to 10. `flat` is one list holding all elements in row-major order (the last subscript
# changes fastest), each starting at the zero value of the array's type. Scalar variables live in a different dict (st["vars"]), so an array and a
# scalar can share a name.
# ---------------------------------------------------------------- arrays

# Create an array. `dims` holds the largest subscript of each dimension, so the element count is the product of (d + 1). "OM" (out of memory)
# if that is more than 20,000 elements. Every element starts at the zero value of the array's type.
def new_array(st, key, dims):
    total = 1
    # Each dimension holds d + 1 elements, because the subscripts run from 0 to d inclusive.
    for d in dims:
        total *= d + 1
    # The limit on the number of elements; more is out of memory.
    if total > 20000:
        return "OM"
    # The array record: [dims, flat list]. The zero value comes from the kind in the key's suffix. (All the elements can share one value, since values are never changed in place.)
    st["arrays"][key] = [dims, [zero_of(key_kind(key))] * total]
    return ""

# Flat position of an element, creating the array (with 10 as every bound) on first use. Returns (array, position, error).
def locate(st, key, subs):
    # None when this array has not been dimensioned yet.
    arr = st["arrays"].get(key)
    # First use without DIM: create it with every dimension's largest subscript 10 (subscripts 0 to 10), as many dimensions as there are subscripts.
    # Eleven to the power of the number of dimensions elements can exceed the limit in new_array (five dimensions do), which gives "OM".
    if arr == None:
        err = new_array(st, key, [10] * len(subs))
        if err != "":
            return (None, 0, err)
        # Read back the record that new_array just stored.
        arr = st["arrays"][key]
    # The subscripts given must be exactly as many as the array has dimensions.
    dims = arr[0]
    if len(dims) != len(subs):
        return (None, 0, "BS")
    # `pos` is built up as the row-major index into the flat list.
    pos = 0
    for j in range(len(dims)):
        s = subs[j]
        # A string subscript is a type mismatch.
        if kind_of(s) == 3:
            return (None, 0, "TM")
        # A numeric subscript is rounded down.
        s = floor_int(to_float(s))
        # Outside 0 to the declared maximum for this dimension is a bad subscript.
        if s < 0 or s > dims[j]:
            return (None, 0, "BS")
        # Row-major: multiply what has been accumulated so far by this dimension's size and add the subscript.
        pos = pos * (dims[j] + 1) + s
    # Returns (record, index, error). The record is [dims, flat], so the element is arr[1][pos] and callers can read or assign it.
    return (arr, pos, "")

# EVALUATING
# eval_rpn is a loop over the items of the RPN list with an explicit value stack. Operands push values; an operator, built-in function or array
# element pops its operands (the right operand is on top) and pushes its result. When the list is used up the stack holds the one result.
#
# A DEF FN call cannot recurse, so it switches the same loop to the function's own RPN list. What is needed to come back is kept in `frames`, a list
# of 4-tuples (code list, position in it, value stack, saved variables) for each suspended caller. The function's parameters are ordinary entries of
# st["vars"] while it runs; the values they hid are saved in the frame and put back (or removed, if the variable did not exist) when the function ends.
# ---------------------------------------------------------------- evaluating

# The most DEF FN calls that may be suspended at once. eval_rpn keeps one frame per suspended caller and refuses a new call ("OM") when it
# already holds this many.
MAX_FN_DEPTH = 30

# Run a compiled expression. Returns (value, error).
def eval_rpn(st, rpn):
    # `stack` is the value stack: the BASIC values (int, float, 1-tuple or str) computed so far and not yet consumed.
    stack = []
    # `frames` is the list of suspended callers; it is empty while the top-level expression is running.
    frames = []
    # `cur` is the RPN list being run now (the expression, or a DEF FN body) and `pc` is the index of the next item in it.
    cur = rpn
    pc = 0
    # A bound, not a limit anyone should reach: Starlark has no `while`, and each turn takes one item.
    for _ in range(100000000):
        # Reached the end of the current code. Either the whole expression is done (no frames left), or a user function has finished: restore the
        # caller's variables, code position and stack, and push the function's value for the caller to use.
        if pc >= len(cur):
            # The value of the finished code is what is left on top of the stack.
            val = stack[-1]
            if len(frames) == 0:
                return (val, "")
            # The frame is (caller's code, caller's position, caller's stack, saved) as built in the FN call at the end of this loop.
            f = frames.pop()
            # Undo the parameter binding. Each pair is (variable key, the value it had before the call, or None if it did not exist).
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
        # Fetch the next item and step on before running it, so the loop continues after it whatever the item does.
        item = cur[pc]
        pc += 1
        # The kind of item: "c", "var", "B", "U", "call", "arr" (all listed at the top of the file); anything else is "ufn".
        tag = item[0]
        # A constant: push its value, which is item[1].
        if tag == "c":
            stack.append(item[1])
        # A scalar: look it up by its full key (name plus suffix). A variable never assigned has its type's zero value; that is pushed but not stored.
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
        # A prefix operator takes only the top of the stack.
        elif tag == "U":
            r = unop(item[1], stack.pop())
            if r[1] != "":
                return (None, r[1])
            stack.append(r[0])
        # A built-in function: take its arguments off the stack, in order, and call it.
        elif tag == "call":
            # `c` is the argument count the compiler recorded for this call.
            c = item[2]
            # The top `c` values, in the order they were written. (Written as len(stack) - c, not -c, because c can be 0 and stack[-0:] would take everything.)
            args = stack[len(stack) - c:]
            # Remove them from the stack.
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
            # item[1] is the (name, suffix) pair; this gives the key of the array.
            key = var_key(st, item[1][0], item[1][1])
            # locate returns (array record, flat index, error).
            loc = locate(st, key, subs)
            if loc[2] != "":
                return (None, loc[2])
            # loc[0] is [dims, flat], so loc[0][1] is the flat list and loc[1] the position.
            stack.append(loc[0][1][loc[1]])
        # The only item left is ("ufn", name, argument count): a call to a user function.
        else:
            c = item[2]
            args = stack[len(stack) - c:]
            stack = stack[:len(stack) - c]
            # Anything else is a call to a DEF FN function.
            defn = st["fns"].get(item[1])
            if defn == None:
                return (None, "UF")
            # defn is (params, body): `params` is the list of parameter variable keys (name plus suffix, as var_key gives), `body` the compiled RPN list.
            params = defn[0]
            # Wrong number of arguments for the DEF FN is a syntax error.
            if len(params) != len(args):
                return (None, "SN")
            # Functions calling functions are limited to 30 deep ("OM"), since each level costs a frame.
            if len(frames) >= MAX_FN_DEPTH:
                return (None, "OM")
            # The values the parameters hide are saved so they can be restored afterwards. None means the variable did not exist.
            saved = []
            # First pass: convert each argument to its parameter's type (a mismatch or overflow ends the call before any variable has been changed) and note
            # what the parameter variable holds now.
            for j in range(len(params)):
                cv = coerce(key_kind(params[j]), args[j])
                if cv[1] != "":
                    return (None, cv[1])
                saved.append((params[j], st["vars"].get(params[j])))
            # Second pass: bind the parameters. Doing it after the first pass means every argument was evaluated and checked before any variable changed.
            for j in range(len(params)):
                st["vars"][params[j]] = coerce(key_kind(params[j]), args[j])[0]
            # Remember where to come back to, then switch to running the function's body.
            frames.append((cur, pc, stack, saved))
            # Run the function body in this same loop: it has its own list, starts at its first item, and has its own empty value stack. The caller's stack
            # (without the arguments) is in the frame.
            cur = defn[1]
            pc = 0
            stack = []
    # The step bound was used up without finishing.
    return (None, "OM")

# The cache st["rpn"] is keyed by the (line, token position) tuple, so lines of any length never share a key, and holds the whole result of
# compile_expr (rpn, next position, error). basic.star replaces it with an empty dict whenever the program text changes.
# Compile (with a cache for program lines) and evaluate the expression at toks[pos]. Returns (value, next position, error).
def eval_at(st, toks, pos):
    # The BASIC line number being run, or 0 in immediate mode (a command typed without a line number).
    line = st["line"]
    cache_key = (line, pos)
    hit = None
    # Only program lines are cached. In immediate mode st["line"] is 0, so the key would depend on the token position alone and could collide between different commands.
    if line > 0:
        hit = st["rpn"].get(cache_key)
    if hit == None:
        # `hit` is (rpn, next position, error). Compile errors are cached too, so a bad expression is reported without compiling it again.
        hit = compile_expr(toks, pos)
        if line > 0:
            st["rpn"][cache_key] = hit
    # The compile failed: no value; the position is where the compiler stopped.
    if hit[2] != "":
        return (None, hit[1], hit[2])
    # The result is (value, next position, error): the value and the error come from evaluation, the position from the compiler.
    r = eval_rpn(st, hit[0])
    return (r[0], hit[1], r[1])

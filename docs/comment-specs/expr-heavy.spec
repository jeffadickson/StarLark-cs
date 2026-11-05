@@ Heavy explanatory comments for apps/Basic/scripts/expr.star. Line numbers (#N) are the line numbers of the file before this spec is applied.
@file apps/Basic/scripts/expr.star

@line load("numbers.star",
How values are represented (kind_of in numbers.star tells them apart):
  int          a BASIC integer (kept within INT_MIN..INT_MAX = -32768..32767 by the code below), kind 0
  float        a single precision number, already rounded by to_single, kind 1
  (x,)         a double precision number: a ONE-item tuple holding a float, so it can be told from a single, kind 2
  str          a BASIC string, kind 3
Whenever this file says "kind" it means one of those four numbers: 0 integer, 1 single, 2 double, 3 string. Most helpers in this file return a
pair (value, error): when the error is "" the value is good, and when the error is a two letter code the value is None (or a dummy 0).
Imported from numbers.star: the integer limits, type tests and conversions (kind_of, to_float, floor_int, negate, to_single, overflows), the text
conversions for STR$ and VAL (str_text, val_text), and the math routines (f_sqr ... f_pow).

@line # The longest string BASIC allows
The error codes this file produces, and when:
  TM  type mismatch: a string where a number is needed, a number where a string is needed, or the wrong kind of argument to a function
  OV  overflow: an integer conversion outside -32768..32767, or a single or double result too large (or not a number)
  SN  syntax error: the compiler found a malformed expression, a function was given the wrong number of arguments, or an operator is unknown
  FC  illegal function call: an argument is out of the range the function accepts (a negative LOG, CHR$(300), a POINT outside the screen...)
  BS  bad subscript: an array subscript out of range, or the wrong number of subscripts
  /0  division by zero
  LS  string too long (a joined string of more than MAX_STRING characters)
  OM  out of memory: an array bigger than the limit, DEF FN calls nested too deeply, or the evaluator's step bound used up
  UF  undefined user function: no DEF FN of that name has been stored
A function that fails returns the code; "" means success.

@line # Operator precedence for the shunting-yard compiler
Levels, from loosest to tightest binding:
  1 OR    2 AND    3 NOT    4 comparisons (= <> < > <= >=)    5 + -    6 * /    7 unary minus    8 ^
Two consequences of how compile_expr uses the table: every binary operator is LEFT associative (an operator of EQUAL precedence already on the
stack is popped first), including ^, so 2^3^2 is (2^3)^2; and NOT sits below the comparisons, so NOT A=B means NOT (A=B). The keys "NOT" and "u-"
only ever name the prefix (unary) operators; "AND" and "OR" are the keyword binary operators.

@line for _w in [
Fill the set from this list. Only membership matters, so every value is True. (The leading underscore marks _w as a throwaway name.)

@line MAX_FN_DEPTH = 30
The most DEF FN calls that may be suspended at once. eval_rpn keeps one frame per suspended caller and refuses a new call ("OM") when it
already holds this many.

@line # Convert a value to what a variable of this kind holds.
How a value is coerced when it is stored (variables, arrays and DEF FN parameters all use this):
  kind 3 (string)   accepts only a string; a number is "TM"
  kind 0, 1, 2      accept any number; a string is "TM"; a value of the same kind is kept as it is
  to integer        the number is rounded down (floor_int); outside -32768..32767 is "OV"
  to single         a value too big for single precision is "OV"; otherwise it is rounded with to_single
  to double         the float is wrapped in a one-item tuple, which cannot fail

@line #53 def var_key(st, name, suffix):
`name` is the two character variable name from the lexer, `suffix` is "", "%", "!", "#" or "$". The result is the key used in st["vars"] (scalars)
and st["arrays"] (arrays), so A%, A!, A# and A$ are four different variables, and none of them is the same as the array of that name.

@line #54 if suffix == "":
No suffix was typed. st["deftype"] maps a first letter to a kind number (the DEFINT / DEFSNG / DEFDBL / DEFSTR setting), and KIND_SUFFIX turns
that number back into the suffix character.

@line #56 return name + suffix
A suffix that was typed always wins over the DEF setting.

@line #62 if kind == 1:
Kind 1 is single precision, which is a plain float.

@line #64 if kind == 2:
Kind 2 is double precision: a one-item tuple, to tell it apart from a single.

@line #66 return ""
Kind 3 (string): the empty string.

@line #70 return SUFFIX_KIND[key[-1]]
var_key always puts the type suffix last, so the final character of any key says what kind of variable it is.

@line #74 vk = kind_of(v)
`vk` is the kind of the value being stored; `kind` is the kind of the variable it is being stored into.

@line #75 if kind == 3:
A string variable accepts only strings; anything else is a type mismatch.

@line #79 if vk == 3:
Here the variable is numeric, so a string value cannot be stored in it.

@line #81 if kind == vk:
Same kind on both sides: nothing to convert.

@line #83 f = to_float(v)
Different numeric kinds: go through a plain float (a double is unwrapped from its tuple, an integer is widened) and then narrow or widen to the target.

@line #85 n = floor_int(f)
Storing into an integer variable rounds the number down, then it must fit in 16 bits.

@line #89 if kind == 1:
Target is single precision. A double or an integer arrives here as the float `f`.

@line #93 return ((f,), "")
The only target left is double precision (kind 2): wrap the float in a one-item tuple.

@line #95 # ----------------
THE COMPILER
compile_expr turns a list of tokens into reverse Polish notation (RPN): a flat list in which every operator comes after its operands, so the
evaluator needs no precedence rules and no brackets. For example A+B*2 compiles to
    ("var","A",""), ("var","B",""), ("c",2), ("B","*"), ("B","+")
and X(I+1)*2 compiles to
    ("var","I",""), ("c",1), ("B","+"), ("arr",("X",""),1), ("c",2), ("B","*")
It is the shunting-yard algorithm: operands go straight to the output list; operators wait on a stack until something of lower or equal
precedence shows up (see PREC). The compiler is a single loop over the tokens that remembers one bit of state, `expect`: whether an operand is
due next or an operator is. That bit is how unary minus is told from binary minus (a "-" where an operand is due is unary), and it is how
malformed input such as "3 +" or "()" is caught. Open brackets of every kind are markers on the operator stack, and a counter of the arguments
seen so far is kept for each marker that opens a function call, an array subscript list or an FN call.

@line def compile_expr(toks, pos):
Token shapes come from lex.star: ("n", number), ("s", text), ("v", name, suffix), ("k", KEYWORD), ("o", operator or punctuation).
Entries on the operator stack `ops` come in three shapes:
  ("B", op)                  a binary operator waiting for its right operand ("+", "<=", "AND"...)
  ("U", op)                  a prefix operator, "u-" (unary minus) or "NOT"
  ("M", what, payload)       a marker for an open bracket. what is "paren" (plain grouping, payload ""), "arr" (payload (name, suffix)),
                             "call" (payload the built-in's name) or "ufn" (payload the DEF FN name).
The ("B", ...) and ("U", ...) entries are exactly the RPN items the evaluator wants, so they are copied to the output unchanged. A marker is never
copied: the closing bracket discards it and emits the finished ("arr"/"call"/"ufn") item instead.
Returns the 3-tuple (rpn, next position, error). On success, "next position" is the index of the first token that is not part of the expression;
on an error the list is empty and the position is the token where the problem was seen.

@line #101 out = []
Shapes: `out` holds finished RPN items (listed at the top of the file); `ops` holds the entries described above; `argc` is parallel to the
call/array/FN markers on `ops` only (not to plain parentheses): its last item is the argument count of the innermost such open bracket.

@line #109 i = pos
`i` is the index of the next token to look at.

@line #110 depth = 0
`depth` counts the brackets (of every kind) that are open right now. It tells ")" and "," whether they belong to this expression: with depth 0
they belong to whatever contains the expression (a statement), so the expression simply ends there.

@line #111 for _ in range(n + 2):
Each turn either consumes at least one token or leaves the loop, so n + 2 turns is more than enough.

@line #112 if i >= n:
Out of tokens: stop. The check after the loop decides whether the expression was complete.

@line #115 kind = t[0]
The token type letter: "n" number, "s" string, "v" variable, "k" keyword, "o" operator.

@line #120 out.append(("c", t[1]))
t[1] is the value itself: an int, a float, a one-item tuple (double) or a string. After an operand an operator must follow, so `expect` goes False.

@line #129 ops.append(("M", "arr", (t[1], t[2])))
Variable followed by "(": an array element. The marker keeps the name and suffix; it is replaced by an ("arr", (name, suffix), count) item
when the matching ")" is reached. `expect` stays True because the first subscript must follow.

@line #130 argc.append(1)
Start the argument counter at 1: the first argument is the one about to be read. (An empty list such as "F()" is rejected at the ")" because
`expect` is still True there.)

@line #132 i += 2
Skip both the name and the "(" token.

@line #134 out.append(("var", t[1], t[2]))
A scalar variable: the name and suffix are kept as typed; eval_rpn applies var_key when it runs, because an untyped name's type comes from the
DEFINT/DEFSTR table.

@line #139 w = t[1]
The keyword text, such as "SQR", "FN", "AND".

@line #140 if w in FUNCS:
A built-in function. It is an operand, so it is only legal where an operand is expected; otherwise the expression has ended.

@line #144 ops.append(("M", "call", w))
Function followed by "(": push a marker (payload: the function name). The ")" will emit ("call", name, argument count).

@line #148 elif w in ZERO_ARG:
No bracket follows. Only the functions that take no arguments may be written that way; they become a call with 0 arguments at once.
Any other function name without "(" is a syntax error (the next branch).

@line #154 elif w == "FN":
A user function call: FN name or FN name(args). The name token that follows FN is a variable token.

@line #157 if i + 1 >= n or toks[i + 1][0] != "v":
FN must be followed by a name; otherwise it is a syntax error.

@line #159 name = toks[i + 1][1] + toks[i + 1][2]
The function's name: the two-character name plus its suffix. DEF FN stores the function under the same string (see st_def in basic.star).

@line #161 ops.append(("M", "ufn", name))
FN name( ... ): a marker, to be turned into ("ufn", name, argument count) at the ")". Skip FN, the name and "(" (three tokens).

@line #166 out.append(("ufn", name, 0))
FN name with no bracket: a call with no arguments. Skip FN and the name (two tokens).

@line #180 for _ in range(len(ops)):
Move operators from the stack to the output while they bind at least as tightly as the new one. The loop can pop at most len(ops) times, which is
the bound Starlark (no `while`) needs.

@line #181 if len(ops) == 0 or ops[-1][0] == "M" or PREC[ops[-1][1]] < p:
Stop when the stack is empty, when the top is a marker (an open bracket is never popped by an operator, so the operators inside a bracket stay
inside it), or when the top binds looser than the new operator. Anything else is popped to the output: equal precedence pops too, which makes the
operators left associative.

@line #184 ops.append(("B", w))
Push the new operator; its right operand is still to come, so an operand is due next.

@line #192 if op == "(":
A "(" that does not follow a function or array name is a grouping bracket. It is an operand start, so it is only allowed where an operand is
expected; after an operand the expression just ends.

@line #195 ops.append(("M", "paren", ""))
The "paren" marker emits nothing when it is closed; it only stops operators from moving out through it.

@line #201 if depth == 0:
No bracket is open, so this ")" belongs to something outside the expression: stop here and leave it for the caller.

@line #203 if expect:
A ")" where an operand is due, as in "(" followed straight away by ")" or "3 + )": a syntax error.

@line #205 for _ in range(len(ops) + 1):
Flush the pending operators to the output, down to the nearest marker. depth is above 0, so a marker is always on the stack and this loop always
reaches it; the bound is only the stack size, plus one for safety.

@line #209 m = ops.pop()
Take the marker off the stack. Its second item says what kind of bracket it was.

@line #211 if m[1] == "arr":
Array, built-in function and FN markers become an RPN item carrying the number of arguments counted (argc.pop() is that bracket's counter). A
plain "paren" marker needs no item: the operators inside it are already in the output.

@line #217 expect = False
A closed bracket ends an operand, so an operator (or another closing bracket) is due next.

@line #223 if expect:
A "," where an operand is due, as in "F(,2)" or "F(1,,2)": a syntax error.

@line #225 for _ in range(len(ops) + 1):
Finish the argument that just ended: flush its pending operators down to the nearest marker (same loop as for ")").

@line #229 if ops[-1][1] == "paren":
The nearest open bracket is a plain grouping bracket, such as "(1,2)". Commas are only allowed in a call, an array or an FN list, so this is a
syntax error.

@line #231 argc[-1] += 1
One more argument seen in the innermost call, array or FN. The marker stays on the stack; the next argument starts, so an operand is due.

@line #236 if op == "-":
This branch is tried before the binary-operator one, so a "-" or "+" that arrives where an operand is due is a sign, and the same character after
an operand falls through to the binary operators below. A minus becomes the prefix item ("U", "u-") that waits on the stack (nothing is popped,
because a prefix operator has no left operand). `expect` stays True: the operand it applies to is still to come.

@line #243 p = PREC[op]
Same pop-while-tighter loop as for AND and OR above: pop while the top is not a marker and binds at least as tightly, then push this operator.

@line #248 ops.append(("B", op))
The binary operator is pushed; an operand must follow it, so `expect` is set True on the next line.

@line #256 if expect or depth != 0:
The tokens ran out (or something ended the expression) while an operand was still due, as in "3 +", or while a bracket was still open.

@line #259 for _ in range(len(ops)):
Every remaining entry is a ("B", ...) or ("U", ...) item here, because depth is 0 and so no marker is left. They go out from the top of the
stack down, which is the right order: the tightest binding operator is on top and is applied first.

@line #263 return (out, i, "")
Success: the RPN list, the index of the first token left over (the expression may be followed by a comma, THEN, a colon...), and no error.

@line #265 # ----------------
OPERATORS
Rules for arithmetic, shared by the evaluator:
  * Any number operand with a string operand is "TM" (both must be strings or both numbers). Two strings may be joined with + (result "LS" when
    longer than 255) or compared; every other operator on strings is "TM".
  * AND, OR and NOT first turn their operands into 16-bit integers (to_int16: rounded down, "OV" if outside -32768..32767).
  * Comparisons give -1 for true and 0 for false.
  * When both operands are integers, + - * give an integer if the result still fits in 16 bits, otherwise a single. "/" and "^" always work in
    floating point.
  * In all other cases the result kind is the wider of the two operands (single beats integer, double beats both). The arithmetic itself is done in
    ordinary floats and then checked and rounded by real_result: "OV" on overflow, a one-item tuple for double, to_single for single.
  * Division by zero is "/0".

@line #270 if kind_of(v) == 3:
A string is not a number.

@line #272 n = floor_int(to_float(v))
Round down, whatever the kind (a double is unwrapped by to_float first).

@line #280 if double:
A double result is checked against the largest finite double, and "r != r" is true only for NaN (not a number). Either is an overflow.

@line #284 if overflows(r):
Single precision: too big (or NaN) is an overflow; anything else is rounded to the nearest single.

@line #291 r = a == b
The six comparisons just use the host comparison on the two values. The caller has already put the operands in a comparable form (both
strings, both ints, or both floats).

@line #302 if r:
BASIC's true is -1 (all bits set), false is 0.

@line #310 ka = kind_of(a)
Kinds of the left and right operand (0 integer, 1 single, 2 double, 3 string).

@line #312 if ka == 3 or kb == 3:
At least one operand is a string.

@line #313 if ka != kb:
One string and one number: always a type mismatch.

@line #315 if op == "+":
Two strings: + joins them.

@line #317 if len(r) > MAX_STRING:
The joined string is longer than 255 characters.

@line #320 if op in ["=", "<>", "<", ">", "<=", ">="]:
Two strings can be compared. The comparison uses the host's string ordering and returns (-1 or 0, "") from compare.

@line #322 return (None, "TM")
Any other operator on two strings (- * / ^ AND OR) is a type mismatch.

@line #323 if op == "AND" or op == "OR":
Both operands are numbers now. AND and OR are bitwise operations on 16-bit integers; the left operand's error is reported before the right's.

@line #331 return (x[0] & y[0], "")
Bitwise AND / OR of two values in -32768..32767 stays in that range, so no overflow check is needed. -1 (true) AND x gives x, which is why true is -1.

@line #333 k = max(ka, kb)
The wider kind of the two numbers: 0 both integers, 1 at least one single, 2 at least one double.

@line #334 if op in ["=", "<>", "<", ">", "<=", ">="]:
Numeric comparison: two integers are compared as they are; any other mix is compared as floats (to_float unwraps doubles).

@line #336 if k == 0 and (op == "+" or op == "-" or op == "*"):
Integer arithmetic: both operands are integers.

@line #343 if r >= INT_MIN and r <= INT_MAX:
The result still fits in 16 bits, so it stays an integer.

@line #345 return real_result(float(r), False)
Too big for an integer: the result becomes a single precision number instead of an error.

@line #346 fa = to_float(a)
Everything left is floating point arithmetic: both operands as floats, with the result kind decided by `double`.

@line #348 double = k == 2
The result is a double only when at least one operand was a double; integer and single operands give a single result.

@line #355 if op == "/":
Division is always done in floating point, even for two integers.

@line #356 if fb == 0.0:
Division by zero is "/0" (checked before dividing).

@line #359 if op == "^":
Exponentiation is done by f_pow from numbers.star, which returns (value, error); its errors ("FC", "OV", "/0") are passed on unchanged.

@line #364 return (None, "SN")
An operator this function does not know (none of the cases above matched).

@line #368 if kind_of(a) == 3:
Both unary operators need a number.

@line #370 if op == "u-":
Unary minus: negate keeps the kind of its operand (and turns the one integer that cannot be negated, -32768, into a single).

@line #372 x = to_int16(a)
The only other prefix operator is NOT, which works on a 16-bit integer.

@line #375 return (-x[0] - 1, "")
NOT is the bitwise complement: -x - 1 (NOT 0 is -1, NOT -1 is 0).

@line #377 # ----------------
BUILT-IN FUNCTIONS
call_function receives the function's name and the list of already evaluated arguments, and returns (value, error). It is one long chain of
tests on the name: first the string functions, then the functions that read the machine (INKEY$, MEM, ERL, ERR, POS, POINT, PEEK), and last
the group of mathematical functions that all take exactly one number. Each case checks the argument count and kinds itself. In the string
functions the count and the kinds are tested together, so a wrong count is reported as "TM" there; POINT, INSTR and the math group report a wrong count as "SN".

@line #384 def int_or_single(n):
`n` is a whole number (an int) of any size, possibly outside the 16-bit range.

@line #396 a = args[0] if n > 0 else None
`a` is the first argument, for convenience (None when there are no arguments; every use is guarded by a count test first).

@line #397 if name == "LEN":
LEN(string): the number of characters.

@line #401 if name == "LEFT$" or name == "RIGHT$":
LEFT$(string, count) and RIGHT$(string, count). The second argument must be a number.

@line #404 c = floor_int(to_float(args[1]))
The count, rounded down; it must be 0 to 255.

@line #409 if c >= len(a):
Asking for at least the whole string gives the whole string (the slice below would start at a negative position otherwise).

@line #412 if name == "MID$":
MID$(string, start) or MID$(string, start, length). `start` counts from 1.

@line #418 if n == 3:
With a length: take `c` characters from position `start` (the slice stops at the end of the string if it is shorter).

@line #425 return (a[start - 1:], "")
Without a length: everything from `start` on (empty if start is past the end).

@line #426 if name == "ASC":
ASC(string): the character code of the first character. The empty string is an illegal function call.

@line #431 return (CODES.get(a[0], 63), "")
A character that is not in the table (code above 255) reads as 63, "?".

@line #432 if name == "CHR$":
CHR$(code): the one-character string with that code (0 to 255).

@line #438 return ("%c" % c, "")
"%c" turns an integer into the character with that code.

@line #439 if name == "STR$":
STR$(number) and VAL(string) do the number/text conversion in numbers.star.

@line #447 if name == "INSTR":
INSTR(string, pattern) or INSTR(start, string, pattern). The search is for a position, counted from 1; 0 means not found.

@line #450 start = 1
The default start is the first character. With two arguments the string is args[0] and the pattern args[1].

@line #453 if n == 3:
Three arguments: the first is the start position (a number, 1 to 255), and the string and pattern are the second and third.

@line #463 if start > len(s):
Starting beyond the end of the string finds nothing.

@line #465 if t == "":
An empty pattern is found at the start position.

@line #467 return (s.find(t, start - 1) + 1, "")
find counts from 0 and gives -1 for "not found"; adding 1 turns that into BASIC's 1-based position and 0 for not found.

@line #468 if name == "STRING$":
STRING$(count, character): the character repeated count times. The character is given either as a string (its first character is used) or as a
character code.

@line #483 if name == "INKEY$":
INKEY$: st["inkey"] is the queue of keys the host has pressed (filled by press_keys in basic.star). Take the oldest one, or give "" if none.

@line #487 if name == "MEM" or name == "FRE":
A fixed number: this machine does not track free memory. Any arguments are ignored.

@line #489 if name == "ERL":
ERL: the line number where the last error happened (st["errline"]). Line numbers above 32767 do not fit an integer, hence int_or_single.

@line #491 if name == "ERR":
ERR: the error code st["errcode"] saved by the error handler in basic.star when an error last occurred.

@line #493 if name == "POS":
POS: the cursor column, 0 to 63 (st["col"]). Any argument is ignored.

@line #495 if name == "POINT":
POINT(x, y): x is the column 0 to 127 and y the row 0 to 47 of the 128 x 48 block graphics.

@line #502 c = st["screen"][(y // 3) * 64 + x // 2]
st["screen"] is the list of 1024 character codes (16 rows of 64). One screen character covers a block of 2 columns by 3 rows, so the character
is at row y // 3, column x // 2.

@line #503 if c >= 128 and c <= 191
Codes 128 to 191 are block graphics cells: 128 plus one bit per block. The bit for this point is numbered (y % 3) * 2 + x % 2 (row in the cell
times 2, plus column in the cell). 1 if that bit is set; a text character, or a clear bit, gives 0.

@line #506 if name == "PEEK" or name == "INP":
PEEK(address). INP is treated the same way, as a read of the same memory. The address is rounded down.

@line #510 if addr < 0:
Negative addresses count back from 65536 (-1 is 65535).

@line #512 if addr >= 15360 and addr < 16384:
15360 to 16383 is the video memory, which is the screen list itself; the address minus 15360 is the cell.

@line #514 return (st["mem"].get(addr, 0), "")
Anywhere else: the value POKE stored in st["mem"], or 0 if that address was never written.

@line #515 if n != 1:
Everything from here on takes exactly one number.

@line #519 k = kind_of(a)
`k` is the kind of the argument (0 to 2 here, the string case is already rejected) and `x` is the argument as a float.

@line #520 double = k == 2
`double` decides whether results are given as double precision (through real_of) or single precision.

@line #522 if name == "ABS":
ABS: an integer stays an integer unless it was -32768; other kinds keep their kind.

@line #526 if name == "SGN":
SGN: 1, -1 or 0 as an integer.

@line #532 if name == "INT":
INT: rounds down to the whole number. An integer is already whole. The result of a double stays a double; otherwise it is an integer if it fits.

@line #539 if name == "FIX":
FIX: drops the fraction (rounds toward zero, as the host int() does), otherwise like INT.

@line #546 if name == "CINT":
CINT: convert to an integer (rounded down), "OV" outside the 16-bit range.

@line #551 if name == "CSNG":
CSNG: convert to single precision (real_of with double False). CDBL: the number as a double.

@line #555 if name == "SQR":
SQR: negative arguments are an illegal function call.

@line #559 if name == "EXP":
EXP: f_exp gives None when the result would be too large.

@line #564 if name == "LOG":
LOG: zero and negative arguments are an illegal function call.

@line #572 if name == "TAN":
TAN is sin / cos; a zero cosine would divide by zero, and is reported as an overflow.

@line #579 if name == "RND":
RND(x). A negative x first reseeds the generator from x. Every call then advances the seed (st["seed"], a 31-bit number) with rnd_next.

@line #583 frac = (st["seed"] // 128) / 16777216.0
The upper 24 bits of the 31-bit seed (seed // 128), divided by 2 to the power 24: a fraction from 0 up to, but not including, 1.

@line #584 if x >= 1.0:
RND(n) for n of 1 or more: a whole number from 1 to the integer part of n.

@line #586 return (to_single(frac), "")
RND(0) and negative or fractional arguments: the fraction itself, as a single.

@line #587 return (None, "SN")
A name that none of the cases above handles.

@line #589 # ----------------
ARRAYS
st["arrays"] maps a variable key (name plus suffix, see var_key) to [dims, flat]. `dims` is a list holding the LARGEST subscript of each
dimension, so a dimension of 10 has the 11 subscripts 0 to 10. `flat` is one list holding all elements in row-major order (the last subscript
changes fastest), each starting at the zero value of the array's type. Scalar variables live in a different dict (st["vars"]), so an array and a
scalar can share a name.

@line #595 for d in dims:
Each dimension holds d + 1 elements, because the subscripts run from 0 to d inclusive.

@line #597 if total > 20000:
The limit on the number of elements; more is out of memory.

@line #599 st["arrays"][key] = [dims, [zero_of(key_kind(key))] * total]
The array record: [dims, flat list]. The zero value comes from the kind in the key's suffix. (All the elements can share one value, since values are never changed in place.)

@line #604 arr = st["arrays"].get(key)
None when this array has not been dimensioned yet.

@line #605 if arr == None:
First use without DIM: create it with every dimension's largest subscript 10 (subscripts 0 to 10), as many dimensions as there are subscripts.
Eleven to the power of the number of dimensions elements can exceed the limit in new_array (five dimensions do), which gives "OM".

@line #609 arr = st["arrays"][key]
Read back the record that new_array just stored.

@line #610 dims = arr[0]
The subscripts given must be exactly as many as the array has dimensions.

@line #613 pos = 0
`pos` is built up as the row-major index into the flat list.

@line #616 if kind_of(s) == 3:
A string subscript is a type mismatch.

@line #618 s = floor_int(to_float(s))
A numeric subscript is rounded down.

@line #619 if s < 0 or s > dims[j]:
Outside 0 to the declared maximum for this dimension is a bad subscript.

@line #621 pos = pos * (dims[j] + 1) + s
Row-major: multiply what has been accumulated so far by this dimension's size and add the subscript.

@line #622 return (arr, pos, "")
Returns (record, index, error). The record is [dims, flat], so the element is arr[1][pos] and callers can read or assign it.

@line #624 # ----------------
EVALUATING
eval_rpn is a loop over the items of the RPN list with an explicit value stack. Operands push values; an operator, built-in function or array
element pops its operands (the right operand is on top) and pushes its result. When the list is used up the stack holds the one result.

A DEF FN call cannot recurse, so it switches the same loop to the function's own RPN list. What is needed to come back is kept in `frames`, a list
of 4-tuples (code list, position in it, value stack, saved variables) for each suspended caller. The function's parameters are ordinary entries of
st["vars"] while it runs; the values they hid are saved in the frame and put back (or removed, if the variable did not exist) when the function ends.

@line #630 stack = []
`stack` is the value stack: the BASIC values (int, float, 1-tuple or str) computed so far and not yet consumed.

@line #631 frames = []
`frames` is the list of suspended callers; it is empty while the top-level expression is running.

@line #632 cur = rpn
`cur` is the RPN list being run now (the expression, or a DEF FN body) and `pc` is the index of the next item in it.

@line #639 val = stack[-1]
The value of the finished code is what is left on top of the stack.

@line #642 f = frames.pop()
The frame is (caller's code, caller's position, caller's stack, saved) as built in the FN call at the end of this loop.

@line #643 for pair in f[3]:
Undo the parameter binding. Each pair is (variable key, the value it had before the call, or None if it did not exist).

@line #653 item = cur[pc]
Fetch the next item and step on before running it, so the loop continues after it whatever the item does.

@line #655 tag = item[0]
The kind of item: "c", "var", "B", "U", "call", "arr" (all listed at the top of the file); anything else is "ufn".

@line #656 if tag == "c":
A constant: push its value, which is item[1].

@line #658 elif tag == "var":
A scalar: look it up by its full key (name plus suffix). A variable never assigned has its type's zero value; that is pushed but not stored.

@line #672 elif tag == "U":
A prefix operator takes only the top of the stack.

@line #679 c = item[2]
`c` is the argument count the compiler recorded for this call.

@line #680 args = stack[len(stack) - c:]
The top `c` values, in the order they were written. (Written as len(stack) - c, not -c, because c can be 0 and stack[-0:] would take everything.)

@line #681 stack = stack[:len(stack) - c]
Remove them from the stack.

@line #691 key = var_key(st, item[1][0], item[1][1])
item[1] is the (name, suffix) pair; this gives the key of the array.

@line #692 loc = locate(st, key, subs)
locate returns (array record, flat index, error).

@line #695 stack.append(loc[0][1][loc[1]])
loc[0] is [dims, flat], so loc[0][1] is the flat list and loc[1] the position.

@line #696 else:
The only item left is ("ufn", name, argument count): a call to a user function.

@line #704 params = defn[0]
defn is (params, body): `params` is the list of parameter variable keys (name plus suffix, as var_key gives), `body` the compiled RPN list.

@line #705 if len(params) != len(args):
Wrong number of arguments for the DEF FN is a syntax error.

@line #712 for j in range(len(params)):
First pass: convert each argument to its parameter's type (a mismatch or overflow ends the call before any variable has been changed) and note
what the parameter variable holds now.

@line #717 for j in range(len(params)):
Second pass: bind the parameters. Doing it after the first pass means every argument was evaluated and checked before any variable changed.

@line #721 cur = defn[1]
Run the function body in this same loop: it has its own list, starts at its first item, and has its own empty value stack. The caller's stack
(without the arguments) is in the frame.

@line #724 return (None, "OM")
The step bound was used up without finishing.

@line #726 # Compile (with a cache for program lines)
The cache st["rpn"] is keyed by line * 4096 + token position (this assumes positions stay under 4096), and holds the whole result of
compile_expr (rpn, next position, error). basic.star replaces it with an empty dict whenever the program text changes.

@line #728 line = st["line"]
The BASIC line number being run, or 0 in immediate mode (a command typed without a line number).

@line #731 if line > 0:
Only program lines are cached. In immediate mode st["line"] is 0, so the key would depend on the token position alone and could collide between different commands.

@line #734 hit = compile_expr(toks, pos)
`hit` is (rpn, next position, error). Compile errors are cached too, so a bad expression is reported without compiling it again.

@line #737 if hit[2] != "":
The compile failed: no value; the position is where the compiler stopped.

@line #739 r = eval_rpn(st, hit[0])
The result is (value, next position, error): the value and the error come from evaluation, the position from the compiler.

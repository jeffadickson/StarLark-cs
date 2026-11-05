@@ Comments for the supporting Basic scripts: expr, lex, numbers, screen, editor.

@file apps/Basic/scripts/expr.star
@line CODES = {
Character -> code (0 to 255), for ASC. Characters outside this table read as 63, "?", since Level II had no such character.

@line SUFFIX_KIND = {
The type suffix of a variable name -> its kind: 0 integer (%), 1 single precision (!), 2 double precision (#), 3 string ($).

@line KIND_SUFFIX = [
The reverse: kind number -> suffix character. Used to give an untyped name its default type.

@line FUNCS = {}
Names that are built-in functions. A set, so the compiler can ask "is this word a function?" in one lookup.

@line ZERO_ARG = {
Functions written without parentheses (INKEY$, MEM, ERL, ERR).

@line PREC = {
Operator precedence for the shunting-yard compiler: a higher number binds tighter. "u-" is unary minus. As on Level II, ^ binds tighter than
unary minus, so -2^2 is -4.

@line BINARY = {
The symbolic binary operators. AND and OR are keywords and are handled separately.

@line MAX_STRING = 255
The longest string BASIC allows; a longer result is the "LS" (string too long) error.

@def var_key
The dictionary key for a variable: its two-character name plus a type suffix. An untyped name takes its type from the DEFINT/DEFSTR table
for its first letter, so with the default (single precision) A is stored as "A!".

@def zero_of
The value a variable of this kind has before it is assigned: 0, 0.0, a double 0.0, or the empty string.

@def key_kind
The kind (0 to 3) of a variable key, from its suffix.

@def to_int16
A number as a 16-bit integer, rounding down. Returns (value, error): "TM" for a string, "OV" outside -32768 to 32767. Used by AND, OR and NOT,
which work on 16-bit integers.

@def real_result
Check a floating-point result and give it the right type: a double stays a double, anything else is rounded to single precision. Returns
(value, error); a result that overflows (or is not a number) is the "OV" error.

@def compare
Apply a comparison operator. BASIC's true is -1 and false is 0, so the result is -1 or 0.

@def binop
Apply a binary operator to two values. Returns (value, error). Strings may be joined with + and compared; AND/OR work on 16-bit integers;
integer + - * stay integers while they fit and become single precision when they overflow; mixing types widens to the larger one; division
by zero is "/0".

@def unop
Apply a unary operator: negation, or NOT, which is the 16-bit complement (-x - 1). Returns (value, error).

@def real_of
Give a math function's result the right type (single, or double if the argument was a double). Same as real_result.

@def int_or_single
A whole number as BASIC's integer type if it fits in 16 bits, otherwise as a single-precision number.

@def need_string
True if `v` is a string (kind 3). Used to check function arguments.

@def new_array
Create an array. `dims` holds the largest subscript of each dimension, so the element count is the product of (d + 1). "OM" (out of memory)
if that is more than 20,000 elements. Every element starts at the zero value of the array's type.

@file apps/Basic/scripts/lex.star
@def _index_keywords
Group the keywords by first letter, longest first, so keyword_at tries STRING$ before STR and INT before IN.

@def keyword_at
The keyword that starts at position i of the (upper-case) text, or "". Keywords are found anywhere, even in the middle of a name, as on Level II.

@def _is_digit
True for the characters "0" to "9".

@def _is_letter
True for the upper-case letters "A" to "Z". The text is upper-cased before it is scanned.

@def _hex_value
The value of a hexadecimal digit (0-9, A-F), or -1.

@def ord_digit
The value of a decimal digit character, or -1.

@file apps/Basic/scripts/numbers.star
@def _make_pow2
Build the table of exact powers of two from 2**-170 to 2**170, indexed by exponent. Multiplying or halving by two is exact in floating point,
so every entry is exact.

@def to_float
Any BASIC number as a float. A double is stored as a one-item tuple (so it can be told from a single), and this unwraps it.

@def negate
The negative of a number, keeping its type. -32768 has no 16-bit negative, so it becomes the single-precision 32768.0.

@def overflows
True if a value is too big for single precision, or is not a number.

@def _is_negative
True if a number is below zero, for any of the three numeric types.

@def span_digits
The index just after the run of decimal digits that starts at s[i] (i itself if there are none).

@def f_sin
Sine by reduction and series: bring x into -pi/2..pi/2 using the periodicity and symmetry of sine, then sum the Taylor series. Good to the six
or seven digits Level II printed. (The separate package std/approx_math.star is a more accurate, general version.)

@def f_cos
Cosine, as the sine of x plus a quarter turn.

@def f_atn
Arc tangent: fold x into 0..1 using atan(-x) = -atan(x) and atan(1/x) = pi/2 - atan(x), halve the angle twice to make the series converge
fast, sum the series, and undo the folding.

@file apps/Basic/scripts/screen.star
@line CODES = {
Character -> screen code, for printing a character into video memory. Characters outside 0 to 255 are stored as 63, "?".

@def _make_sextants
Build the table that shows the 64 possible 2 x 3 block-graphics cells as Unicode "sextant" characters (and the half and full blocks, which
Unicode keeps elsewhere). Index is the six-bit pattern, one bit per block.

@def new_screen
A blank screen: 1,024 cells (16 rows of 64) all holding the space character (32).

@def _scroll
Move every row up one and blank the bottom row; the cursor goes to the start of the bottom row.

@def newline
Move the cursor to the start of the next row, scrolling if that was the last one.

@def clear_screen
CLS: blank the screen, home the cursor, and put a form feed in the output stream so a terminal host can clear too.

@def set_cursor
Move the cursor to the cell `pos` (0 to 1023), as PRINT @ does.

@def _cell_and_bit
Which character cell holds the block-graphics dot (x, y), and which of the cell's six bits it is. A cell is 2 dots wide and 3 high.

@def set_pixel
Turn the dot (x, y) on or off. A cell that held text becomes a graphics cell (codes 128 to 191); one whose dots are all off stays a blank
graphics cell (128), not the space character.

@def point_on
True if the dot (x, y) is on: the cell must be a graphics cell and the dot's bit set. This is what POINT reads.

@def _glyph
The character to show for a screen code: text for 32 to 127, a sextant for 128 to 191, a space for anything else.

@def _count
How many times the character `ch` repeats in s starting at i. Used to measure runs of # in a PRINT USING template.

@def _with_commas
Put a comma between every group of three digits, from the right: "1234567" becomes "1,234,567".

@file apps/Basic/scripts/editor.star
@def edit_head
The text shown at the start of an edit and after L or A: the line number and a space.

@def edit_text
The line as it is now, as a string.

@def _printable
True for a one-character key that can go into the line (not a control key or DEL).

@def _is_backspace
True for the keys that mean backspace: BS (8) and DEL (127).

@def edit_step
Feed the editor one key. Returns (text to show, action) where action is "" (carry on), "save" or "quit". Enter always saves. Otherwise the key goes
to the handler for the current mode: inserting, changing, waiting for the character of S or K, or an ordinary command.

@def _insert_key
A key while inserting. Escape ends the insert. Backspace takes back the last typed character but never goes before where the insert began.
Any printable key is put in at the cursor, up to the 255-character limit.

@def _change_key
A key in change mode (the C command): each printable key replaces the character under the cursor, until the counted number have been
changed or Escape is pressed.

@def _argument_key
The character that S (search) or K (kill) was waiting for. Finds the nth following occurrence, counting from the character after the cursor.
S moves the cursor to just before it and shows what it passes (to the end of the line if there is none); K deletes up to just before it
(and does nothing if there is none).

@def _command_key
A key in command mode: a digit builds up a repeat count, then Space, Backspace, L, D, C, I, X, H, S, K, Q, A, E act as described at the top of this
file. Any other key does nothing.

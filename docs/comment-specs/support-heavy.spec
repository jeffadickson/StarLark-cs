@@ Heavy comments for numbers.star, screen.star and editor.star, applied by `CommentCheck insert` (lex.star is in lex-heavy.spec).

@file apps/Basic/scripts/numbers.star

@line INT_MIN = -32768
The range of a 16-bit signed integer, the type of a variable with the % suffix and of small whole-number constants.

@line SNG_LIMIT = 1.7014118346046923e38
2 to the power 127. Real single precision reaches nearly 2 to the power 128, but Level II reported ?OV from this value on, and so does this code.

@line LN2 = 0.6931471805599453
Natural logarithm of 2. Used to split e**x and ln(x) into a power of two and a small remainder.

@line PI = 3.141592653589793
Constants for the sine and arc tangent code: pi, half of it (90 degrees) and twice it (a full turn).

@def _make_pow2
Build a dict from an integer exponent k to the float 2**k, for k from -170 to 170. Doubling or halving a float is exact, so the table is exact. Starlark
has no way to read the exponent bits of a float or to compute 2**k exactly for negative k, so everything that needs a power of two uses this table.

@line POW2 = _make_pow2()
Built once at load.

@def kind_of
Which of the four BASIC types a value is: 0 integer, 1 single, 2 double, 3 string. The three number types have three different Starlark types
(int, float, 1-tuple of float), so type() is enough. Anything that is not an int, float or tuple is treated as a string.

@def to_float
Any BASIC number as a plain float, for calculation. An integer is converted and a double is unwrapped from its tuple.

@def negate
The negative of a number of any type, keeping its type. The one special case is the integer -32768: its negative, 32768, does not fit in 16 bits, so the
result becomes the single-precision float 32768.0 (which is what Level II did).

@def binary_exponent
Find e such that 2**e <= a < 2**(e+1) for 2**-170 <= a < 2**170 by binary search over the POW2 table. Ten halvings cover the 341 possible exponents.
This is the stand-in for frexp, which Starlark does not have.

@def to_single
Round a float to the nearest 24-bit-mantissa value, which is what single precision stores. Scale a so that its 24 significant bits become the integer
part, round that to the nearest integer (ties to even), then scale back. Anything below 2**-127 becomes 0, and anything at or above the single limit
is clamped to it (the caller has already decided whether that is an overflow error).

@line scale = POW2[23 - e]
a * 2**(23 - e) puts the leading bit at position 23, so the integer part of the result is the 24-bit mantissa.

@line frac = q - n
The part that was cut off. More than a half rounds up; exactly a half rounds to the even neighbour, as the hardware does.

@def overflows
True if x is not a usable single: at or beyond +/- the limit, or NaN. NaN is caught by the first comparison, because this Starlark orders NaN above every
number (so nan >= SNG_LIMIT is true); the `x != x` test is never true here, since nan == nan is true in this Starlark.

@def _sci_digits
Print a positive float in scientific notation with nd significant digits, and return the digits as a string (no dot) with the decimal exponent
of the first digit. Starlark's %e formatting does the correct rounding.

@def format_real
Format a float the way PRINT shows it, without the sign slot and the trailing space. nd is how many significant digits the type has (6 for single, 16
for double). Fixed notation (.5, 123.456, 100) is used when the decimal exponent is from -2 up to nd - 1; otherwise scientific notation d.dddE+xx,
with expc ("E" or "D") as the exponent letter. Trailing zeros are dropped.

@line digits = d[0].rstrip("0")
Trailing zeros are never printed (so 2.50 shows as 2.5 and 100 as 100, with the zeros put back below).

@line if e >= -2 and e < nd:
The fixed-notation range. e is the exponent of the first digit: -2 is 0.0ddd (written .0ddd), and nd - 1 is the last place a number still fits in nd digits.

@line body = "." + "0" * (-e - 1) + digits
A number below 1: leading zeros after the point, no zero before it (Level II printed .5, not 0.5).

@line elif len(digits) <= e + 1:
A whole number: the digits followed by the zeros that were dropped.

@line body += expc + ("-" if e < 0 else "+") + ("%02d" % abs(e))
Scientific notation: E+07, E-05, and so on, always with a sign and at least two digits.

@def number_text
A number as text with no sign slot: integers as they are, singles with 6 digits and an E exponent, doubles with 16 digits and a D exponent.

@def _is_negative
True if a number is below zero, for the three number types (a double has to be unwrapped first).

@def str_text
The text STR$ gives: positive numbers get a leading space where the minus sign would go; negative numbers do not.

@def print_text
The text PRINT shows for a number: the STR$ text plus one trailing space. This is why PRINT 5 shows " 5 ".

@def span_digits
The index just after the run of digits that starts at s[i]; i itself if the first character is not a digit.

@def scan_number
Read an unsigned number from s starting at index i (s is already upper case). Returns (value, next index, kind): kind 0 integer, 1 single, 2 double
(the value is a 1-tuple), -1 means no number was found, -2 means it is too large. It never raises an error, because it is used on text typed by the
user and on INPUT and VAL text.
The type rules: an "!" suffix makes a single, "#" or a "D" exponent or more than 7 significant digits makes a double, "%" makes an integer.
Otherwise a whole number up to 32767 is an integer and anything else is a single.

@line if ndig == 0:
No digits at all (for example a lone "."): there is no number here.

@line sig = len(sig_text.lstrip("0"))
Count only significant digits, ignoring leading zeros, because that is what decides single versus double precision.

@line if j < n and (s[j] == "E" or s[j] == "D"):
An exponent: E for single precision, D for double. It only counts if digits follow (with an optional sign), so "1E" is the number 1 followed by an E.

@line if len(exp_digits) > 6:
A huge exponent is clamped to an arbitrary large value instead of being converted, which keeps the next check simple and prevents very long digit runs.

@line if int_digits + ev > 40:
Too big for any float this interpreter allows: report overflow before calling float(), which would raise an error on huge input.

@line if not (has_exp and ev < -400):
An extremely small exponent underflows to zero, so skip float() (value stays 0.0).

@line if value >= SNG_LIMIT:
At or above the single limit counts as an overflow, even for a double constant (Level II doubles had a far wider range, but this is a deliberate limit).

@line if suffix == "%":
The % suffix forces an integer; it overflows if the number is above 32767.

@def val_text
VAL: the number at the start of the string, after leading spaces, with an optional sign. Anything else after it is ignored, and no number at all gives 0.

@def parse_number_text
Read a whole INPUT or DATA field as a number. Returns (ok, value). An empty field is 0, and a field that has anything left over after the number (for
example "12ABC") is not ok, so the caller can ask again or report an error.

@def f_sqr
Square root for x >= 0 by Newton's method (average the guess and x / guess). The first guess is a power of two near the answer, so 8 steps
reach the full precision. A value of zero or below gives 0.0; the caller reports the error for negative arguments.

@def f_exp
e**x by range reduction: write x = k * ln2 + r with |r| <= ln2 / 2, sum the Taylor series for e**r (16 terms is far more than enough for such a small r),
and multiply the result by the exact power of two 2**k. Returns None above 88.7, where the result would be too large, and 0.0 below -100.

@def f_log
Natural logarithm for x > 0. Split x into m * 2**e with m in [1/sqrt(2), sqrt(2)]; then ln(x) = e*ln2 + ln(m), and ln(m) = 2*(z + z**3/3 + z**5/5 + ...)
with z = (m - 1) / (m + 1). That series converges quickly because |z| is small for such an m.

@def f_sin
Sine by range reduction and the Taylor series: subtract the nearest whole number of full turns, fold the remainder into -pi/2..pi/2 using
sin(pi - r) = sin(r), then sum 12 terms of r - r**3/3! + r**5/5! - ... Accurate to the six or seven digits that single precision shows.

@def f_cos
Cosine as the sine of the angle plus a quarter turn.

@def f_atn
Arc tangent. Fold the argument into 0..1 (using atan(-x) = -atan(x) and atan(x) = pi/2 - atan(1/x)), halve the angle twice with
tan(t/2) = tan(t) / (1 + sqrt(1 + tan(t)**2)) so that the series converges fast, sum the series, multiply back by 4 (two halvings), and undo the folding.

@def f_pow
x raised to y, as (value, error). A zero exponent gives 1. Zero to a positive power is 0 and to a negative power is a division by zero. A whole-number
exponent up to 2048 in size is done by repeated squaring (exact for integers); otherwise it is exp(y * ln(x)), which needs a positive x ("FC" if not).
Results that overflow a single give "OV".

@line for _ in range(12):
Repeated squaring: look at the bits of n from the low end. 12 passes cover exponents up to 4095, more than the 2048 allowed.

@def rnd_next
One step of a linear congruential generator: seed * 1103515245 + 12345, modulo 2**31. These are the constants of the classic C library rand().
The program's RND takes the high bits of the new seed and divides by 2**24 to make a number in [0, 1).

@file apps/Basic/scripts/screen.star

@line COLUMNS = 64
The screen geometry: 64 columns by 16 rows, 1024 cells. VIDEO_BASE is where the real machine's video memory started, so POKE and PEEK use real addresses.

@line CODES = {"%c" % i: i for i in range(256)}
Map each character to its code, so putting text on the screen is a dictionary lookup per character.

@def _make_sextants
Build the 64 pictures for graphics cells. A cell is 2 blocks wide and 3 high, six bits, so 64 patterns. Unicode has the "sextant" characters
(U+1FB00 and following) for 60 of them; the other four (empty, left half, right half, full) live elsewhere in Unicode, hence the special cases
for patterns 0, 21 (left column), 42 (right column) and 63 (all six). `skip` shifts the index past the two halves that are missing from the sextant block.

@def new_screen
A blank screen: 1024 copies of the space code.

@def _scroll
Scroll the screen one row up: drop the first row, add a blank one, and put the cursor at the start of the bottom row.

@def newline
Move to the start of the next row. If that is off the bottom, scroll. Updates `col`, the column used by TAB, POS and the comma fields of PRINT.

@def put_text
Print a string at the cursor. The text is first added to the plain output stream (unless `quiet` is set) so a terminal host can echo it, then each character
is stored in video memory: a newline starts a new row, any other character fills one cell, a row that fills wraps to the next, and the bottom scrolls.
Characters outside the 0 to 255 range are stored as code 63, the question mark.

@def clear_screen
CLS: blank all cells, home the cursor, and send a form feed through the output stream so a terminal host also clears.

@def set_cursor
PRINT @ position: put the cursor at cell pos and recompute the column.

@def _cell_and_bit
Which character cell holds the dot (x, y), and which of its six bits: the cell is at row y // 3, column x // 2, and the bit number is 2 * (y % 3) + (x % 2).
Returns (cell index, bit mask).

@def set_pixel
SET and RESET. Turn one dot on or off. If the cell held text it becomes an empty graphics cell first (code 128, which is the "no dots" pattern).
Clearing the last dot leaves code 128, not a space, so the cell stays a graphics cell.

@def point_on
POINT: true when the cell is a graphics cell (codes 128 to 191) and the dot's bit is set.

@def _glyph
The character to display for a screen code: text for 32 to 127, a sextant picture for 128 to 191, and a space for anything else.

@def screen_lines
The whole screen as 16 strings, each with its trailing spaces removed. Used by hosts and tests to show or compare the screen.

@def _count
Count how many times ch repeats in a row starting at s[i]. Used to measure a run of # characters in a PRINT USING template.

@def parse_using
Break a PRINT USING template into pieces. Each piece is one of: ("lit", text) printed as it is; ("str", width) for a string field, where width -1 is
the single character field "!", 0 is the whole-string field "&", and a positive number is a "\ \" field that many characters wide (including the
backslashes); and ("num", spec) for a numeric field, where spec is a dict describing it (leading plus, stars fill, dollar, digits before and after the
point, commas, trailing sign, exponent, total width). An underscore makes the next character literal.
A run of # and the other numeric symbols that has no digit places at all is treated as plain text.

@line elif c == "#" or c == "." or c == "+" or c == "*" or c == "$" or (c == "-" and False):
Any of these characters can start a numeric field. (The last test can never be true: a leading "-" is not a field start, only a trailing one is.)

@line if fmt.startswith("**", j):
"**" at the start of a field fills leading blanks with asterisks and adds two digit places.

@line if fmt.startswith("$$", j):
"$$" puts a dollar sign just before the first digit and adds one digit place.

@line elif d == "," and not spec["dot"]:
A comma before the decimal point asks for thousands separators.

@line if fmt.startswith("^^^^", j):
Four carets ask for exponent notation, E+xx.

@line if j < n and (fmt[j] == "-" or fmt[j] == "+") and not spec["lead_plus"]:
A trailing sign: "-" shows a minus for negatives and a blank otherwise, "+" shows the sign always. Not allowed if a leading plus was used.

@line spec["width"] = j - i
The whole field width in characters, including signs, commas, the point and so on. The formatted number is padded on the left to this width.

@def _with_commas
Insert a comma between each group of three digits, counting from the right: "1234567" becomes "1,234,567".

@def format_field
Format one number into one numeric field (a spec from parse_using). Round to the requested decimals, add commas, the point, the dollar sign and the sign,
then pad on the left (with spaces, or asterisks for "**") to the field width. If the result is wider than the field, a percent sign is put in front
and the full number is shown, as Level II does.

@def format_using
Apply a PRINT USING template to a list of values. Returns (text, error). The template is reused from the start if there are more values than fields.
A template with no fields is an "FC" error; a number given to a string field, or the reverse, is "TM". Output stops when the values run out, but
literal text before the first field that has no value is still printed.

@file apps/Basic/scripts/editor.star

@line MAX_LENGTH = 255
A program line is at most 255 characters, so insert and change stop adding at that length.

@line DELETE_MARK = "\\"
Deleted text is shown between two backslashes so the user can see what was removed.

@def edit_head
The line number and a space: what the editor prints at the start of the edit, after L, and after A.

@def new_edit
Start editing line `line` whose text after the number is `text`. The text is held as a list of code points (not a string) so each character is one
step, even outside the basic plane. `orig` is a copy for the A command. The other fields are the state machine: `mode` says which key handler is
active, `count` is the repeat count being typed, `pending`/`arg_count` remember an S or K waiting for its character, `left` counts characters left to
change, and `insert_from` is where the current insert began.

@def edit_text
The edited line as a string.

@def _printable
True for a key that can be put into the line: one character, not a control code, not DEL.

@def _is_backspace
Both the backspace code (8) and DEL (127) count as backspace, because terminals send either.

@def edit_step
The editor's only entry point: give it a key, get back (text to echo, action). Enter always ends the edit and stores the line, showing the rest of
it. Every other key goes to the handler for the current mode, in this order: inserting, changing, waiting for the character of S or K, and finally
the ordinary command handler.

@def _insert_key
Handle a key while inserting. Escape returns to command mode. Backspace removes the character just typed, but not any before the point where the
insert began. A printable key is added at the cursor if there is room. The returned text is exactly what the terminal should show.

@def _change_key
Handle a key in change mode (the C command). Each printable key replaces the character under the cursor and moves on. The mode ends when the
counted number of characters has been changed, or at the end of the line, or on Escape.

@def _argument_key
Handle the character that follows S (search) or K (kill). Look for the nth following occurrence of that character, starting from the character
after the cursor, ignoring case. S moves the cursor to just before it, echoing what is passed over, or to the end of the line if not found. K deletes
up to just before it and echoes the deleted text between backslashes, or does nothing if it is not found. A control key such as Escape cancels.

@line for j in range(pos + 1, len(buf)):
Start one past the cursor: the character under the cursor never matches.

@def _command_key
Handle a key in command mode. A digit adds to the repeat count (capped at 255). Any other key uses that count n (1 if none was typed), then does
its command: Space moves right, Backspace moves left, L lists the rest and returns to the start, D deletes n characters, C starts change mode, I starts
insert mode, X goes to the end and inserts, H deletes the rest and inserts, S and K wait for their character, Q quits without saving, E saves
and ends, and A restores the original line. Unknown keys are ignored.

@line if k == "L":
L: show the rest of the line from the cursor, then go back to the start and show the line number again.

@line if k == "X":
X: jump to the end of the line and start inserting there. The rest of the line is shown as the cursor passes over it.

@line if k == "H":
H ("hack"): delete everything after the cursor and start inserting there.

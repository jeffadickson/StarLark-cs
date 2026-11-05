@@ Heavy comments for lex.star, applied by `CommentCheck insert`.

@file apps/Basic/scripts/lex.star

@line KEYWORDS = [
Every keyword the tokenizer recognises, in the groups the machine used. Order does not matter here: _index_keywords sorts them. Two entries end in
"(" (TAB( and SPC() because those words always open an argument list, and several end in "$" (the string functions). The statement words
include the ones Level II BASIC had but this interpreter treats as errors or no-ops (for example OUT).

@def _index_keywords
Build KEYWORD_INDEX: a dict from a first letter to the list of keywords that start with it, longest first. Longest first matters because some
keywords begin with another one (DEF and DEFINT, ERR and ERROR, INP and INPUT): trying the short one first would split INPUT into INP and UT.

@line KEYWORD_INDEX = _index_keywords()
Built once when the file is loaded, then only read.

@def keyword_at
Look for a keyword starting exactly at position i of the upper-case text u. Returns the keyword (the longest that fits, because of the order in
KEYWORD_INDEX) or "" if none starts there. There is no check for what comes before or after: this is why FORI=1TO10 is understood.

@def _hex_value
The numeric value of one hexadecimal digit character (0 to 9, then A to F as 10 to 15), or -1 if it is not one. Used for &H and &O constants, where
the caller then rejects digits that are too large for the base.

@def ord_digit
The value of a decimal digit character, or -1. A lookup by position in "0123456789" (Starlark's ord would also work, but this avoids a call).

@def _data_end
Where the text of a DATA statement stops: the index of the first colon that is not inside double quotes, or the end of the line. DATA keeps its text
raw (the READ statement splits it later), so the tokenizer only has to find where the statement ends.

@def tokenize
Turn one line of program text into tokens (see the list at the top of the file). Returns (tokens, error): error is "" on success, "SN" for a
character that cannot be part of BASIC, and "OV" for a number that is too big. Tokens found before an error are still returned.
It is one flat loop with a position i. A line can never need more steps than characters plus a little, so the loop is bounded by n + 2 rather than
being a `while`. Starlark has no while loop.

@line u = text.upper()
Keywords and names are matched in upper case, while strings, REM text and DATA text are taken from the original `t` so they keep their case.

@line if len(u) != len(t):
A few characters change length when upper-cased (for example the German sharp s becomes SS). Positions in u would no longer line up with t, so in that
rare case give up on upper-casing and use the text as typed. Names with such characters then simply do not match the keywords.

@line for _ in range(n + 2):
The bounded loop: every pass moves i forward by at least one, or returns.

@line elif c == "\"":
A string literal. It runs to the next double quote; an unterminated string is allowed and runs to the end of the line, as on Level II.

@line elif _is_digit(c) or (c == "." and i + 1 < n and _is_digit(u[i + 1])):
A number: it starts with a digit, or with a dot that is followed by a digit (so ".5" is a number but a lone "." is not). scan_number reads it and
decides integer, single or double; its third result is the type, or -1 (no number) or -2 (too large).

@line elif c == "&":
A hexadecimal (&H) or octal (&O) constant. Anything else after the & is a syntax error.

@line value = value * base + d
Accumulate the digits left to right. The loop stops at the first character that is not a digit of this base.

@line if value > 65535:
Level II held these constants in 16 bits, so more than 65535 overflows.

@line if value > INT_MAX:
Values from 32768 to 65535 are stored as negative integers (two's complement): &HFFFF is -1.

@line elif _is_letter(c):
A letter starts either a keyword or a variable name. Keywords win: if one starts here, it is taken even if the letters could also be a longer name.

@line if k == "REM":
REM (and the apostrophe form below) take the rest of the line as one string token, so nothing in a comment is tokenized.

@line elif k == "DATA":
DATA also keeps its text raw, up to the next unquoted colon (see _data_end), because its items are separated by commas and are not BASIC expressions.

@line j = i + 1
Not a keyword, so this is a variable name. Scan forward over letters and digits, but stop where a keyword starts: in FORTOTAL=1 the name ends at TO.

@line name = u[i:j][:2]
Only the first two characters of a name are significant (so ALPHA and ALBERT are the same variable AL). Longer names are accepted and shortened.

@line if j < n and (u[j] == "$"
A type suffix may follow the name: $ string, % integer, ! single, # double. It is kept separately from the name.

@line elif c == "'":
The apostrophe is a short form of REM.

@line elif c == "?":
A question mark is a short form of PRINT.

@line elif c == "<" or c == ">" or c == "=":
Comparison operators. Two-character forms are normalised: "><" and "<>" become "<>", "=<" becomes "<=", and "=>" becomes ">=".

@line elif c in "+-*/^(),;:#@":
Single-character operators and punctuation. The @ is the PRINT @ position marker. Any character that is not handled by this or an earlier branch (for
example a bare ] or a control character) reaches the final else, which is a syntax error.


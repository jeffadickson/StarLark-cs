# The tokenizer for TRS-80 Level II BASIC.
#
# It follows the real machine on purpose: keywords are recognised anywhere in a line, even in the middle of a name, so
# FORI=1TO10 works with no spaces and so does the famous trap that a variable called TOTAL is read as the keyword TO followed by the variable TAL. Names have
# two significant characters. Everything outside a string is case-insensitive (REM and ' comment text and DATA text keep their case).
#
# A token is a tuple whose first item says what it is:
#   ("n", value)               a number (int, single float, or a double held in a 1-tuple, see numbers.star)
#   ("s", text)                a string literal, or the comment text after REM, or the raw text of a DATA statement
#   ("v", name, suffix)        a variable name (two characters) and its type suffix: "" or one of $ % ! #
#   ("k", WORD)                a keyword
#   ("o", op)                  an operator or punctuation mark

load("numbers.star", "scan_number", "INT_MAX")

# Every keyword the tokenizer recognises, in the groups the machine used. Order does not matter here: _index_keywords sorts them. Two entries end in
# "(" (TAB( and SPC() because those words always open an argument list, and several end in "$" (the string functions). The statement words
# include the ones Level II BASIC had but this interpreter treats as errors or no-ops (for example OUT).
KEYWORDS = [
    # statements
    "END", "FOR", "NEXT", "DATA", "INPUT", "DIM", "READ", "LET", "GOTO", "RUN", "IF", "RESTORE", "GOSUB", "RETURN",
    "REM", "STOP", "ELSE", "TRON", "TROFF", "DEFSTR", "DEFINT", "DEFSNG", "DEFDBL", "ERROR", "RESUME", "ON", "POKE",
    "PRINT", "CONT", "LIST", "DELETE", "CLEAR", "NEW", "CLS", "SET", "RESET", "RANDOM", "DEF", "OUT", "EDIT",
    # words that sit inside statements
    "TAB(", "SPC(", "TO", "FN", "USING", "THEN", "NOT", "STEP", "AND", "OR",
    # functions
    "SGN", "INT", "ABS", "FRE", "INP", "POS", "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "CINT",
    "CSNG", "CDBL", "FIX", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "INSTR", "STRING$",
    "POINT", "MEM", "INKEY$", "ERL", "ERR",
]

# Group the keywords by first letter, longest first, so that where one keyword is the start of another (DEF and DEFINT, ERR and ERROR,
# INP and INPUT) keyword_at finds the longer one.
def _index_keywords():
    index = {}
    # Longest first, so that INPUT is not read as INP followed by UT.
    for word in sorted(KEYWORDS, key = lambda w: -len(w)):
        index.setdefault(word[0], []).append(word)
    return index

# Built once when the file is loaded, then only read.
KEYWORD_INDEX = _index_keywords()

# The keyword that starts at position i of the (upper-case) text, or "". Keywords are found anywhere, even in the middle of a name, as on Level II.
def keyword_at(u, i):
    for word in KEYWORD_INDEX.get(u[i], []):
        if u.startswith(word, i):
            return word
    return ""

# True for the characters "0" to "9".
def _is_digit(c):
    return c >= "0" and c <= "9"

# True for the upper-case letters "A" to "Z". The text is upper-cased before it is scanned.
def _is_letter(c):
    return c >= "A" and c <= "Z"

# The value of a hexadecimal digit (0-9, A-F), or -1.
def _hex_value(c):
    if c >= "0" and c <= "9":
        return ord_digit(c)
    if c >= "A" and c <= "F":
        return 10 + "ABCDEF".find(c)
    return -1

# The value of a decimal digit character, or -1.
def ord_digit(c):
    return "0123456789".find(c)

# The text of a DATA statement: everything up to the next colon that is not inside quotes.
def _data_end(t, i):
    n = len(t)
    in_quotes = False
    for k in range(i, n):
        c = t[k]
        if c == "\"":
            in_quotes = not in_quotes
        elif c == ":" and not in_quotes:
            return k
    return n

# Split a line of text into tokens. Returns (tokens, error code); the error code is "" when all is well.
def tokenize(text):
    t = text
    # Keywords and names are matched in upper case, while strings, REM text and DATA text are taken from the original `t` so they keep their case.
    u = text.upper()
    # A few characters change length when upper-cased (for example the German sharp s becomes SS). Positions in u would no longer line up with t, so in that
    # rare case give up on upper-casing and use the text as typed. Names with such characters then simply do not match the keywords.
    if len(u) != len(t):
        u = t
    n = len(t)
    tokens = []
    i = 0
    # The bounded loop: every pass moves i forward by at least one, or returns.
    for _ in range(n + 2):
        if i >= n:
            break
        c = u[i]
        if c == " ":
            i += 1
        # A string literal. It runs to the next double quote; an unterminated string is allowed and runs to the end of the line, as on Level II.
        elif c == "\"":
            j = t.find("\"", i + 1)
            if j < 0:
                tokens.append(("s", t[i + 1:]))
                i = n
            else:
                tokens.append(("s", t[i + 1:j]))
                i = j + 1
        # A number: it starts with a digit, or with a dot that is followed by a digit (so ".5" is a number but a lone "." is not). scan_number reads it and
        # decides integer, single or double; its third result is the type, or -1 (no number) or -2 (too large).
        elif _is_digit(c) or (c == "." and i + 1 < n and _is_digit(u[i + 1])):
            r = scan_number(u, i)
            if r[2] == -2:
                return (tokens, "OV")
            if r[2] == -1:
                return (tokens, "SN")
            tokens.append(("n", r[0]))
            i = r[1]
        # A hexadecimal (&H) or octal (&O) constant. Anything else after the & is a syntax error.
        elif c == "&":
            base = 0
            if i + 1 < n and u[i + 1] == "H":
                base = 16
            elif i + 1 < n and u[i + 1] == "O":
                base = 8
            if base == 0:
                return (tokens, "SN")
            j = i + 2
            value = 0
            for k in range(j, n):
                d = _hex_value(u[k])
                if d < 0 or d >= base:
                    break
                # Accumulate the digits left to right. The loop stops at the first character that is not a digit of this base.
                value = value * base + d
                j = k + 1
            # Level II held these constants in 16 bits, so more than 65535 overflows.
            if value > 65535:
                return (tokens, "OV")
            # Values from 32768 to 65535 are stored as negative integers (two's complement): &HFFFF is -1.
            if value > INT_MAX:
                value -= 65536
            tokens.append(("n", value))
            i = j
        # A letter starts either a keyword or a variable name. Keywords win: if one starts here, it is taken even if the letters could also be a longer name.
        elif _is_letter(c):
            k = keyword_at(u, i)
            if k != "":
                i += len(k)
                # REM (and the apostrophe form below) take the rest of the line as one string token, so nothing in a comment is tokenized.
                if k == "REM":
                    tokens.append(("k", "REM"))
                    tokens.append(("s", t[i:]))
                    i = n
                # DATA also keeps its text raw, up to the next unquoted colon (see _data_end), because its items are separated by commas and are not BASIC expressions.
                elif k == "DATA":
                    end = _data_end(t, i)
                    tokens.append(("k", "DATA"))
                    tokens.append(("s", t[i:end]))
                    i = end
                else:
                    tokens.append(("k", k))
            else:
                # Not a keyword, so this is a variable name. Scan forward over letters and digits, but stop where a keyword starts: in FORTOTAL=1 the name ends at TO.
                j = i + 1
                for m in range(j, n):
                    d = u[m]
                    if not (_is_letter(d) or _is_digit(d)):
                        break
                    if keyword_at(u, m) != "":
                        break
                    j = m + 1
                # Only the first two characters of a name are significant (so ALPHA and ALBERT are the same variable AL). Longer names are accepted and shortened.
                name = u[i:j][:2]
                suffix = ""
                # A type suffix may follow the name: $ string, % integer, ! single, # double. It is kept separately from the name.
                if j < n and (u[j] == "$" or u[j] == "%" or u[j] == "!" or u[j] == "#"):
                    suffix = u[j]
                    j += 1
                tokens.append(("v", name, suffix))
                i = j
        # The apostrophe is a short form of REM.
        elif c == "'":
            tokens.append(("k", "REM"))
            tokens.append(("s", t[i + 1:]))
            i = n
        # A question mark is a short form of PRINT.
        elif c == "?":
            tokens.append(("k", "PRINT"))
            i += 1
        # Comparison operators. Two-character forms are normalised: "><" and "<>" become "<>", "=<" becomes "<=", and "=>" becomes ">=".
        elif c == "<" or c == ">" or c == "=":
            pair = u[i:i + 2]
            if pair == "<>" or pair == "><":
                tokens.append(("o", "<>"))
                i += 2
            elif pair == "<=" or pair == "=<":
                tokens.append(("o", "<="))
                i += 2
            elif pair == ">=" or pair == "=>":
                tokens.append(("o", ">="))
                i += 2
            else:
                tokens.append(("o", c))
                i += 1
        # Single-character operators and punctuation. The @ is the PRINT @ position marker. Any character that is not handled by this or an earlier branch (for
        # example a bare ] or a control character) reaches the final else, which is a syntax error.
        elif c in "+-*/^(),;:#@":
            tokens.append(("o", c))
            i += 1
        else:
            return (tokens, "SN")
    return (tokens, "")

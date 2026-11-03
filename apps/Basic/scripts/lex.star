# The tokenizer for TRS-80 Level II BASIC.
#
# It follows the real machine on purpose: keywords are recognised anywhere in a line, even in the middle of a name, so
# FORI=1TO10 works with no spaces and so does the famous trap that a variable called TOTAL is read as TO T AL. Names have
# two significant characters. Everything outside a string is case-insensitive.
#
# A token is a tuple whose first item says what it is:
#   ("n", value)               a number (int, single float, or a double held in a 1-tuple, see numbers.star)
#   ("s", text)                a string literal, or the comment text after REM, or the raw text of a DATA statement
#   ("v", name, suffix)        a variable name (two characters) and its type suffix: "" or one of $ % ! #
#   ("k", WORD)                a keyword
#   ("o", op)                  an operator or punctuation mark

load("numbers.star", "scan_number", "INT_MAX")

KEYWORDS = [
    # statements
    "END", "FOR", "NEXT", "DATA", "INPUT", "DIM", "READ", "LET", "GOTO", "RUN", "IF", "RESTORE", "GOSUB", "RETURN",
    "REM", "STOP", "ELSE", "TRON", "TROFF", "DEFSTR", "DEFINT", "DEFSNG", "DEFDBL", "ERROR", "RESUME", "ON", "POKE",
    "PRINT", "CONT", "LIST", "DELETE", "CLEAR", "NEW", "CLS", "SET", "RESET", "RANDOM", "DEF", "OUT",
    # words that sit inside statements
    "TAB(", "SPC(", "TO", "FN", "USING", "THEN", "NOT", "STEP", "AND", "OR",
    # functions
    "SGN", "INT", "ABS", "FRE", "INP", "POS", "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "CINT",
    "CSNG", "CDBL", "FIX", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$", "INSTR", "STRING$",
    "POINT", "MEM", "INKEY$", "ERL", "ERR",
]

def _index_keywords():
    index = {}
    # Longest first, so that STRING$ is not read as STR and INT is not read as IN.
    for word in sorted(KEYWORDS, key = lambda w: -len(w)):
        index.setdefault(word[0], []).append(word)
    return index

KEYWORD_INDEX = _index_keywords()

def keyword_at(u, i):
    for word in KEYWORD_INDEX.get(u[i], []):
        if u.startswith(word, i):
            return word
    return ""

def _is_digit(c):
    return c >= "0" and c <= "9"

def _is_letter(c):
    return c >= "A" and c <= "Z"

def _hex_value(c):
    if c >= "0" and c <= "9":
        return ord_digit(c)
    if c >= "A" and c <= "F":
        return 10 + "ABCDEF".find(c)
    return -1

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
    u = text.upper()
    if len(u) != len(t):
        u = t
    n = len(t)
    tokens = []
    i = 0
    for _ in range(n + 2):
        if i >= n:
            break
        c = u[i]
        if c == " ":
            i += 1
        elif c == "\"":
            j = t.find("\"", i + 1)
            if j < 0:
                tokens.append(("s", t[i + 1:]))
                i = n
            else:
                tokens.append(("s", t[i + 1:j]))
                i = j + 1
        elif _is_digit(c) or (c == "." and i + 1 < n and _is_digit(u[i + 1])):
            r = scan_number(u, i)
            if r[2] == -2:
                return (tokens, "OV")
            if r[2] == -1:
                return (tokens, "SN")
            tokens.append(("n", r[0]))
            i = r[1]
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
                value = value * base + d
                j = k + 1
            if value > 65535:
                return (tokens, "OV")
            if value > INT_MAX:
                value -= 65536
            tokens.append(("n", value))
            i = j
        elif _is_letter(c):
            k = keyword_at(u, i)
            if k != "":
                i += len(k)
                if k == "REM":
                    tokens.append(("k", "REM"))
                    tokens.append(("s", t[i:]))
                    i = n
                elif k == "DATA":
                    end = _data_end(t, i)
                    tokens.append(("k", "DATA"))
                    tokens.append(("s", t[i:end]))
                    i = end
                else:
                    tokens.append(("k", k))
            else:
                j = i + 1
                for m in range(j, n):
                    d = u[m]
                    if not (_is_letter(d) or _is_digit(d)):
                        break
                    if keyword_at(u, m) != "":
                        break
                    j = m + 1
                name = u[i:j][:2]
                suffix = ""
                if j < n and (u[j] == "$" or u[j] == "%" or u[j] == "!" or u[j] == "#"):
                    suffix = u[j]
                    j += 1
                tokens.append(("v", name, suffix))
                i = j
        elif c == "'":
            tokens.append(("k", "REM"))
            tokens.append(("s", t[i + 1:]))
            i = n
        elif c == "?":
            tokens.append(("k", "PRINT"))
            i += 1
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
        elif c in "+-*/^(),;:#@":
            tokens.append(("o", c))
            i += 1
        else:
            return (tokens, "SN")
    return (tokens, "")

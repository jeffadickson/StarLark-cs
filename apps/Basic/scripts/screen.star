# The TRS-80 screen: 16 rows of 64 characters, and 128 x 48 block graphics.
#
# The screen is a list of 1024 character codes, the same as the video memory the real machine had at addresses 15360 to
# 16383, so PRINT @, POKE, PEEK, SET, RESET and POINT all work on one store. Codes 32 to 127 are text, and 128 to 191 are
# graphics cells of 2 x 3 blocks (code 128 plus one bit per block).
#
# Everything printed (unless st["quiet"] is set) is also appended to a plain text stream (st["out"]) so a terminal host can show it as it comes, and
# the tests can read it without caring about the screen.

load("numbers.star", "floor_int", "to_float", "kind_of", "overflows")

# The screen geometry: 64 columns by 16 rows, 1024 cells. VIDEO_BASE is where the real machine's video memory started, so POKE and PEEK use real addresses.
COLUMNS = 64
ROWS = 16
CELLS = 1024
VIDEO_BASE = 15360

# Character -> screen code, for printing a character into video memory. Characters outside 0 to 255 are stored as 63, "?".
# Map each character to its code, so putting text on the screen is a dictionary lookup per character.
CODES = {"%c" % i: i for i in range(256)}

# Build the table that shows the 64 possible 2 x 3 block-graphics cells as Unicode "sextant" characters (and the half and full blocks, which
# Unicode keeps elsewhere). Index is the six-bit pattern, one bit per block.
def _make_sextants():
    table = []
    for pattern in range(64):
        if pattern == 0:
            table.append(" ")
        elif pattern == 21:
            table.append("%c" % 0x258C)
        elif pattern == 42:
            table.append("%c" % 0x2590)
        elif pattern == 63:
            table.append("%c" % 0x2588)
        else:
            skip = 0
            if pattern > 21:
                skip += 1
            if pattern > 42:
                skip += 1
            table.append("%c" % (0x1FB00 + pattern - 1 - skip))
    return table

SEXTANTS = _make_sextants()

# A blank screen: 1,024 cells (16 rows of 64) all holding the space character (32).
def new_screen():
    return [32] * CELLS

# Move every row up one and blank the bottom row; the cursor goes to the start of the bottom row.
def _scroll(st):
    st["screen"] = st["screen"][COLUMNS:] + [32] * COLUMNS
    st["cur"] = CELLS - COLUMNS

# Move the cursor to the start of the next row, scrolling if that was the last one.
def newline(st):
    cur = (st["cur"] // COLUMNS + 1) * COLUMNS
    st["cur"] = cur
    if cur >= CELLS:
        _scroll(st)
    st["col"] = st["cur"] % COLUMNS

# Write text at the cursor. Newlines move to the start of the next line, and a line that reaches column 64 wraps.
def put_text(st, s):
    if not st["quiet"]:
        st["out"].append(s)
    screen = st["screen"]
    for ch in s.elems():
        if ch == "\n" or ch == "\r":
            newline(st)
            screen = st["screen"]
            continue
        screen[st["cur"]] = CODES.get(ch, 63)
        st["cur"] += 1
        if st["cur"] >= CELLS:
            _scroll(st)
            screen = st["screen"]
        st["col"] = st["cur"] % COLUMNS

# CLS: blank the screen, home the cursor, and put a form feed in the output stream so a terminal host can clear too.
def clear_screen(st):
    st["screen"] = new_screen()
    st["cur"] = 0
    st["col"] = 0
    st["out"].append("\f")

# Move the cursor to the cell `pos` (0 to 1023), as PRINT @ does.
def set_cursor(st, pos):
    st["cur"] = pos
    st["col"] = pos % COLUMNS

# ---------------------------------------------------------------- graphics

# Which character cell holds the block-graphics dot (x, y), and which of the cell's six bits it is. A cell is 2 dots wide and 3 high.
def _cell_and_bit(x, y):
    return ((y // 3) * COLUMNS + x // 2, 1 << ((y % 3) * 2 + x % 2))

# Turn the dot (x, y) on or off. A cell that held text becomes a graphics cell (codes 128 to 191); one whose dots are all off stays a blank
# graphics cell (128), not the space character.
def set_pixel(st, x, y, on):
    c = _cell_and_bit(x, y)
    code = st["screen"][c[0]]
    if code < 128 or code > 191:
        code = 128
    if on:
        code = code | c[1]
    else:
        code = code & (255 - c[1])
        if code < 128:
            code = 128
    st["screen"][c[0]] = code

# True if the dot (x, y) is on: the cell must be a graphics cell and the dot's bit set. This is what POINT reads.
def point_on(st, x, y):
    c = _cell_and_bit(x, y)
    code = st["screen"][c[0]]
    return code >= 128 and code <= 191 and (code & c[1]) != 0

# ---------------------------------------------------------------- showing the screen

# The character to show for a screen code: text for 32 to 127, a sextant for 128 to 191, a space for anything else.
def _glyph(code):
    if code >= 32 and code < 128:
        return "%c" % code
    if code >= 128 and code < 192:
        return SEXTANTS[code - 128]
    return " "

# The 16 lines of the screen as text, with trailing spaces removed.
def screen_lines(st):
    screen = st["screen"]
    lines = []
    for r in range(ROWS):
        row = screen[r * COLUMNS:(r + 1) * COLUMNS]
        lines.append("".join([_glyph(c) for c in row]).rstrip(" "))
    return lines

# ---------------------------------------------------------------- PRINT USING

# How many times the character `ch` repeats in s starting at i. Used to measure runs of # in a PRINT USING template.
def _count(s, i, ch):
    j = i
    for k in range(i, len(s)):
        if s[k] != ch:
            break
        j = k + 1
    return j - i

# Cut a format string into literal text, numeric fields and string fields.
# A numeric field is ("num", dict), a string field is ("str", width) with width 0 meaning "the whole string" and -1 the
# first character only.
def parse_using(fmt):
    segs = []
    lit = ""
    n = len(fmt)
    i = 0
    for _ in range(n + 1):
        if i >= n:
            break
        c = fmt[i]
        if c == "_" and i + 1 < n:
            lit += fmt[i + 1]
            i += 2
        elif c == "!":
            if lit != "":
                segs.append(("lit", lit))
                lit = ""
            segs.append(("str", -1))
            i += 1
        elif c == "&":
            if lit != "":
                segs.append(("lit", lit))
                lit = ""
            segs.append(("str", 0))
            i += 1
        elif c == "\\":
            j = fmt.find("\\", i + 1)
            if j < 0:
                lit += c
                i += 1
            else:
                if lit != "":
                    segs.append(("lit", lit))
                    lit = ""
                segs.append(("str", j - i + 1))
                i = j + 1
        # Any of these characters can start a numeric field. (The last test can never be true: a leading "-" is not a field start, only a trailing one is.)
        elif c == "#" or c == "." or c == "+" or c == "*" or c == "$" or (c == "-" and False):
            spec = {"lead_plus": False, "stars": False, "dollar": False, "int": 0, "frac": 0, "dot": False,
                    "commas": False, "trail": "", "exp": False, "width": 0}
            j = i
            if fmt[j] == "+":
                spec["lead_plus"] = True
                j += 1
            # "**" at the start of a field fills leading blanks with asterisks and adds two digit places.
            if fmt.startswith("**", j):
                spec["stars"] = True
                spec["int"] += 2
                j += 2
            # "$$" puts a dollar sign just before the first digit and adds one digit place.
            if fmt.startswith("$$", j):
                spec["dollar"] = True
                spec["int"] += 1
                j += 2
            for _ in range(n + 1):
                if j >= n:
                    break
                d = fmt[j]
                if d == "#":
                    spec["int"] += 1
                    j += 1
                # A comma before the decimal point asks for thousands separators.
                elif d == "," and not spec["dot"]:
                    spec["commas"] = True
                    j += 1
                else:
                    break
            if j < n and fmt[j] == "." and (j + 1 < n and fmt[j + 1] == "#" or spec["int"] > 0):
                spec["dot"] = True
                j += 1
                k = _count(fmt, j, "#")
                spec["frac"] = k
                j += k
            # Four carets ask for exponent notation, E+xx.
            if fmt.startswith("^^^^", j):
                spec["exp"] = True
                j += 4
            # A trailing sign: "-" shows a minus for negatives and a blank otherwise, "+" shows the sign always. Not allowed if a leading plus was used.
            if j < n and (fmt[j] == "-" or fmt[j] == "+") and not spec["lead_plus"]:
                spec["trail"] = fmt[j]
                j += 1
            if spec["int"] + spec["frac"] == 0:
                lit += fmt[i:j] if j > i else c
                i = max(j, i + 1)
            else:
                if lit != "":
                    segs.append(("lit", lit))
                    lit = ""
                # The whole field width in characters, including signs, commas, the point and so on. The formatted number is padded on the left to this width.
                spec["width"] = j - i
                segs.append(("num", spec))
                i = j
        else:
            lit += c
            i += 1
    if lit != "":
        segs.append(("lit", lit))
    return segs

# Put a comma between every group of three digits, from the right: "1234567" becomes "1,234,567".
def _with_commas(digits):
    out = ""
    n = len(digits)
    for k in range(n):
        if k > 0 and (n - k) % 3 == 0:
            out += ","
        out += digits[k]
    return out

# Format x in a numeric field.
def format_field(x, spec):
    neg = x < 0.0
    a = -x if neg else x
    if spec["exp"]:
        body = ("%." + str(spec["frac"] + spec["int"] - 1 if spec["int"] > 0 else spec["frac"]) + "e") % a
        parts = body.split("e")
        mant = parts[0]
        ev = int(parts[1])
        text = mant + "E" + ("-" if ev < 0 else "+") + ("%02d" % abs(ev))
    else:
        text = ("%." + str(spec["frac"]) + "f") % a
        if spec["commas"]:
            ip = text.split(".")
            text = _with_commas(ip[0]) + (("." + ip[1]) if len(ip) > 1 else "")
        if spec["dot"] and spec["frac"] == 0:
            text += "."
    if spec["dollar"]:
        text = "$" + text
    sign = ""
    if neg:
        sign = "-"
    elif spec["lead_plus"]:
        sign = "+"
    if spec["trail"] != "":
        tail = ""
        if neg:
            tail = "-"
        elif spec["trail"] == "+":
            tail = "+"
        else:
            tail = " "
        full = text + tail
    else:
        full = sign + text
    if len(full) > spec["width"]:
        return "%" + full
    fill = "*" if spec["stars"] else " "
    return fill * (spec["width"] - len(full)) + full

# Format values by a PRINT USING format string. Returns (text, error).
def format_using(fmt, values):
    segs = parse_using(fmt)
    fields = 0
    for s in segs:
        if s[0] != "lit":
            fields += 1
    if fields == 0:
        return ("", "FC")
    out = ""
    vi = 0
    n = len(values)
    for _ in range(n * len(segs) + len(segs) + 2):
        done = False
        for s in segs:
            if s[0] == "lit":
                out += s[1]
            else:
                if vi >= n:
                    done = True
                    break
                v = values[vi]
                vi += 1
                if s[0] == "num":
                    if kind_of(v) == 3:
                        return ("", "TM")
                    out += format_field(to_float(v), s[1])
                else:
                    if kind_of(v) != 3:
                        return ("", "TM")
                    w = s[1]
                    if w == 0:
                        out += v
                    elif w == -1:
                        out += v[:1] if v != "" else " "
                    else:
                        out += (v + " " * w)[:w]
        if done or vi >= n:
            break
    return (out, "")

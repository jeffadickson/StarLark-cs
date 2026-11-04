# TRS-80 Level II BASIC, written in Starlark.
#
# The whole machine is one dict, the state `st`. The host (C# or a test) calls new_state(), then feeds it lines with
# submit(st, text, budget) and keeps calling run(st, budget) until it returns "done" or "waiting" (for an INPUT). It
# collects what was printed with take_output(st). Nothing here touches the console, the clock or the disk, and RND is a
# generator written in this program, so a BASIC program prints exactly the same thing every time it is run.
#
# Starlark has no `while` and no recursion, so the interpreter is one flat loop: run() executes one BASIC statement per turn
# of a bounded `for`, and BASIC's own GOTO, GOSUB and FOR are just changes to the program counter (st["line"], st["pos"])
# and to two plain lists used as stacks. Expressions are compiled and evaluated without recursion too (see expr.star).
#
# What is reproduced from Level II: line-numbered programs; the direct commands RUN, LIST, NEW, CONT, DELETE, CLEAR; PRINT
# with ; , TAB( SPC( @ and USING; LET, IF/THEN/ELSE, FOR/NEXT/STEP, GOTO, GOSUB/RETURN, ON..GOTO/GOSUB, INPUT, DATA/READ/
# RESTORE, DIM, DEF FN, DEFINT/SNG/DBL/STR, RANDOM, CLS, SET/RESET/POINT, PEEK/POKE on video memory, TRON/TROFF, STOP, END,
# ERROR, ON ERROR GOTO, RESUME; integer, single and double precision numbers and strings; the usual functions; and the
# two letter error codes ("?SN ERROR IN 100"). A FOR loop runs its body once even when the start is already past the limit,
# as Level II did.

load("numbers.star", "INT_MAX", "INT_MIN", "floor_int", "to_float", "kind_of", "span_digits", "parse_number_text", "print_text")
load("lex.star", "tokenize")
load("editor.star", "new_edit", "edit_step", "edit_text", "edit_head")
load("expr.star", "eval_at", "compile_expr", "var_key", "key_kind", "zero_of", "coerce", "locate", "new_array", "binop")
load("screen.star", "new_screen", "put_text", "newline", "clear_screen", "set_cursor", "set_pixel", "screen_lines",
     "format_using", "COLUMNS", "CELLS", "VIDEO_BASE")

ERRORS = ["NF", "SN", "RG", "OD", "FC", "OV", "OM", "UL", "BS", "DD", "/0", "ID", "TM", "OS", "LS", "ST", "CN", "NR", "RW", "UE",
          "MO", "FD", "L3", "UF"]

# Long names, for hosts that want to explain a code. The machine itself printed only the two letters.
ERROR_NAMES = {
    "NF": "NEXT without FOR", "SN": "Syntax error", "RG": "RETURN without GOSUB", "OD": "Out of DATA",
    "FC": "Illegal function call", "OV": "Overflow", "OM": "Out of memory", "UL": "Undefined line number",
    "BS": "Subscript out of range", "DD": "Redimensioned array", "/0": "Division by zero", "ID": "Illegal direct",
    "TM": "Type mismatch", "OS": "Out of string space", "LS": "String too long", "ST": "String formula too complex",
    "CN": "Can't continue", "NR": "No RESUME", "RW": "RESUME without error", "UE": "Unprintable error",
    "MO": "Missing operand", "FD": "Bad file data", "L3": "Disk BASIC only", "UF": "Undefined user function",
}

LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"

# ---------------------------------------------------------------- the machine

def new_state():
    st = {
        "program": {},          # line number -> text after the number
        "order": [],            # line numbers in order
        "index": {},            # line number -> position in order
        "dirty": False,         # order/index need rebuilding
        "tokcache": {},         # line -> (tokens, error)
        "rpn": {},              # compiled expressions, keyed by line * 4096 + token position
        "vars": {}, "arrays": {}, "fns": {},
        "deftype": {c: 1 for c in LETTERS.elems()},
        "line": 0, "toks": [], "tokerr": "", "pos": 0, "direct": ([], ""),
        "running": False, "waiting": None, "cont": None,
        "gosub": [], "fors": [],
        "data": [], "dptr": 0,
        "screen": new_screen(), "cur": 0, "col": 0, "out": [], "quiet": False, "echo": True,
        "mem": {}, "inkey": [], "seed": 12345, "trace": False,
        "edit": None, "onerr": 0, "inerr": False, "errline": 0, "errcode": 0, "resume": (0, 0), "stmt_start": (0, 0),
        "steps": 0,
    }
    put_text(st, "RADIO SHACK LEVEL II BASIC\n\n")
    ready(st)
    return st

def take_output(st):
    text = "".join(st["out"])
    st["out"] = []
    return text

def get_screen(st):
    return screen_lines(st)

def ready(st):
    if st["col"] != 0:
        newline(st)
    put_text(st, "READY\n>")

# Just the ">" prompt on a new line, with no READY: shown after a program line is stored and after a bare Enter.
def prompt_again(st):
    if st["col"] != 0:
        newline(st)
    put_text(st, ">")

def stop(st):
    st["running"] = False
    ready(st)

# ---------------------------------------------------------------- program storage

def ensure_index(st):
    if st["dirty"]:
        order = sorted(st["program"].keys())
        st["order"] = order
        st["index"] = {order[i]: i for i in range(len(order))}
        st["dirty"] = False

def get_toks(st, line):
    hit = st["tokcache"].get(line)
    if hit == None:
        hit = tokenize(st["program"][line])
        st["tokcache"][line] = hit
    return hit

def load_line(st, line):
    if line == 0:
        st["toks"] = st["direct"][0]
        st["tokerr"] = st["direct"][1]
    else:
        t = get_toks(st, line)
        st["toks"] = t[0]
        st["tokerr"] = t[1]
    st["line"] = line

def jump_to(st, line):
    if line not in st["program"]:
        return "UL"
    load_line(st, line)
    st["pos"] = 0
    if st["trace"]:
        put_text(st, "[%d]" % line)
    return ""

def next_line(st):
    if st["line"] == 0:
        stop(st)
        return ""
    ensure_index(st)
    i = st["index"].get(st["line"])
    if i == None or i + 1 >= len(st["order"]):
        stop(st)
        return ""
    return jump_to(st, st["order"][i + 1])

# Rebuild the DATA list from the program text.
def build_data(st):
    ensure_index(st)
    items = []
    for line in st["order"]:
        toks = get_toks(st, line)[0]
        for k in range(len(toks) - 1):
            if toks[k] == ("k", "DATA") and toks[k + 1][0] == "s":
                for f in split_fields(toks[k + 1][1]):
                    items.append((f[0], f[1], line))   # remember the DATA line, which is where a bad item is reported
    st["data"] = items
    st["dptr"] = 0

# Split the text of an INPUT line or a DATA statement at commas outside quotes. Each field is (text, was quoted).
def split_fields(text):
    fields = []
    cur = ""
    quoted = False
    in_quotes = False
    for ch in text.elems():
        if ch == "\"":
            if not quoted:
                cur = ""    # spaces before the opening quote are not part of the text
            in_quotes = not in_quotes
            quoted = True
        elif ch == "," and not in_quotes:
            fields.append((cur if quoted else cur.strip(), quoted))
            cur = ""
            quoted = False
        elif quoted and not in_quotes:
            pass            # after the closing quote, up to the comma, is ignored
        else:
            cur += ch
    fields.append((cur if quoted else cur.strip(), quoted))
    return fields

# ---------------------------------------------------------------- errors

def handle_error(st, code):
    line = st.pop("errat", st["line"])   # a bad DATA item names its own line
    st["errcode"] = ERRORS.index(code) * 2 if code in ERRORS else 40
    st["errline"] = line
    if st["onerr"] != 0 and not st["inerr"] and line != 0:
        st["resume"] = st["stmt_start"]
        st["inerr"] = True
        if jump_to(st, st["onerr"]) == "":
            return
    if st["col"] != 0:
        newline(st)
    text = "?" + code + " ERROR"
    if line != 0:
        text += " IN %d" % line
    put_text(st, text + "\n")
    st["cont"] = None
    stop(st)

def break_program(st):
    if not st["running"]:
        return
    if st["col"] != 0:
        newline(st)
    put_text(st, "BREAK" + ((" IN %d" % st["line"]) if st["line"] != 0 else "") + "\n")
    st["cont"] = (st["line"], st["pos"])
    st["waiting"] = None
    stop(st)

# ---------------------------------------------------------------- statement helpers

def at_end(toks, p):
    if p >= len(toks):
        return True
    t = toks[p]
    return (t[0] == "o" and t[1] == ":") or (t[0] == "k" and t[1] == "ELSE")

def done_stmt(st, toks, p):
    if not at_end(toks, p):
        return "SN"
    st["pos"] = p
    return ""

def is_op(toks, p, op):
    return p < len(toks) and toks[p][0] == "o" and toks[p][1] == op

def is_kw(toks, p, word):
    return p < len(toks) and toks[p][0] == "k" and toks[p][1] == word

def line_number(toks, p):
    if p >= len(toks) or toks[p][0] != "n":
        return -1
    v = toks[p][1]
    if type(v) == "tuple":
        return -1
    return floor_int(to_float(v))

# Evaluate "( e , e , ... )" starting at the open parenthesis. Returns (values, next position, error).
def paren_args(st, toks, p):
    if not is_op(toks, p, "("):
        return ([], p, "SN")
    vals = []
    q = p + 1
    for _ in range(len(toks) + 1):
        r = eval_at(st, toks, q)
        if r[2] != "":
            return ([], q, r[2])
        vals.append(r[0])
        q = r[1]
        if is_op(toks, q, ","):
            q += 1
        elif is_op(toks, q, ")"):
            return (vals, q + 1, "")
        else:
            return ([], q, "SN")
    return ([], q, "SN")

# A variable reference at toks[p]: ("name", subscripts or None). Returns (key, subs, next position, error).
def parse_target(st, toks, p):
    if p >= len(toks) or toks[p][0] != "v":
        return ("", None, p, "SN")
    key = var_key(st, toks[p][1], toks[p][2])
    q = p + 1
    if is_op(toks, q, "("):
        r = paren_args(st, toks, q)
        if r[2] != "":
            return ("", None, q, r[2])
        return (key, r[0], r[1], "")
    return (key, None, q, "")

def assign(st, key, subs, v):
    kind = key_kind(key)
    cv = coerce(kind, v)
    if cv[1] != "":
        return cv[1]
    if kind == 3 and len(cv[0]) > 255:
        return "LS"
    if subs == None:
        st["vars"][key] = cv[0]
        return ""
    loc = locate(st, key, subs)
    if loc[2] != "":
        return loc[2]
    loc[0][1][loc[1]] = cv[0]
    return ""

def number_arg(v):
    if kind_of(v) == 3:
        return (0, "TM")
    return (floor_int(to_float(v)), "")

# ---------------------------------------------------------------- statements

def st_print(st, toks, pos):
    n = len(toks)
    p = pos
    if is_op(toks, p, "@"):
        r = eval_at(st, toks, p + 1)
        if r[2] != "":
            return r[2]
        loc = number_arg(r[0])
        if loc[1] != "":
            return loc[1]
        if loc[0] < 0 or loc[0] >= CELLS:
            return "FC"
        set_cursor(st, loc[0])
        p = r[1]
        if is_op(toks, p, ",") or is_op(toks, p, ";"):
            p += 1
    if is_kw(toks, p, "USING"):
        return print_using(st, toks, p + 1)
    newline_needed = True
    for _ in range(n + 1):
        if at_end(toks, p):
            break
        t = toks[p]
        if t[0] == "o" and t[1] == ";":
            newline_needed = False
            p += 1
            continue
        if t[0] == "o" and t[1] == ",":
            newline_needed = False
            col = st["col"]
            if col >= 48:
                put_text(st, "\n")
            else:
                put_text(st, " " * (16 - col % 16))
            p += 1
            continue
        newline_needed = True
        if t[0] == "k" and (t[1] == "TAB(" or t[1] == "SPC("):
            r = eval_at(st, toks, p + 1)
            if r[2] != "":
                return r[2]
            if not is_op(toks, r[1], ")"):
                return "SN"
            c = number_arg(r[0])
            if c[1] != "":
                return c[1]
            if c[0] < 0 or c[0] > 255:
                return "FC"
            if t[1] == "TAB(":
                if c[0] > st["col"]:
                    put_text(st, " " * (c[0] - st["col"]))
            else:
                put_text(st, " " * c[0])
            p = r[1] + 1
            continue
        r = eval_at(st, toks, p)
        if r[2] != "":
            return r[2]
        v = r[0]
        if kind_of(v) == 3:
            put_text(st, v)
        else:
            put_text(st, print_text(v))
        p = r[1]
    if newline_needed:
        put_text(st, "\n")
    return done_stmt(st, toks, p)

def print_using(st, toks, pos):
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    if kind_of(r[0]) != 3:
        return "TM"
    fmt = r[0]
    p = r[1]
    if not (is_op(toks, p, ";") or is_op(toks, p, ",")):
        return "SN"
    p += 1
    values = []
    trailing = False
    for _ in range(len(toks) + 1):
        if at_end(toks, p):
            break
        e = eval_at(st, toks, p)
        if e[2] != "":
            return e[2]
        values.append(e[0])
        p = e[1]
        trailing = False
        if is_op(toks, p, ";") or is_op(toks, p, ","):
            p += 1
            trailing = True
        elif not at_end(toks, p):
            return "SN"
    f = format_using(fmt, values)
    if f[1] != "":
        return f[1]
    put_text(st, f[0])
    if not trailing:
        put_text(st, "\n")
    return done_stmt(st, toks, p)

def st_let(st, toks, pos):
    t = parse_target(st, toks, pos)
    if t[3] != "":
        return t[3]
    p = t[2]
    if not is_op(toks, p, "="):
        return "SN"
    r = eval_at(st, toks, p + 1)
    if r[2] != "":
        return r[2]
    err = assign(st, t[0], t[1], r[0])
    if err != "":
        return err
    return done_stmt(st, toks, r[1])

def goto_line(st, line):
    if line < 0:
        return "SN"
    return jump_to(st, line)

def st_goto(st, toks, pos):
    return goto_line(st, line_number(toks, pos))

def st_gosub(st, toks, pos):
    line = line_number(toks, pos)
    if line < 0:
        return "SN"
    if line not in st["program"]:
        return "UL"
    if len(st["gosub"]) >= 400:
        return "OM"
    st["gosub"].append((st["line"], pos + 1))
    return jump_to(st, line)

def st_return(st, toks, pos):
    if len(st["gosub"]) == 0:
        return "RG"
    back = st["gosub"].pop()
    load_line(st, back[0])
    st["pos"] = back[1]
    return ""

def st_if(st, toks, pos):
    n = len(toks)
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    if kind_of(r[0]) == 3:
        return "TM"
    truth = to_float(r[0]) != 0.0
    p = r[1]
    if is_kw(toks, p, "THEN"):
        p += 1
    elif not is_kw(toks, p, "GOTO"):
        return "SN"
    if not truth:
        depth = 0
        q = p
        found = -1
        for _ in range(n + 1):
            if q >= n:
                break
            if toks[q] == ("k", "IF"):
                depth += 1
            elif toks[q] == ("k", "ELSE"):
                if depth == 0:
                    found = q
                    break
                depth -= 1
            q += 1
        if found < 0:
            return next_line(st)
        p = found + 1
    if p < n and toks[p][0] == "n":
        return goto_line(st, line_number(toks, p))
    st["pos"] = p
    return ""

def st_else(st, toks, pos):
    # Reached only by running into the ELSE of an IF whose THEN part has just finished: skip the rest of the line.
    return next_line(st)

def st_for(st, toks, pos):
    t = parse_target(st, toks, pos)
    if t[3] != "":
        return t[3]
    if t[1] != None:
        return "SN"
    key = t[0]
    if key_kind(key) == 3:
        return "TM"
    p = t[2]
    if not is_op(toks, p, "="):
        return "SN"
    r1 = eval_at(st, toks, p + 1)
    if r1[2] != "":
        return r1[2]
    if not is_kw(toks, r1[1], "TO"):
        return "SN"
    r2 = eval_at(st, toks, r1[1] + 1)
    if r2[2] != "":
        return r2[2]
    step = 1
    p = r2[1]
    if is_kw(toks, p, "STEP"):
        r3 = eval_at(st, toks, p + 1)
        if r3[2] != "":
            return r3[2]
        step = r3[0]
        p = r3[1]
    if kind_of(r1[0]) == 3 or kind_of(r2[0]) == 3 or kind_of(step) == 3:
        return "TM"
    err = assign(st, key, None, r1[0])
    if err != "":
        return err
    if not at_end(toks, p):
        return "SN"
    frames = [f for f in st["fors"] if f[0] != key]
    if len(frames) >= 100:
        return "OM"
    frames.append((key, r2[0], step, st["line"], p))
    st["fors"] = frames
    st["pos"] = p
    return ""

def st_next(st, toks, pos):
    n = len(toks)
    p = pos
    keys = []
    for _ in range(n + 1):
        if p < n and toks[p][0] == "v":
            keys.append(var_key(st, toks[p][1], toks[p][2]))
            p += 1
            if is_op(toks, p, ","):
                p += 1
                continue
        break
    if len(keys) == 0:
        keys = [""]
    for key in keys:
        fors = st["fors"]
        f = -1
        if key == "":
            f = len(fors) - 1
        else:
            for k in range(len(fors)):
                if fors[k][0] == key:
                    f = k
        if f < 0:
            return "NF"
        frame = fors[f]
        st["fors"] = fors[:f + 1]
        cur = st["vars"].get(frame[0])
        if cur == None:
            cur = zero_of(key_kind(frame[0]))
        s = binop("+", cur, frame[2])
        if s[1] != "":
            return s[1]
        cv = coerce(key_kind(frame[0]), s[0])
        if cv[1] != "":
            return cv[1]
        st["vars"][frame[0]] = cv[0]
        step = to_float(frame[2])
        value = to_float(cv[0])
        limit = to_float(frame[1])
        if (step >= 0.0 and value <= limit) or (step < 0.0 and value >= limit):
            load_line(st, frame[3])
            st["pos"] = frame[4]
            return ""
        st["fors"] = fors[:f]
    return done_stmt(st, toks, p)

def st_on(st, toks, pos):
    if is_kw(toks, pos, "ERROR"):
        if not is_kw(toks, pos + 1, "GOTO"):
            return "SN"
        line = line_number(toks, pos + 2)
        if line < 0:
            return "SN"
        if line != 0 and line not in st["program"]:
            return "UL"
        st["onerr"] = line
        return done_stmt(st, toks, pos + 3)
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    sel = number_arg(r[0])
    if sel[1] != "":
        return sel[1]
    p = r[1]
    if is_kw(toks, p, "GOTO"):
        sub = False
    elif is_kw(toks, p, "GOSUB"):
        sub = True
    else:
        return "SN"
    p += 1
    lines = []
    for _ in range(len(toks) + 1):
        ln = line_number(toks, p)
        if ln < 0:
            return "SN"
        lines.append(ln)
        p += 1
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    if sel[0] < 0 or sel[0] > 255:
        return "FC"
    if sel[0] == 0 or sel[0] > len(lines):
        return done_stmt(st, toks, p)
    target = lines[sel[0] - 1]
    if sub:
        if target not in st["program"]:
            return "UL"
        st["gosub"].append((st["line"], p))
    return jump_to(st, target)

def st_input(st, toks, pos):
    if st["line"] == 0:
        return "ID"
    p = pos
    prompt = "? "
    if p < len(toks) and toks[p][0] == "s":
        prompt = toks[p][1]
        p += 1
        if is_op(toks, p, ";"):
            prompt += "? "
            p += 1
        elif is_op(toks, p, ","):
            p += 1
        else:
            return "SN"
    targets = []
    for _ in range(len(toks) + 1):
        t = parse_target(st, toks, p)
        if t[3] != "":
            return t[3]
        targets.append((t[0], t[1]))
        p = t[2]
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    if not at_end(toks, p):
        return "SN"
    st["pos"] = p
    st["waiting"] = {"targets": targets, "fields": [], "prompt": prompt}
    put_text(st, prompt)
    return ""

# Called with a typed line when the program is waiting at an INPUT.
def feed_input(st, text):
    w = st["waiting"]
    if w == None:
        return
    line = text.rstrip("\r\n")
    if st["echo"]:
        put_text(st, line + "\n")
    fields = w["fields"] + split_fields(line)
    targets = w["targets"]
    if len(fields) < len(targets):
        w["fields"] = fields
        put_text(st, "?? ")
        return
    if len(fields) > len(targets):
        put_text(st, "?EXTRA IGNORED\n")
    values = []
    for i in range(len(targets)):
        f = fields[i]
        if key_kind(targets[i][0]) == 3:
            values.append(f[0])
        else:
            r = parse_number_text(f[0]) if not f[1] else (False, 0)
            if not r[0]:
                put_text(st, "?REDO FROM START\n")
                w["fields"] = []
                put_text(st, w["prompt"])
                return
            values.append(r[1])
    st["waiting"] = None
    for i in range(len(targets)):
        err = assign(st, targets[i][0], targets[i][1], values[i])
        if err != "":
            handle_error(st, err)
            return

def st_read(st, toks, pos):
    p = pos
    for _ in range(len(toks) + 1):
        t = parse_target(st, toks, p)
        if t[3] != "":
            return t[3]
        if st["dptr"] >= len(st["data"]):
            return "OD"
        item = st["data"][st["dptr"]]
        st["dptr"] += 1
        if key_kind(t[0]) == 3:
            v = item[0]
        else:
            r = parse_number_text(item[0]) if not item[1] else (False, 0)
            if not r[0]:
                st["errat"] = item[2]   # Level II blames the DATA line, not the READ
                return "SN"
            v = r[1]
        err = assign(st, t[0], t[1], v)
        if err != "":
            return err
        p = t[2]
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    return done_stmt(st, toks, p)

def st_data(st, toks, pos):
    return done_stmt(st, toks, pos + 1)

def st_restore(st, toks, pos):
    st["dptr"] = 0
    return done_stmt(st, toks, pos)

def st_dim(st, toks, pos):
    p = pos
    for _ in range(len(toks) + 1):
        if p >= len(toks) or toks[p][0] != "v" or not is_op(toks, p + 1, "("):
            return "SN"
        key = var_key(st, toks[p][1], toks[p][2])
        r = paren_args(st, toks, p + 1)
        if r[2] != "":
            return r[2]
        dims = []
        for d in r[0]:
            v = number_arg(d)
            if v[1] != "":
                return v[1]
            if v[0] < 0:
                return "FC"
            dims.append(v[0])
        if key in st["arrays"]:
            return "DD"
        err = new_array(st, key, dims)
        if err != "":
            return err
        p = r[1]
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    return done_stmt(st, toks, p)

def st_rem(st, toks, pos):
    return next_line(st)

def st_end(st, toks, pos):
    st["running"] = False
    st["cont"] = None
    ready(st)
    return ""

def st_stop(st, toks, pos):
    if st["col"] != 0:
        newline(st)
    put_text(st, "BREAK" + ((" IN %d" % st["line"]) if st["line"] != 0 else "") + "\n")
    st["cont"] = (st["line"], pos)
    stop(st)
    return ""

def st_cls(st, toks, pos):
    clear_screen(st)
    return done_stmt(st, toks, pos)

def st_set(st, toks, pos):
    return pixel(st, toks, pos, True)

def st_reset(st, toks, pos):
    return pixel(st, toks, pos, False)

def pixel(st, toks, pos, on):
    r = paren_args(st, toks, pos)
    if r[2] != "":
        return r[2]
    if len(r[0]) != 2:
        return "SN"
    x = number_arg(r[0][0])
    y = number_arg(r[0][1])
    if x[1] != "" or y[1] != "":
        return "TM"
    if x[0] < 0 or x[0] > 127 or y[0] < 0 or y[0] > 47:
        return "FC"
    set_pixel(st, x[0], y[0], on)
    return done_stmt(st, toks, r[1])

def st_poke(st, toks, pos):
    r1 = eval_at(st, toks, pos)
    if r1[2] != "":
        return r1[2]
    if not is_op(toks, r1[1], ","):
        return "SN"
    r2 = eval_at(st, toks, r1[1] + 1)
    if r2[2] != "":
        return r2[2]
    a = number_arg(r1[0])
    v = number_arg(r2[0])
    if a[1] != "" or v[1] != "":
        return "TM"
    addr = a[0]
    if addr < 0:
        addr += 65536
    if addr < 0 or addr > 65535 or v[0] < 0 or v[0] > 255:
        return "FC"
    if addr >= VIDEO_BASE and addr < VIDEO_BASE + CELLS:
        st["screen"][addr - VIDEO_BASE] = v[0]
    else:
        st["mem"][addr] = v[0]
    return done_stmt(st, toks, r2[1])

def st_random(st, toks, pos):
    st["seed"] = (st["seed"] * 3 + 17 + st["steps"]) % 2147483648
    return done_stmt(st, toks, pos)

def st_tron(st, toks, pos):
    st["trace"] = True
    return done_stmt(st, toks, pos)

def st_troff(st, toks, pos):
    st["trace"] = False
    return done_stmt(st, toks, pos)

def deftype_statement(kind):
    def run_it(st, toks, pos):
        p = pos
        for _ in range(len(toks) + 1):
            if p >= len(toks) or toks[p][0] != "v" or len(toks[p][1]) != 1 or toks[p][2] != "":
                return "SN"
            lo = LETTERS.find(toks[p][1])
            hi = lo
            p += 1
            if is_op(toks, p, "-"):
                if p + 1 >= len(toks) or toks[p + 1][0] != "v" or len(toks[p + 1][1]) != 1:
                    return "SN"
                hi = LETTERS.find(toks[p + 1][1])
                p += 2
            for c in range(lo, hi + 1):
                st["deftype"][LETTERS[c]] = kind
            if is_op(toks, p, ","):
                p += 1
            else:
                break
        return done_stmt(st, toks, p)
    return run_it

def st_def(st, toks, pos):
    if st["line"] == 0:
        return "ID"
    if not is_kw(toks, pos, "FN") or pos + 1 >= len(toks) or toks[pos + 1][0] != "v":
        return "SN"
    name = toks[pos + 1][1] + toks[pos + 1][2]
    p = pos + 2
    params = []
    if is_op(toks, p, "("):
        p += 1
        for _ in range(len(toks) + 1):
            if p >= len(toks) or toks[p][0] != "v":
                return "SN"
            params.append(var_key(st, toks[p][1], toks[p][2]))
            p += 1
            if is_op(toks, p, ","):
                p += 1
            else:
                break
        if not is_op(toks, p, ")"):
            return "SN"
        p += 1
    if not is_op(toks, p, "="):
        return "SN"
    r = compile_expr(toks, p + 1)
    if r[2] != "":
        return r[2]
    st["fns"][name] = (params, r[0])
    return done_stmt(st, toks, r[1])

def st_clear(st, toks, pos):
    p = pos
    if not at_end(toks, p):
        r = eval_at(st, toks, p)
        if r[2] != "":
            return r[2]
        p = r[1]
    st["vars"] = {}
    st["arrays"] = {}
    st["gosub"] = []
    st["fors"] = []
    return done_stmt(st, toks, p)

def st_error(st, toks, pos):
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    c = number_arg(r[0])
    if c[1] != "":
        return c[1]
    if c[0] < 1 or c[0] > 255:
        return "FC"
    if c[0] <= len(ERRORS):
        return ERRORS[c[0] - 1]
    return "UE"

def st_resume(st, toks, pos):
    if not st["inerr"]:
        return "RW"
    where = st["resume"]
    st["inerr"] = False
    p = pos
    if is_kw(toks, p, "NEXT"):
        load_line(st, where[0])
        t = st["toks"]
        q = where[1]
        found = len(t)
        for _ in range(len(t) + 1):
            if q >= len(t):
                break
            if t[q] == ("o", ":"):
                found = q + 1
                break
            q += 1
        st["pos"] = found
        return ""
    ln = line_number(toks, p)
    if ln > 0:
        return jump_to(st, ln)
    load_line(st, where[0])
    st["pos"] = where[1]
    return ""

# ---------------------------------------------------------------- commands (also usable as statements)

def reset_run_state(st):
    st["vars"] = {}
    st["arrays"] = {}
    st["fns"] = {}
    st["gosub"] = []
    st["fors"] = []
    st["onerr"] = 0
    st["inerr"] = False
    st["waiting"] = None
    st["cont"] = None
    st["deftype"] = {c: 1 for c in LETTERS.elems()}
    build_data(st)

def st_run(st, toks, pos):
    ensure_index(st)
    reset_run_state(st)
    if len(st["order"]) == 0:
        stop(st)
        return ""
    start = st["order"][0]
    if pos < len(toks) and toks[pos][0] == "n":
        start = line_number(toks, pos)
    return goto_line(st, start)

# EDIT n: start editing line n. From here until the editor finishes, every key typed goes to edit_key.
def st_edit(st, toks, pos):
    if st["line"] != 0:
        return "ID"     # EDIT takes over the keyboard, so it is a direct command only
    line = line_number(toks, pos)
    if line < 0 or not at_end(toks, pos + 1):
        return "SN"
    if line not in st["program"]:
        return "UL"
    st["edit"] = new_edit(line, display_text(st["program"][line]))
    put_text(st, "%d " % line)
    st["running"] = False
    return ""

# One key for the line editor. Returns "edit" while the editor is still open, otherwise "done".
def edit_key(st, key):
    e = st["edit"]
    if e == None:
        return "done"
    r = edit_step(e, key)
    if r[0] != "":
        put_text(st, r[0])
    if r[1] == "":
        return "edit"
    st["edit"] = None
    if r[1] == "save":
        text = edit_text(e)
        if text.strip() == "":
            st["program"].pop(e["line"], None)
        else:
            st["program"][e["line"]] = text
        st["tokcache"].pop(e["line"], None)
        st["rpn"] = {}
        st["dirty"] = True
        st["cont"] = None
    put_text(st, "\n")
    prompt_again(st)
    return "done"

# Whether the editor has the keyboard.
def editing(st):
    return st["edit"] != None

def st_new(st, toks, pos):
    st["program"] = {}
    st["tokcache"] = {}
    st["rpn"] = {}
    st["dirty"] = True
    reset_run_state(st)
    stop(st)
    return ""

def st_cont(st, toks, pos):
    if st["cont"] == None:
        return "CN"
    where = st["cont"]
    st["cont"] = None
    if where[0] != 0 and where[0] not in st["program"]:
        return "CN"
    load_line(st, where[0])
    st["pos"] = where[1]
    st["running"] = True
    return ""

# Parse an optional "n", "n-m", "-m" or "n-" after LIST or DELETE. Returns (low, high, error).
def line_range(toks, pos):
    lo = 0
    hi = 65535
    p = pos
    if p < len(toks) and toks[p][0] == "n":
        lo = line_number(toks, p)
        hi = lo
        p += 1
        if is_op(toks, p, "-"):
            hi = 65535
            p += 1
            if p < len(toks) and toks[p][0] == "n":
                hi = line_number(toks, p)
                p += 1
    elif is_op(toks, p, "-"):
        p += 1
        if p < len(toks) and toks[p][0] == "n":
            hi = line_number(toks, p)
            p += 1
        else:
            return (0, 0, "SN")
    if not at_end(toks, p):
        return (0, 0, "SN")
    return (lo, hi, "")

# Keywords and names in upper case, but text in quotes, after REM and in DATA exactly as typed.
def display_text(text):
    out = ""
    in_quotes = False
    n = len(text)
    i = 0
    u = text.upper()
    for _ in range(n + 1):
        if i >= n:
            break
        c = text[i]
        if c == "\"":
            in_quotes = not in_quotes
            out += c
            i += 1
        elif in_quotes:
            out += c
            i += 1
        elif u.startswith("REM", i) or c == "'":
            out += u[i:i + 3] + text[i + 3:] if c != "'" else text[i:]
            i = n
        elif u.startswith("DATA", i):
            end = n
            q = False
            for k in range(i + 4, n):
                if text[k] == "\"":
                    q = not q
                elif text[k] == ":" and not q:
                    end = k
                    break
            out += "DATA" + text[i + 4:end]
            i = end
        else:
            out += u[i]
            i += 1
    return out

def st_list(st, toks, pos):
    r = line_range(toks, pos)
    if r[2] != "":
        return r[2]
    ensure_index(st)
    for line in st["order"]:
        if line >= r[0] and line <= r[1]:
            put_text(st, "%d %s\n" % (line, display_text(st["program"][line])))
    stop(st)
    return ""

def st_delete(st, toks, pos):
    r = line_range(toks, pos)
    if r[2] != "":
        return r[2]
    removed = [l for l in st["program"].keys() if l >= r[0] and l <= r[1]]
    if len(removed) == 0:
        return "FC"
    for l in removed:
        st["program"].pop(l)
        st["tokcache"].pop(l, None)
    st["rpn"] = {}
    st["dirty"] = True
    stop(st)
    return ""

STATEMENTS = {
    "PRINT": st_print, "GOTO": st_goto, "GOSUB": st_gosub, "RETURN": st_return, "IF": st_if, "ELSE": st_else,
    "FOR": st_for, "NEXT": st_next, "ON": st_on, "INPUT": st_input, "READ": st_read, "DATA": st_data,
    "RESTORE": st_restore, "DIM": st_dim, "REM": st_rem, "END": st_end, "STOP": st_stop, "CLS": st_cls, "SET": st_set,
    "RESET": st_reset, "POKE": st_poke, "RANDOM": st_random, "TRON": st_tron, "TROFF": st_troff, "DEF": st_def,
    "DEFINT": deftype_statement(0), "DEFSNG": deftype_statement(1), "DEFDBL": deftype_statement(2),
    "DEFSTR": deftype_statement(3), "CLEAR": st_clear, "ERROR": st_error, "RESUME": st_resume, "RUN": st_run,
    "EDIT": st_edit, "NEW": st_new, "CONT": st_cont, "LIST": st_list, "DELETE": st_delete, "LET": st_let,
}

# ---------------------------------------------------------------- running

# Execute one statement (or step past the end of a line). Returns an error code or "".
def exec_stmt(st):
    toks = st["toks"]
    pos = st["pos"]
    if pos >= len(toks):
        if st["tokerr"] != "":
            return st["tokerr"]
        return next_line(st)
    t = toks[pos]
    if t[0] == "o" and t[1] == ":":
        st["pos"] = pos + 1
        return ""
    st["stmt_start"] = (st["line"], pos)
    if t[0] == "v":
        return st_let(st, toks, pos)
    if t[0] == "k":
        handler = STATEMENTS.get(t[1])
        if handler != None:
            if t[1] == "LET":
                return st_let(st, toks, pos + 1)
            return handler(st, toks, pos + 1)
    return "SN"

# Run up to `budget` statements. Returns "done" (back at the READY prompt), "waiting" (at an INPUT) or "budget" (more to do).
def run(st, budget):
    for _ in range(budget):
        if not st["running"] or st["waiting"] != None:
            break
        st["steps"] += 1
        err = exec_stmt(st)
        if err != "":
            handle_error(st, err)
    if st["waiting"] != None:
        return "waiting"
    if st["running"]:
        return "budget"
    return "done"

# Deal with one line typed at the keyboard: a program line is stored, anything else is run at once.
def enter_line(st, text):
    s = text.rstrip("\r\n").lstrip()
    if st["echo"]:
        put_text(st, s + "\n")
    if s == "":
        prompt_again(st)    # a bare Enter gets a fresh prompt, as on the real machine
        return
    k = span_digits(s, 0)
    if k > 0:
        num = int(s[:k])
        rest = s[k:].lstrip()
        if num > 65529:
            st["direct"] = ([], "")
            st["line"] = 0
            handle_error(st, "SN")
            return
        if rest == "":
            st["program"].pop(num, None)
            st["tokcache"].pop(num, None)
        else:
            st["program"][num] = rest
            st["tokcache"].pop(num, None)
        st["rpn"] = {}
        st["dirty"] = True
        st["cont"] = None
        prompt_again(st)    # storing a program line shows no READY, only a new prompt
        return
    toks = tokenize(s)
    st["direct"] = toks
    st["line"] = 0
    load_line(st, 0)
    st["pos"] = 0
    st["running"] = True

# Entry point for a typed line: answer a waiting INPUT, or enter a command or program line.
def submit(st, text, budget):
    if st["waiting"] != None:
        feed_input(st, text)
    elif not st["running"]:
        enter_line(st, text)
    return run(st, budget)

# After a fault in this script itself (a bug, not a BASIC error): drop whatever was running and show READY again. The program,
# variables and screen are left as they were.
def recover(st):
    st["running"] = False
    st["waiting"] = None
    st["cont"] = None
    ready(st)
    return "done"

# Whether typed lines are echoed into the output. A terminal shows what the user types by itself, so a terminal host turns this off;
# a test or a pipe has no echo of its own, so it stays on.
def set_echo(st, on):
    st["echo"] = on

# Queue keys for INKEY$.
def press_keys(st, keys):
    for ch in keys.elems():
        st["inkey"].append(ch)

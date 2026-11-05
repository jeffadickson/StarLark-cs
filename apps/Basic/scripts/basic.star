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

# Imports. numbers.star: number kinds and text conversion (kind_of gives 0 integer, 1 single, 2 double, 3 string). lex.star: tokenize. editor.star: the
# EDIT key state machine. expr.star: expression compile/evaluate, variable keys, arrays and type coercion. screen.star: the 16 x 64 screen, the
# cursor and PRINT USING. Token tuples used throughout this file: ("n", value) number, ("s", text) string, ("v", name, suffix) variable,
# ("k", WORD) keyword, ("o", op) operator or punctuation.
load("numbers.star", "INT_MAX", "INT_MIN", "floor_int", "to_float", "kind_of", "span_digits", "parse_number_text", "print_text")
load("lex.star", "tokenize")
load("editor.star", "new_edit", "edit_step", "edit_text", "edit_head")
load("expr.star", "eval_at", "compile_expr", "var_key", "key_kind", "zero_of", "coerce", "locate", "new_array", "binop")
load("screen.star", "new_screen", "put_text", "newline", "clear_screen", "set_cursor", "set_pixel", "screen_lines",
     "format_using", "COLUMNS", "CELLS", "VIDEO_BASE")

# The two-letter error codes in the order of the BASIC `ERROR n` statement: ERROR 1 is "NF", ERROR 2 is "SN", and so on. `ERR` reports twice the
# position here, as Level II did (its codes were even numbers). A statement that fails returns one of these strings; handle_error prints it.
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

# The alphabet, used to build the per-letter default-type table (DEFINT and friends) and to turn a letter into a table index with .find().
LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"

# Design of this section. All state lives in one dict, `st`, created once by new_state() and passed as the first argument to every function.
# Nothing is global and nothing is mutable at module level, so two machines can exist side by side. Almost everything below returns "" for success
# or a two-letter error code from ERRORS; the code travels up to run(), which hands it to handle_error().
# ---------------------------------------------------------------- the machine

# Build a fresh machine and show the power-on banner and the READY prompt. The machine is this one dict `st`; every other function takes it first.
# The keys fall into groups: the stored program and its caches, variables and arrays, where execution is (line, toks, pos), the two control
# stacks (gosub, fors), DATA, the screen, the error handler, and the line editor.
def new_state():
    # `st` is the whole machine. The groups of keys are described next to each group.
    st = {
        # The stored program. `program` maps a line number to the program text after the number. `order` and `index` are derived from it (see
        # ensure_index) and are only valid while `dirty` is False. `tokcache` and `rpn` are caches of work already done on the text: they must be emptied
        # or trimmed whenever program text changes, or a stale line would run.
        "program": {},          # line number -> text after the number
        "order": [],            # line numbers in order
        "index": {},            # line number -> position in order
        "dirty": False,         # order/index need rebuilding
        "tokcache": {},         # line -> (tokens, error)
        # Compiled expressions (see eval_at in expr.star), keyed by line * 4096 + token position so the same expression is compiled only once per line. Any
        # edit of any line empties the whole dict, because the keys depend on token positions.
        "rpn": {},              # compiled expressions, keyed by line * 4096 + token position
        # Variables: `vars` maps a key from var_key (name plus type suffix, for example "A!" or "N$") to its value. `arrays` maps the same sort of key to
        # [dims, flat list of elements] (see new_array/locate). `fns` maps a DEF FN function name to (list of parameter keys, compiled expression).
        "vars": {}, "arrays": {}, "fns": {},
        # The default type of an untyped variable, by first letter: 0 integer, 1 single, 2 double, 3 string. Every letter starts as 1 (single precision);
        # DEFINT, DEFSNG, DEFDBL and DEFSTR change entries.
        "deftype": {c: 1 for c in LETTERS.elems()},
        # Where execution is. `line` is the BASIC line number being executed, with 0 meaning direct mode (a command typed without a line number). `toks` and
        # `tokerr` are that line's tokens and its tokenizer error ("" if none). `pos` is the index in `toks` of the next token to run. `direct` is the
        # (tokens, error) pair for the last typed command, which load_line(st, 0) uses.
        "line": 0, "toks": [], "tokerr": "", "pos": 0, "direct": ([], ""),
        # `running` is True while the run loop should keep executing statements; it is also True while a direct command executes. `waiting` is None, or
        # a dict {"targets", "fields", "prompt"} while a program sits at an INPUT (see st_input and feed_input). `cont` is None, or the (line, pos) at which
        # CONT carries on after a BREAK or STOP.
        "running": False, "waiting": None, "cont": None,
        # The two control stacks, both plain lists used as stacks. `gosub` holds return points as (line, pos) pairs. `fors` holds one frame per active
        # FOR loop as (variable key, limit, step, line, pos), where line and pos are the place just after the FOR statement, which NEXT jumps back to.
        "gosub": [], "fors": [],
        # The DATA values of the whole program, as (text, was quoted, line number) tuples built by build_data, and the index of the next one READ will take.
        "data": [], "dptr": 0,
        # Output. `screen` is a list of 1024 character codes (16 rows of 64). `cur` is the cursor as a cell number 0..1023 and `col` is its column,
        # cur % 64. `out` collects every string printed since the host last called take_output. When `quiet` is True put_text does not add to `out`
        # (the screen still changes; nothing in this file sets it, so it is for the host). `echo` says whether typed lines are printed back (see set_echo).
        "screen": new_screen(), "cur": 0, "col": 0, "out": [], "quiet": False, "echo": True,
        # Odds and ends. `mem` holds bytes POKEd outside video memory, address -> value, which PEEK reads back. `inkey` is the queue of keys for INKEY$ (fed by
        # press_keys). `seed` is the state of the RND generator. `trace` is TRON/TROFF.
        "mem": {}, "inkey": [], "seed": 12345, "trace": False,
        # Line editor and error handling. `edit` is None, or the editor's state dict while EDIT has the keyboard. `onerr` is the line of the ON ERROR GOTO
        # handler (0 means none). `inerr` is True while that handler runs. `errline` and `errcode` are what ERL and ERR report. `resume` is where
        # RESUME goes (the start of the statement that failed) and `stmt_start` is the (line, pos) of the statement now executing, copied into `resume` when
        # an error is caught.
        "edit": None, "onerr": 0, "inerr": False, "errline": 0, "errcode": 0, "resume": (0, 0), "stmt_start": (0, 0),
        # Count of statements executed so far. Only RANDOM uses it, to make each reseed different.
        "steps": 0,
    }
    # Print the power-on banner, then the READY prompt, so a new machine looks like a freshly switched-on TRS-80.
    put_text(st, "RADIO SHACK LEVEL II BASIC\n\n")
    ready(st)
    return st

# Return everything printed since the last call as one string, and empty the buffer. The host calls this after each step to show the user.
def take_output(st):
    text = "".join(st["out"])
    st["out"] = []
    return text

# The 16 lines of the 64-column screen as a list of strings, trailing spaces removed (see screen_lines).
def get_screen(st):
    return screen_lines(st)

# Print "READY" and the ">" prompt on a fresh line. The end of every command, program run and error.
def ready(st):
    if st["col"] != 0:
        newline(st)
    put_text(st, "READY\n>")

# Just the ">" prompt on a new line, with no READY: shown after a program line is stored and after a bare Enter.
def prompt_again(st):
    if st["col"] != 0:
        newline(st)
    put_text(st, ">")

# Leave run mode and show READY. Used when a program ends, runs off its last line, or hits an error.
def stop(st):
    st["running"] = False
    ready(st)

# Design of this section. A program is a dict of line number -> source text. Source text is only tokenized when a line first runs (get_toks) and
# the result is cached. Running in line order needs the sorted list of numbers, which is rebuilt lazily (ensure_index) only after an edit has set
# `dirty`. Execution position is the pair st["line"], st["pos"]; jumping means changing that pair and loading the target line's tokens.
# ---------------------------------------------------------------- program storage

# Rebuild `order` (line numbers sorted) and `index` (line number -> position) if the program changed since they were built. Cheap to call often.
def ensure_index(st):
    if st["dirty"]:
        # Sort the line numbers, then build the reverse lookup: line number -> its position in the sorted list. next_line uses it to find the following line.
        order = sorted(st["program"].keys())
        st["order"] = order
        st["index"] = {order[i]: i for i in range(len(order))}
        st["dirty"] = False

# The tokens of a program line, tokenized on first use and cached in `tokcache`. The cache entry is dropped whenever the line changes.
def get_toks(st, line):
    # `hit` is the cached (tokens, error) pair from tokenize, or None if this line has not been tokenized since it last changed.
    hit = st["tokcache"].get(line)
    if hit == None:
        hit = tokenize(st["program"][line])
        st["tokcache"][line] = hit
    return hit

# Make `line` the line being executed: load its tokens into `toks`/`tokerr`. Line 0 is direct mode and uses the tokens of the typed command.
def load_line(st, line):
    # Line 0 is direct mode: there is no program text, so use the tokens of the command the user just typed.
    if line == 0:
        st["toks"] = st["direct"][0]
        st["tokerr"] = st["direct"][1]
    else:
        # A program line: take its cached tokens. t is the (tokens, error) pair.
        t = get_toks(st, line)
        st["toks"] = t[0]
        st["tokerr"] = t[1]
    st["line"] = line

# Go to the start of program line `line` (GOTO, GOSUB, RUN, ON...GOTO). Returns "UL" (undefined line) if it does not exist, otherwise "".
# With TRON on, also prints the line number in square brackets.
def jump_to(st, line):
    if line not in st["program"]:
        return "UL"
    # The target exists (checked above, so a bad target leaves the position unchanged). Load its tokens and start at its first token, position 0.
    load_line(st, line)
    st["pos"] = 0
    # TRON: show each line that is jumped to. Note this runs for jumps (GOTO, GOSUB, RUN, ON...GOTO), and for next_line, which also goes through
    # jump_to; it does not run for RETURN or NEXT, which use load_line directly.
    if st["trace"]:
        put_text(st, "[%d]" % line)
    return ""

# Fall through to the line after the current one. In direct mode, or after the last line, the run ends instead.
def next_line(st):
    if st["line"] == 0:
        stop(st)
        return ""
    ensure_index(st)
    # The position of the current line in `order`. It is None if the current line is not in the program (for example a deleted line). Direct mode
    # (line 0) never gets this far: it was ended just above, since a typed command has no following line.
    i = st["index"].get(st["line"])
    # No current line, or the last line has just finished: the program ends (and READY is shown).
    if i == None or i + 1 >= len(st["order"]):
        stop(st)
        return ""
    # Carry on at the next higher line number.
    return jump_to(st, st["order"][i + 1])

# Rebuild the DATA list from the program text.
def build_data(st):
    ensure_index(st)
    items = []
    for line in st["order"]:
        # Only the token list is needed here ([1] would be the tokenizer error, which is ignored when collecting DATA).
        toks = get_toks(st, line)[0]
        # Stop one short because a DATA keyword needs a following token. The tokenizer keeps the raw text of a DATA statement as one ("s", text) token
        # right after the DATA keyword, so the items are split from that text here.
        for k in range(len(toks) - 1):
            if toks[k] == ("k", "DATA") and toks[k + 1][0] == "s":
                for f in split_fields(toks[k + 1][1]):
                    items.append((f[0], f[1], line))   # remember the DATA line, which is where a bad item is reported
    st["data"] = items
    st["dptr"] = 0

# Split the text of an INPUT line or a DATA statement at commas outside quotes. Each field is (text, was quoted).
def split_fields(text):
    # split_fields works one character at a time. `cur` is the text of the field being built, `quoted` means this field has had an opening quote, and
    # `in_quotes` means the scan is now between a pair of quotes. A comma only ends a field outside quotes.
    fields = []
    cur = ""
    quoted = False
    in_quotes = False
    for ch in text.elems():
        # A quote mark never becomes part of the text. It flips in_quotes; the first quote of a field also throws away anything typed before it.
        if ch == "\"":
            if not quoted:
                cur = ""    # spaces before the opening quote are not part of the text
            in_quotes = not in_quotes
            quoted = True
        # End of a field. Unquoted text is trimmed of surrounding spaces; quoted text is kept exactly as it was between the quotes.
        elif ch == "," and not in_quotes:
            fields.append((cur if quoted else cur.strip(), quoted))
            cur = ""
            quoted = False
        elif quoted and not in_quotes:
            pass            # after the closing quote, up to the comma, is ignored
        else:
            cur += ch
    fields.append((cur if quoted else cur.strip(), quoted))
    # The final field has no comma after it, so it was appended just above rather than inside the loop. Empty text therefore gives one empty
    # field, not an empty list.
    return fields

# Design of this section. A BASIC error is not an exception: a statement function returns a two-letter code and run() passes it to handle_error.
# That function either diverts to the ON ERROR GOTO handler (remembering where RESUME should go) or prints the message and stops the program.
# ---------------------------------------------------------------- errors

# Report error `code`. If ON ERROR GOTO is active, jump to the handler and remember where to RESUME; otherwise print "?XX ERROR IN line" (no
# "IN" in direct mode) and stop. A bad DATA item sets `errat` so the message names its own line.
def handle_error(st, code):
    # Normally the error belongs to the current line. st_read sets the key "errat" (not part of the initial state dict) to the DATA line of a bad item;
    # pop reads it and removes it, so it only applies to this one error.
    line = st.pop("errat", st["line"])   # a bad DATA item names its own line
    # Record the error number for ERR/ERL. ERR is twice the zero-based position of the code in ERRORS; a code not in the list gets 40.
    st["errcode"] = ERRORS.index(code) * 2 if code in ERRORS else 40
    st["errline"] = line
    # Divert to the error handler only if one is set, we are not already inside it (an error inside the handler is fatal), and the error happened in a
    # program line (in direct mode there is nothing to resume).
    if st["onerr"] != 0 and not st["inerr"] and line != 0:
        # RESUME will go back to the start of the statement that failed.
        st["resume"] = st["stmt_start"]
        st["inerr"] = True
        # Handler line found and entered: the program carries on there. If the line has since been deleted jump_to returns "UL" and we fall through to
        # the normal report below (note that `inerr` stays True in that case).
        if jump_to(st, st["onerr"]) == "":
            return
    if st["col"] != 0:
        newline(st)
    # Message format: "?SN ERROR" in direct mode, "?SN ERROR IN 100" in a program. (Just above, a newline is started first if the cursor is part way
    # along a row, so the message does not follow other output.)
    text = "?" + code + " ERROR"
    if line != 0:
        text += " IN %d" % line
    # Print the message. Then forget the CONT point (an error ends the program for good, so CONT has nothing to carry on from) and show READY.
    put_text(st, text + "\n")
    st["cont"] = None
    stop(st)

# The BREAK key. Prints "BREAK IN line", remembers where the program was so CONT can carry on, and stops. Does nothing if nothing is running.
def break_program(st):
    if not st["running"]:
        return
    if st["col"] != 0:
        newline(st)
    put_text(st, "BREAK" + ((" IN %d" % st["line"]) if st["line"] != 0 else "") + "\n")
    # Remember where the program was. st["pos"] is the next token to run, so CONT carries on from there. The INPUT that may have been waiting is
    # abandoned (`waiting` is cleared on the next line) before READY is shown.
    st["cont"] = (st["line"], st["pos"])
    st["waiting"] = None
    stop(st)

# Design of this section. Every st_* statement function has the signature (st, toks, pos): `toks` is the current line's tokens and `pos` is the
# index just after the statement's keyword. It returns "" or an error code, and on success must leave st["pos"] pointing at the next token to run
# (normally by returning done_stmt(...)). The helpers below parse the pieces statements share. Each loop here is written `for _ in range(len(toks) + 1)`
# because Starlark has no `while`: every pass consumes at least one token, so len(toks) + 1 passes is more than can ever be needed.
# ---------------------------------------------------------------- statement helpers

# True when the next token ends a statement: the end of the line, a colon, or ELSE.
def at_end(toks, p):
    if p >= len(toks):
        return True
    t = toks[p]
    # Colon and ELSE both close a statement; ELSE is included so that the THEN part of an IF stops at it.
    return (t[0] == "o" and t[1] == ":") or (t[0] == "k" and t[1] == "ELSE")

# Finish a statement: check nothing but the end of the statement is left, then record `p` as where execution continues. Returns "SN" if
# there is junk after the statement, otherwise "". Almost every st_* function returns through this.
def done_stmt(st, toks, p):
    if not at_end(toks, p):
        return "SN"
    st["pos"] = p
    return ""

# True if the token at position p is the operator or punctuation mark `op` (for example "," or "(").
def is_op(toks, p, op):
    return p < len(toks) and toks[p][0] == "o" and toks[p][1] == op

# True if the token at position p is the keyword `word` (for example "THEN").
def is_kw(toks, p, word):
    return p < len(toks) and toks[p][0] == "k" and toks[p][1] == word

# The line number written at toks[p] (a GOTO target, say) as an int, or -1 if the token is not a number or is a double precision number (any other number is rounded down).
def line_number(toks, p):
    if p >= len(toks) or toks[p][0] != "n":
        return -1
    v = toks[p][1]
    # A double precision number is held as a 1-tuple (see numbers.star), and a line number cannot be one.
    if type(v) == "tuple":
        return -1
    return floor_int(to_float(v))

# Evaluate "( e , e , ... )" starting at the open parenthesis. Returns (values, next position, error).
def paren_args(st, toks, p):
    # paren_args must be called at an open parenthesis; anything else is a syntax error.
    if not is_op(toks, p, "("):
        return ([], p, "SN")
    vals = []
    q = p + 1
    for _ in range(len(toks) + 1):
        # Evaluate one argument. r is (value, next position, error). eval_at stops at a comma or close parenthesis.
        r = eval_at(st, toks, q)
        if r[2] != "":
            return ([], q, r[2])
        vals.append(r[0])
        q = r[1]
        # After an argument: a comma means another follows, a close parenthesis ends the list (return the position after it), anything else is a syntax
        # error. If the tokens run out before a close parenthesis, the last branch returns "SN" as well.
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
    # The name token is ("v", name, suffix); var_key turns it into the dictionary key (name plus type suffix, using the default type for the first
    # letter if no suffix was typed). A "(" right after the name means an array element, so the subscripts are parsed; otherwise it is a plain variable
    # and the subscripts are None.
    if is_op(toks, q, "("):
        r = paren_args(st, toks, q)
        if r[2] != "":
            return ("", None, q, r[2])
        # Array element: the key, the list of evaluated subscripts, and the position after the ")".
        return (key, r[0], r[1], "")
    # Plain variable: no subscripts, and the position just after the name.
    return (key, None, q, "")

# Store `v` in the variable `key`, or in the array element `key(subs)`, converting it to the variable's type first. Returns an error code or "":
# "TM" for a type mismatch, "OV" for a number out of range, "LS" for a string over 255 characters, "BS" for a bad subscript.
def assign(st, key, subs, v):
    # The variable's type, from the suffix of the key: 0 integer, 1 single, 2 double, 3 string.
    kind = key_kind(key)
    # Convert the value to that type; coerce returns (converted value, error). A string into a number (or the reverse) gives "TM", a number too big gives "OV".
    cv = coerce(kind, v)
    if cv[1] != "":
        return cv[1]
    # Level II strings are at most 255 characters.
    if kind == 3 and len(cv[0]) > 255:
        return "LS"
    if subs == None:
        st["vars"][key] = cv[0]
        return ""
    # locate returns (array, flat position, error). An array used without DIM is created with every bound 10 on first use. The array is [dims, flat list],
    # so loc[0][1] is the flat list of elements and loc[1] the index of this element.
    loc = locate(st, key, subs)
    if loc[2] != "":
        return loc[2]
    loc[0][1][loc[1]] = cv[0]
    return ""

# A numeric argument as a whole number. Returns (int, "") or (0, "TM") if it is a string. Fractions round down, as BASIC does.
def number_arg(v):
    if kind_of(v) == 3:
        # A string is not a number: type mismatch. The 0 is only a placeholder; callers check the error first.
        return (0, "TM")
    # Round down to an integer (to_float widens integer, single and double values to a Python float first).
    return (floor_int(to_float(v)), "")

# Design of this section. One function per BASIC statement, all with the signature (st, toks, pos) described under "statement helpers" and all
# returning "" or an error code. Statements that jump (GOTO, GOSUB, RETURN, NEXT, IF, ON) set the position themselves; the rest finish with
# done_stmt. A function that fails must do so before changing state where it can, since the error handler may retry the statement with RESUME.
# ---------------------------------------------------------------- statements

# PRINT. Handles `@ position`, USING, and the separators: ";" joins, "," moves to the next 16-column field (or the next line from column 48 on),
# TAB( and SPC(. A newline is printed at the end unless the statement ends in ; or ,. Numbers print with a leading sign slot and a trailing space.
def st_print(st, toks, pos):
    n = len(toks)
    p = pos
    # PRINT @ n: put the cursor on screen cell n (0 to 1023, with 64 cells per row) before printing. A "," or ";" after the position is allowed and skipped.
    if is_op(toks, p, "@"):
        r = eval_at(st, toks, p + 1)
        if r[2] != "":
            return r[2]
        loc = number_arg(r[0])
        if loc[1] != "":
            return loc[1]
        # Only the 1024 cells of the screen exist, anything else is an illegal function call.
        if loc[0] < 0 or loc[0] >= CELLS:
            return "FC"
        set_cursor(st, loc[0])
        p = r[1]
        if is_op(toks, p, ",") or is_op(toks, p, ";"):
            p += 1
    # PRINT USING has its own routine; it handles the rest of the statement, including the final newline.
    if is_kw(toks, p, "USING"):
        return print_using(st, toks, p + 1)
    newline_needed = True
    for _ in range(n + 1):
        if at_end(toks, p):
            break
        t = toks[p]
        # ";" prints nothing: the next item follows straight on. `newline_needed` (True at the start) is set False by a separator and back to True by
        # every printed item, so only a statement whose last thing is ; or , leaves the cursor where it is. Each pass of this loop handles one item.
        if t[0] == "o" and t[1] == ";":
            newline_needed = False
            p += 1
            continue
        # "," moves to the next print field. The 64 columns are four fields of 16 starting at columns 0, 16, 32 and 48. From column 48 on there is no
        # next field on the row, so it goes to the next line instead. Otherwise it pads with spaces to the next multiple of 16.
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
        # TAB( and SPC( are keywords whose token already includes the open parenthesis, so the argument starts at p + 1 and must be followed by ")".
        if t[0] == "k" and (t[1] == "TAB(" or t[1] == "SPC("):
            r = eval_at(st, toks, p + 1)
            if r[2] != "":
                return r[2]
            if not is_op(toks, r[1], ")"):
                return "SN"
            c = number_arg(r[0])
            if c[1] != "":
                return c[1]
            # The count must be 0 to 255, otherwise "FC".
            if c[0] < 0 or c[0] > 255:
                return "FC"
            if t[1] == "TAB(":
                # TAB(n) only moves right: if the cursor is already at or past column n nothing is printed. SPC(n) always prints n spaces.
                if c[0] > st["col"]:
                    put_text(st, " " * (c[0] - st["col"]))
            else:
                put_text(st, " " * c[0])
            p = r[1] + 1
            continue
        r = eval_at(st, toks, p)
        if r[2] != "":
            return r[2]
        # An ordinary expression. Strings print as they are; numbers go through print_text, which adds a space for the sign in front and a space after.
        v = r[0]
        if kind_of(v) == 3:
            put_text(st, v)
        else:
            put_text(st, print_text(v))
        p = r[1]
    # End of the statement. Print the newline unless the last thing in the statement was ; or ,.
    if newline_needed:
        put_text(st, "\n")
    return done_stmt(st, toks, p)

# PRINT USING "format"; values: format each value by the template and print the result. A trailing ; or , suppresses the newline.
def print_using(st, toks, pos):
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    # The format must be a string expression.
    if kind_of(r[0]) != 3:
        return "TM"
    # After the format string a ";" or "," must come before the first value.
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
        # Collect all the values first (the list `values`), then format them together with format_using. `trailing` is True only if the value list ended
        # with a separator, which suppresses the newline. Here: evaluate one value; after it a ; or , is skipped (and sets `trailing`); anything else
        # must be the end of the statement.
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
    # format_using returns (text, error). Nothing is printed unless the whole format worked.
    f = format_using(fmt, values)
    if f[1] != "":
        return f[1]
    put_text(st, f[0])
    if not trailing:
        put_text(st, "\n")
    return done_stmt(st, toks, p)

# Assignment: `LET A=expr`, or just `A=expr`. The target may be an array element.
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
    # t is parse_target's (key, subscripts or None, next position, error) for the left side; r is eval_at's (value, next position, error) for the right.
    # assign does the type conversion and can fail with TM, OV, LS or BS; the statement then fails with that code.
    err = assign(st, t[0], t[1], r[0])
    if err != "":
        return err
    return done_stmt(st, toks, r[1])

# Jump to `line`, or "SN" if the number was not a valid line number.
def goto_line(st, line):
    if line < 0:
        return "SN"
    return jump_to(st, line)

# GOTO line.
def st_goto(st, toks, pos):
    return goto_line(st, line_number(toks, pos))

# GOSUB line: push the place to come back to (the line and the position after the number) and jump. At most 400 deep ("OM" beyond that).
def st_gosub(st, toks, pos):
    line = line_number(toks, pos)
    if line < 0:
        return "SN"
    if line not in st["program"]:
        return "UL"
    # The GOSUB stack is capped at 400 return points; deeper is "OM" (out of memory).
    if len(st["gosub"]) >= 400:
        return "OM"
    # The return point: this line and the position just after the line number (pos is the number's position), so any ": more statements" that follow
    # the GOSUB run after the RETURN. The target was checked above, so nothing is pushed for a failed GOSUB.
    st["gosub"].append((st["line"], pos + 1))
    return jump_to(st, line)

# RETURN: pop the saved place and carry on after the GOSUB. "RG" if there is no GOSUB to return to.
def st_return(st, toks, pos):
    if len(st["gosub"]) == 0:
        return "RG"
    # Return to the saved line and token position; load_line brings back that line's tokens.
    back = st["gosub"].pop()
    load_line(st, back[0])
    st["pos"] = back[1]
    return ""

# IF cond THEN ... [ELSE ...]. A true condition continues after THEN (a bare number there means GOTO that line). A false one skips to the
# matching ELSE, counting nested IFs, or to the next line if there is none.
def st_if(st, toks, pos):
    n = len(toks)
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    if kind_of(r[0]) == 3:
        return "TM"
    # Any non-zero number is true (a true comparison is -1, false is 0). A string condition is a type mismatch.
    truth = to_float(r[0]) != 0.0
    p = r[1]
    # After the condition, THEN or GOTO is required. For THEN, p moves past it; for GOTO p is left on the GOTO keyword, so the GOTO runs as the next statement.
    if is_kw(toks, p, "THEN"):
        p += 1
    elif not is_kw(toks, p, "GOTO"):
        return "SN"
    # False condition: scan the rest of the line for the ELSE that belongs to this IF. `depth` counts IFs opened after this one; an ELSE at depth 0
    # is ours and an ELSE at a deeper level closes one of those nested IFs.
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
        # No ELSE: the rest of the line is skipped, so the next statement is on the next line.
        if found < 0:
            return next_line(st)
        # Resume after the ELSE and run the false branch.
        p = found + 1
    # THEN 100 or ELSE 100 (a bare number) means GOTO 100. Anything else just continues with the statements at p.
    if p < n and toks[p][0] == "n":
        return goto_line(st, line_number(toks, p))
    st["pos"] = p
    return ""

# Reached by running into ELSE after a finished THEN part: the rest of the line belongs to the false branch, so skip it.
def st_else(st, toks, pos):
    # Reached only by running into the ELSE of an IF whose THEN part has just finished: skip the rest of the line.
    return next_line(st)

# FOR v = start TO limit [STEP s]. Sets the variable and pushes a loop frame (variable, limit, step, line, position). Starting a FOR on a
# variable that already has a frame removes that old frame and pushes the new one on top; other frames are left alone (NEXT is what discards inner loops). At most 100 loops ("OM").
def st_for(st, toks, pos):
    t = parse_target(st, toks, pos)
    if t[3] != "":
        return t[3]
    if t[1] != None:
        return "SN"
    # t is parse_target's result for the loop variable.
    # The loop variable: it must be a plain numeric variable. An array element (subscripts present) is a syntax error, a string variable a type mismatch.
    key = t[0]
    if key_kind(key) == 3:
        return "TM"
    p = t[2]
    if not is_op(toks, p, "="):
        return "SN"
    # Start value, then TO and the limit, then an optional STEP; any of them may be an error. STEP defaults to the integer 1.
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
    # Strings anywhere in the header are a type mismatch.
    if kind_of(r1[0]) == 3 or kind_of(r2[0]) == 3 or kind_of(step) == 3:
        return "TM"
    # Set the loop variable to the start value, converted to the variable's type.
    err = assign(st, key, None, r1[0])
    if err != "":
        return err
    if not at_end(toks, p):
        return "SN"
    # Drop any existing frame for the same variable: starting FOR I again restarts that loop rather than nesting it.
    frames = [f for f in st["fors"] if f[0] != key]
    if len(frames) >= 100:
        return "OM"
    # Push the frame. The limit and step are stored as evaluated now (a later change to the variables they came from has no effect). The place
    # stored is just after the FOR statement, which NEXT will jump back to. Nothing tests the limit yet: that is done by NEXT, so the body always runs once.
    frames.append((key, r2[0], step, st["line"], p))
    st["fors"] = frames
    st["pos"] = p
    return ""

# NEXT [v[,v...]]. Adds the step to the loop variable and jumps back to just after the FOR if the loop should go on, otherwise pops the frame.
# The test is made here, at NEXT, so a loop body always runs once, as on Level II. "NF" if there is no matching FOR.
def st_next(st, toks, pos):
    n = len(toks)
    p = pos
    # Collect the variables named after NEXT (NEXT I,J). `continue` after a comma goes round for the next name; anything else leaves the loop.
    keys = []
    for _ in range(n + 1):
        if p < n and toks[p][0] == "v":
            keys.append(var_key(st, toks[p][1], toks[p][2]))
            p += 1
            if is_op(toks, p, ","):
                p += 1
                continue
        break
    # Plain NEXT with no variable: the empty key stands for "the innermost loop".
    if len(keys) == 0:
        keys = [""]
    # Handle the names one at a time, in the order written: each loop is stepped, and if it has finished its frame is dropped and the next name is handled.
    for key in keys:
        fors = st["fors"]
        f = -1
        # Find the frame: the top of the stack for plain NEXT; otherwise the last frame for that variable (the loop does not stop at the first match).
        if key == "":
            f = len(fors) - 1
        else:
            for k in range(len(fors)):
                if fors[k][0] == key:
                    f = k
        if f < 0:
            return "NF"
        frame = fors[f]
        # Discard any loops nested inside this one. A NEXT of an outer variable ends the loops that were still open inside it.
        st["fors"] = fors[:f + 1]
        # The current value of the loop variable (zero of its type if it has none), plus the step by binop, then converted back to the variable's type by
        # coerce. An error from either is returned as the error of the NEXT statement.
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
        # Compare as floats: a negative step counts down and tests value >= limit; otherwise (zero or positive) it counts up and tests value <= limit.
        step = to_float(frame[2])
        value = to_float(cv[0])
        limit = to_float(frame[1])
        if (step >= 0.0 and value <= limit) or (step < 0.0 and value >= limit):
            # Loop again: go back to just after the FOR statement. Any remaining names after NEXT are not looked at.
            load_line(st, frame[3])
            st["pos"] = frame[4]
            return ""
        # The loop is finished: pop its frame and continue with the next name, or the end of the statement.
        st["fors"] = fors[:f]
    return done_stmt(st, toks, p)

# ON expr GOTO/GOSUB line,line,... picks the nth line (nothing happens if n is 0 or too big). Also ON ERROR GOTO line, which sets the error handler.
def st_on(st, toks, pos):
    # ON ERROR GOTO line. A line of 0 turns the handler off; any other line must exist now. It is stored in st["onerr"].
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
    # ON n GOTO/GOSUB: n picks which line number in the list to use, counting from 1.
    sel = number_arg(r[0])
    if sel[1] != "":
        return sel[1]
    p = r[1]
    # `sub` remembers whether it was GOSUB, to push a return point later.
    if is_kw(toks, p, "GOTO"):
        sub = False
    elif is_kw(toks, p, "GOSUB"):
        sub = True
    else:
        return "SN"
    p += 1
    # Read the whole comma-separated list of line numbers first.
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
    # A selector outside 0 to 255 is an illegal function call, but one that is merely 0 or past the end of the list does nothing.
    if sel[0] < 0 or sel[0] > 255:
        return "FC"
    if sel[0] == 0 or sel[0] > len(lines):
        return done_stmt(st, toks, p)
    # Pick the line (selector 1 is the first). For GOSUB push the return point first: the same line and the position after the whole list. Unlike
    # GOSUB, this does not check the 400-entry limit.
    target = lines[sel[0] - 1]
    if sub:
        if target not in st["program"]:
            return "UL"
        st["gosub"].append((st["line"], p))
    return jump_to(st, target)

# INPUT ["prompt";] variables. Prints the prompt and leaves the machine waiting: `run` returns "waiting" and the host calls submit with the
# typed line, which feed_input handles. Only allowed inside a program ("ID" in direct mode).
def st_input(st, toks, pos):
    if st["line"] == 0:
        return "ID"
    p = pos
    # The default prompt is "? ". A quoted string first supplies the prompt text: followed by ";" the "? " is added after it, followed by "," it is not.
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
    # The variables to fill, as (key, subscripts or None) pairs.
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
    # Park the machine. run() sees `waiting` is set and returns "waiting"; the host then calls submit with the typed line, which feed_input answers.
    # "fields" collects values typed so far, for when the answer has too few and the prompt "?? " asks for the rest.
    st["waiting"] = {"targets": targets, "fields": [], "prompt": prompt}
    put_text(st, prompt)
    return ""

# Called with a typed line when the program is waiting at an INPUT.
def feed_input(st, text):
    w = st["waiting"]
    if w == None:
        return
    # The typed answer, without the end of line.
    line = text.rstrip("\r\n")
    if st["echo"]:
        put_text(st, line + "\n")
    # Split by commas (honouring quotes) and add to anything already collected by an earlier, too short, answer.
    fields = w["fields"] + split_fields(line)
    targets = w["targets"]
    # Too few: keep what we have, print "?? " and stay waiting.
    if len(fields) < len(targets):
        w["fields"] = fields
        put_text(st, "?? ")
        return
    # Too many: warn, and the extra fields are not used.
    if len(fields) > len(targets):
        put_text(st, "?EXTRA IGNORED\n")
    values = []
    for i in range(len(targets)):
        # Convert every field before storing any, so a bad one stores nothing. f is (text, was quoted). A string variable takes the text as typed. A
        # numeric variable needs an unquoted field that parse_number_text accepts; that function returns (ok, value).
        f = fields[i]
        if key_kind(targets[i][0]) == 3:
            values.append(f[0])
        else:
            r = parse_number_text(f[0]) if not f[1] else (False, 0)
            if not r[0]:
                # A bad number: say so, throw away the fields collected, and ask again with the same prompt. `waiting` stays set.
                put_text(st, "?REDO FROM START\n")
                w["fields"] = []
                put_text(st, w["prompt"])
                return
            values.append(r[1])
    st["waiting"] = None
    for i in range(len(targets)):
        # All values are good and `waiting` has just been cleared, so the INPUT is over. The stores can still fail (for example a bad subscript); the
        # error is then reported through handle_error directly, since this function is not called from exec_stmt.
        err = assign(st, targets[i][0], targets[i][1], values[i])
        if err != "":
            handle_error(st, err)
            return

# READ variables: take the next DATA items, converting each to the variable's type. "OD" when the data runs out.
def st_read(st, toks, pos):
    p = pos
    for _ in range(len(toks) + 1):
        t = parse_target(st, toks, p)
        if t[3] != "":
            return t[3]
        # Ran off the end of the DATA list.
        if st["dptr"] >= len(st["data"]):
            return "OD"
        # Take the next DATA item, a (text, was quoted, line) tuple, and advance the pointer.
        item = st["data"][st["dptr"]]
        st["dptr"] += 1
        if key_kind(t[0]) == 3:
            v = item[0]
        else:
            # A numeric variable needs an unquoted item that reads as a number.
            r = parse_number_text(item[0]) if not item[1] else (False, 0)
            if not r[0]:
                # A bad item is a syntax error reported on the DATA line, not on the READ (see handle_error).
                st["errat"] = item[2]   # Level II blames the DATA line, not the READ
                return "SN"
            v = r[1]
        # Store the item (converted to the variable's type). Afterwards, in READ A,B,C a comma after a variable means another variable follows.
        err = assign(st, t[0], t[1], v)
        if err != "":
            return err
        p = t[2]
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    return done_stmt(st, toks, p)

# DATA is only read by build_data; when execution reaches the statement it does nothing.
def st_data(st, toks, pos):
    return done_stmt(st, toks, pos + 1)

# RESTORE: read DATA again from the first item.
def st_restore(st, toks, pos):
    st["dptr"] = 0
    return done_stmt(st, toks, pos)

# DIM A(n[,m...]) ...: create arrays; each subscript runs from 0 to the number given. "DD" if the array exists already.
def st_dim(st, toks, pos):
    p = pos
    for _ in range(len(toks) + 1):
        # Each item must be a name followed by "(". The subscripts are read with paren_args and are bounds, not indexes: DIM A(10) gives eleven elements, 0 to 10.
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
            # A negative bound is "FC".
            if v[0] < 0:
                return "FC"
            dims.append(v[0])
        # An array that exists already (DIMmed twice, or created by being used first, which gives it bounds of 10) cannot be DIMmed: "DD".
        if key in st["arrays"]:
            return "DD"
        # new_array fails with "OM" if the array would be too large.
        err = new_array(st, key, dims)
        if err != "":
            return err
        p = r[1]
        if is_op(toks, p, ","):
            p += 1
        else:
            break
    return done_stmt(st, toks, p)

# REM: ignore the rest of the line.
def st_rem(st, toks, pos):
    return next_line(st)

# END: stop the program and show READY. It cannot be continued with CONT.
def st_end(st, toks, pos):
    st["running"] = False
    st["cont"] = None
    ready(st)
    return ""

# STOP: like BREAK, so CONT can carry on from here.
def st_stop(st, toks, pos):
    if st["col"] != 0:
        newline(st)
    put_text(st, "BREAK" + ((" IN %d" % st["line"]) if st["line"] != 0 else "") + "\n")
    # `pos` here is the position just after STOP, so CONT carries on with the next statement.
    st["cont"] = (st["line"], pos)
    stop(st)
    return ""

# CLS: clear the screen.
def st_cls(st, toks, pos):
    clear_screen(st)
    return done_stmt(st, toks, pos)

# SET(x,y): turn on a block-graphics dot (128 x 48 dots).
def st_set(st, toks, pos):
    return pixel(st, toks, pos, True)

# RESET(x,y): turn a block-graphics dot off.
def st_reset(st, toks, pos):
    return pixel(st, toks, pos, False)

# Shared by SET and RESET: check the two coordinates (0-127 and 0-47) and change the dot.
def pixel(st, toks, pos, on):
    r = paren_args(st, toks, pos)
    if r[2] != "":
        return r[2]
    # SET and RESET take exactly (x, y).
    if len(r[0]) != 2:
        return "SN"
    x = number_arg(r[0][0])
    y = number_arg(r[0][1])
    if x[1] != "" or y[1] != "":
        return "TM"
    # The graphics grid is 128 dots across (x 0 to 127) and 48 down (y 0 to 47).
    if x[0] < 0 or x[0] > 127 or y[0] < 0 or y[0] > 47:
        return "FC"
    set_pixel(st, x[0], y[0], on)
    return done_stmt(st, toks, r[1])

# POKE address,value. Writes to video memory (addresses 15360 to 16383) change the screen; any other address is kept in `mem`, which PEEK reads back.
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
    # A negative address counts as 65536 plus the address (so -1 is 65535); one still negative after that is rejected by the range check below.
    if addr < 0:
        addr += 65536
    # Addresses are 0 to 65535 and values 0 to 255 (one byte).
    if addr < 0 or addr > 65535 or v[0] < 0 or v[0] > 255:
        return "FC"
    # Video memory is the 1024 bytes from 15360: the byte is the character code of that screen cell. The cursor is not moved. Other addresses are
    # only stored in `mem` so that PEEK reads them back.
    if addr >= VIDEO_BASE and addr < VIDEO_BASE + CELLS:
        st["screen"][addr - VIDEO_BASE] = v[0]
    else:
        st["mem"][addr] = v[0]
    return done_stmt(st, toks, r2[1])

# RANDOM: reseed the random generator. Mixes in the step count so repeated calls differ, while a run from the same state stays repeatable.
def st_random(st, toks, pos):
    # Scramble the seed with a simple multiply and add, and fold in the statement count so two RANDOMs at different times differ. 2147483648 is 2 to the
    # power 31. The same program from the same state always gives the same numbers.
    st["seed"] = (st["seed"] * 3 + 17 + st["steps"]) % 2147483648
    return done_stmt(st, toks, pos)

# TRON: from now on print each line number in brackets as it runs.
def st_tron(st, toks, pos):
    st["trace"] = True
    return done_stmt(st, toks, pos)

# TROFF: stop printing line numbers.
def st_troff(st, toks, pos):
    st["trace"] = False
    return done_stmt(st, toks, pos)

# Make the handler for DEFINT, DEFSNG, DEFDBL or DEFSTR. `kind` is the type (0 integer, 1 single, 2 double, 3 string) given to variables whose
# names start with the letters listed (A, or a range like A-F). Returns a function because the four statements differ only in `kind`.
def deftype_statement(kind):
    # The statement itself, closed over `kind`: read a list of single letters or ranges (A, B-F) and set their default type.
    def run_it(st, toks, pos):
        p = pos
        for _ in range(len(toks) + 1):
            # Each item must start with a single letter with no type suffix (a name token is ("v", name, suffix)).
            if p >= len(toks) or toks[p][0] != "v" or len(toks[p][1]) != 1 or toks[p][2] != "":
                return "SN"
            # `lo` and `hi` are positions in the alphabet. A single letter has lo == hi.
            lo = LETTERS.find(toks[p][1])
            hi = lo
            p += 1
            if is_op(toks, p, "-"):
                if p + 1 >= len(toks) or toks[p + 1][0] != "v" or len(toks[p + 1][1]) != 1:
                    return "SN"
                # A range such as A-F: the letter after the "-" is the end. If it comes before the start, range(lo, hi + 1) is empty and nothing changes.
                hi = LETTERS.find(toks[p + 1][1])
                p += 2
            # Set the default type of every letter in the range. Values already stored keep the keys (with suffix) they were stored under.
            for c in range(lo, hi + 1):
                st["deftype"][LETTERS[c]] = kind
            if is_op(toks, p, ","):
                p += 1
            else:
                break
        return done_stmt(st, toks, p)
    return run_it

# DEF FNname(params) = expression. The expression is compiled now and stored under the name; it is evaluated when called. Program lines only.
def st_def(st, toks, pos):
    if st["line"] == 0:
        return "ID"
    if not is_kw(toks, pos, "FN") or pos + 1 >= len(toks) or toks[pos + 1][0] != "v":
        return "SN"
    # The function's name is the variable token after FN (its name plus any type suffix), and it is the key into st["fns"].
    name = toks[pos + 1][1] + toks[pos + 1][2]
    p = pos + 2
    # Optional parameter list in parentheses. Each parameter is turned into a variable key.
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
    # Compile the body when the DEF statement runs, to the form eval_rpn runs. compile_expr returns (compiled expression, next position, error). It is
    # stored with the parameter keys; nothing is evaluated until the function is called.
    r = compile_expr(toks, p + 1)
    if r[2] != "":
        return r[2]
    st["fns"][name] = (params, r[0])
    return done_stmt(st, toks, r[1])

# CLEAR: forget all variables, arrays and loop and subroutine state. The optional number (string space in BASIC) is evaluated and ignored.
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

# ERROR n: raise error number n as if it had happened. 1 to 24 are the standard codes; larger ones are "UE" (unprintable error).
def st_error(st, toks, pos):
    r = eval_at(st, toks, pos)
    if r[2] != "":
        return r[2]
    c = number_arg(r[0])
    if c[1] != "":
        return c[1]
    # Error numbers run 1 to 255.
    if c[0] < 1 or c[0] > 255:
        return "FC"
    # 1 to 24 are the standard errors in the order of ERRORS. Returning the code makes run() handle it exactly as if the error had happened.
    if c[0] <= len(ERRORS):
        return ERRORS[c[0] - 1]
    return "UE"

# RESUME [NEXT | line]: leave the ON ERROR handler. Plain RESUME retries the statement that failed, NEXT carries on after it, and a line number
# goes there. "RW" if no error is being handled.
def st_resume(st, toks, pos):
    if not st["inerr"]:
        return "RW"
    # The statement that failed, saved by handle_error. Leaving the handler clears `inerr` so the next error is caught again.
    where = st["resume"]
    st["inerr"] = False
    p = pos
    # RESUME NEXT: carry on with the statement after the one that failed. Load the failed line, then scan its tokens from the failed statement for the
    # next ":" and continue after it. With no ":" the position goes past the last token, which moves to the next line.
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
    # RESUME n goes to line n. A plain RESUME (or RESUME 0, or anything that is not a line number) falls through and runs the failed statement again.
    if ln > 0:
        return jump_to(st, ln)
    load_line(st, where[0])
    st["pos"] = where[1]
    return ""

# Design of this section. RUN, EDIT, NEW, CONT, LIST and DELETE are commands you normally type in direct mode, but they are ordinary statements
# (they are in STATEMENTS too) so they work from a program and from a colon-separated direct line. They end by calling stop(), which prints READY
# and clears `running`.
# ---------------------------------------------------------------- commands (also usable as statements)

# What RUN and NEW clear: variables, arrays, user functions, the control stacks, the error handler, type defaults. Then rebuild the DATA list.
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
    # Above, `waiting` and `cont` were also cleared, because a pending INPUT or CONT point no longer applies to a fresh run. DATA is gathered from
    # the whole program once here, so READ works however the lines are ordered.
    build_data(st)

# RUN [line]: clear the run state and start at the first line (or the given one).
def st_run(st, toks, pos):
    ensure_index(st)
    reset_run_state(st)
    # RUN with no program just shows READY.
    if len(st["order"]) == 0:
        stop(st)
        return ""
    # The default is the lowest line number; RUN n starts at n instead (goto_line gives "UL" for a line that does not exist).
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
    # Open the editor on the line as LIST would show it. From now on the host sends each key to edit_key instead of submitting lines. Clearing
    # `running` stops the run loop so the editor owns the keyboard.
    st["edit"] = new_edit(line, display_text(st["program"][line]))
    put_text(st, "%d " % line)
    st["running"] = False
    return ""

# One key for the line editor. Returns "edit" while the editor is still open, otherwise "done".
def edit_key(st, key):
    e = st["edit"]
    if e == None:
        return "done"
    # edit_step returns (text to print, action). The action is "" while editing continues, otherwise "save" or "quit".
    r = edit_step(e, key)
    if r[0] != "":
        put_text(st, r[0])
    if r[1] == "":
        return "edit"
    # The editor is finished either way. Only "save" changes the program.
    st["edit"] = None
    if r[1] == "save":
        text = edit_text(e)
        # Saving an empty line deletes it from the program; otherwise the new text replaces it. Either way the caches for that line are dropped.
        if text.strip() == "":
            st["program"].pop(e["line"], None)
        else:
            st["program"][e["line"]] = text
        # Drop the cached tokens for the edited line. Below, the compiled-expression cache is emptied (its keys depend on token positions), the sorted
        # order is marked stale, and the CONT point is forgotten.
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

# NEW: delete the program and everything else.
def st_new(st, toks, pos):
    st["program"] = {}
    st["tokcache"] = {}
    st["rpn"] = {}
    st["dirty"] = True
    reset_run_state(st)
    stop(st)
    return ""

# CONT: carry on after BREAK or STOP. "CN" if the program changed or ended, since there is nothing to continue.
def st_cont(st, toks, pos):
    # Nothing to continue (never stopped, ended with END, or an error or an edit happened since).
    if st["cont"] == None:
        return "CN"
    # Take the saved (line, pos) and clear it, so one BREAK or STOP can be continued only once. Once the check below passes, the position is
    # restored and `running` is set to True so the run loop carries on.
    where = st["cont"]
    st["cont"] = None
    # The line it stopped in has been deleted. A stop in direct mode (line 0) is always accepted by this check.
    if where[0] != 0 and where[0] not in st["program"]:
        return "CN"
    load_line(st, where[0])
    st["pos"] = where[1]
    st["running"] = True
    return ""

# Parse an optional "n", "n-m", "-m" or "n-" after LIST or DELETE. Returns (low, high, error).
def line_range(toks, pos):
    # The defaults cover the whole program: line 0 to 65535. The forms handled below are "n" (just that line), "n-m", "n-" (to the end) and "-m"
    # (from the start); a bare "-" with no number after it is a syntax error.
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
    # Work along the text with index i. `u` is an upper-case copy; the output takes characters from `u` normally, and from `text` where the original
    # case must be kept.
    u = text.upper()
    for _ in range(n + 1):
        if i >= n:
            break
        c = text[i]
        # Inside quotes everything is copied unchanged, quotes included.
        if c == "\"":
            in_quotes = not in_quotes
            out += c
            i += 1
        elif in_quotes:
            out += c
            i += 1
        # A comment: REM is shown in upper case and the rest of the line exactly as typed; after an apostrophe the whole rest is as typed. Setting i = n ends the scan.
        elif u.startswith("REM", i) or c == "'":
            out += u[i:i + 3] + text[i + 3:] if c != "'" else text[i:]
            i = n
        # DATA items keep their case up to the next colon that is not inside quotes (`q` tracks the quotes), which is where scanning resumes.
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
            # Everything else (keywords, names, numbers) comes out in upper case.
            out += u[i]
            i += 1
    return out

# LIST [range]: print the program lines in order.
def st_list(st, toks, pos):
    r = line_range(toks, pos)
    if r[2] != "":
        return r[2]
    ensure_index(st)
    for line in st["order"]:
        # Walk the lines in order and print the ones inside the range (low and high are both included), each as its number, a space and display_text.
        if line >= r[0] and line <= r[1]:
            put_text(st, "%d %s\n" % (line, display_text(st["program"][line])))
    stop(st)
    return ""

# DELETE range: remove the lines. "FC" if the range holds no lines.
def st_delete(st, toks, pos):
    r = line_range(toks, pos)
    if r[2] != "":
        return r[2]
    # Find the lines to delete first. If there are none that is an error, "FC".
    removed = [l for l in st["program"].keys() if l >= r[0] and l <= r[1]]
    if len(removed) == 0:
        return "FC"
    for l in removed:
        # Delete the lines and their cached tokens, empty the compiled-expression cache and mark the sorted order stale.
        st["program"].pop(l)
        st["tokcache"].pop(l, None)
    st["rpn"] = {}
    st["dirty"] = True
    stop(st)
    return ""

# Statement keyword -> the function that runs it. exec_stmt looks the first keyword of a statement up here. A statement starting with a
# variable name is an assignment and does not need an entry.
STATEMENTS = {
    # x
    "PRINT": st_print, "GOTO": st_goto, "GOSUB": st_gosub, "RETURN": st_return, "IF": st_if, "ELSE": st_else,
    "FOR": st_for, "NEXT": st_next, "ON": st_on, "INPUT": st_input, "READ": st_read, "DATA": st_data,
    "RESTORE": st_restore, "DIM": st_dim, "REM": st_rem, "END": st_end, "STOP": st_stop, "CLS": st_cls, "SET": st_set,
    "RESET": st_reset, "POKE": st_poke, "RANDOM": st_random, "TRON": st_tron, "TROFF": st_troff, "DEF": st_def,
    "DEFINT": deftype_statement(0), "DEFSNG": deftype_statement(1), "DEFDBL": deftype_statement(2),
    "DEFSTR": deftype_statement(3), "CLEAR": st_clear, "ERROR": st_error, "RESUME": st_resume, "RUN": st_run,
    "EDIT": st_edit, "NEW": st_new, "CONT": st_cont, "LIST": st_list, "DELETE": st_delete, "LET": st_let,
}

# Design of this section. run() is the engine: a bounded loop that executes one statement per turn using exec_stmt, and feeds any error code to
# handle_error. submit() is what the host calls with typed text: it either answers a waiting INPUT or enters a line, then runs. The status
# words returned are "done" (idle, READY shown), "waiting" (needs an INPUT line) and "budget" (the statement budget ran out; call run again).
# ---------------------------------------------------------------- running

# Execute one statement (or step past the end of a line). Returns an error code or "".
def exec_stmt(st):
    # The statement to run is at toks[pos] of the current line.
    toks = st["toks"]
    pos = st["pos"]
    # End of the line's tokens. If the tokenizer had reported an error for this line, it is returned now; otherwise go to the next line (or end).
    if pos >= len(toks):
        if st["tokerr"] != "":
            return st["tokerr"]
        return next_line(st)
    t = toks[pos]
    # A colon between statements: step over it.
    if t[0] == "o" and t[1] == ":":
        st["pos"] = pos + 1
        return ""
    # Remember where this statement began, so an error can set up RESUME (and ERL) from it.
    st["stmt_start"] = (st["line"], pos)
    # A statement that starts with a variable name is an assignment (LET with the LET left out). It is called with pos, not pos + 1, because the name is part of it.
    if t[0] == "v":
        return st_let(st, toks, pos)
    if t[0] == "k":
        # A keyword: find its function. The handlers get the position after the keyword. LET is the same as a bare assignment, so it skips the keyword
        # and uses st_let. A keyword with no entry (THEN, TO, a function name...) falls through to the syntax error below.
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
        # Count each statement executed. Only RANDOM reads this count; the budget limit is the range of the for loop above, not this counter.
        st["steps"] += 1
        # A non-empty result is a BASIC error: handle_error either jumps to the handler or ends the run (clearing `running`), which stops this loop.
        err = exec_stmt(st)
        if err != "":
            handle_error(st, err)
    if st["waiting"] != None:
        return "waiting"
    # Report why the loop ended. Waiting for an INPUT line (tested just above) takes precedence, then "budget" if still running, otherwise "done".
    if st["running"]:
        return "budget"
    return "done"

# Deal with one line typed at the keyboard: a program line is stored, anything else is run at once.
def enter_line(st, text):
    # Strip the end of line and leading spaces. With echo on, print the line back (a terminal host leaves echo off because it already shows typing).
    s = text.rstrip("\r\n").lstrip()
    if st["echo"]:
        put_text(st, s + "\n")
    if s == "":
        prompt_again(st)    # a bare Enter gets a fresh prompt, as on the real machine
        return
    # k is the number of leading digits. If there are any, this is a program line, not a command.
    k = span_digits(s, 0)
    if k > 0:
        num = int(s[:k])
        rest = s[k:].lstrip()
        # 65529 is the highest line number accepted. A larger one is a syntax error reported in direct mode (line 0, so no "IN" part).
        if num > 65529:
            st["direct"] = ([], "")
            st["line"] = 0
            handle_error(st, "SN")
            return
        # A line number alone deletes that line. Otherwise the text after the number is stored. Either way drop its cached tokens.
        if rest == "":
            st["program"].pop(num, None)
            st["tokcache"].pop(num, None)
        else:
            st["program"][num] = rest
            st["tokcache"].pop(num, None)
        st["rpn"] = {}
        st["dirty"] = True
        st["cont"] = None
        # The program changed, so (above) the compiled-expression cache was emptied, the sorted line order marked stale, and the CONT point forgotten,
        # since the program is no longer what was stopped.
        prompt_again(st)    # storing a program line shows no READY, only a new prompt
        return
    # A command without a line number: tokenize it, make it the direct-mode line (line 0) and set `running` so the run loop executes it.
    toks = tokenize(s)
    st["direct"] = toks
    st["line"] = 0
    load_line(st, 0)
    st["pos"] = 0
    st["running"] = True

# Entry point for a typed line: answer a waiting INPUT, or enter a command or program line.
def submit(st, text, budget):
    if st["waiting"] != None:
        # If an INPUT is waiting, the text is its answer. Otherwise, if nothing is running, it is a command or program line. Text submitted while a
        # program is still running (the last call returned "budget") is neither, so it is ignored. Either way the loop then runs for `budget` statements.
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

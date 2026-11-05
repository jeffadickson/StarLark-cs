@@ Heavy explanatory comments for apps/Basic/scripts/basic.star, applied by `CommentCheck insert`.
@file apps/Basic/scripts/basic.star

@line load("numbers.star"
Imports. numbers.star: number kinds and text conversion (kind_of gives 0 integer, 1 single, 2 double, 3 string). lex.star: tokenize. editor.star: the
EDIT key state machine. expr.star: expression compile/evaluate, variable keys, arrays and type coercion. screen.star: the 16 x 64 screen, the
cursor and PRINT USING. Token tuples used throughout this file: ("n", value) number, ("s", text) string, ("v", name, suffix) variable,
("k", WORD) keyword, ("o", op) operator or punctuation.

@line LETTERS = "ABC
The alphabet, used to build the per-letter default-type table (DEFINT and friends) and to turn a letter into a table index with .find().

@line # ---------------------------------------------------------------- the machine
Design of this section. All state lives in one dict, `st`, created once by new_state() and passed as the first argument to every function.
Nothing is global and nothing is mutable at module level, so two machines can exist side by side. Almost everything below returns "" for success
or a two-letter error code from ERRORS; the code travels up to run(), which hands it to handle_error().

@line st = {
`st` is the whole machine. The groups of keys are described next to each group.

@line "program": {}
The stored program. `program` maps a line number to the program text after the number. `order` and `index` are derived from it (see
ensure_index) and are only valid while `dirty` is False. `tokcache` and `rpn` are caches of work already done on the text: they must be emptied
or trimmed whenever program text changes, or a stale line would run.

@line "rpn": {}
Compiled expressions (see eval_at in expr.star), keyed by line * 4096 + token position so the same expression is compiled only once per line. Any
edit of any line empties the whole dict, because the keys depend on token positions.

@line "vars": {}, "arrays": {}, "fns": {},
Variables: `vars` maps a key from var_key (name plus type suffix, for example "A!" or "N$") to its value. `arrays` maps the same sort of key to
[dims, flat list of elements] (see new_array/locate). `fns` maps a DEF FN function name to (list of parameter keys, compiled expression).

@line "deftype": {c: 1
The default type of an untyped variable, by first letter: 0 integer, 1 single, 2 double, 3 string. Every letter starts as 1 (single precision);
DEFINT, DEFSNG, DEFDBL and DEFSTR change entries.

@line "line": 0, "toks": [], "tokerr": "", "pos": 0, "direct": ([], ""),
Where execution is. `line` is the BASIC line number being executed, with 0 meaning direct mode (a command typed without a line number). `toks` and
`tokerr` are that line's tokens and its tokenizer error ("" if none). `pos` is the index in `toks` of the next token to run. `direct` is the
(tokens, error) pair for the last typed command, which load_line(st, 0) uses.

@line "running": False, "waiting": None, "cont": None,
`running` is True while the run loop should keep executing statements; it is also True while a direct command executes. `waiting` is None, or
a dict {"targets", "fields", "prompt"} while a program sits at an INPUT (see st_input and feed_input). `cont` is None, or the (line, pos) at which
CONT carries on after a BREAK or STOP.

@line "gosub": [], "fors": [],
The two control stacks, both plain lists used as stacks. `gosub` holds return points as (line, pos) pairs. `fors` holds one frame per active
FOR loop as (variable key, limit, step, line, pos), where line and pos are the place just after the FOR statement, which NEXT jumps back to.

@line "data": [], "dptr": 0,
The DATA values of the whole program, as (text, was quoted, line number) tuples built by build_data, and the index of the next one READ will take.

@line "screen": new_screen(), "cur": 0, "col": 0, "out": [], "quiet": False, "echo": True,
Output. `screen` is a list of 1024 character codes (16 rows of 64). `cur` is the cursor as a cell number 0..1023 and `col` is its column,
cur % 64. `out` collects every string printed since the host last called take_output. When `quiet` is True put_text does not add to `out`
(the screen still changes; nothing in this file sets it, so it is for the host). `echo` says whether typed lines are printed back (see set_echo).

@line "mem": {}, "inkey": [], "seed": 12345, "trace": False,
Odds and ends. `mem` holds bytes POKEd outside video memory, address -> value, which PEEK reads back. `inkey` is the queue of keys for INKEY$ (fed by
press_keys). `seed` is the state of the RND generator. `trace` is TRON/TROFF.

@line "edit": None, "onerr": 0,
Line editor and error handling. `edit` is None, or the editor's state dict while EDIT has the keyboard. `onerr` is the line of the ON ERROR GOTO
handler (0 means none). `inerr` is True while that handler runs. `errline` and `errcode` are what ERL and ERR report. `resume` is where
RESUME goes (the start of the statement that failed) and `stmt_start` is the (line, pos) of the statement now executing, copied into `resume` when
an error is caught.

@line "steps": 0,
Count of statements executed so far. Only RANDOM uses it, to make each reseed different.

@line put_text(st, "RADIO SHACK
Print the power-on banner, then the READY prompt, so a new machine looks like a freshly switched-on TRS-80.

@line # ---------------------------------------------------------------- program storage
Design of this section. A program is a dict of line number -> source text. Source text is only tokenized when a line first runs (get_toks) and
the result is cached. Running in line order needs the sorted list of numbers, which is rebuilt lazily (ensure_index) only after an edit has set
`dirty`. Execution position is the pair st["line"], st["pos"]; jumping means changing that pair and loading the target line's tokens.

@line order = sorted(st["program"].keys())
Sort the line numbers, then build the reverse lookup: line number -> its position in the sorted list. next_line uses it to find the following line.

@line hit = st["tokcache"].get(line)
`hit` is the cached (tokens, error) pair from tokenize, or None if this line has not been tokenized since it last changed.

@line if line == 0:
Line 0 is direct mode: there is no program text, so use the tokens of the command the user just typed.

@line t = get_toks(st, line)
A program line: take its cached tokens. t is the (tokens, error) pair.

@line load_line(st, line)
The target exists (checked above, so a bad target leaves the position unchanged). Load its tokens and start at its first token, position 0.

@line if st["trace"]:
TRON: show each line that is jumped to. Note this runs for jumps (GOTO, GOSUB, RUN, ON...GOTO), and for next_line, which also goes through
jump_to; it does not run for RETURN or NEXT, which use load_line directly.

@line i = st["index"].get(st["line"])
The position of the current line in `order`. It is None if the current line is not in the program (for example a deleted line). Direct mode
(line 0) never gets this far: it was ended just above, since a typed command has no following line.

@line if i == None or i + 1 >= len(st["order"]):
No current line, or the last line has just finished: the program ends (and READY is shown).

@line return jump_to(st, st["order"][i + 1])
Carry on at the next higher line number.

@line toks = get_toks(st, line)[0]
Only the token list is needed here ([1] would be the tokenizer error, which is ignored when collecting DATA).

@line for k in range(len(toks) - 1):
Stop one short because a DATA keyword needs a following token. The tokenizer keeps the raw text of a DATA statement as one ("s", text) token
right after the DATA keyword, so the items are split from that text here.

@line fields = []
split_fields works one character at a time. `cur` is the text of the field being built, `quoted` means this field has had an opening quote, and
`in_quotes` means the scan is now between a pair of quotes. A comma only ends a field outside quotes.

@line if ch == "\"":
A quote mark never becomes part of the text. It flips in_quotes; the first quote of a field also throws away anything typed before it.

@line elif ch == "," and not in_quotes:
End of a field. Unquoted text is trimmed of surrounding spaces; quoted text is kept exactly as it was between the quotes.

@line return fields
The final field has no comma after it, so it was appended just above rather than inside the loop. Empty text therefore gives one empty
field, not an empty list.

@line # ---------------------------------------------------------------- errors
Design of this section. A BASIC error is not an exception: a statement function returns a two-letter code and run() passes it to handle_error.
That function either diverts to the ON ERROR GOTO handler (remembering where RESUME should go) or prints the message and stops the program.

@line line = st.pop("errat", st["line"])
Normally the error belongs to the current line. st_read sets the key "errat" (not part of the initial state dict) to the DATA line of a bad item;
pop reads it and removes it, so it only applies to this one error.

@line st["errcode"] = ERRORS.index(code)
Record the error number for ERR/ERL. ERR is twice the zero-based position of the code in ERRORS; a code not in the list gets 40.

@line if st["onerr"] != 0 and not st["inerr"] and line != 0:
Divert to the error handler only if one is set, we are not already inside it (an error inside the handler is fatal), and the error happened in a
program line (in direct mode there is nothing to resume).

@line st["resume"] = st["stmt_start"]
RESUME will go back to the start of the statement that failed.

@line if jump_to(st, st["onerr"]) == "":
Handler line found and entered: the program carries on there. If the line has since been deleted jump_to returns "UL" and we fall through to
the normal report below (note that `inerr` stays True in that case).

@line text = "?" + code
Message format: "?SN ERROR" in direct mode, "?SN ERROR IN 100" in a program. (Just above, a newline is started first if the cursor is part way
along a row, so the message does not follow other output.)

@line put_text(st, text
Print the message. Then forget the CONT point (an error ends the program for good, so CONT has nothing to carry on from) and show READY.

@line st["cont"] = (st["line"], st["pos"])
Remember where the program was. st["pos"] is the next token to run, so CONT carries on from there. The INPUT that may have been waiting is
abandoned (`waiting` is cleared on the next line) before READY is shown.

@line # ---------------------------------------------------------------- statement helpers
Design of this section. Every st_* statement function has the signature (st, toks, pos): `toks` is the current line's tokens and `pos` is the
index just after the statement's keyword. It returns "" or an error code, and on success must leave st["pos"] pointing at the next token to run
(normally by returning done_stmt(...)). The helpers below parse the pieces statements share. Each loop here is written `for _ in range(len(toks) + 1)`
because Starlark has no `while`: every pass consumes at least one token, so len(toks) + 1 passes is more than can ever be needed.

@line return (t[0] == "o" and t[1] == ":") or (t[0] == "k" and t[1] == "ELSE")
Colon and ELSE both close a statement; ELSE is included so that the THEN part of an IF stops at it.

@line if type(v) == "tuple":
A double precision number is held as a 1-tuple (see numbers.star), and a line number cannot be one.

@line if not is_op(toks, p, "("):
paren_args must be called at an open parenthesis; anything else is a syntax error.

@line r = eval_at(st, toks, q)
Evaluate one argument. r is (value, next position, error). eval_at stops at a comma or close parenthesis.

@line if is_op(toks, q, ","):
After an argument: a comma means another follows, a close parenthesis ends the list (return the position after it), anything else is a syntax
error. If the tokens run out before a close parenthesis, the last branch returns "SN" as well.

@line if is_op(toks, q, "("):
The name token is ("v", name, suffix); var_key turns it into the dictionary key (name plus type suffix, using the default type for the first
letter if no suffix was typed). A "(" right after the name means an array element, so the subscripts are parsed; otherwise it is a plain variable
and the subscripts are None.

@line return (key, r[0], r[1], "")
Array element: the key, the list of evaluated subscripts, and the position after the ")".

@line return (key, None, q, "")
Plain variable: no subscripts, and the position just after the name.

@line kind = key_kind(key)
The variable's type, from the suffix of the key: 0 integer, 1 single, 2 double, 3 string.

@line cv = coerce(kind, v)
Convert the value to that type; coerce returns (converted value, error). A string into a number (or the reverse) gives "TM", a number too big gives "OV".

@line if kind == 3 and len(cv[0]) > 255:
Level II strings are at most 255 characters.

@line loc = locate(st, key, subs)
locate returns (array, flat position, error). An array used without DIM is created with every bound 10 on first use. The array is [dims, flat list],
so loc[0][1] is the flat list of elements and loc[1] the index of this element.

@line return (0, "TM")
A string is not a number: type mismatch. The 0 is only a placeholder; callers check the error first.

@line return (floor_int(to_float(v)), "")
Round down to an integer (to_float widens integer, single and double values to a Python float first).

@line # ---------------------------------------------------------------- statements
Design of this section. One function per BASIC statement, all with the signature (st, toks, pos) described under "statement helpers" and all
returning "" or an error code. Statements that jump (GOTO, GOSUB, RETURN, NEXT, IF, ON) set the position themselves; the rest finish with
done_stmt. A function that fails must do so before changing state where it can, since the error handler may retry the statement with RESUME.

@line if is_op(toks, p, "@"):
PRINT @ n: put the cursor on screen cell n (0 to 1023, with 64 cells per row) before printing. A "," or ";" after the position is allowed and skipped.

@line if loc[0] < 0 or loc[0] >= CELLS:
Only the 1024 cells of the screen exist, anything else is an illegal function call.

@line if is_kw(toks, p, "USING"):
PRINT USING has its own routine; it handles the rest of the statement, including the final newline.

@line if t[0] == "o" and t[1] == ";":
";" prints nothing: the next item follows straight on. `newline_needed` (True at the start) is set False by a separator and back to True by
every printed item, so only a statement whose last thing is ; or , leaves the cursor where it is. Each pass of this loop handles one item.

@line if t[0] == "o" and t[1] == ",":
"," moves to the next print field. The 64 columns are four fields of 16 starting at columns 0, 16, 32 and 48. From column 48 on there is no
next field on the row, so it goes to the next line instead. Otherwise it pads with spaces to the next multiple of 16.

@line if t[0] == "k" and (t[1] == "TAB(" or t[1] == "SPC("):
TAB( and SPC( are keywords whose token already includes the open parenthesis, so the argument starts at p + 1 and must be followed by ")".

@line if c[0] < 0 or c[0] > 255:
The count must be 0 to 255, otherwise "FC".

@line if c[0] > st["col"]:
TAB(n) only moves right: if the cursor is already at or past column n nothing is printed. SPC(n) always prints n spaces.

@line v = r[0]
An ordinary expression. Strings print as they are; numbers go through print_text, which adds a space for the sign in front and a space after.

@line if newline_needed:
End of the statement. Print the newline unless the last thing in the statement was ; or ,.

@line if kind_of(r[0]) != 3:
The format must be a string expression.

@line fmt = r[0]
After the format string a ";" or "," must come before the first value.

@line e = eval_at(st, toks, p)
Collect all the values first (the list `values`), then format them together with format_using. `trailing` is True only if the value list ended
with a separator, which suppresses the newline. Here: evaluate one value; after it a ; or , is skipped (and sets `trailing`); anything else
must be the end of the statement.

@line f = format_using(fmt, values)
format_using returns (text, error). Nothing is printed unless the whole format worked.

@line err = assign(st, t[0], t[1], r[0])
t is parse_target's (key, subscripts or None, next position, error) for the left side; r is eval_at's (value, next position, error) for the right.
assign does the type conversion and can fail with TM, OV, LS or BS; the statement then fails with that code.

@line if len(st["gosub"]) >= 400:
The GOSUB stack is capped at 400 return points; deeper is "OM" (out of memory).

@line st["gosub"].append((st["line"], pos + 1))
The return point: this line and the position just after the line number (pos is the number's position), so any ": more statements" that follow
the GOSUB run after the RETURN. The target was checked above, so nothing is pushed for a failed GOSUB.

@line back = st["gosub"].pop()
Return to the saved line and token position; load_line brings back that line's tokens.

@line truth = to_float(r[0]) != 0.0
Any non-zero number is true (a true comparison is -1, false is 0). A string condition is a type mismatch.

@line if is_kw(toks, p, "THEN"):
After the condition, THEN or GOTO is required. For THEN, p moves past it; for GOTO p is left on the GOTO keyword, so the GOTO runs as the next statement.

@line if not truth:
False condition: scan the rest of the line for the ELSE that belongs to this IF. `depth` counts IFs opened after this one; an ELSE at depth 0
is ours and an ELSE at a deeper level closes one of those nested IFs.

@line if found < 0:
No ELSE: the rest of the line is skipped, so the next statement is on the next line.

@line p = found + 1
Resume after the ELSE and run the false branch.

@line if p < n and toks[p][0] == "n":
THEN 100 or ELSE 100 (a bare number) means GOTO 100. Anything else just continues with the statements at p.

@line key = t[0]
t is parse_target's result for the loop variable.
The loop variable: it must be a plain numeric variable. An array element (subscripts present) is a syntax error, a string variable a type mismatch.

@line r1 = eval_at(st, toks, p + 1)
Start value, then TO and the limit, then an optional STEP; any of them may be an error. STEP defaults to the integer 1.

@line if kind_of(r1[0]) == 3
Strings anywhere in the header are a type mismatch.

@line err = assign(st, key, None, r1[0])
Set the loop variable to the start value, converted to the variable's type.

@line frames = [f for f in st["fors"] if f[0] != key]
Drop any existing frame for the same variable: starting FOR I again restarts that loop rather than nesting it.

@line frames.append((key, r2[0], step, st["line"], p))
Push the frame. The limit and step are stored as evaluated now (a later change to the variables they came from has no effect). The place
stored is just after the FOR statement, which NEXT will jump back to. Nothing tests the limit yet: that is done by NEXT, so the body always runs once.

@line keys = []
Collect the variables named after NEXT (NEXT I,J). `continue` after a comma goes round for the next name; anything else leaves the loop.

@line if len(keys) == 0:
Plain NEXT with no variable: the empty key stands for "the innermost loop".

@line for key in keys:
Handle the names one at a time, in the order written: each loop is stepped, and if it has finished its frame is dropped and the next name is handled.

@line if key == "":
Find the frame: the top of the stack for plain NEXT; otherwise the last frame for that variable (the loop does not stop at the first match).

@line st["fors"] = fors[:f + 1]
Discard any loops nested inside this one. A NEXT of an outer variable ends the loops that were still open inside it.

@line cur = st["vars"].get(frame[0])
The current value of the loop variable (zero of its type if it has none), plus the step by binop, then converted back to the variable's type by
coerce. An error from either is returned as the error of the NEXT statement.

@line step = to_float(frame[2])
Compare as floats: a negative step counts down and tests value >= limit; otherwise (zero or positive) it counts up and tests value <= limit.

@line load_line(st, frame[3])
Loop again: go back to just after the FOR statement. Any remaining names after NEXT are not looked at.

@line st["fors"] = fors[:f]
The loop is finished: pop its frame and continue with the next name, or the end of the statement.

@line if is_kw(toks, pos, "ERROR"):
ON ERROR GOTO line. A line of 0 turns the handler off; any other line must exist now. It is stored in st["onerr"].

@line sel = number_arg(r[0])
ON n GOTO/GOSUB: n picks which line number in the list to use, counting from 1.

@line if is_kw(toks, p, "GOTO"):
`sub` remembers whether it was GOSUB, to push a return point later.

@line lines = []
Read the whole comma-separated list of line numbers first.

@line if sel[0] < 0 or sel[0] > 255:
A selector outside 0 to 255 is an illegal function call, but one that is merely 0 or past the end of the list does nothing.

@line target = lines[sel[0] - 1]
Pick the line (selector 1 is the first). For GOSUB push the return point first: the same line and the position after the whole list. Unlike
GOSUB, this does not check the 400-entry limit.

@line prompt = "? "
The default prompt is "? ". A quoted string first supplies the prompt text: followed by ";" the "? " is added after it, followed by "," it is not.

@line targets = []
The variables to fill, as (key, subscripts or None) pairs.

@line st["waiting"] = {
Park the machine. run() sees `waiting` is set and returns "waiting"; the host then calls submit with the typed line, which feed_input answers.
"fields" collects values typed so far, for when the answer has too few and the prompt "?? " asks for the rest.

@line line = text.rstrip("\r\n")
The typed answer, without the end of line.

@line fields = w["fields"] + split_fields(line)
Split by commas (honouring quotes) and add to anything already collected by an earlier, too short, answer.

@line if len(fields) < len(targets):
Too few: keep what we have, print "?? " and stay waiting.

@line if len(fields) > len(targets):
Too many: warn, and the extra fields are not used.

@line f = fields[i]
Convert every field before storing any, so a bad one stores nothing. f is (text, was quoted). A string variable takes the text as typed. A
numeric variable needs an unquoted field that parse_number_text accepts; that function returns (ok, value).

@line put_text(st, "?REDO FROM START
A bad number: say so, throw away the fields collected, and ask again with the same prompt. `waiting` stays set.

@line err = assign(st, targets
All values are good and `waiting` has just been cleared, so the INPUT is over. The stores can still fail (for example a bad subscript); the
error is then reported through handle_error directly, since this function is not called from exec_stmt.

@line if st["dptr"] >= len(st["data"]):
Ran off the end of the DATA list.

@line item = st["data"][st["dptr"]]
Take the next DATA item, a (text, was quoted, line) tuple, and advance the pointer.

@line r = parse_number_text(item[0]) if not item[1] else (False, 0)
A numeric variable needs an unquoted item that reads as a number.

@line st["errat"] = item[2]
A bad item is a syntax error reported on the DATA line, not on the READ (see handle_error).

@line err = assign(st, t[0], t[1], v)
Store the item (converted to the variable's type). Afterwards, in READ A,B,C a comma after a variable means another variable follows.

@line if p >= len(toks) or toks[p][0] != "v" or not is_op(toks, p + 1, "("):
Each item must be a name followed by "(". The subscripts are read with paren_args and are bounds, not indexes: DIM A(10) gives eleven elements, 0 to 10.

@line if v[0] < 0:
A negative bound is "FC".

@line if key in st["arrays"]:
An array that exists already (DIMmed twice, or created by being used first, which gives it bounds of 10) cannot be DIMmed: "DD".

@line err = new_array(st, key, dims)
new_array fails with "OM" if the array would be too large.

@line st["cont"] = (st["line"], pos)
`pos` here is the position just after STOP, so CONT carries on with the next statement.

@line if len(r[0]) != 2:
SET and RESET take exactly (x, y).

@line if x[0] < 0 or x[0] > 127
The graphics grid is 128 dots across (x 0 to 127) and 48 down (y 0 to 47).

@line if addr < 0:
A negative address counts as 65536 plus the address (so -1 is 65535); one still negative after that is rejected by the range check below.

@line if addr < 0 or addr > 65535 or v[0] < 0 or v[0] > 255:
Addresses are 0 to 65535 and values 0 to 255 (one byte).

@line if addr >= VIDEO_BASE and addr < VIDEO_BASE + CELLS:
Video memory is the 1024 bytes from 15360: the byte is the character code of that screen cell. The cursor is not moved. Other addresses are
only stored in `mem` so that PEEK reads them back.

@line st["seed"] = (st["seed"] * 3 + 17 + st["steps"]) % 2147483648
Scramble the seed with a simple multiply and add, and fold in the statement count so two RANDOMs at different times differ. 2147483648 is 2 to the
power 31. The same program from the same state always gives the same numbers.

@line if p >= len(toks) or toks[p][0] != "v" or len(toks[p][1]) != 1 or toks[p][2] != "":
Each item must start with a single letter with no type suffix (a name token is ("v", name, suffix)).

@line lo = LETTERS.find(toks[p][1])
`lo` and `hi` are positions in the alphabet. A single letter has lo == hi.

@line hi = LETTERS.find(toks[p + 1][1])
A range such as A-F: the letter after the "-" is the end. If it comes before the start, range(lo, hi + 1) is empty and nothing changes.

@line for c in range(lo, hi + 1):
Set the default type of every letter in the range. Values already stored keep the keys (with suffix) they were stored under.

@line name = toks[pos + 1][1] + toks[pos + 1][2]
The function's name is the variable token after FN (its name plus any type suffix), and it is the key into st["fns"].

@line params = []
Optional parameter list in parentheses. Each parameter is turned into a variable key.

@line r = compile_expr(toks, p + 1)
Compile the body when the DEF statement runs, to the form eval_rpn runs. compile_expr returns (compiled expression, next position, error). It is
stored with the parameter keys; nothing is evaluated until the function is called.

@line if c[0] < 1 or c[0] > 255:
Error numbers run 1 to 255.

@line if c[0] <= len(ERRORS):
1 to 24 are the standard errors in the order of ERRORS. Returning the code makes run() handle it exactly as if the error had happened.

@line where = st["resume"]
The statement that failed, saved by handle_error. Leaving the handler clears `inerr` so the next error is caught again.

@line if is_kw(toks, p, "NEXT"):
RESUME NEXT: carry on with the statement after the one that failed. Load the failed line, then scan its tokens from the failed statement for the
next ":" and continue after it. With no ":" the position goes past the last token, which moves to the next line.

@line if ln > 0:
RESUME n goes to line n. A plain RESUME (or RESUME 0, or anything that is not a line number) falls through and runs the failed statement again.

@line # ---------------------------------------------------------------- commands (also usable as statements)
Design of this section. RUN, EDIT, NEW, CONT, LIST and DELETE are commands you normally type in direct mode, but they are ordinary statements
(they are in STATEMENTS too) so they work from a program and from a colon-separated direct line. They end by calling stop(), which prints READY
and clears `running`.

@line build_data(st)
Above, `waiting` and `cont` were also cleared, because a pending INPUT or CONT point no longer applies to a fresh run. DATA is gathered from
the whole program once here, so READ works however the lines are ordered.

@line if len(st["order"]) == 0:
RUN with no program just shows READY.

@line start = st["order"][0]
The default is the lowest line number; RUN n starts at n instead (goto_line gives "UL" for a line that does not exist).

@line st["edit"] = new_edit(line, display_text(st["program"][line]))
Open the editor on the line as LIST would show it. From now on the host sends each key to edit_key instead of submitting lines. Clearing
`running` stops the run loop so the editor owns the keyboard.

@line r = edit_step(e, key)
edit_step returns (text to print, action). The action is "" while editing continues, otherwise "save" or "quit".

@line st["edit"] = None
The editor is finished either way. Only "save" changes the program.

@line if text.strip() == "":
Saving an empty line deletes it from the program; otherwise the new text replaces it. Either way the caches for that line are dropped.

@line st["tokcache"].pop(e
Drop the cached tokens for the edited line. Below, the compiled-expression cache is emptied (its keys depend on token positions), the sorted
order is marked stale, and the CONT point is forgotten.

@line if st["cont"] == None:
Nothing to continue (never stopped, ended with END, or an error or an edit happened since).

@line where = st["cont"]
Take the saved (line, pos) and clear it, so one BREAK or STOP can be continued only once. Once the check below passes, the position is
restored and `running` is set to True so the run loop carries on.

@line if where[0] != 0 and where[0] not in st["program"]:
The line it stopped in has been deleted. A stop in direct mode (line 0) is always accepted by this check.

@line lo = 0
The defaults cover the whole program: line 0 to 65535. The forms handled below are "n" (just that line), "n-m", "n-" (to the end) and "-m"
(from the start); a bare "-" with no number after it is a syntax error.

@line u = text.upper()
Work along the text with index i. `u` is an upper-case copy; the output takes characters from `u` normally, and from `text` where the original
case must be kept.

@line if c == "\"":
Inside quotes everything is copied unchanged, quotes included.

@line elif u.startswith("REM", i) or c == "'":
A comment: REM is shown in upper case and the rest of the line exactly as typed; after an apostrophe the whole rest is as typed. Setting i = n ends the scan.

@line elif u.startswith("DATA", i):
DATA items keep their case up to the next colon that is not inside quotes (`q` tracks the quotes), which is where scanning resumes.

@line out += u[i]
Everything else (keywords, names, numbers) comes out in upper case.

@line if line >= r[0] and line <= r[1]:
Walk the lines in order and print the ones inside the range (low and high are both included), each as its number, a space and display_text.

@line removed = [l for l in st["program"].keys() if l >= r[0] and l <= r[1]]
Find the lines to delete first. If there are none that is an error, "FC".

@line st["program"].pop(l)
Delete the lines and their cached tokens, empty the compiled-expression cache and mark the sorted order stale.

@line "PRINT": st_print
x

@line # ---------------------------------------------------------------- running
Design of this section. run() is the engine: a bounded loop that executes one statement per turn using exec_stmt, and feeds any error code to
handle_error. submit() is what the host calls with typed text: it either answers a waiting INPUT or enters a line, then runs. The status
words returned are "done" (idle, READY shown), "waiting" (needs an INPUT line) and "budget" (the statement budget ran out; call run again).

@line toks = st["toks"]
The statement to run is at toks[pos] of the current line.

@line if pos >= len(toks):
End of the line's tokens. If the tokenizer had reported an error for this line, it is returned now; otherwise go to the next line (or end).

@line if t[0] == "o" and t[1] == ":":
A colon between statements: step over it.

@line st["stmt_start"] = (st["line"], pos)
Remember where this statement began, so an error can set up RESUME (and ERL) from it.

@line if t[0] == "v":
A statement that starts with a variable name is an assignment (LET with the LET left out). It is called with pos, not pos + 1, because the name is part of it.

@line handler = STATEMENTS.get(t[1])
A keyword: find its function. The handlers get the position after the keyword. LET is the same as a bare assignment, so it skips the keyword
and uses st_let. A keyword with no entry (THEN, TO, a function name...) falls through to the syntax error below.

@line st["steps"] += 1
Count each statement executed. Only RANDOM reads this count; the budget limit is the range of the for loop above, not this counter.

@line err = exec_stmt(st)
A non-empty result is a BASIC error: handle_error either jumps to the handler or ends the run (clearing `running`), which stops this loop.

@line if st["running"]:
Report why the loop ended. Waiting for an INPUT line (tested just above) takes precedence, then "budget" if still running, otherwise "done".

@line s = text.rstrip("\r\n").lstrip()
Strip the end of line and leading spaces. With echo on, print the line back (a terminal host leaves echo off because it already shows typing).

@line k = span_digits(s, 0)
k is the number of leading digits. If there are any, this is a program line, not a command.

@line if num > 65529:
65529 is the highest line number accepted. A larger one is a syntax error reported in direct mode (line 0, so no "IN" part).

@line if rest == "":
A line number alone deletes that line. Otherwise the text after the number is stored. Either way drop its cached tokens.

@line prompt_again(st)    # storing
The program changed, so (above) the compiled-expression cache was emptied, the sorted line order marked stale, and the CONT point forgotten,
since the program is no longer what was stopped.

@line toks = tokenize(s)
A command without a line number: tokenize it, make it the direct-mode line (line 0) and set `running` so the run loop executes it.

@line feed_input(st, text)
If an INPUT is waiting, the text is its answer. Otherwise, if nothing is running, it is a command or program line. Text submitted while a
program is still running (the last call returned "budget") is neither, so it is ignored. Either way the loop then runs for `budget` statements.

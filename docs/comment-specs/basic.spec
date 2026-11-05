@@ Comments for apps/Basic/scripts/basic.star, applied by `CommentCheck insert`. Each block is a comment placed above a function or a line.
@file apps/Basic/scripts/basic.star

@line ERRORS = [
The two-letter error codes in the order of the BASIC `ERROR n` statement: ERROR 1 is "NF", ERROR 2 is "SN", and so on. `ERR` reports twice the
position here, as Level II did (its codes were even numbers). A statement that fails returns one of these strings; handle_error prints it.

@def new_state
Build a fresh machine and show the power-on banner and the READY prompt. The machine is this one dict `st`; every other function takes it first.
The keys fall into groups: the stored program and its caches, variables and arrays, where execution is (line, toks, pos), the two control
stacks (gosub, fors), DATA, the screen, the error handler, and the line editor.

@def take_output
Return everything printed since the last call as one string, and empty the buffer. The host calls this after each step to show the user.

@def get_screen
The 16 lines of the 64-column screen as a list of strings, trailing spaces removed (see screen_lines).

@def ready
Print "READY" and the ">" prompt on a fresh line. The end of every command, program run and error.

@def stop
Leave run mode and show READY. Used when a program ends, runs off its last line, or hits an error.

@def ensure_index
Rebuild `order` (line numbers sorted) and `index` (line number -> position) if the program changed since they were built. Cheap to call often.

@def get_toks
The tokens of a program line, tokenized on first use and cached in `tokcache`. The cache entry is dropped whenever the line changes.

@def load_line
Make `line` the line being executed: load its tokens into `toks`/`tokerr`. Line 0 is direct mode and uses the tokens of the typed command.

@def jump_to
Go to the start of program line `line` (GOTO, GOSUB, RUN, ON...GOTO). Returns "UL" (undefined line) if it does not exist, otherwise "".
With TRON on, also prints the line number in square brackets.

@def next_line
Fall through to the line after the current one. In direct mode, or after the last line, the run ends instead.

@def build_data
Collect every DATA item in the program, in line order, into `st["data"]` and rewind the read pointer. Each item remembers its line so a bad
item can be blamed on the DATA line and not on the READ. Done at RUN.

@def split_fields
Split text at commas that are not inside quotes. Used for INPUT answers and DATA items. Returns (text, was_quoted) pairs: unquoted text is
trimmed, quoted text keeps its spaces, spaces before an opening quote are dropped, and anything after the closing quote up to the comma is ignored.

@def handle_error
Report error `code`. If ON ERROR GOTO is active, jump to the handler and remember where to RESUME; otherwise print "?XX ERROR IN line" (no
"IN" in direct mode) and stop. A bad DATA item sets `errat` so the message names its own line.

@def break_program
The BREAK key. Prints "BREAK IN line", remembers where the program was so CONT can carry on, and stops. Does nothing if nothing is running.

@def at_end
True when the next token ends a statement: the end of the line, a colon, or ELSE.

@def done_stmt
Finish a statement: check nothing but the end of the statement is left, then record `p` as where execution continues. Returns "SN" if
there is junk after the statement, otherwise "". Almost every st_* function returns through this.

@def is_op
True if the token at position p is the operator or punctuation mark `op` (for example "," or "(").

@def is_kw
True if the token at position p is the keyword `word` (for example "THEN").

@def line_number
The line number written at toks[p] (a GOTO target, say) as an int, or -1 if that token is not a plain integer.

@def paren_args
Evaluate a parenthesised, comma-separated argument list starting at the "(" at p. Returns (values, position after the ")", error).

@def parse_target
Read a variable reference at p: a name, or a name with subscripts. Returns (key, subscripts or None, next position, error). The key includes
the type suffix, so A and A$ are different variables.

@def assign
Store `v` in the variable `key`, or in the array element `key(subs)`, converting it to the variable's type first. Returns an error code or "":
"TM" for a type mismatch, "OV" for a number out of range, "LS" for a string over 255 characters, "BS" for a bad subscript.

@def number_arg
A numeric argument as a whole number. Returns (int, "") or (0, "TM") if it is a string. Fractions round down, as BASIC does.

@def st_print
PRINT. Handles `@ position`, USING, and the separators: ";" joins, "," moves to the next 16-column field (or the next line past column 48),
TAB( and SPC(. A newline is printed at the end unless the statement ends in ; or ,. Numbers print with a leading sign slot and a trailing space.

@def print_using
PRINT USING "format"; values: format each value by the template and print the result. A trailing ; or , suppresses the newline.

@def st_let
Assignment: `LET A=expr`, or just `A=expr`. The target may be an array element.

@def goto_line
Jump to `line`, or "SN" if the number was not a valid line number.

@def st_goto
GOTO line.

@def st_gosub
GOSUB line: push the place to come back to (the line and the position after the number) and jump. At most 400 deep ("OM" beyond that).

@def st_return
RETURN: pop the saved place and carry on after the GOSUB. "RG" if there is no GOSUB to return to.

@def st_if
IF cond THEN ... [ELSE ...]. A true condition continues after THEN (a bare number there means GOTO that line). A false one skips to the
matching ELSE, counting nested IFs, or to the next line if there is none.

@def st_else
Reached by running into ELSE after a finished THEN part: the rest of the line belongs to the false branch, so skip it.

@def st_for
FOR v = start TO limit [STEP s]. Sets the variable and pushes a loop frame (variable, limit, step, line, position). Starting a FOR on a
variable that already has a frame replaces that frame, which also discards any loops nested inside it. At most 100 loops ("OM").

@def st_next
NEXT [v[,v...]]. Adds the step to the loop variable and jumps back to just after the FOR if the loop should go on, otherwise pops the frame.
The test is made here, at NEXT, so a loop body always runs once, as on Level II. "NF" if there is no matching FOR.

@def st_on
ON expr GOTO/GOSUB line,line,... picks the nth line (nothing happens if n is 0 or too big). Also ON ERROR GOTO line, which sets the error handler.

@def st_input
INPUT ["prompt";] variables. Prints the prompt and leaves the machine waiting: `run` returns "waiting" and the host calls submit with the
typed line, which feed_input handles. Only allowed inside a program ("ID" in direct mode).

@def st_read
READ variables: take the next DATA items, converting each to the variable's type. "OD" when the data runs out.

@def st_data
DATA is only read by build_data; when execution reaches the statement it does nothing.

@def st_restore
RESTORE: read DATA again from the first item.

@def st_dim
DIM A(n[,m...]) ...: create arrays; each subscript runs from 0 to the number given. "DD" if the array exists already.

@def st_rem
REM: ignore the rest of the line.

@def st_end
END: stop the program and show READY. It cannot be continued with CONT.

@def st_stop
STOP: like BREAK, so CONT can carry on from here.

@def st_cls
CLS: clear the screen.

@def st_set
SET(x,y): turn on a block-graphics dot (128 x 48 dots).

@def st_reset
RESET(x,y): turn a block-graphics dot off.

@def pixel
Shared by SET and RESET: check the two coordinates (0-127 and 0-47) and change the dot.

@def st_poke
POKE address,value. Writes to video memory (addresses 15360 to 16383) change the screen; any other address is kept in `mem`, which PEEK reads back.

@def st_random
RANDOM: reseed the random generator. Mixes in the step count so repeated calls differ, while a run from the same state stays repeatable.

@def st_tron
TRON: from now on print each line number in brackets as it runs.

@def st_troff
TROFF: stop printing line numbers.

@def deftype_statement
Make the handler for DEFINT, DEFSNG, DEFDBL or DEFSTR. `kind` is the type (0 integer, 1 single, 2 double, 3 string) given to variables whose
names start with the letters listed (A, or a range like A-F). Returns a function because the four statements differ only in `kind`.

@def st_def
DEF FNname(params) = expression. The expression is compiled now and stored under the name; it is evaluated when called. Program lines only.

@def st_clear
CLEAR: forget all variables, arrays and loop and subroutine state. The optional number (string space in BASIC) is evaluated and ignored.

@def st_error
ERROR n: raise error number n as if it had happened. 1 to 24 are the standard codes; larger ones are "UE" (unprintable error).

@def st_resume
RESUME [NEXT | line]: leave the ON ERROR handler. Plain RESUME retries the statement that failed, NEXT carries on after it, and a line number
goes there. "RW" if no error is being handled.

@def reset_run_state
What RUN and NEW clear: variables, arrays, user functions, the control stacks, the error handler, type defaults. Then rebuild the DATA list.

@def st_run
RUN [line]: clear the run state and start at the first line (or the given one).

@def st_edit
EDIT line: hand the keyboard to the line editor (editor.star). Direct mode only. Nothing runs while it is open; edit_key gets each key.

@def edit_key
Give the open line editor one key. The editor says what to print and whether it is finished; on "save" the edited text replaces the program
line (an empty line deletes it) and the caches are cleared. Returns "edit" while still open, "done" when finished.

@def editing
True while the line editor has the keyboard. The host uses this to read single keys instead of whole lines.

@def st_new
NEW: delete the program and everything else.

@def st_cont
CONT: carry on after BREAK or STOP. "CN" if the program changed or ended, since there is nothing to continue.

@def line_range
Read the optional range after LIST or DELETE: "n", "n-m", "-m" or "n-". Returns (low, high, error); with nothing given that is every line.

@def display_text
A program line as LIST shows it: keywords and names in upper case, but text inside quotes, after REM or ', and the items of DATA exactly as typed.

@def st_list
LIST [range]: print the program lines in order.

@def st_delete
DELETE range: remove the lines. "FC" if the range holds no lines.

@line STATEMENTS = {
Statement keyword -> the function that runs it. exec_stmt looks the first keyword of a statement up here. A statement starting with a
variable name is an assignment and does not need an entry.

@def exec_stmt
Run one statement, or move past the end of a line. Returns an error code or "". Looks at the first token: a variable means assignment, a
keyword is looked up in STATEMENTS, a colon is skipped, anything else is a syntax error. Also records where the statement began, for RESUME.

@def run
The interpreter loop: execute up to `budget` statements, one per turn. Returns "done" (back at READY), "waiting" (the program is at an INPUT) or
"budget" (still running; call again). The bound keeps each call short, so a host can stay responsive and stop a runaway program.

@def enter_line
Handle one typed line. A line that starts with a number is stored in the program (or deleted if nothing follows the number); an empty line
just shows a new prompt; anything else is tokenized and set up to run now as a direct command.

@def submit
The host's entry point for a typed line. If a program is waiting at an INPUT the line answers it, otherwise it is a new command or program line.
Then run for up to `budget` statements and return the same status as run.

@def recover
Used by the host if this script itself failed part-way (a bug, not a BASIC error): drop whatever was running and show READY, keeping the
program, variables and screen.

@def set_echo
Whether a typed line is printed back into the output. On for tests and pipes; a terminal already shows the user's typing, so it turns this off.

@def press_keys
Queue characters for INKEY$ to return, one per call.

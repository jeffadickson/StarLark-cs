# The Level II EDIT command, as a function of one keystroke.
#
# EDIT is not line oriented. After `EDIT 100` the machine shows the line number and then reacts to every key at once: a space steps
# the cursor over the next character (which makes it appear), D deletes, I inserts, and so on. This file holds that editor as a
# pure state machine so it can be tested on its own: edit_step(e, key) changes the dict `e` and returns (text to show, action),
# where action is "" (carry on), "save" (store the edited line and finish) or "quit" (finish and keep the original).
#
# The line is held as a list of characters (code points), so a character outside the basic plane is one step, never half of one.
# A key is a one character string, or one of:  "\r" Enter,  "\x1b" Escape,  "\b" Backspace.
#
# Subcommands (n is an optional count typed first, 1 if absent):
#   Space        n   move right over n characters, showing them
#   Backspace    n   move left n characters (in insert mode: delete the character just typed)
#   L                list the rest of the line, then go back to the start of it
#   D            n   delete n characters; the deleted text is shown between backslashes
#   C            n   change the next n characters: each key typed replaces one
#   I                insert what is typed at the cursor, until Escape
#   X                go to the end of the line and insert there
#   H                hack: delete the rest of the line and insert there
#   S c          n   search: move to just before the nth c after the cursor, showing what is passed (the end if there is none)
#   K c          n   kill: delete up to just before the nth c after the cursor (nothing if there is none)
#   Q                quit: leave the line as it was
#   A                abort: throw away the changes so far and start the line again
#   E                end: store the changes and leave, without showing the rest of the line
#   Enter            store the changes, show the rest of the line and leave
#   Escape           leave insert or change mode, or cancel a count or a pending S or K
#
# Not checked against a real machine (written from the manual as remembered): the backslash around deleted text, that S starts at the
# character after the cursor, that a failed S runs to the end, and that a failed K does nothing. Each is one line below.

MAX_LENGTH = 255
DELETE_MARK = "\\"

def edit_head(e):
    return "%d " % e["line"]

# A new edit of line `line` whose text (after the line number) is `text`.
def new_edit(line, text):
    chars = list(text.codepoints())
    return {
        "line": line, "orig": list(chars), "buf": chars, "pos": 0,
        "mode": "cmd",        # "cmd", "insert" or "change"
        "count": 0,           # digits typed so far
        "pending": "",        # "S" or "K" while waiting for the character to look for
        "arg_count": 1,       # the count that was typed before that S or K
        "left": 0,            # characters still to change in change mode
        "insert_from": 0,     # where this insert began; backspace never goes before it
    }

def edit_text(e):
    return "".join(e["buf"])

def _printable(key):
    return len(key) > 0 and ord(key) >= 32 and ord(key) != 127

def _is_backspace(key):
    return key == "\b" or key == "\x7f"

def edit_step(e, key):
    if key == "\r" or key == "\n":
        return ("".join(e["buf"][e["pos"]:]), "save")
    mode = e["mode"]
    if mode == "insert":
        return _insert_key(e, key)
    if mode == "change":
        return _change_key(e, key)
    if e["pending"] != "":
        return _argument_key(e, key)
    return _command_key(e, key)

def _insert_key(e, key):
    if key == "\x1b":
        e["mode"] = "cmd"
        return ("", "")
    if _is_backspace(key):
        if e["pos"] > e["insert_from"]:
            e["pos"] -= 1
            e["buf"] = e["buf"][:e["pos"]] + e["buf"][e["pos"] + 1:]
            return ("\b \b", "")
        return ("", "")
    if _printable(key) and len(e["buf"]) < MAX_LENGTH:
        e["buf"] = e["buf"][:e["pos"]] + [key] + e["buf"][e["pos"]:]
        e["pos"] += 1
        return (key, "")
    return ("", "")

def _change_key(e, key):
    if key == "\x1b":
        e["mode"] = "cmd"
        e["left"] = 0
        return ("", "")
    if _printable(key) and e["pos"] < len(e["buf"]):
        e["buf"] = e["buf"][:e["pos"]] + [key] + e["buf"][e["pos"] + 1:]
        e["pos"] += 1
        e["left"] -= 1
        if e["left"] <= 0 or e["pos"] >= len(e["buf"]):
            e["mode"] = "cmd"
        return (key, "")
    return ("", "")

def _argument_key(e, key):
    command = e["pending"]
    e["pending"] = ""
    if not _printable(key):
        return ("", "")          # Escape (or any control key) cancels the S or K
    want = key.upper()
    buf = e["buf"]
    pos = e["pos"]
    found = -1
    seen = 0
    for j in range(pos + 1, len(buf)):
        if buf[j].upper() == want:
            seen += 1
            if seen == e["arg_count"]:
                found = j
                break
    if command == "S":
        if found < 0:
            found = len(buf)
        e["pos"] = found
        return ("".join(buf[pos:found]), "")
    if found < 0:
        return ("", "")
    e["buf"] = buf[:pos] + buf[found:]
    return (DELETE_MARK + "".join(buf[pos:found]) + DELETE_MARK, "")

def _command_key(e, key):
    if len(key) == 1 and key >= "0" and key <= "9":
        e["count"] = min(255, e["count"] * 10 + int(key))
        return ("", "")
    n = e["count"] if e["count"] > 0 else 1
    e["count"] = 0
    buf = e["buf"]
    pos = e["pos"]
    if key == "\x1b":
        return ("", "")
    if key == " ":
        stop = min(len(buf), pos + n)
        e["pos"] = stop
        return ("".join(buf[pos:stop]), "")
    if _is_backspace(key):
        back = min(n, pos)
        e["pos"] = pos - back
        return ("\b" * back, "")
    k = key.upper()
    if k == "L":
        e["pos"] = 0
        return ("".join(buf[pos:]) + "\n" + edit_head(e), "")
    if k == "D":
        stop = min(len(buf), pos + n)
        if stop == pos:
            return ("", "")
        e["buf"] = buf[:pos] + buf[stop:]
        return (DELETE_MARK + "".join(buf[pos:stop]) + DELETE_MARK, "")
    if k == "C":
        if pos >= len(buf):
            return ("", "")
        e["mode"] = "change"
        e["left"] = n
        return ("", "")
    if k == "I":
        e["mode"] = "insert"
        e["insert_from"] = pos
        return ("", "")
    if k == "X":
        e["pos"] = len(buf)
        e["mode"] = "insert"
        e["insert_from"] = len(buf)
        return ("".join(buf[pos:]), "")
    if k == "H":
        e["buf"] = buf[:pos]
        e["mode"] = "insert"
        e["insert_from"] = pos
        return ("", "")
    if k == "S" or k == "K":
        e["pending"] = k
        e["arg_count"] = n
        return ("", "")
    if k == "Q":
        return ("", "quit")
    if k == "E":
        return ("", "save")
    if k == "A":
        e["buf"] = list(e["orig"])
        e["pos"] = 0
        e["mode"] = "cmd"
        e["pending"] = ""
        return ("\n" + edit_head(e), "")
    return ("", "")              # any other key does nothing

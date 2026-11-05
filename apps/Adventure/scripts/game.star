# The rules. `step(state, line)` takes the game state and one line the player typed, changes the state in place, and returns
# the words to show. State is a plain dict; nothing here prints, so the same code runs in a terminal and in a test.

load("world.star", "ROOMS", "ITEMS", "SYNONYMS", "DIRECTIONS", "START", "MAX_TURNS")

# A new game's state: the starting room, where every item is, and counters. `where` maps each item to a room name, to "inventory", or to None for an
# item that does not exist yet (the parchment and gem appear when the bones and chest are examined). The state is a plain dict, so a game can be saved or compared.
def new_game():
    where = {}
    for name in ITEMS:
        where[name] = ITEMS[name]["where"]
    return {
        "room": START,
        "where": where,              # item -> room name, "inventory", or None (not yet in the world)
        "seen": [START],             # rooms whose long description has been shown
        "turns": 0,
        "lamp_on": False,
        "unlocked": [],              # doors that have been opened
        "done": False,
        "won": False,
        "score": 0,
        "found": [],                 # scoring events already awarded
    }

# Add `points` to the score the first time `key` is earned. Each event scores once however often it is repeated (recorded in `found`).
def award(state, key, points):
    if key not in state["found"]:
        state["found"].append(key)
        state["score"] += points

# Names of the items lying in the room the player is in.
def items_here(state):
    return [n for n in ITEMS if state["where"][n] == state["room"]]

# Names of the items the player is carrying.
def carried(state):
    return [n for n in ITEMS if state["where"][n] == "inventory"]

# True if the player cannot see: the room is dark and either the lamp is off or the player is not carrying it.
def is_dark(state):
    room = ROOMS[state["room"]]
    return room.get("dark", False) and not (state["lamp_on"] and "lamp" in carried(state))

# Match a typed noun against the names of the items in `where_list`; returns the item's name, or None. An item has several names (lamp, lantern, brass lamp).
def find_item(state, word, where_list):
    # Match a typed noun against item names, looking only in the places given.
    for name in where_list:
        if word in ITEMS[name]["names"]:
            return name
    return None

# The text for the room: its name, its long description (the first time and on LOOK), the items here, and the exits. In the dark that is replaced by a
# warning. Marks the room as seen.
def describe(state, full):
    room = ROOMS[state["room"]]
    if is_dark(state):
        return "It is pitch dark. You might be eaten by something with too many teeth."
    out = ["== " + room["name"] + " =="]
    if full or state["room"] not in state["seen"]:
        out.append(room["text"])
        if state["room"] not in state["seen"]:
            state["seen"].append(state["room"])
    here = items_here(state)
    if here:
        out.append("You see: " + ", ".join([ITEMS[n]["names"][0] for n in here]) + ".")
    exits = sorted(room["exits"].keys())
    out.append("Exits: " + ", ".join(exits) + ".")
    return "\n".join(out)

# Go in `direction`. A dark room is dangerous to go deeper into: heading anywhere but back out ends the game. A locked exit stays shut until its door is unlocked.
# Entering the vault scores points.
def move(state, direction):
    room = ROOMS[state["room"]]
    if direction not in room["exits"]:
        return "You can't go " + direction + " from here."
    if is_dark(state) and direction not in ["south", "up", "west", "out"]:
        # Blundering about in the dark is dangerous, but only if you head deeper in.
        state["done"] = True
        return "You stumble forward in the dark and step into nothing. It is a long way down.\n*** YOU HAVE DIED ***"
    locked = room.get("locked", {})
    if direction in locked and locked[direction] not in state["unlocked"]:
        return "The " + locked[direction] + " is locked."
    state["room"] = room["exits"][direction]
    if state["room"] == "vault":
        award(state, "vault", 20)
    return describe(state, False)

# Look at an item here or carried. Examining the bones reveals the parchment and examining the chest reveals the gem, by moving them into the room (each also scores).
def examine(state, word):
    if word == "":
        return "Examine what?"
    pool = items_here(state) + carried(state)
    if is_dark(state):
        return "You can't see a thing."
    name = find_item(state, word, pool)
    if name == None:
        return "You see no " + word + " here."
    text = ITEMS[name]["text"]
    if name == "bones" and state["where"]["parchment"] == None:
        state["where"]["parchment"] = state["room"]
        award(state, "parchment", 5)
        text += "\nYou prise the parchment from the bony fingers; it falls to the floor."
    if name == "chest" and state["where"]["gem"] == None:
        state["where"]["gem"] = state["room"]
        award(state, "chest", 10)
        text += "\nInside, resting on velvet, is a gem."
    return text

# Pick an item up if it is here and can be taken. Taking the gem wins the game.
def take(state, word):
    if word == "":
        return "Take what?"
    if is_dark(state):
        return "You fumble around in the dark and find nothing."
    name = find_item(state, word, items_here(state))
    if name == None:
        return "You see no " + word + " here."
    if not ITEMS[name]["take"]:
        return "You can't take the " + word + "."
    state["where"][name] = "inventory"
    if name == "key":
        award(state, "key", 10)
    if name == "gem":
        award(state, "gem", 50)
        state["won"] = True
        state["done"] = True
        return "Taken.\nThe gem blazes and the vault fills with light. You have found the treasure!\n*** YOU HAVE WON ***"
    return "Taken."

# Put an item down in the current room. Dropping the lamp turns it off.
def drop(state, word):
    name = find_item(state, word, carried(state))
    if name == None:
        return "You aren't carrying that."
    state["where"][name] = state["room"]
    if name == "lamp":
        state["lamp_on"] = False
    return "Dropped."

# Turn the lamp on or off. Needs the lamp in the inventory. Turning it on in a dark room shows the room.
def use_light(state, on):
    if "lamp" not in carried(state):
        return "You have no lamp."
    if state["lamp_on"] == on:
        return "The lamp is already " + ("on." if on else "off.")
    state["lamp_on"] = on
    if on:
        award(state, "light", 5)
        return "The lamp glows warmly." + ("\n" + describe(state, True) if ROOMS[state["room"]].get("dark", False) else "")
    return "The lamp goes out."

# Unlock the first locked door in the room, if the player carries the key.
def unlock(state):
    room = ROOMS[state["room"]]
    locked = room.get("locked", {})
    if not locked:
        return "There is nothing here to unlock."
    if "key" not in carried(state):
        return "You have nothing that fits."
    for direction in locked:
        if locked[direction] not in state["unlocked"]:
            state["unlocked"].append(locked[direction])
            award(state, "door", 10)
            return "The bronze key turns with a groan. The " + locked[direction] + " swings open."
    return "It is already unlocked."

# List what the player is carrying.
def inventory(state):
    c = carried(state)
    if not c:
        return "You are carrying nothing."
    return "You are carrying: " + ", ".join([ITEMS[n]["names"][0] for n in c]) + "."

# Turn a typed line into (verb, noun). Filler words (the, a, an, to, at) are dropped, synonyms are mapped to one verb, and a bare direction like "n" or "north"
# becomes "go" plus the direction. Returns ("", "") for an empty line.
def parse(line):
    words = [w for w in line.lower().split(" ") if w != "" and w not in ["the", "a", "an", "to", "at"]]
    if not words:
        return "", ""
    verb = SYNONYMS.get(words[0], words[0])
    if words[0] in DIRECTIONS:
        verb = "go"
        words = ["go"] + words
    elif words[0] in SYNONYMS and SYNONYMS[words[0]] in DIRECTIONS:
        words = ["go", SYNONYMS[words[0]]]
        verb = "go"
    noun = " ".join(words[1:])
    if verb in SYNONYMS.values() and noun in SYNONYMS:
        noun = SYNONYMS[noun]
    return verb, noun

# One turn of the game: parse the line, run the matching action, and return the reply. A finished game only reports the score. Running out of turns (MAX_TURNS)
# ends the game. An unknown verb gets a polite reply and still counts as a turn.
def step(state, line):
    if state["done"]:
        return "The game is over. Final score: " + str(state["score"]) + "."
    verb, noun = parse(line)
    state["turns"] += 1
    if verb == "":
        state["turns"] -= 1
        return "Say something."
    reply = ""
    if verb == "go":
        if noun in SYNONYMS and SYNONYMS[noun] in DIRECTIONS:
            noun = SYNONYMS[noun]
        reply = move(state, noun) if noun != "" else "Go where?"
    elif verb == "look":
        reply = describe(state, True)
    elif verb == "examine":
        reply = examine(state, noun)
    elif verb == "take":
        reply = take(state, noun)
    elif verb == "drop":
        reply = drop(state, noun)
    elif verb == "inventory":
        reply = inventory(state)
    elif verb == "light" or (verb == "turn" and noun.startswith("on")) or (verb == "switch" and noun.startswith("on")):
        reply = use_light(state, True)
    elif verb == "extinguish" or (verb == "turn" and noun.startswith("off")) or (verb == "switch" and noun.startswith("off")):
        reply = use_light(state, False)
    elif verb == "unlock" or verb == "open":
        reply = unlock(state)
    elif verb == "xyzzy":
        reply = "Nothing happens. (This is not that kind of cave.)"
    elif verb == "score":
        reply = "You have " + str(state["score"]) + " points in " + str(state["turns"]) + " turns."
    elif verb == "help":
        reply = "Try: north, south, look, take lamp, examine sign, light lamp, inventory, unlock door, score, quit."
    elif verb == "quit":
        state["done"] = True
        reply = "You give up. Final score: " + str(state["score"]) + "."
    else:
        reply = "I don't know how to \"" + verb + "\"."
    if not state["done"] and state["turns"] >= MAX_TURNS:
        state["done"] = True
        reply += "\nThe night closes in. You have run out of time."
    return reply

# THE LAMPLIGHTER'S CAVE
#
# A small text adventure in the tradition of Colossal Cave. The whole world is data in this file; the rules of play
# are in game.star; the C# host only reads a line, runs it, and prints what comes back.
#
# A room is a record: a name, a description (shown the first time and on LOOK), and its exits. An item says where it
# is, what it is called, and what you can do with it. "dark" rooms need the lamp.

# Every room: `name`, `text` (shown the first time and on LOOK), `exits` (direction -> room), an optional `dark` flag, and an optional `locked` map of
# direction -> the door that blocks it. Adding a room is only adding an entry.
ROOMS = {
    "clearing": {
        "name": "Forest clearing",
        "text": "You stand in a sunlit clearing. A mossy path winds north to a low hill. To the east a stream " +
                "chatters over stones. A weathered signpost leans at the edge of the trees.",
        "exits": {"north": "hill", "east": "stream"},
    },
    "stream": {
        "name": "By the stream",
        "text": "Cold water runs over smooth stones. Something glints in the shallows. The clearing lies to the west.",
        "exits": {"west": "clearing"},
    },
    "hill": {
        "name": "Hillside",
        "text": "A grassy hill. In its flank is the mouth of a cave, breathing cool air. A rusted iron gate stands " +
                "open in front of it. The clearing is back to the south.",
        "exits": {"south": "clearing", "in": "entrance", "north": "entrance"},
    },
    "entrance": {
        "name": "Cave entrance",
        "text": "Daylight fades behind you. Passages lead down into the dark, and the way out is to the south.",
        "exits": {"south": "hill", "down": "hall", "north": "hall"},
        "dark": True,
    },
    "hall": {
        "name": "Hall of echoes",
        "text": "A vast hall. Your footsteps return to you from a hundred directions. A narrow stair climbs " +
                "up, an archway leads east, and a crack in the floor leads down. Names are scratched into the wall.",
        "exits": {"up": "entrance", "south": "entrance", "east": "bridge", "down": "pit"},
        "dark": True,
    },
    "bridge": {
        "name": "Stone bridge",
        "text": "A narrow stone bridge spans a chasm. Far below, water roars. On the other side, east, a door " +
                "of green bronze is set into the rock. The hall is to the west.",
        "exits": {"west": "hall", "east": "vault"},
        "dark": True,
        "locked": {"east": "bronze door"},
    },
    "pit": {
        "name": "Bottom of the pit",
        "text": "You have climbed down into a damp pit. Bones lie scattered here. There is no way on, except " +
                "back up.",
        "exits": {"up": "hall"},
        "dark": True,
    },
    "vault": {
        "name": "The Lamplighter's vault",
        "text": "Gold light spills from a great lantern that has burned here for a thousand years. Beneath it " +
                "sits a small chest. Written on the wall: THE LIGHT IS THE TREASURE.",
        "exits": {"west": "bridge"},
        "dark": False,
    },
}

# Items: where they start, what they are called, what they weigh in the player's mind (can they be taken), and text.
# Every item: its starting room (`where`, None if it only appears later), the names a player may use (the first is the one shown), its description, and whether it can be taken.
ITEMS = {
    "lamp": {
        "where": "clearing",
        "names": ["lamp", "lantern", "brass lamp"],
        "text": "A brass lamp, dented but sound. It smells faintly of oil.",
        "take": True,
    },
    "sign": {
        "where": "clearing",
        "names": ["sign", "signpost", "post"],
        "text": "Carved into the wood: \"BEWARE THE DARK. THE LAMP IS YOUR FRIEND. SAY XYZZY TO NO ONE.\"",
        "take": False,
    },
    "key": {
        "where": "stream",
        "names": ["key", "bronze key", "glint"],
        "text": "A small bronze key, green with age.",
        "take": True,
    },
    "bones": {
        "where": "pit",
        "names": ["bones", "skeleton"],
        "text": "Old bones. One hand still grips a scrap of parchment.",
        "take": False,
    },
    "parchment": {
        "where": None,
        "names": ["parchment", "scrap", "paper"],
        "text": "In a shaky hand: \"The door answers only to bronze.\"",
        "take": True,
    },
    "chest": {
        "where": "vault",
        "names": ["chest", "box"],
        "text": "A small chest of dark wood, its lid slightly open.",
        "take": False,
    },
    "gem": {
        "where": None,
        "names": ["gem", "jewel", "stone"],
        "text": "A pale blue gem that holds a small flame at its heart.",
        "take": True,
    },
}

# Things the player can type that are not movement. Each is a short rule written as data.
# Words the parser treats as the same verb or direction: single letters, and alternative verbs such as get/grab/pick for take.
SYNONYMS = {
    "n": "north", "s": "south", "e": "east", "w": "west", "u": "up", "d": "down",
    "x": "examine", "look": "look", "l": "look", "i": "inventory", "inv": "inventory",
    "get": "take", "grab": "take", "pick": "take", "drop": "drop", "put": "drop",
    "go": "go", "walk": "go", "enter": "go", "climb": "go",
    "q": "quit", "quit": "quit", "exit": "quit",
}

# The words that mean movement; a line starting with one of these is a "go" command.
DIRECTIONS = ["north", "south", "east", "west", "up", "down", "in", "out"]

# The room the player starts in.
START = "clearing"
# The game ends after this many turns, win or not.
MAX_TURNS = 200

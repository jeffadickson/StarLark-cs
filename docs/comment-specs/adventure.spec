@@ Comments for the Adventure game scripts.

@file apps/Adventure/scripts/game.star
@def new_game
A new game's state: the starting room, where every item is, and counters. `where` maps each item to a room name, to "inventory", or to None for an
item that does not exist yet (the parchment and gem appear when the bones and chest are examined). The state is a plain dict, so a game can be saved or compared.

@def award
Add `points` to the score the first time `key` is earned. Each event scores once however often it is repeated (recorded in `found`).

@def items_here
Names of the items lying in the room the player is in.

@def carried
Names of the items the player is carrying.

@def is_dark
True if the player cannot see: the room is dark and either the lamp is off or the player is not carrying it.

@def find_item
Match a typed noun against the names of the items in `where_list`; returns the item's name, or None. An item has several names (lamp, lantern, brass lamp).

@def describe
The text for the room: its name, its long description (the first time and on LOOK), the items here, and the exits. In the dark that is replaced by a
warning. Marks the room as seen.

@def move
Go in `direction`. A dark room is dangerous to go deeper into: heading anywhere but back out ends the game. A locked exit stays shut until its door is unlocked.
Entering the vault scores points.

@def examine
Look at an item here or carried. Examining the bones reveals the parchment and examining the chest reveals the gem, by moving them into the room (each also scores).

@def take
Pick an item up if it is here and can be taken. Taking the gem wins the game.

@def drop
Put an item down in the current room. Dropping the lamp turns it off.

@def use_light
Turn the lamp on or off. Needs the lamp in the inventory. Turning it on in a dark room shows the room.

@def unlock
Unlock the first locked door in the room, if the player carries the key.

@def inventory
List what the player is carrying.

@def parse
Turn a typed line into (verb, noun). Filler words (the, a, an, to, at) are dropped, synonyms are mapped to one verb, and a bare direction like "n" or "north"
becomes "go" plus the direction. Returns ("", "") for an empty line.

@def step
One turn of the game: parse the line, run the matching action, and return the reply. A finished game only reports the score. Running out of turns (MAX_TURNS)
ends the game. An unknown verb gets a polite reply and still counts as a turn.

@file apps/Adventure/scripts/world.star
@line ROOMS = {
Every room: `name`, `text` (shown the first time and on LOOK), `exits` (direction -> room), an optional `dark` flag, and an optional `locked` map of
direction -> the door that blocks it. Adding a room is only adding an entry.

@line ITEMS = {
Every item: its starting room (`where`, None if it only appears later), the names a player may use (the first is the one shown), its description, and whether it can be taken.

@line SYNONYMS = {
Words the parser treats as the same verb or direction: single letters, and alternative verbs such as get/grab/pick for take.

@line DIRECTIONS = [
The words that mean movement; a line starting with one of these is a "go" command.

@line START = "clearing"
The room the player starts in.

@line MAX_TURNS = 200
The game ends after this many turns, win or not.

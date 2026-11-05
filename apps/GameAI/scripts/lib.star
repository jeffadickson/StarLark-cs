# The kinds of action a player may take. An enum rejects anything else.
Kind = enum("move", "attack", "rest")
# What a player returns each turn: the kind of action and, for a move, a direction (+1 or -1).
Action = record(kind = str, dir = field(int, 0))

# An action: step one cell in the direction `dir` (any positive number is +1, anything else -1).
def move(dir):
    return Action(kind = Kind("move").value, dir = 1 if dir > 0 else -1)

# An action: attack the foe. Costs energy and only works within range 2.
def attack():
    return Action(kind = Kind("attack").value)

# An action: do nothing and recover energy.
def rest():
    return Action(kind = Kind("rest").value)

# The direction (+1 or -1) from `me` towards `foe`.
def toward(me, foe):
    return 1 if foe.pos > me.pos else -1

# How far apart two fighters are on the line (an absolute value, written out by hand to keep the example plain; standard Starlark has no abs).
def distance(a, b):
    d = a.pos - b.pos
    return d if d >= 0 else -d

Kind = enum("move", "attack", "rest")
Action = record(kind = str, dir = field(int, 0))

def move(dir):
    return Action(kind = Kind("move").value, dir = 1 if dir > 0 else -1)

def attack():
    return Action(kind = Kind("attack").value)

def rest():
    return Action(kind = Kind("rest").value)

def toward(me, foe):
    return 1 if foe.pos > me.pos else -1

def distance(a, b):
    d = a.pos - b.pos
    return d if d >= 0 else -d

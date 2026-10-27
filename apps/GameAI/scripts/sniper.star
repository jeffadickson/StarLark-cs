load("lib.star", "move", "attack", "rest", "toward", "distance")

def act(state):
    foe = state.foes[0]
    d = distance(state.me, foe)
    if d <= 2 and state.me.energy >= 2:
        return attack()
    if d < 2:
        return move(-toward(state.me, foe))
    if d > 2:
        return move(toward(state.me, foe))
    return rest()

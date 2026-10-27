load("lib.star", "move", "attack", "rest", "toward", "distance")

def act(state):
    foe = state.foes[0]
    if distance(state.me, foe) <= 2 and state.me.energy >= 2:
        return attack()
    if distance(state.me, foe) > 2:
        return move(toward(state.me, foe))
    return rest()

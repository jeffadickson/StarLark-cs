load("lib.star", "move", "attack", "rest", "toward", "distance")

def act(state):
    foe = state.foes[0]
    if state.me.energy < 4:
        return rest()
    if distance(state.me, foe) <= 2:
        return attack()
    return rest()

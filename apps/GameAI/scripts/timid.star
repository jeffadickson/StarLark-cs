load("lib.star", "move", "attack", "rest", "toward", "distance")

# Strategy: only fight with plenty of energy. Rest below 4 energy; attack when in range; otherwise rest.
def act(state):
    foe = state.foes[0]
    if state.me.energy < 4:
        return rest()
    if distance(state.me, foe) <= 2:
        return attack()
    return rest()

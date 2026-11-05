load("lib.star", "move", "attack", "rest", "toward", "distance")

# Strategy: stay at range 2. Attack when in range and with energy, back away when too close, approach when too far, otherwise rest.
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

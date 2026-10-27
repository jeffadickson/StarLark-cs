load("lib.star", "rest")

# Burns far more than its per-turn step budget.
def act(state):
    total = 0
    for i in range(100000):
        total += i
    return rest()

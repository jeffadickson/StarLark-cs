P = record(x = int, y = int)
def run():
    t = 0
    for i in range(8000):
        p = P(x = i, y = i + 1)
        t += p.x * p.y
    return t
r = run()

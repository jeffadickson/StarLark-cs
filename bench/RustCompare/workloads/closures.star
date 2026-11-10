def run():
    adders = [(lambda x, k = k: x + k) for k in range(50)]
    t = 0
    for i in range(1500):
        for f in adders:
            t = f(t) % 100003
    return t
r = run()

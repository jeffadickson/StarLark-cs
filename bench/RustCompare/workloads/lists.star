def run():
    xs = [(i * 37) % 1009 for i in range(8000)]
    ys = sorted(xs)
    zs = [x for x in ys[100:7000] if x % 3 == 0]
    return len(zs) + sum_of(zs)
def sum_of(xs):
    t = 0
    for x in xs:
        t += x
    return t
r = run()

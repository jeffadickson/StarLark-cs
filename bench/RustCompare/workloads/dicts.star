def run():
    d = {}
    for i in range(6000):
        d['k' + str(i % 500)] = i
    t = 0
    for k in d:
        t += d[k]
    for k, v in d.items():
        t += v
    return t
r = run()

def run():
    t = 0.0
    for i in range(120000):
        t += (i * 0.5) / (i + 1.0)
        if t > 1e9:
            t = 0.0
    return t
r = run()

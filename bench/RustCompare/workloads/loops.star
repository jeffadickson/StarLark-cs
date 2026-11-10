def run():
    t = 0
    for i in range(300):
        for j in range(300):
            t += i * j % 7
    return t
r = run()

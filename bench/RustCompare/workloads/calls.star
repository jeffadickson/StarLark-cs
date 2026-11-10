def f(a, b):
    return a + b
def run():
    t = 0
    for i in range(60000):
        t = f(t, i)
    return t
r = run()

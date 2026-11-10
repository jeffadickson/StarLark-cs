def run():
    a = 1
    for i in range(1, 600):
        a = a * i
    return len(str(a))
r = run()

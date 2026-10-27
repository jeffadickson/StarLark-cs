# title: spawn copies mutable arguments: the caller can keep changing its own list
# dialect: futures
# output: [6, 4]

def total(xs):
    t = 0
    for x in xs:
        t += x
    return t

def run():
    xs = [1, 2, 3]
    f = spawn(total, xs)
    xs.append(100)
    return [f.get(), len(xs)]

print(run())

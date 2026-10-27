# title: A task that changes its argument changes only its own copy
# dialect: futures
# output: [2, [1]]

def grow(xs):
    xs.append(99)
    return len(xs)

def run():
    xs = [1]
    n = spawn(grow, xs).get()
    return [n, xs]

print(run())

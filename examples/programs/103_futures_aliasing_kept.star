# title: Two arguments that are the same list are still the same list inside the task
# dialect: futures
# output: 1

def check(a, b):
    a.append(1)
    return len(b)

def run():
    xs = []
    return spawn(check, xs, xs).get()

print(run())

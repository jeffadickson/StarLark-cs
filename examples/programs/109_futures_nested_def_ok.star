# title: A nested function that reads only globals and its arguments can be spawned
# dialect: futures
# output: 105

OFFSET = 100

def run():
    def inner(x):
        return x + OFFSET
    return spawn(inner, 5).get()

print(run())

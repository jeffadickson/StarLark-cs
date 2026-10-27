# title: Spawning a function that is already running is recursion
# dialect: futures
# error: called recursively

def f(n):
    return spawn(f, n).get()

f(1)

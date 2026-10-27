# title: A future that is never collected is an error, not a silent cancellation
# dialect: futures
# error: never collected

def f():
    return 1

spawn(f)

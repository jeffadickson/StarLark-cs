# title: A future has no truth value
# dialect: futures
# error: no truth value

def f():
    return 1

x = spawn(f)
if x:
    pass

# title: A future cannot be a dict key
# dialect: futures
# error: unhashable

def f():
    return 1

x = spawn(f)
d = {x: 1}

# title: A task cannot call parallel_map either
# dialect: futures
# error: cannot be called from inside a task

def child(x):
    return x

def parent():
    return parallel_map(child, [1, 2])

spawn(parent).get()

# title: The result of a task is frozen
# dialect: futures
# error: frozen

def make():
    return [1, 2]

xs = spawn(make).get()
xs.append(3)

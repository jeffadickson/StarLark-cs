# title: spawn starts work and get() collects the result
# dialect: futures
# output: 3
# output: 15

def add(a, b = 10):
    return a + b

f = spawn(add, 1, b = 2)
g = spawn(add, 5)
print(f.get())
print(g.get())

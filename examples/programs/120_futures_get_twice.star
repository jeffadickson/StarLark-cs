# title: get() can be called more than once on the same future
# dialect: futures
# output: 42
# output: 42

def f():
    return 42

x = spawn(f)
print(x.get())
print(x.get())

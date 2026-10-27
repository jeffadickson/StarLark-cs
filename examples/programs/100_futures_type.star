# title: type() and repr() of a future
# dialect: futures
# output: future
# output: <future>
# output: 1

def f():
    return 1

x = spawn(f)
print(type(x))
print(repr(x))
print(x.get())

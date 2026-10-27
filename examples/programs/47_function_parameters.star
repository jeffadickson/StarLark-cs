# title: Defaults, keyword arguments, *args and **kwargs
# output: [(1, 3), (1, 2)]
# output: (1, 2, (3, 4), {"k": 5})
# output: 11
# output: [2, 1, 1]

def f(x, y = 3):
    return (x, y)

def g(a, b, *rest, **named):
    return (a, b, rest, named)

def h(**kw):
    return len(kw)

print([f(1), f(1, 2)])
print(g(1, 2, 3, 4, k = 5))
print(f(*[5, 6])[0] + f(**{"x": 6})[0])
print([f(y = 1, x = 2)[0], f(1, y = 1)[0], h(a = 1)])

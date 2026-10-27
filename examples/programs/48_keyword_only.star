# title: Keyword-only and positional-only parameters
# dialect: keyword-only, positional-only
# output: (1, 2, 3)
# output: 7

def f(a, *, b = 2, c):
    return (a, b, c)

def g(x, /, y):
    return x + y

print(f(1, c = 3))
print(g(3, y = 4))

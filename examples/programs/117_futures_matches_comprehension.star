# title: parallel_map gives the same answer as a list comprehension
# dialect: futures
# output: True

def f(x):
    return x * 3 + 1

xs = range(50)
print(parallel_map(f, xs) == [f(x) for x in xs])

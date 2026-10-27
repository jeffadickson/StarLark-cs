# title: parallel_map runs a function over a list and returns results in input order
# dialect: futures
# output: [1, 4, 9, 16, 25]
# output: [1, 2, 3]
# output: []

def square(n):
    return n * n

print(parallel_map(square, [1, 2, 3, 4, 5]))
print(parallel_map(len, ["a", "bb", "ccc"]))
print(parallel_map(square, range(0)))

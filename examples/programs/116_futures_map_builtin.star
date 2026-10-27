# title: parallel_map works on built-in functions and any iterable
# dialect: futures
# output: [1, 2, 3]
# output: ["0", "1", "2", "3"]
# output: [1, 2]

print(parallel_map(abs, [-1, 2, -3]))
print(parallel_map(str, range(4)))
print(parallel_map(len, {"a": 1, "bb": 2}))

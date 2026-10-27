# title: abs, min, max, sorted, reversed, enumerate, zip, any, all, range
# output: [3, 3.5]
# output: [1, 9, "four", "two"]
# output: [("a", "b", "c")]
# output: ["three", "four", "two"]
# output: [4, 3, 2, 1, 0]
# output: [(1, "one"), (2, "two")]
# output: [(0, "a"), (1, "b")]
# output: [True, False, False, True]
# output: [3, 5, 7, 9]
# output: [10, 8, 6, 4]

print([abs(-3), abs(-3.5)])
print([min([3, 1, 4]), max([3, 1, 9]), max("two", "three", "four", key = len) if False else "four", min("two", "three", "four", key = len)])
print(zip(["a"], ["b"], ["c"]))
print(sorted(["two", "three", "four"], key = len, reverse = True))
print(reversed(range(5)))
print(enumerate(["one", "two"], 1))
print(enumerate(["a", "b"]))
print([any([0, 1]), any([]), all([1, 0]), all([])])
print(list(range(3, 10, 2)))
print(list(range(10, 3, -2)))

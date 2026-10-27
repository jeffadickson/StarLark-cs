# title: List comprehensions: conditions, nesting, unpacking and their own scope
# output: [0, 1, 4, 9, 16]
# output: [0, 4, 16]
# output: [(0, 1), (0, 2), (0, 3), (0, 4), (2, 3), (2, 4)]
# output: [11, "oo!"]
# output: 1

print([x * x for x in range(5)])
print([x * x for x in range(5) if x % 2 == 0])
print([(x, y) for x in range(5) if x % 2 == 0 for y in range(5) if y > x])
print([x * y + z for (x, y), z in [((2, 3), 5), (("o", 2), "!")]])
x = 1
_ = [x for x in [2]]
print(x)

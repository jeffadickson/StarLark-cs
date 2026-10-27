# title: Tuples: construction, unpacking, concatenation, repetition, comparison and hashing
# output: [(), (1,), (1, 2)]
# output: 3 4
# output: (1, 2, 3, 4)
# output: (True, "a", True, "a")
# output: [True, False]
# output: {(1, 2): "pair"}

print([(), (1,), (1, 2)])
a, b = 3, 4
print(a, b)
print((1, 2) + (3, 4))
print(2 * (True, "a"))
print([(1, 2) < (1, 3), (2,) < (1, 9)])
print({(1, 2): "pair"})

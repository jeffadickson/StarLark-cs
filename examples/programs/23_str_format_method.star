# title: str.format with automatic, numbered and named fields
# output: a2b3c1
# output: (one, zero)
# output: {braces} 7
# output: a1b2c

print("a{x}b{y}c{}".format(1, x = 2, y = 3))
print("({1}, {0})".format("zero", "one"))
print("{{braces}} {}".format(7))
print("a{}b{}c".format(1, 2))

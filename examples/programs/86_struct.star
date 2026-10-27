# title: struct: fields, equality, repr, and use as a record-like value
# output: 1 x
# output: struct(a = 1, b = "x")
# output: [True, False]
# output: ["a", "b"]
# output: [True, False, 7]

s = struct(a = 1, b = "x")
print(s.a, s.b)
print(s)
print([s == struct(a = 1, b = "x"), s == struct(a = 2, b = "x")])
print(dir(s))
print([hasattr(s, "a"), hasattr(s, "z"), getattr(struct(q = 7), "q")])

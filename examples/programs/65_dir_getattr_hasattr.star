# title: dir, getattr and hasattr see methods; a missing attribute gets the default
# output: True
# output: ["append", "clear", "extend", "index", "insert", "pop", "remove"]
# output: [False, True]
# output: ["b", "n", "n", ""]
# output: mydefault

print("find" in dir("x"))
print(dir([]))
print([hasattr("", "nonexistent"), hasattr("", "find")])
print(getattr("banana", "split")("a"))
print(getattr("banana", "myattr", "mydefault"))

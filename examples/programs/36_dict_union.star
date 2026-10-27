# title: dict | dict keeps left-hand order, then right-hand additions
# output: {"a": 3, "b": 2, "c": 4}
# output: {"x": 1}

print({"a": 1, "b": 2} | {"a": 3, "c": 4})
d = {"x": 1}
print(d | {})

# title: type() names every kind of value
# output: ["NoneType", "bool", "int", "float", "string", "bytes", "list", "tuple", "dict", "set", "range", "function", "builtin_function_or_method"]

def f():
    pass

print([type(None), type(True), type(1), type(1.5), type("s"), type(b"b"), type([]), type(()), type({}), type(set()), type(range(3)), type(f), type(len)])

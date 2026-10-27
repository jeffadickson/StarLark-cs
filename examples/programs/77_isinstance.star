# title: isinstance with built-in types, containers, unions and typing.Any
# dialect: types
# output: [True, True, True, True, True]
# output: [True, False, True, False]
# output: [True, True, False]

print([isinstance(1, int), isinstance("s", str), isinstance(None, None), isinstance([1], list[int]), isinstance(1, typing.Any)])
print([isinstance(1, int | str), isinstance(1.5, int | str), isinstance({"a": 1}, dict[str, int]), isinstance({"a": "b"}, dict[str, int])])
print([isinstance((1, "a"), tuple[int, str]), isinstance((1, 2, 3), tuple[int, ...]), isinstance((1, 2), tuple[int, str])])

# title: Types are values: they print, combine with |, and can be checked with eval_type
# dialect: types
# output: list[int]
# output: int | str
# output: typing.Callable[[int], str]
# output: (int, str)
# output: True
# output: False

print(eval_type(list[int]))
print(eval_type(int | str))
print(eval_type(typing.Callable[[int], str]))
print(eval_type(tuple[int, str]))
print(isinstance(1, eval_type(int)))
print(isinstance("a", eval_type(int)))

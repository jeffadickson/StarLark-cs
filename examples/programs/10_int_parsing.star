# title: int() with bases and prefixes
# output: [255, 255, 255, 5, 7, 15, -10, 42]

print([int("ff", 16), int("0xff", 16), int("0xff", 0), int("0b101", 0), int("0o7", 0), int("1111", 2), int("-a", 16), int("+42")])

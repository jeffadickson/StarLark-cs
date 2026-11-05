# title: int() with a base outside 2..36 is an error
# error: not a valid base

int("123", 37)

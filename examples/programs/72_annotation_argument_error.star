# title: A wrong argument type fails at the call, naming the argument
# dialect: types
# error: does not match the type annotation `int` for argument `n`

def double(n: int) -> int:
    return n * 2

double("two")

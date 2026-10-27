# title: A wrong return type fails at the return
# dialect: types
# error: for return type

def f() -> int:
    return "oops"

f()

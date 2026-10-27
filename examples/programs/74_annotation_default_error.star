# title: A default that breaks its own annotation fails when the function is defined
# dialect: types
# error: does not match the type annotation `int`

def f(x: int = None):
    pass

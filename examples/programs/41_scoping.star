# title: A name bound anywhere in a function is local to all of it
# error: referenced before assignment

y = "global"

def hello():
    z = y
    y = "local"
    return z

hello()

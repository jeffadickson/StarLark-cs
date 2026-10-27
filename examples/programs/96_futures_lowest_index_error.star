# title: When several tasks fail, all_of raises the lowest-index failure
# dialect: futures
# error: bad 3

def f(i):
    if i >= 3:
        fail("bad " + str(i))
    return i

all_of([spawn(f, i) for i in range(8)])

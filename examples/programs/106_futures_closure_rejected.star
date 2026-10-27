# title: A function that captures a running function's variable cannot be spawned
# dialect: futures
# error: captures variables

def run():
    n = 5
    def inner():
        return n
    return spawn(inner).get()

run()

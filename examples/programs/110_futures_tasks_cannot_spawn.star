# title: A task cannot start other tasks
# dialect: futures
# error: cannot be called from inside a task

def child():
    return 1

def parent():
    return spawn(child).get()

spawn(parent).get()

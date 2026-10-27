# title: An error inside a task is raised by get()
# dialect: futures
# error: broken task

def bad():
    fail("broken task")

spawn(bad).get()

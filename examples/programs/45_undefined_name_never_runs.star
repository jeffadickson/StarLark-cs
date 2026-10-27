# title: An undefined name is a static error, even inside code that never runs
# error: not found

def never_called():
    if False:
        undefined_function()

print("this line must not be reached")

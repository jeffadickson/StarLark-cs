# This script is wrong on purpose. `decide` promises a dict and returns an int, and `helper` promises an int and adds a string to a number. The type checker reports both before anything runs.
def decide(request) -> dict:
    return 42

# Wrong on purpose: promises an int but adds a string to a number, so the type checker reports it. Never called.
def helper(x: int) -> int:
    return x + "one"

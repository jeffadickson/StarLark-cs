# title: Removing an absent element from a set is an error (discard is the quiet version)
# error: not found

def run():
    set([1]).remove(2)

run()

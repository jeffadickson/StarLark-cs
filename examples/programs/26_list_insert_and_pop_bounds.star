# title: insert clamps its index; pop takes a position; both accept negatives
# output: ["a", "b", "c", "d", "e"]
# output: ["x", "a", "b"]
# output: ["a", "b", "y"]
# output: [3, [1, 2]]
# output: 'c'

def run():
    x = ["b", "c", "e"]
    x.insert(0, "a")
    x.insert(-1, "d")
    print(x)

    y = ["a", "b"]
    y.insert(-100, "x")
    print(y)
    y.pop(0)
    y.insert(100, "y")
    print(["a", "b", "y"])

    z = [1, 2, 3]
    print([z.pop(), z])
    print(repr(["a", "b", "c"].pop(-1)).replace('"', "'"))

run()

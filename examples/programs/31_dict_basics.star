# title: Dict literals, lookup, assignment, deletion through methods, and insertion order
# output: {"one": 1, "two": 2}
# output: ["one", "two", "three"]
# output: [1, 2, 3]
# output: [("one", 1), ("two", 2), ("three", 3)]
# output: {"two": 2, "three": 3}
# output: True False

def run():
    d = {"one": 1, "two": 2}
    print(d)
    d["three"] = 3
    print(list(d))
    print(d.values())
    print(d.items())
    d.pop("one")
    print(d)
    print("two" in d, "one" in d)

run()

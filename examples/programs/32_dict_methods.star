# title: get, setdefault, update, pop with default, popitem and clear
# output: [1, None, 0]
# output: [1, None, 3, 3]
# output: {"one": 1, "three": 3, "four": None}
# output: {"a": 1, "b": "2", "c": 3, "d": 4, "e": 5}
# output: [0, ("a", 1)]
# output: {}

def run():
    x = {"one": 1, "two": 2}
    print([x.get("one"), x.get("zzz"), x.get("zzz", 0)])
    y = {"one": 1}
    print([y.setdefault("one"), y.setdefault("two"), y.setdefault("three", 3), y.setdefault("three", 33)])
    z = {"one": 1}
    z.setdefault("three", 3)
    z.setdefault("four")
    print(z)
    u = {}
    u.update([("a", 1), ("b", "2")], c = 3)
    u.update({"d": 4})
    u.update(e = 5)
    print(u)
    p = {"a": 1}
    print([p.pop("zz", 0), p.popitem()])
    p.clear()
    print(p)

run()

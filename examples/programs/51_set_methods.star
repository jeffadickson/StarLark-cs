# title: The full set method list: add, discard, remove, pop, union, intersection, difference, and the predicates
# output: [1, 2, 3]
# output: [1, 3]
# output: [1, 3, 2, 4]
# output: [1]
# output: [2]
# output: [1, 3]
# output: [1, 2, 3, 4]
# output: [1]
# output: [True, True, True, False]
# output: 3

def run():
    s = set([1, 2])
    s.add(3)
    s.add(3)
    print(list(s))
    s.discard(2)
    s.discard(99)
    print(list(s))
    print(list(s.union([2, 4])))
    print(list(set([1, 2, 3]).intersection([0, 1], [1, 2])))
    print(list(set([1, 2, 3]).difference([0, 1], [3, 4])))
    print(list(set([1, 2]).symmetric_difference([2, 3])))
    t = set([1])
    t.update([2, 3], [3, 4])
    print(list(t))
    t.difference_update([2, 3, 4])
    print(list(t))
    print([set([1]).issubset([1, 2]), set([1, 2]).issuperset([2]), set([1]).isdisjoint([2]), set([1]).isdisjoint([1])])
    u = set([3, 1, 2])
    print(u.pop())

run()

# title: intersection_update, symmetric_difference_update and remove
# output: [1, 2]
# output: [1]
# output: [1, 3]
# output: [2]

def run():
    s = set([1, 2, 3, 4])
    s.intersection_update([0, 1, 2])
    print(list(s))
    s.intersection_update([0, 1], [1, 2])
    print(list(s))
    t = set([1, 2])
    t.symmetric_difference_update([2, 3])
    print(list(t))
    u = set([1, 2])
    u.remove(1)
    print(list(u))

run()

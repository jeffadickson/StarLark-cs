# title: Sets: operators, methods, ordering and in-place updates
# output: [1, 2, 3]
# output: [2]
# output: [1]
# output: [1, 3]
# output: True
# output: [3, 1, 2]
# output: [1, 2, 4, 3]
# output: [2, 3]

def run():
    a = set([1, 2])
    b = set([2, 3])
    print(list(a | b))
    print(list(a & b))
    print(list(a - b))
    print(list(a ^ b))
    print(set([1, 2]) == set([2, 1]))
    print(list(set([3, 1, 1, 2])))
    a |= set([4, 3])
    print(list(a))
    a -= set([1, 4])
    print(list(a))

run()

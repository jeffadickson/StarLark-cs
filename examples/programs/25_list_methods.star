# title: Every list method: append, extend, insert, pop, remove, index, clear
# output: [1, 2, 3, 4, 5]
# output: [0, 1, 2, 3, 4, 5]
# output: [0, 1, 99, 2, 3, 4, 5]
# output: 5
# output: [0, 1, 99, 2, 3, 4]
# output: [0, 99, 2, 3, 4]
# output: 2
# output: []

def run():
    xs = [1, 2, 3]
    xs.append(4)
    xs.extend([5])
    print(xs)
    xs.insert(0, 0)
    print(xs)
    xs.insert(2, 99)
    print(xs)
    print(xs.pop())
    print(xs)
    xs.remove(1)
    print(xs)
    print(xs.index(2))
    xs.clear()
    print(xs)

run()

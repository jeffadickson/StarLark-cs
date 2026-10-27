# title: Lists are references: aliasing, += in place, and + making a new list
# output: [1, 2, 3, 4]
# output: [1, 2, 3, 4]
# output: [1, 2, 3, 4, 5]
# output: [1, 2, 3, 4]

def run():
    a = [1, 2, 3]
    b = a
    a += [4]
    print(a)
    print(b)
    a = a + [5]
    print(a)
    print(b)

run()

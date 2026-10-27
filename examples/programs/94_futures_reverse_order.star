# title: all_of returns results in the order the futures are listed, not the order they finish
# dialect: futures
# output: [5, 4, 3, 2, 1, 0]

def value(i):
    return i

fs = [spawn(value, i) for i in range(6)]
print(all_of(reversed(fs)))

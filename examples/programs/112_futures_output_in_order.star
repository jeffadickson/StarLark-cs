# title: Output printed by tasks appears in index order, whatever order they ran in
# dialect: futures
# output: item 0
# output: item 1
# output: item 2
# output: item 3
# output: item 4

def say(i):
    print("item", i)
    return i

parallel_map(say, range(5))

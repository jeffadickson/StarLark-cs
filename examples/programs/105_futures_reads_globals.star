# title: Tasks can read the module's global tables
# dialect: futures
# output: [3, 1, 2]

TABLE = {"a": 1, "b": 2, "c": 3}

def look(k):
    return TABLE[k]

print(parallel_map(look, ["c", "a", "b"]))

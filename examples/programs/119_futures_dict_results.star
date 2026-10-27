# title: Tasks can return dicts and tuples; results are frozen but readable
# dialect: futures
# output: 2 4 (2, 3)
# output: 3 9 (3, 4)

def info(n):
    return {"n": n, "square": n * n, "pair": (n, n + 1)}

for r in parallel_map(info, [2, 3]):
    print(r["n"], r["square"], r["pair"])

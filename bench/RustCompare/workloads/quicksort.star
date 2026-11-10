# Iterative quicksort with an explicit stack, plus BFS over a generated graph (no recursion, no while).
def gen(n, seed):
    xs = []
    x = seed
    for _ in range(n):
        x = (x * 1103515245 + 12345) % 2147483648
        xs.append(x % 100000)
    return xs

def qsort(xs):
    stack = [(0, len(xs) - 1)]
    for _ in range(len(xs) * 4):
        if not stack:
            break
        lo, hi = stack.pop()
        if lo >= hi:
            continue
        p = xs[(lo + hi) // 2]
        i, j = lo, hi
        for _ in range(hi - lo + 2):
            if i > j:
                break
            for _ in range(hi - lo + 1):
                if xs[i] >= p:
                    break
                i += 1
            for _ in range(hi - lo + 1):
                if xs[j] <= p:
                    break
                j -= 1
            if i <= j:
                xs[i], xs[j] = xs[j], xs[i]
                i += 1
                j -= 1
        stack.append((lo, j))
        stack.append((i, hi))
    return xs

def bfs(n):
    adj = [[(v * 7 + 1) % n, (v * 13 + 5) % n, (v + 1) % n] for v in range(n)]
    dist = [-1] * n
    dist[0] = 0
    queue = [0]
    head = 0
    for _ in range(n):
        if head >= len(queue):
            break
        v = queue[head]
        head += 1
        for w in adj[v]:
            if dist[w] < 0:
                dist[w] = dist[v] + 1
                queue.append(w)
    return max(dist)

def run():
    ys = qsort(gen(3000, 42))
    ok = all([ys[k] <= ys[k + 1] for k in range(len(ys) - 1)])
    return (ok, bfs(5000))

r = run()

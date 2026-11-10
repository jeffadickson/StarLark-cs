# String processing: build a log, split into lines and fields, count by level and path, reformat slow requests.
def make_log(n):
    levels = ["INFO", "WARN", "ERROR", "DEBUG"]
    lines = []
    for i in range(n):
        lines.append("2026-11-%d %s worker-%d: request id=%d path=/api/v1/item/%d took %dms" % (i % 28 + 1, levels[i % 4], i % 16, i, i * 7 % 1000, i * 13 % 500))
    return "\n".join(lines)

def run():
    text = make_log(3000)
    counts = {}
    slow = []
    for line in text.split("\n"):
        fields = line.split(" ")
        level = fields[1]
        counts[level] = counts.get(level, 0) + 1
        took = fields[-1]
        if took.endswith("ms") and int(took[:-2]) > 400:
            slow.append(fields[2].rstrip(":").replace("worker-", "w") + "@" + fields[4].partition("=")[2])
        for w in line.lower().split():
            if w.startswith("path="):
                counts[w[:12]] = counts.get(w[:12], 0) + 1
    return (len(slow), sorted(counts.items())[:3], ",".join(slow)[:40].title())

r = run()

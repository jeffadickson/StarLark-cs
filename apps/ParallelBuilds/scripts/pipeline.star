# A release pipeline that waits on three builds at once, with futures.
#
# `run_build(name, millis, fail)` is a host function (the C# program provides it). Registered "as a future", calling it
# does not wait: it starts the build and hands back a *future* straight away. The script keeps going, starts the other
# builds, and only waits when it needs the answers, with all_of() or get(). The three builds run side by side while the
# script itself stays one plain, single-threaded program.

# name, how long the build takes in milliseconds, and whether it is rigged to fail (for the broken run).
PLAN = [
    ("core", 700, False),
    ("web", 1100, False),
    ("docs", 500, False),
]

PLAN_BROKEN = [
    ("core", 700, False),
    ("web", 1100, True),
    ("docs", 500, False),
]

# ---------------------------------------------------------------- the two ways to wait

# One at a time: get() right after starting each build waits for it before the next one starts.
def build_serial(plan):
    return [run_build(p[0], p[1], p[2]).get() for p in plan]

# All at once: start every build first (nothing waits yet), then collect them together. The results come back in plan
# order, whichever build finishes first.
def build_parallel(plan):
    started = [run_build(p[0], p[1], p[2]) for p in plan]
    return all_of(started)

# ---------------------------------------------------------------- the pipeline

def report(results):
    for r in results:
        print("  %-5s %-6s %4d ms  %s" % (r.name, "ok" if r.ok else "FAILED", r.millis, r.log))

# Build everything in parallel, and package only if every build worked.
def release(plan):
    results = build_parallel(plan)
    report(results)
    failed = [r.name for r in results if not r.ok]
    if failed:
        print("  release blocked: " + ", ".join(failed) + " failed")
        return False
    package = run_build("package", 300, False).get()
    print("  %-5s %-6s %4d ms  %s" % (package.name, "ok", package.millis, package.log))
    print("  release ready")
    return True

# Entry points the host calls (it times each one).
def serial():
    return len(build_serial(PLAN))

def parallel():
    return len(build_parallel(PLAN))

def good_release():
    return release(PLAN)

def broken_release():
    return release(PLAN_BROKEN)

# The host caps how many builds may be in flight at once (here 4); starting a fifth before one finishes stops the run
# with a limit error rather than queueing without bound. So a script with many builds works in batches of the cap.
def many():
    finished = 0
    for first in range(0, 12, 4):
        batch = [run_build("job" + str(first + i), 200, False) for i in range(4)]
        finished += len(all_of(batch))
    return finished

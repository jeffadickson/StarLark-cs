# Configuration-style script: build many targets as dicts and structs, merge defaults, group and serialise.
DEFAULTS = {"visibility": ["//visibility:public"], "copts": ["-O2", "-Wall"], "testonly": False}

def target(name, kind, deps, **kwargs):
    t = dict(DEFAULTS)
    t.update(kwargs)
    t["name"] = name
    t["kind"] = kind
    t["deps"] = [":" + d for d in deps]
    t["copts"] = t["copts"] + ["-DNAME=" + name.upper()]
    return struct(**t)

def run():
    targets = []
    for i in range(600):
        deps = ["lib%d" % j for j in range(max(0, i - 3), i)]
        kind = "cc_test" if i % 5 == 0 else "cc_library"
        targets.append(target("lib%d" % i, kind, deps, srcs = ["src/lib%d.cc" % i], testonly = kind == "cc_test"))
    by_kind = {}
    for t in targets:
        by_kind.setdefault(t.kind, []).append(t.name)
    total_deps = 0
    for t in targets:
        total_deps += len(t.deps)
    out = json.encode([{"name": t.name, "deps": t.deps, "copts": t.copts} for t in targets[:100]])
    return (len(by_kind["cc_test"]), total_deps, len(out))

r = run()

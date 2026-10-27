# Rule library: the only place that knows what a "cc" target is.
load("//rules:common.star", "label", "check_name")

Opt = enum("O0", "O2", "O3")

def _cc(kind, name, srcs, deps, opt):
    check_name(name)
    if not srcs:
        fail("%s %s needs at least one source" % (kind, name))
    declare(
        name = name,
        kind = kind,
        srcs = srcs,
        deps = [label(d) for d in deps],
        opt = Opt(opt).value,   # validates the optimisation level
    )

def cc_library(name, srcs = [], deps = [], opt = "O2"):
    _cc("cc_library", name, srcs, deps, opt)

def cc_binary(name, srcs = [], deps = [], opt = "O2"):
    _cc("cc_binary", name, srcs, deps, opt)

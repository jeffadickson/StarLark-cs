# Rule library: the only place that knows what a "cc" target is.
load("//rules:common.star", "label", "check_name")

# The allowed optimisation levels; anything else (such as "Ofast") is rejected when the target is declared.
Opt = enum("O0", "O2", "O3")

# The work common to cc_library and cc_binary: check the name, require at least one source, normalise the dependency labels, validate the optimisation level, and declare the target to the host.
# The leading underscore marks it as private to this file; BUILD files cannot load it.
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

# A C library target. `deps` may be written ":name" or "name".
def cc_library(name, srcs = [], deps = [], opt = "O2"):
    _cc("cc_library", name, srcs, deps, opt)

# A C executable target, with the same arguments as cc_library.
def cc_binary(name, srcs = [], deps = [], opt = "O2"):
    _cc("cc_binary", name, srcs, deps, opt)

def label(dep):
    """Normalises ":name" and "name" to "name"."""
    return dep.removeprefix(":")

def check_name(name):
    if not name.replace("_", "").isalnum():
        fail("invalid target name %r" % name)

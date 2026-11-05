def label(dep):
    """Normalises ":name" and "name" to "name"."""
    return dep.removeprefix(":")

# Reject a target name that is not letters, digits and underscores, with a clear error.
def check_name(name):
    if not name.replace("_", "").isalnum():
        fail("invalid target name %r" % name)

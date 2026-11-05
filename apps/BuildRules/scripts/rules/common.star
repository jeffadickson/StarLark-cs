def label(dep):
    """Normalises ":name" and "name" to "name"."""
    return dep.removeprefix(":")

# Reject a target name made of anything but letters, digits and underscores, or of underscores alone (including an empty name), with a clear error.
def check_name(name):
    if not name.replace("_", "").isalnum():
        fail("invalid target name %r" % name)

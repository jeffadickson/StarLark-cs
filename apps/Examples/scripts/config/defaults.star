# Shared building blocks for every environment. An environment file loads service() from here and passes only what differs.

# The registry every image comes from.
REGISTRY = "registry.example.com"

def service(name, version, port, replicas = 1, env = {}):
    "One service: the image is built from the shared registry, and every service gets the same base environment."
    base = {"LOG_FORMAT": "json", "SERVICE": name}
    merged = dict(base)
    merged.update(env)
    return struct(
        name = name,
        image = "%s/%s:%s" % (REGISTRY, name, version),
        port = port,
        replicas = replicas,
        env = merged,
    )

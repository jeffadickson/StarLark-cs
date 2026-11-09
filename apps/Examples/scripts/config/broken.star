# A configuration with three mistakes, which the host's validation reports together rather than one at a time: an image
# without a tag, a replica count out of range, and two services on the same port.
load("defaults.star", "service")

def services():
    "Services with mistakes."
    return [
        struct(name = "cache", image = "redis", port = 6379, replicas = 1, env = {}),
        service("api", "2.3.1", 8080, replicas = 500),
        service("web", "5.0.2", 8080),
    ]

# Staging: one copy of everything, verbose logging, the same versions as production plus the release candidate of the API.
load("defaults.star", "service")

def services():
    "The services to deploy to staging."
    return [
        service("api", "2.4.0-rc1", 8080, env = {"LOG_LEVEL": "debug"}),
        service("worker", "2.3.1", 8081, env = {"LOG_LEVEL": "debug"}),
        service("web", "5.0.2", 8000),
    ]

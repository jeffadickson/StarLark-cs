# Production: replicas scale with the expected load per service, computed rather than typed in.
load("defaults.star", "service")

# Requests per second each service must handle at peak, and how many one replica can serve.
PEAK = {"api": 1800, "worker": 400, "web": 2600}
PER_REPLICA = 250

def replicas_for(name):
    "Enough replicas for the peak load, rounded up, and never fewer than two so one can fail."
    need = (PEAK[name] + PER_REPLICA - 1) // PER_REPLICA
    return max(2, need)

def services():
    "The services to deploy to production."
    return [service(n, v, p, replicas = replicas_for(n)) for n, v, p in [
        ("api", "2.3.1", 8080),
        ("worker", "2.3.1", 8081),
        ("web", "5.0.2", 8000),
    ]]

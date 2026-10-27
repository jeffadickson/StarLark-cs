load("defaults.star", "make_config")

def replicas_for(n: int) -> int:
    return n + "2"

config = make_config({"name": "app-broken", "replicas": replicas_for(3), "log_level": "verbose"})

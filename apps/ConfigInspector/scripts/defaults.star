LogLevel = enum("debug", "info", "warn", "error")

Config = record(
    name = str,
    replicas = int,
    log_level = str,
    timeout_s = float,
    features = dict[str, bool],
    regions = list[str],
)

DEFAULTS = {
    "name": "app",
    "replicas": 1,
    "log_level": "info",
    "timeout_s": 30.0,
    "features": {"search": False, "beta_ui": False},
    "regions": ["eu"],
}

def make_config(overrides = {}):
    """Layers overrides over DEFAULTS; features merge key by key."""
    d = dict(DEFAULTS)
    features = dict(DEFAULTS["features"])
    features.update(overrides.get("features", {}))
    d.update(overrides)
    d["features"] = features
    d["log_level"] = LogLevel(d["log_level"]).value   # validates the enum
    return Config(**d)

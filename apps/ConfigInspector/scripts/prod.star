load("defaults.star", "make_config")

def replicas_for(regions: list[str]) -> int:
    return 2 * len(regions)

REGIONS = ["eu", "us", "ap"]
config = make_config({
    "name": "app-prod",
    "replicas": replicas_for(REGIONS),
    "regions": REGIONS,
    "timeout_s": 10.0,
    "features": {"search": True},
})

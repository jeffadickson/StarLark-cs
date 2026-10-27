load("defaults.star", "make_config")

config = make_config({"name": "app-dev", "log_level": "debug", "features": {"beta_ui": True}})

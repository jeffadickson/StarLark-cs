# title: A record needs all fields that have no default
# dialect: types
# error: missing parameter `port`

R = record(host = str, port = int)
R(host = "h")

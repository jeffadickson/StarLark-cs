# title: A record field of the wrong type is rejected at construction
# dialect: types
# error: does not match the type annotation `str` for argument `host`

R = record(host = str, port = int)
R(host = 1, port = 2)

# title: Records: typed fields, defaults, equality and dir
# dialect: types
# output: localhost 80
# output: True
# output: False
# output: ["host", "port"]

Server = record(host = str, port = field(int, 80))
a = Server(host = "localhost")
b = Server(host = "localhost", port = 80)
c = Server(host = "other")
print(a.host, a.port)
print(a == b)
print(a == c)
print(dir(a))

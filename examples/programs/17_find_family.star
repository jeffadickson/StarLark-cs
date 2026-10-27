# title: find, rfind, index, rindex and count with bounds
# output: [1, 4, -1, 1, -1]
# output: [1, 4]
# output: [2, 2, 1]

print(["bonbon".find("on"), "bonbon".find("on", 2), "bonbon".find("on", 2, 5), "bonbon".rfind("on", None, 5), "bonbon".find("zz")])
print(["bonbon".index("on"), "bonbon".rindex("on")])
print(["banana".count("an"), "hello, world!".count("o"), "hello, world!".count("o", 7, 12)])

# title: partition, rpartition, join and splitlines
# output: ("one", "/", "two/three")
# output: ("one/two", "/", "three")
# output: ("abc", "", "")
# output: a-b-c
# output: ["A", "B", "C", "D"]
# output: ["one\n", "\n", "two"]

print("one/two/three".partition("/"))
print("one/two/three".rpartition("/"))
print("abc".partition("/"))
print("-".join(["a", "b", "c"]))
print("A\nB\rC\r\nD".splitlines())
print("one\n\ntwo".splitlines(True))

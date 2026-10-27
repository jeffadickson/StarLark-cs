# title: bytes: literals, indexing, slicing, concatenation, membership, comparison and conversion
# output: 3
# output: 65
# output: BC
# output: ABCD
# output: [True, True, False]
# output: [65, 66, 67]
# output: ABC
# output: ABC
# output: [True, False]

b = b"ABC"
print(len(b))
print(b[0])
print(b[1:])
print(b + b"D")
print([97 in b"abc", b"nas" in b"dynasty", 300 > 255 and False])
print(list(b.elems()))
print(bytes([65, 66, 67]))
print(str(b))
print([b"a" < b"b", b"b" < b"a"])

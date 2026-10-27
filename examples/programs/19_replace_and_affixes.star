# title: replace (with a count), startswith/endswith (with tuples) and prefix removal
# output: ["bonono", "bonona", "xaxbxcx", "aXc"]
# output: [True, True, False, True]
# output: ["ana", "banana", "ban", "banana"]

print(["banana".replace("a", "o"), "banana".replace("a", "o", 2), "abc".replace("", "x"), "abc".replace("b", "X")])
print(["foo.cc".endswith((".cc", ".h")), "abc".startswith(("a", "A")), "def".startswith(("a", "A")), "filename.star".startswith("name", 4)])
print(["banana".removeprefix("ban"), "banana".removeprefix("ana"), "banana".removesuffix("ana"), "banana".removesuffix("ban")])

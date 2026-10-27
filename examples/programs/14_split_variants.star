# title: split and rsplit with separators, limits and whitespace mode
# output: ["one", "two", "", "three"]
# output: ["one", "two  three"]
# output: ["one two", "three"]
# output: ["ba", "ana"]
# output: ["bana", "a"]
# output: [""]
# output: []

print("one two  three".split(" "))
print("one two  three".split(None, 1))
print("one two  three".rsplit(None, 1))
print("banana".split("n", 1))
print("banana".rsplit("n", 1))
print("".split("x"))
print("   ".split())

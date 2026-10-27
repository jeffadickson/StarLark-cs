# title: Concatenation, repetition, slicing and membership on strings
# output: hello world
# output: ababab
# output: ell
# output: olleh
# output: [True, False, True]

print("hello" + " " + "world")
print("ab" * 3)
print("hello"[1:4])
print("hello"[::-1])
print(["ell" in "hello", "z" in "hello", "" in "hello"])

# title: Case conversion and the is* predicates
# output: ["Hello, world!", "HELLO", "hello", "Hello, World!"]
# output: [True, True, True, True, True, True]
# output: [False, False, False, False, False, False]

s = "hello, world!"
print([s.capitalize(), "Hello".upper(), "HELLO".lower(), "hElLo, WoRlD!".title()])
print(["abc".isalpha(), "123".isdigit(), "a1".isalnum(), "   ".isspace(), "abc".islower(), "ABC".isupper()])
print(["".isalpha(), "".isdigit(), "".isalnum(), "".isspace(), "123".islower(), "123".isupper()])

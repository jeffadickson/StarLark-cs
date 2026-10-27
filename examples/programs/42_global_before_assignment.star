# title: Reading a global before it is assigned is an error, even at the top level
# error: referenced before assignment

print(x)
x = "hello"

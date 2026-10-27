# title: A type alias is just a variable holding a type
# dialect: types
# output: 3
# error: does not match

IntList = list[int]

def total(xs: IntList) -> int:
    t = 0
    for x in xs:
        t += x
    return t

print(total([1, 2]))
total(["a"])

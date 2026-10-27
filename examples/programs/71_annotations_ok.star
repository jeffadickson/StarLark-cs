# title: Annotated functions are checked at call time; a conforming program runs normally
# dialect: types
# output: 5
# output: ["a", "b"]
# output: [1, "x"]
# output: None

def add(a: int, b: int) -> int:
    return a + b

def names(xs: list[str]) -> list[str]:
    return xs

def either(x: int | str) -> int | str:
    return x

def nothing() -> None:
    pass

print(add(2, 3))
print(names(["a", "b"]))
print([either(1), either("x")])
print(nothing())

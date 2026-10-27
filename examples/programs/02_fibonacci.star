# title: Iterative Fibonacci with tuple unpacking (recursion is forbidden in Starlark)
# output: [0, 1, 1, 2, 3, 5, 8, 13, 21, 34]
# output: 12586269025

def fib(count):
    a, b = 0, 1
    out = []
    for _ in range(count):
        out.append(a)
        a, b = b, a + b
    return out

print(fib(10))
print(fib(51)[50])

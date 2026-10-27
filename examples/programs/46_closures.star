# title: Closures capture variables, and see later assignments in the enclosing function
# output: [1, 2]
# output: [11, 12]
# output: 20

def make_adder(n):
    def add(x):
        return x + n
    return add

def collect(x):
    res = []
    def get_x():
        res.append(x)
    get_x()
    x = 2
    get_x()
    return res

add10 = make_adder(10)
print(collect(1))
print([add10(1), add10(2)])
print((lambda a, b: a + b)(10, 10))

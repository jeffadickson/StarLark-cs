# title: A mutable default value is created once and shared between calls
# output: [1]
# output: [1, 2]
# output: [1, 2, 3]

def push(x, xs = []):
    xs.append(x)
    return xs

print(push(1))
print(push(2))
print(push(3))

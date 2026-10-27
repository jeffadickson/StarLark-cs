# title: Ints and floats compare exactly, even beyond 2**53
# output: [True, False]
# output: True
# output: False

big = (1 << 53) + 1
print([1 == 1.0, big == big + 0.0])
print(big > float(1 << 53))
print(big + 0.0 == big)

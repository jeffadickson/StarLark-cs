# title: Floored division, with the remainder taking the sign of the divisor
# output: [(7, 2, 3, 1), (-7, 2, -4, 1), (7, -2, -4, -1), (-7, -2, 3, -1)]
# output: [True, True, True, True]

cases = [(7, 2), (-7, 2), (7, -2), (-7, -2)]
print([(x, y, x // y, x % y) for x, y in cases])
print([(x // y) * y + (x % y) == x for x, y in cases])

# title: NaN, infinity and negative zero
# output: [True, True, True, True]
# output: +inf -inf nan
# output: True

nan = float("nan")
inf = float("inf")
print([nan == nan, inf > 1e308, -inf < -1e308, 0.0 == -0.0])
print(inf, -inf, nan)
print(inf < nan)

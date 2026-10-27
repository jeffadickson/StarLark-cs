# title: int, float, bool, str, repr, list, tuple, dict conversions
# output: [3, -3, 1, 0, 2]
# output: [1.5, 3.0, 1.0, 0.0, +inf, -inf]
# output: [False, True, False, True]
# output: ["1", "x", "\"x\"", "[1, \"x\"]"]
# output: [[1, 2], (1, 2), {"a": 1}]

print([int(3.9), int(-3.9), int(True), int(False), int("2")])
print([float("1.5"), float(3), float(True), float(), float("Infinity"), float("-inf")])
print([bool(0), bool(1), bool(""), bool("x")])
print([str(1), str("x"), repr("x"), str([1, "x"])])
print([list((1, 2)), tuple([1, 2]), dict([("a", 1)])])

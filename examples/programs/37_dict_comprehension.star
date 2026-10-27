# title: Dict comprehensions, including from pairs and with conditions
# output: {"able": 4, "baker": 5, "charlie": 7}
# output: {0: 0, 2: 4, 4: 16}
# output: {"a": 1, "b": 2}

words = ["able", "baker", "charlie"]
print({w: len(w) for w in words})
print({x: x * x for x in range(5) if x % 2 == 0})
print(dict([("a", 1), ("b", 2)]))

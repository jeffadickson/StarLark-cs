# title: getattr without a default fails on a missing attribute
# error: no attribute|no field or method

getattr("x", "nope")

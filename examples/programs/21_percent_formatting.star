# title: The % operator: conversions, mappings and the single-value rule
# output: Hello Bob, your score is 75
# output: 3 ff FF 17 100%
# output: A 123 hi Z
# output: coordinates=(40, -74)
# output: 1.230000e+12 1230000000000.000000 1.5 1e+45 1.230000E+12
# output: A!

print("Hello %s, your score is %d" % ("Bob", 75))
print("%d %x %X %o 100%%" % (3.9, 255, 255, 15))
print("A %(foo)d %(bar)s Z" % {"foo": 123, "bar": "hi"})
print("coordinates=%s" % ((40, -74),))
print("%e %f %g %g %E" % (1.23e12, 1.23e12, 1.5, 1e45, 1.23e12))
print("%c%c" % (65, "!"))

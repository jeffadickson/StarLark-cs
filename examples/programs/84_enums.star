# title: Enums: values, index, iteration, indexing, length and equality
# dialect: types
# output: GREEN 1
# output: ["RED", "GREEN", "BLUE"]
# output: 3
# output: BLUE
# output: [True, False, True]
# output: ["RED", "GREEN", "BLUE"]

Color = enum("RED", "GREEN", "BLUE")
g = Color("GREEN")
print(g.value, g.index)
print(Color.values())
print(len(Color))
print(Color[2].value)
print([Color("RED") == Color("RED"), Color("RED") == Color("BLUE"), Color("RED") == Color[0]])
print([c.value for c in Color])

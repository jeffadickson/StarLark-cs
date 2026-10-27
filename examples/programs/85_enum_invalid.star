# title: Constructing an enum from a value that is not an element is an error
# dialect: types
# error: not a valid element

Color = enum("RED")
Color("PURPLE")

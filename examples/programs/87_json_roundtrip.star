# title: json.encode and json.decode round-trip nested data, including big ints and escapes
# output: {"name":"x","tags":["a","b"],"n":null,"ok":true}
# output: [1, 2.5, "s", None, True, {"k": [1]}]
# output: 123456789012345678901234567890
# output: "line\nbreak \"quoted\""
# output: True

print(json.encode(struct(name = "x", tags = ["a", "b"], n = None, ok = True)))
print(json.decode('[1, 2.5, "s", null, true, {"k": [1]}]'))
print(json.decode("123456789012345678901234567890"))
print(json.encode("line\nbreak \"quoted\""))
data = {"a": [1, 2, {"b": None}], "c": "d"}
print(json.decode(json.encode(data)) == data)

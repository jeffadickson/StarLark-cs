# title: json.indent pretty-prints already-encoded JSON
# output: {
# output: TAB"a": [
# output: TABTAB1,
# output: TABTAB2
# output: TAB],
# output: TAB"b": {}
# output: }

for line in json.indent('{"a":[1,2],"b":{}}').split("\n"):
    print(line.replace("\t", "TAB"))

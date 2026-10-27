# title: strip, lstrip and rstrip with and without a cutset
# output: ["hello", "hello  ", "  hello", "ell", "ello  ", "  hell"]

print(["  hello  ".strip(), "  hello  ".lstrip(), "  hello  ".rstrip(),
       "  hello   ".strip("h o"), "   hello  ".lstrip("h o"), "  hello   ".rstrip("h o")])

# title: Encoding a cyclic structure is an error, not an infinite loop
# error: cycle

def run():
    x = []
    x.append(x)
    return json.encode(x)

run()

# title: Pass values as arguments instead of capturing them
# dialect: futures
# output: [0, 10, 20]

def run():
    fs = []
    for i in range(3):
        fs.append(spawn(lambda x: x * 10, i))
    return all_of(fs)

print(run())

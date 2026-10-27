# title: A lambda that captures a loop variable is rejected rather than racing
# dialect: futures
# error: captures variables

def run():
    fs = []
    for i in range(3):
        fs.append(spawn(lambda: i))
    return all_of(fs)

run()

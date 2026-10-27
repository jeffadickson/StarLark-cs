# title: all_of waits for every future and keeps the argument order
# dialect: futures
# output: ["task-0", "task-1", "task-2", "task-3", "task-4"]

def name(i):
    return "task-" + str(i)

fs = [spawn(name, i) for i in range(5)]
print(all_of(fs))

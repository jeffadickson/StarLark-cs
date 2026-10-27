# title: A small pipeline: map in parallel, then reduce on the main thread
# dialect: futures
# output: [55, 66, 66, 44, 103]
# output: elderberry

WORDS = ["apple", "banana", "cherry", "date", "elderberry"]

def score(w):
    return len(w) * 10 + len(w) % 7

def best_index(scores):
    best = 0
    for i in range(len(scores)):
        if scores[i] > scores[best]:
            best = i
    return best

scores = parallel_map(score, WORDS)
print(scores)
print(WORDS[best_index(scores)])

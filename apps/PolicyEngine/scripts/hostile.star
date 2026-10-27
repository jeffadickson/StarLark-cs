# Starlark has no `while`, but a huge loop does the same damage.
def decide(request):
    n = 0
    for _ in range(1000000000):
        n += 1
    return {"allow": True, "discount": 0.0, "reason": "never"}

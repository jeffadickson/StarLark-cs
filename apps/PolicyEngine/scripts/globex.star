# Tenant "globex": blocks some regions, flat discount for big baskets.
BLOCKED = ["XX", "YY"]

def decide(request):
    if request.customer.region in BLOCKED:
        return {"allow": False, "discount": 0.0, "reason": "region blocked"}
    total = 0.0
    for i in request.items:
        total += i.price * i.qty
    return {"allow": True, "discount": 25.0 if total > 1000 else 0.0, "reason": "ok"}

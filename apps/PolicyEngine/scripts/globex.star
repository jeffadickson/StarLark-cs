# Tenant "globex": blocks some regions, flat discount for big baskets.
BLOCKED = ["XX", "YY"]

# The tenant's policy: customers in a blocked region are refused outright; otherwise the request is allowed, with a flat discount of 25 on a basket worth more than 1000.
def decide(request):
    if request.customer.region in BLOCKED:
        return {"allow": False, "discount": 0.0, "reason": "region blocked"}
    total = 0.0
    for i in request.items:
        total += i.price * i.qty
    return {"allow": True, "discount": 25.0 if total > 1000 else 0.0, "reason": "ok"}

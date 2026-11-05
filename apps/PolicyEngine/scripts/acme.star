# Tenant "acme": tiered discounts and an approval rule.
def basket_total(items):
    total = 0.0
    for i in items:
        total += i.price * i.qty
    return total

# The tenant's policy. Called by the host with a request (customer and items) and returns a decision: whether to allow it, the discount, and a reason. Gold and Silver customers get 15% and 5%,
# five or more items add 2%, and a basket over 5000 needs a verified customer.
def decide(request):
    total = basket_total(request.items)
    rate = 0.0
    if request.customer.tier == "Gold":
        rate = 0.15
    elif request.customer.tier == "Silver":
        rate = 0.05
    if len(request.items) >= 5:
        rate += 0.02
    allowed = total <= 5000 or request.customer.verified
    return {"allow": allowed, "discount": total * rate, "reason": "ok" if allowed else "needs verification"}

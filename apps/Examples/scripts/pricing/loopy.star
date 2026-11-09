# A tenant script that is well typed but does far too much work per order: it "simulates" every possible coupon code. The
# step budget stops it after a fraction of a second, and the other tenants are never slowed down.

def price(order: list, customer: dict) -> tuple:
    "Tries ten million coupon codes and keeps the best one."
    best = 0.0
    for code in range(10000000):
        if code % 7919 == 0:
            best = max(best, 0.01)
    total = 0.0
    for line in order:
        total += line["qty"] * line["unit"]
    return (total * (1.0 - best), ["best coupon"])

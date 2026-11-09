# Pricing rules for the tenant "acme". The service calls price(order, customer) for every checkout.
#
# order is a list of lines, each {"sku": str, "qty": int, "unit": float}; customer is {"tier": str, "currency": str}.
# Return (total, explanations): the amount in the customer's currency, and one line of text per adjustment for the invoice.

# Discount for each customer tier. A tier that is not listed gets none.
TIER_DISCOUNT = {"gold": 0.10, "silver": 0.05}

# Buying this many of one item or more earns the bulk rate on that line.
BULK_QTY = 10
BULK_RATE = 0.15

def line_total(line: dict) -> float:
    "The price of one order line in US dollars, with the bulk rate applied when the quantity qualifies."
    gross = line["qty"] * line["unit"]
    if line["qty"] >= BULK_QTY:
        return gross * (1.0 - BULK_RATE)
    return gross

def price(order: list, customer: dict) -> tuple:
    "The total for the order in the customer's currency, and why."
    why = []
    total = 0.0
    for line in order:
        total += line_total(line)
        if line["qty"] >= BULK_QTY:
            why.append(f"bulk rate on {line['sku']}")
    discount = TIER_DISCOUNT.get(customer["tier"], 0.0)
    if discount > 0.0:
        total = total * (1.0 - discount)
        why.append(f"{customer['tier']} discount {int(discount * 100)}%")
    rate = usd_rate(customer["currency"])
    if customer["currency"] != "USD":
        why.append(f"converted to {customer['currency']} at {rate}")
    return (total * rate, why)

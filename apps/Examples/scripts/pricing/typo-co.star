# A tenant script with a mistake the checker finds at upload time: usd_rate takes a currency code (a string), and this passes
# the customer dict instead. Without the check, this would only fail when a customer checks out.

def price(order: list, customer: dict) -> tuple:
    "Prices with a 5% discount for everyone."
    total = 0.0
    for line in order:
        total += line["qty"] * line["unit"]
    return (total * 0.95 * usd_rate(customer), ["5% for everyone"])

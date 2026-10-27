# Transform script: `rows` is a predeclared list of structs; `log` and `lookup_region` come from the host.
log.info("processing %d rows" % len(rows))

valid = [r for r in rows if r.amount > 0 and r.status != "cancelled"]
log.info("kept %d, dropped %d" % (len(valid), len(rows) - len(valid)))

enriched = [
    {"region": lookup_region(r.country), "country": r.country, "product": r.product,
     "amount": r.amount, "net": net_of(r.amount, r.country)}
    for r in valid
]

def group_sum(items, key, field):
    out = {}
    for it in items:
        out[it[key]] = out.get(it[key], 0.0) + it[field]
    return out

by_region = group_sum(enriched, "region", "net")
by_product = group_sum(enriched, "product", "amount")
top_products = sorted(by_product.items(), key = lambda kv: -kv[1])[:2]

summary = [
    {"region": k, "net": round_to(v, 2), "share": round_to(100.0 * v / sum_values(by_region.values()), 1)}
    for k, v in sorted(by_region.items())
]
payload = json.encode({"summary": summary, "top": [{"product": p, "total": t} for p, t in top_products]})

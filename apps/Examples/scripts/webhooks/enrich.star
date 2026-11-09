# Enrichment rule: before a human looks at the issue, collect the reporter's tier and whether an incident is open. The two
# lookups are independent, so the rule starts both and waits for them together: they overlap instead of running one after the
# other.

def on_event():
    "Comments on every new issue with what support needs to know."
    if event["kind"] != "issue_opened":
        return
    tier, status = all_of([
        lookup_customer(event["reporter"]),
        http_get("https://status.example.com/api/status"),
    ])
    incident = json.decode(status).get("incident", False)
    note = "reporter tier: " + tier
    if incident:
        note += "; an incident is open, link this issue to it"
    act("comment", note)

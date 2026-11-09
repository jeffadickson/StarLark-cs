# Triage rule: an issue that mentions a crash gets the "crash" label, and a crash from an enterprise customer pages on-call.
#
# `event` is the webhook payload as Starlark data. act(kind, detail) records an action; the host applies them only if this rule
# finishes without an error. lookup_customer(email) is a slow host call that returns a future.

KEYWORDS = ["crash", "crashes", "segfault", "panic"]

def mentions_crash(issue):
    "True if the title or the body names a crash."
    text = (issue["title"] + " " + issue["body"]).lower()
    return any([word in text.split(" ") for word in KEYWORDS])

def on_event():
    "Called once per event."
    if event["kind"] != "issue_opened" or not mentions_crash(event):
        return
    act("label", "crash")
    tier = lookup_customer(event["reporter"]).get()
    if tier == "enterprise":
        act("notify", f"#oncall: enterprise crash report #{event['number']}: {event['title']}")

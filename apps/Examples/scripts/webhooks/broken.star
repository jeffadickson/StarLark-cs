# A rule with a bug: it records an action, then reads a field the event does not have. The error ends this rule only. The
# action it recorded first is thrown away, because the host applies a rule's actions only when the whole rule succeeded.

def on_event():
    "Assigns the issue to the reporter's team."
    act("label", "needs-team")
    act("assign", event["team"])

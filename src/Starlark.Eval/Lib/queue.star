# A first-in, first-out queue in pure Starlark.
#
# Load it with:   load("std/queue.star", "queue", "enqueue", "dequeue", "peek", "queue_len", "queue_empty", "queue_clear", "queue_items")
#
#     q = queue([1, 2])        # optionally start with some items; queue(limit = 100) caps the length
#     enqueue(q, 3)
#     dequeue(q)               # 1, the oldest item
#
# Why not list.pop(0)? That moves every remaining item, so draining a list of n items that way takes time proportional to n*n.
# This queue keeps two lists: new items are added to the end of `back`; items are taken from the end of `front`, which holds
# the older items in reverse order. When `front` runs out, `back` is reversed into it. Each item is moved at most once, so
# enqueue and dequeue take constant time on average.
#
# A queue is a dict, so it is mutable and is passed by reference like any dict; the functions below are the way to use it.
# Build one at run time (inside a function or at the top level of the script that loads this package). A queue stored in a
# frozen module cannot change.

_KIND = "std/queue"     # stored in every queue so that the functions can tell a queue from any other dict

# Fail unless q is a queue made by queue(). `who` names the calling function in the message.
def _check(who, q):
    if type(q) != "dict" or q.get("kind") != _KIND:
        fail("%s: got %s, want a queue made by queue()" % (who, type(q)))

# Make sure `front` holds the oldest item at its end, moving `back` over if `front` is empty.
def _refill(q):
    if not q["front"]:
        q["front"] = list(reversed(q["back"]))
        q["back"] = []

def queue(items = None, limit = None):
    "A new queue, holding the items of the iterable in order (the first becomes the oldest), with an optional cap on its length."
    if limit != None:
        if type(limit) != "int" or limit < 0:
            fail("queue: limit must be a non-negative int or None, got %s" % repr(limit))
    q = {"kind": _KIND, "front": [], "back": [], "limit": limit}
    if items != None:
        for x in items:
            enqueue(q, x)
    return q

def queue_len(q):
    "The number of items waiting in the queue."
    _check("queue_len", q)
    return len(q["front"]) + len(q["back"])

def queue_empty(q):
    "True if the queue has no items."
    return queue_len(q) == 0

def enqueue(q, item):
    "Add an item at the back. Fails if the queue has a limit and is full."
    _check("enqueue", q)
    limit = q["limit"]
    if limit != None and len(q["front"]) + len(q["back"]) >= limit:
        fail("enqueue: queue is full (limit %d)" % limit)
    q["back"].append(item)

def dequeue(q):
    "Remove and return the oldest item. Fails if the queue is empty."
    _check("dequeue", q)
    _refill(q)
    if not q["front"]:
        fail("dequeue: queue is empty")
    return q["front"].pop()

def peek(q):
    "Return the oldest item without removing it. Fails if the queue is empty."
    _check("peek", q)
    _refill(q)
    if not q["front"]:
        fail("peek: queue is empty")
    return q["front"][len(q["front"]) - 1]

def queue_clear(q):
    "Remove every item."
    _check("queue_clear", q)
    q["front"] = []
    q["back"] = []

def queue_items(q):
    "A new list of the items, oldest first. The queue is not changed."
    _check("queue_items", q)
    out = list(reversed(q["front"]))
    out.extend(q["back"])
    return out

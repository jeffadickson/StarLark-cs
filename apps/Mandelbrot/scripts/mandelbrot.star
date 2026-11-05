# A Mandelbrot set renderer in Starlark, drawn in 24-bit colour and computed in parallel with the futures extension.
#
# One *text row* of the picture is one task, and it covers two pixel rows: the top pixel is the foreground colour of a
# half-block character and the bottom pixel is its background, so every character cell is two square-ish pixels.
# parallel_map returns the rows in order, so the picture is identical however many threads helped.
#
# Starlark has no math module, which keeps scripts deterministic, so the smooth colouring below uses a small log2
# approximation written in the script itself.

# The most iterations tried for one point. A point that has not escaped by then is treated as inside the set (drawn black).
MAX_ITER = 400

# Colour stops (position 0..1, red, green, blue), a cycle that returns to its first colour. Deep ocean blue, cyan, white
# foam, gold, burnt orange, plum, and back.
STOPS = [
    (0.00, 5, 10, 60),
    (0.16, 20, 90, 200),
    (0.32, 120, 230, 255),
    (0.45, 255, 255, 240),
    (0.58, 255, 200, 60),
    (0.74, 200, 70, 20),
    (0.88, 90, 10, 90),
    (1.00, 5, 10, 60),
]

# Characters from light to dense, used for the plain (no colour) picture. The last, "@", is reserved for cells where either point is inside the set.
RAMP = " .,:;-=+*%#@"

# ---------------------------------------------------------------- numbers

# log2 for x >= 1: count the halvings, then a quadratic fit for the mantissa in [1, 2).
def log2(x):
    e = 0.0
    m = x
    for _ in range(64):
        if m < 2.0:
            break
        m = m / 2.0
        e += 1.0
    return e + (m - 1.0) * (1.3466 - 0.3466 * m)

# The smooth iteration count of the point c, or -1.0 when it never escapes (inside the set).
def smooth(cx, cy):
    # The main cardioid and the period-2 bulb are inside; skipping them saves most of the work in the black region.
    q = (cx - 0.25) * (cx - 0.25) + cy * cy
    if q * (q + (cx - 0.25)) <= 0.25 * cy * cy:
        return -1.0
    if (cx + 1.0) * (cx + 1.0) + cy * cy <= 0.0625:
        return -1.0
    x = 0.0
    y = 0.0
    for i in range(MAX_ITER):
        x2 = x * x
        y2 = y * y
        if x2 + y2 > 65536.0:
            # |z| is large, so log2(log2|z|) is the fractional part of the escape time.
            return i + 1.0 - log2(0.5 * log2(x2 + y2))
        y = 2.0 * x * y + cy
        x = x2 - y2 + cx
    return -1.0

# ---------------------------------------------------------------- colour

# The colour for a smooth iteration count `mu`: black inside the set, otherwise a position `t` along the cycle (one trip round the STOPS every `cycle` iterations)
# blended linearly between the two stops either side of it. Returns (red, green, blue), each 0 to 255.
def colour(mu, cycle):
    if mu < 0.0:
        return (0, 0, 0)
    t = (mu / cycle) % 1.0
    for k in range(1, len(STOPS)):
        hi = STOPS[k]
        if t <= hi[0]:
            lo = STOPS[k - 1]
            f = (t - lo[0]) / (hi[0] - lo[0])
            return (
                int(lo[1] + (hi[1] - lo[1]) * f),
                int(lo[2] + (hi[2] - lo[2]) * f),
                int(lo[3] + (hi[3] - lo[3]) * f),
            )
    return (STOPS[0][1], STOPS[0][2], STOPS[0][3])

# The ANSI escape sequence that sets the foreground (kind "38") or background (kind "48") to a 24-bit colour `c`.
def sgr(kind, c):
    return "\x1b[" + kind + ";2;" + str(c[0]) + ";" + str(c[1]) + ";" + str(c[2]) + "m"

# ---------------------------------------------------------------- rendering

# One text row: job is (row, width, height, left, top, step, ansi, cycle). `step` is the size of one pixel in the plane.
def render_row(job):
    row = job[0]
    width = job[1]
    left = job[3]
    top = job[4]
    step = job[5]
    ansi = job[6]
    cycle = job[7]
    # Each text row draws two pixel rows: the upper pixel (the half-block's foreground) and the lower one (its background), one pixel step apart in the plane (a text cell is two steps tall).
    cy_top = top - (2 * row) * step
    cy_bot = top - (2 * row + 1) * step
    out = []
    # For each character cell: work out both of its points and pick the output, either a half-block with two true colours or one character from RAMP.
    for col in range(width):
        cx = left + col * step
        a = smooth(cx, cy_top)
        b = smooth(cx, cy_bot)
        if ansi:
            # A half block "▀" is drawn in the foreground colour on its top half; the background colour shows through on the bottom half. So one cell carries two pixels.
            out.append(sgr("38", colour(a, cycle)) + sgr("48", colour(b, cycle)) + "▀")
        else:
            m = (a + b) / 2.0
            if a < 0.0 or b < 0.0:
                out.append(RAMP[-1])
            else:
                out.append(RAMP[int(m) % (len(RAMP) - 1)])
    if ansi:
        out.append("\x1b[0m")
    return "".join(out)

# Render `width` x `height` text cells (so `width` x `2*height` pixels) of the window centred on (cx, cy) that is
# `span` wide in the complex plane. Returns the list of text rows.
def render(width, height, cx, cy, span, ansi, cycle):
    step = span / width
    left = cx - span / 2.0
    # 2*height pixel rows centred on cy: the first row is (height - 0.5) steps above cy and the last is its mirror image below.
    top = cy + step * (height - 0.5)
    jobs = [(r, width, height, left, top, step, ansi, cycle) for r in range(height)]
    return parallel_map(render_row, jobs)

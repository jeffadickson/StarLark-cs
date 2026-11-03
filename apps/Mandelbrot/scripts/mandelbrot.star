# A Mandelbrot set renderer in Starlark, drawn in 24-bit colour and computed in parallel with the futures extension.
#
# One *text row* of the picture is one task, and it covers two pixel rows: the top pixel is the foreground colour of a
# half-block character and the bottom pixel is its background, so every character cell is two square-ish pixels.
# parallel_map returns the rows in order, so the picture is identical however many threads helped.
#
# Starlark has no math module, which keeps scripts deterministic, so the smooth colouring below uses a small log2
# approximation written in the script itself.

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
    cy_top = top - (2 * row) * step
    cy_bot = top - (2 * row + 1) * step
    out = []
    for col in range(width):
        cx = left + col * step
        a = smooth(cx, cy_top)
        b = smooth(cx, cy_bot)
        if ansi:
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
    # 2*height pixel rows centred on cy: the first sits half a pixel above the last one's mirror image.
    top = cy + step * (height - 0.5)
    jobs = [(r, width, height, left, top, step, ansi, cycle) for r in range(height)]
    return parallel_map(render_row, jobs)

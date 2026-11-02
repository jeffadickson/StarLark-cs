# A tiny ray tracer, written in Starlark, rendered in parallel with the futures extension.
#
# One *row* of the picture is one task. parallel_map hands the rows to worker threads and returns them in order, so
# the picture is identical however many threads helped. Everything a row needs is a module-level value or an
# argument, which is why it is allowed to run on another thread.

# ---------------------------------------------------------------- vectors (tuples of three floats)

def add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])

def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def mul(a, k):
    return (a[0] * k, a[1] * k, a[2] * k)

def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def length(a):
    return sqrt(dot(a, a))

def unit(a):
    n = length(a)
    return (a[0] / n, a[1] / n, a[2] / n)

# Newton's method: Starlark has no math module, which keeps scripts deterministic.
def sqrt(x):
    if x <= 0.0:
        return 0.0
    g = x if x > 1.0 else 1.0
    for _ in range(14):
        g = 0.5 * (g + x / g)
    return g

# ---------------------------------------------------------------- the scene

SPHERES = [
    # centre, radius, brightness 0..1, how mirror-like 0..1
    ((0.0, -0.2, 5.0), 1.2, 0.95, 0.35),
    ((-2.3, -0.7, 4.2), 0.7, 0.70, 0.15),
    ((2.2, -0.6, 4.4), 0.8, 0.80, 0.50),
    ((0.0, -1001.4, 0.0), 1000.0, 0.55, 0.20),     # the floor is a very large sphere
]
LIGHT = (-4.0, 6.0, 0.0)
EYE = (0.0, 0.2, -1.5)

# Characters from dark to bright. The picture is shown as ASCII art.
RAMP = " .:-=+*#%@"

def hit_sphere(origin, direction, sphere):
    # Returns the distance along the ray to the sphere, or -1.0 if it misses.
    oc = sub(origin, sphere[0])
    b = dot(oc, direction)
    c = dot(oc, oc) - sphere[1] * sphere[1]
    disc = b * b - c
    if disc < 0.0:
        return -1.0
    t = -b - sqrt(disc)
    return t if t > 0.001 else -1.0

def nearest(origin, direction):
    best = -1.0
    which = -1
    for i in range(len(SPHERES)):
        t = hit_sphere(origin, direction, SPHERES[i])
        if t > 0.0 and (best < 0.0 or t < best):
            best = t
            which = i
    return best, which

def shade(origin, direction, depth):
    # Starlark forbids recursion, so reflections are a loop: each bounce adds a mirror-weighted share of the light
    # seen from the next hit, and `weight` is how much of the final colour that bounce still contributes.
    total = 0.0
    weight = 1.0
    for _ in range(depth + 1):
        t, i = nearest(origin, direction)
        if i < 0:
            # sky: a little brighter toward the horizon
            return total + weight * (0.02 + 0.10 * (1.0 - (direction[1] + 1.0) / 2.0))
        sphere = SPHERES[i]
        point = add(origin, mul(direction, t))
        normal = unit(sub(point, sphere[0]))
        to_light = unit(sub(LIGHT, point))
        brightness = dot(normal, to_light)
        if brightness < 0.0:
            brightness = 0.0
        st, si = nearest(add(point, mul(normal, 0.01)), to_light)   # shadow: anything between us and the light?
        if si >= 0:
            brightness = brightness * 0.35
        own = sphere[2] * (0.06 + 0.94 * brightness)
        mirror = sphere[3]
        total += weight * own * (1.0 - mirror)
        weight = weight * mirror
        if mirror <= 0.0:
            break
        origin = add(point, mul(normal, 0.01))
        direction = unit(sub(direction, mul(normal, 2.0 * dot(direction, normal))))
    return total

def render_row(job):
    # job is (row, width, height). The result is a string of characters, one per pixel.
    row = job[0]
    width = job[1]
    height = job[2]
    out = []
    y = (1.0 - 2.0 * (row + 0.5) / height) * 0.55
    for col in range(width):
        x = (2.0 * (col + 0.5) / width - 1.0) * 0.55 * (width / (height * 2.0))
        direction = unit((x, y, 1.0))
        v = shade(EYE, direction, 2)
        n = int(v * 9.0 + 0.5)
        n = 0 if n < 0 else (9 if n > 9 else n)
        out.append(RAMP[n])
    return "".join(out)

def render(width, height):
    jobs = [(row, width, height) for row in range(height)]
    return parallel_map(render_row, jobs)

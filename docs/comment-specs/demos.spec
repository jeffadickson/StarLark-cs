@@ Comments for the Mandelbrot, ParallelBuilds and RayTracer scripts.

@file apps/Mandelbrot/scripts/mandelbrot.star
@line MAX_ITER = 400
The most iterations tried for one point. A point that has not escaped by then is treated as inside the set (drawn black).

@line RAMP = " .,:;-=+*%#@"
Characters from light to dense, used for the plain (no colour) picture. The last, "@", is also used for points inside the set.

@def colour
The colour for a smooth iteration count `mu`: black inside the set, otherwise a position `t` along the cycle (one trip round the STOPS every `cycle` iterations)
blended linearly between the two stops either side of it. Returns (red, green, blue), each 0 to 255.

@def sgr
The ANSI escape sequence that sets the foreground (kind "38") or background (kind "48") to a 24-bit colour `c`.

@line cy_top = top - (2 * row) * step
Each text row draws two pixel rows: the upper pixel (the half-block's foreground) and the lower one (its background), a half step apart in the plane.

@line for col in range(width):
For each character cell: work out both of its points and pick the output, either a half-block with two true colours or one character from RAMP.

@line out.append(sgr("38", colour(a, cycle))
A half block "▀" is drawn in the foreground colour on its top half; the background colour shows through on the bottom half. So one cell carries two pixels.

@file apps/ParallelBuilds/scripts/pipeline.star
@line PLAN = [
The three builds of the demo: component name, how long the (simulated) build takes in milliseconds, and whether it is made to fail.

@line PLAN_BROKEN = [
The same plan with the web build failing, to show a release being blocked.

@def build_serial
Run the builds one after another. `.get()` straight after each start waits for that build before the next one begins, so the times add up.

@def build_parallel
Start every build first, then wait for all of them together. `run_build` returns a future at once, so the builds overlap and the total is about the longest one.

@def report
Print one line per build result: name, ok or FAILED, how long it took, and its log line.

@def release
A release: build everything in parallel, report, and stop if any build failed. Only when all succeed is the package step run, and that waits (`.get()`) because it needs the results.

@def serial
Entry point for the host: build the plan one at a time, return how many builds ran.

@def parallel
Entry point for the host: build the plan all at once, return how many builds ran.

@def good_release
Entry point for the host: a release where every build works.

@def broken_release
Entry point for the host: a release with one failing build, which is blocked.

@def many
Twelve builds in batches of four (the host's limit on builds in flight). Starting a fifth before one finishes would stop the run with a limit error, so each batch is collected before the next starts.

@file apps/RayTracer/scripts/render.star
@def add
Add two vectors (three-item tuples).

@def sub
Subtract the second vector from the first.

@def mul
Scale a vector by the number k.

@def dot
The dot product of two vectors.

@def length
The length of a vector.

@def unit
The vector scaled to length 1 (the same direction).

@def sqrt
Square root by Newton's method, starting from the larger of x and 1 and iterating 14 times. Starlark has no math module, which keeps scripts deterministic. Returns 0 for x <= 0.

@line SPHERES = [
The scene is only spheres: centre, radius, brightness (0 to 1) and how mirror-like it is (0 to 1).

@line LIGHT = (
Position of the single light.

@line EYE = (
Position of the camera.

@def hit_sphere
Where a ray meets a sphere: the distance along the ray to the nearer intersection in front of the origin, or -1.0 for a miss. Solves the ray-sphere quadratic; hits closer than 0.001 are ignored so a ray does not hit the surface it just left.

@def nearest
The closest sphere a ray hits. Returns (distance, index of the sphere), or (-1.0, -1) if it hits none.

@def shade
The brightness (0 to about 1) seen along a ray. Hits the nearest sphere, lights it by the angle to the light (dimmed to 35% if something is in the way, which gives shadows), then follows the reflection for `depth` more bounces. Written as a loop because Starlark does not allow recursion.

@def render_row
One picture row, as a string with one character per pixel. Works out the ray for each pixel from the row and column, shades it, and picks a character from RAMP. This is one task for parallel_map.

@def render
The picture as a list of row strings. Each row is independent, so parallel_map computes them side by side and returns them in order.

@@ The last functions without a comment, and one duplicate to tidy.

@file src/Starlark.Eval/Lib/approx_math.star
@def _check
Validate an argument: it must be an int or a float, otherwise fail with "name: got TYPE, want float or int". Returns it as a float. Every public function starts with this.

@def _scale
Multiply x by 2**k using the bits of k: one multiplication (or division, for a negative k) per set bit, by the exact table value _BIG[j] = 2**(2**j). Exact, because multiplying by a power of two never rounds.

@def _ksin
The sine of a reduced argument |r| <= pi/4, as r times a polynomial in r*r (the Taylor series, evaluated by Horner's rule).

@def _kcos
The cosine of a reduced argument |r| <= pi/4, as a polynomial in r*r.

@file apps/PolicyEngine/scripts/badtypes.star
@def helper
Wrong on purpose: promises an int but adds a string to a number, so the type checker reports it. Never called.

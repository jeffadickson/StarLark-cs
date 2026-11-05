@@ Comments for the small demo apps: PolicyEngine, GameAI, ReportPipeline, ConfigInspector, BuildRules.

@file apps/PolicyEngine/scripts/acme.star
@def basket_total
The value of a basket: price times quantity, summed over the items of the request.

@def decide
The tenant's policy. Called by the host with a request (customer and items) and returns a decision: whether to allow it, the discount, and a reason. Gold and Silver customers get 15% and 5%,
five or more items add 2%, and a basket over 5000 needs a verified customer.

@file apps/PolicyEngine/scripts/badtypes.star
@@ Deliberately wrong: shows the type checker rejecting a script before it runs.
@line def decide
This script is wrong on purpose. `decide` promises a dict and returns an int, and `helper` promises an int and adds a string to a number. The type checker reports both before anything runs.

@file apps/PolicyEngine/scripts/globex.star
@def decide
The tenant's policy: customers in a blocked region are refused outright; otherwise the request is allowed, with a flat discount of 25 on a basket worth more than 1000.

@file apps/PolicyEngine/scripts/hostile.star
@def decide
A hostile policy, to show the host's limits working: a billion-step loop. The host's step budget stops it with an error, so one bad tenant cannot hang the service.

@file apps/GameAI/scripts/glutton.star
@def act
A player that wastes its turn on purpose, spending far more than the per-turn step budget. The host stops it and counts the turn as lost.

@file apps/GameAI/scripts/lib.star
@line Kind = enum(
The kinds of action a player may take. An enum rejects anything else.

@line Action = record(
What a player returns each turn: the kind of action and, for a move, a direction (+1 or -1).

@def move
An action: step one cell in the direction `dir` (any positive number is +1, anything else -1).

@def attack
An action: attack the foe. Costs energy and only works within range 2.

@def rest
An action: do nothing and recover energy.

@def toward
The direction (+1 or -1) from `me` towards `foe`.

@def distance
How far apart two fighters are on the line (an absolute value; Starlark has no abs here to keep the example plain).

@file apps/GameAI/scripts/rusher.star
@def act
Strategy: charge. Attack when within range and with enough energy, close the distance when not, and rest otherwise.

@file apps/GameAI/scripts/sniper.star
@def act
Strategy: stay at range 2. Attack when in range and with energy, back away when too close, approach when too far, otherwise rest.

@file apps/GameAI/scripts/timid.star
@def act
Strategy: only fight with plenty of energy. Rest below 4 energy; attack when in range; otherwise rest.

@file apps/ReportPipeline/scripts/report.star
@line valid = [
Keep only rows with a positive amount that are not cancelled.

@line enriched = [
Add the region (looked up by the host from the country) and the net amount after country-specific tax (also computed by the host).

@def group_sum
Sum `field` of each item, grouped by the value of `key`. Returns a dict of key -> total.

@line by_region = group_sum
Totals by region (net) and by product (gross).

@line top_products = sorted(
The two products with the largest totals, largest first.

@line summary = [
One row per region with its net total and its share of all regions as a percentage, rounded by the host.

@line payload = json.encode(
The final result, as JSON text for the host to read back.

@file apps/ConfigInspector/scripts/broken.star
@def replicas_for
Wrong on purpose: promises an int and returns a string (n + "2"). The checker finds it before the config is built.

@file apps/ConfigInspector/scripts/defaults.star
@line LogLevel = enum(
The allowed log levels. Anything else is an error when the config is made.

@line Config = record(
The shape of a configuration, with a type for each field. Making a Config with a wrongly typed field is an error.

@line DEFAULTS = {
Values used when an environment does not set them.

@file apps/ConfigInspector/scripts/prod.star
@def replicas_for
Two replicas per region.

@file apps/BuildRules/scripts/rules/cc.star
@line Opt = enum(
The allowed optimisation levels; anything else (such as "Ofast") is rejected when the target is declared.

@def _cc
The work common to cc_library and cc_binary: check the name, require at least one source, normalise the dependency labels, validate the optimisation level, and declare the target to the host.
The leading underscore marks it as private to this file; BUILD files cannot load it.

@def cc_library
A C library target. `deps` may be written ":name" or "name".

@def cc_binary
A C executable target, with the same arguments as cc_library.

@file apps/BuildRules/scripts/rules/common.star
@def check_name
Reject a target name that is not letters, digits and underscores, with a clear error.

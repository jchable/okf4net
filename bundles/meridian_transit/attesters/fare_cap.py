# SPDX-License-Identifier: LGPL-3.0-or-later
"""Attester for `computations/capped-fare.md`.

Deterministic, no LLM, no network, no I/O (§10.2). It is handed the receipt a run
produced and the values that run was given, and it recomputes the policy from those
values rather than trusting anything the executor reported.

What it recomputes is the whole sanctioned result, not only the day's total: trip by
trip, in the order the fares were presented, each trip is charged
`max(0, min(fare, cap - charged_so_far))`. The receipt must then match that
recomputation exactly — `charged_cents`, `waived_cents`, and `per_trip_cents`
element for element.

The per-trip comparison is the check that matters most. A total is order-blind:
`[0, 250, 250, 200]` sums to 700, reconciles with 300 waived, and keeps every charge
within its own fare, yet it is not what the policy charges for four 250 fares against
a 700 cap — that is `[250, 250, 200, 0]`. The per-trip split is what appears on a
rider's statement and what a rider disputes (`policies/fare-capping.md`), so an
attester that only checked the total would pass a wrong statement.

That independence is the point. Comparing `executed_sql`-style echoes only proves
the host sent what it was given; recomputing the sequential split here proves the
numbers being displayed are the numbers the policy defines.

Every failure returns a specific `reason`; a malformed receipt is a failing verdict,
never an exception.
"""

import json


def _is_int(value):
    # JSON `true`/`false` arrive as Python bools, which are ints to `isinstance`.
    return isinstance(value, int) and not isinstance(value, bool)


def _sequential_split(fares, cap):
    """The sanctioned computation's own loop, restated: the policy's per-trip charges."""
    charged = 0
    per_trip = []
    for fare in fares:
        take = max(0, min(fare, cap - charged))
        charged += take
        per_trip.append(take)
    return per_trip


def attest(*, sanctioned_computation, receipt, values):
    try:
        fares = json.loads(values["fares_cents"])
        cap = values["cap_cents"]
    except (KeyError, ValueError, TypeError) as exc:
        return {"ok": False, "reason": f"inputs unusable: {type(exc).__name__}"}

    if not isinstance(fares, list) or not all(_is_int(fare) for fare in fares):
        return {"ok": False, "reason": "inputs unusable: fares_cents is not a JSON array of integers"}
    if not _is_int(cap):
        return {"ok": False, "reason": "inputs unusable: cap_cents is not an integer"}

    if not isinstance(receipt, dict):
        return {"ok": False, "reason": "receipt is not an object"}
    for field in ("charged_cents", "waived_cents", "per_trip_cents"):
        if field not in receipt:
            return {"ok": False, "reason": f"receipt is missing '{field}'"}

    charged = receipt["charged_cents"]
    waived = receipt["waived_cents"]
    per_trip = receipt["per_trip_cents"]

    if not _is_int(charged):
        return {"ok": False, "reason": f"charged_cents is not an integer: {charged!r}"}
    if not _is_int(waived):
        return {"ok": False, "reason": f"waived_cents is not an integer: {waived!r}"}
    if not isinstance(per_trip, list):
        return {"ok": False, "reason": f"per_trip_cents is not a list: {per_trip!r}"}
    for i, charge in enumerate(per_trip, start=1):
        if not _is_int(charge):
            return {"ok": False, "reason": f"per-trip charge for trip {i} is not an integer: {charge!r}"}

    expected_split = _sequential_split(fares, cap)
    expected_charged = sum(expected_split)

    # The day's total. For non-negative fares this is min(sum(fares), cap), the
    # policy's defining property; it is derived from the split so the two can never
    # disagree about what the policy says.
    if charged != expected_charged:
        return {"ok": False, "reason": f"charged {charged}, policy says {expected_charged}"}

    # Nothing created or lost between the fares presented and the two totals.
    if charged + waived != sum(fares):
        return {"ok": False, "reason": f"charged {charged} + waived {waived} does not reconcile to the {sum(fares)} in fares presented"}

    if len(per_trip) != len(fares):
        return {"ok": False, "reason": f"{len(per_trip)} per-trip charges for {len(fares)} trips"}

    if sum(per_trip) != charged:
        return {"ok": False, "reason": f"per-trip split sums to {sum(per_trip)}, not {charged}"}

    for i, (charge, fare) in enumerate(zip(per_trip, fares), start=1):
        if charge > fare or charge < 0:
            return {"ok": False, "reason": f"per-trip charge {charge} for trip {i} is not within [0, {fare}]"}

    # The order check. Everything above can pass with the right charges on the wrong
    # trips; only this comparison sees that.
    for i, (charge, want) in enumerate(zip(per_trip, expected_split), start=1):
        if charge != want:
            return {
                "ok": False,
                "reason": f"trip {i} charged {charge}, but the policy's sequential split is {expected_split}",
            }

    return {"ok": True, "reason": None}

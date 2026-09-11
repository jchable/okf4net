# SPDX-License-Identifier: LGPL-3.0-or-later
"""Attester for `computations/capped-fare.md`.

Deterministic, no LLM, no network, no I/O (§10.2). It is handed the receipt a run
produced and the values that run was given, and it recomputes the policy's defining
property from those values rather than trusting anything the executor reported.

That independence is the point. Comparing `executed_sql`-style echoes only proves
the host sent what it was given; recomputing `min(sum(fares), cap)` here proves the
number being displayed is the number the policy defines.
"""


def attest(*, sanctioned_computation, receipt, values):
    try:
        fares = __import__("json").loads(values["fares_cents"])
        cap = int(values["cap_cents"])
    except (KeyError, ValueError, TypeError) as exc:
        return {"ok": False, "reason": f"inputs unusable: {type(exc).__name__}"}

    for field in ("charged_cents", "waived_cents", "per_trip_cents"):
        if field not in receipt:
            return {"ok": False, "reason": f"receipt is missing '{field}'"}

    charged = receipt["charged_cents"]
    waived = receipt["waived_cents"]
    per_trip = receipt["per_trip_cents"]

    expected = min(sum(fares), cap)
    if charged != expected:
        return {"ok": False, "reason": f"charged {charged}, policy says {expected}"}

    if sum(per_trip) != charged:
        return {"ok": False, "reason": f"per-trip split sums to {sum(per_trip)}, not {charged}"}

    if charged + waived != sum(fares):
        return {"ok": False, "reason": "charged + waived does not reconcile to the fares presented"}

    if len(per_trip) != len(fares):
        return {"ok": False, "reason": f"{len(per_trip)} per-trip charges for {len(fares)} trips"}

    for charge, fare in zip(per_trip, fares):
        if charge > fare or charge < 0:
            return {"ok": False, "reason": f"per-trip charge {charge} is not within [0, {fare}]"}

    return {"ok": True, "reason": None}

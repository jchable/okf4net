# SPDX-License-Identifier: LGPL-3.0-or-later
"""Attester for `computations/daily-ridership.md`.

Deterministic, no LLM, no network, no I/O (§10.2).

What it can and cannot establish is worth stating plainly, because the difference
is the whole subject of §10:

*   It **can** check invariants the query's own shape guarantees — one row, both
    declared fields present, each count a genuine JSON integer, neither count
    negative, `distinct_riders <= completed_trips`. A result violating any of those
    was not produced by this query, whatever the receipt says about it.

    "Genuine integer" is strict on purpose. `count(*)` is a Postgres `bigint`, which
    the SQL wrapper's driver hands back as a Python `int` and `json.dumps` writes as
    a bare integer literal, so a real receipt never carries anything else. A string
    (`"5"`), a float (`2.9`, and an integral `5.0` too) or a boolean (`True`, which
    Python treats as an `int`) is not what this query produces, and is rejected
    rather than coerced: `int()` would accept `"5"` and silently truncate `2.9` to
    `2`, the opposite of an attester's job. (Under the .NET container host an
    integral `5.0` in the executor's output is re-serialized as `5` before it gets
    here, so in practice this sees an `int`; the check still refuses a float it is
    actually handed.)

*   It **cannot** establish provenance. Under a container host, `executed_sql` is
    echoed back by the host's own wrapper from the string the host sent, so
    comparing it against the sanctioned text can only fail if the host is broken.
    Postgres exposes no equivalent of BigQuery's `job_id` — an identifier the
    engine itself mints, which a consumer can resolve against the engine's own
    record of what ran. Until a receipt carries one, this check is a convention,
    not evidence.

The sibling attester for `capped-fare.md` does not have this limitation: it
recomputes the policy from the inputs.
"""


def attest(*, sanctioned_computation, receipt, values):
    for field in ("executed_sql", "result"):
        if field not in receipt:
            return {"ok": False, "reason": f"receipt is missing '{field}'"}

    rows = receipt["result"]
    if not isinstance(rows, list) or len(rows) != 1:
        return {"ok": False, "reason": f"expected exactly one row, got {len(rows) if isinstance(rows, list) else type(rows).__name__}"}

    row = rows[0]
    if not isinstance(row, dict):
        return {"ok": False, "reason": f"expected the row to be an object, got {type(row).__name__}"}

    for column in ("completed_trips", "distinct_riders"):
        if column not in row:
            return {"ok": False, "reason": f"row is missing column '{column}'"}
        value = row[column]
        # bool first: True/False pass isinstance(_, int), and are never a count.
        if isinstance(value, bool) or not isinstance(value, int):
            return {"ok": False, "reason": f"{column} is not an integer: {value!r} ({type(value).__name__})"}

    trips = row["completed_trips"]
    riders = row["distinct_riders"]

    if trips < 0 or riders < 0:
        return {"ok": False, "reason": f"negative counts: {trips} trips, {riders} riders"}

    if riders > trips:
        return {"ok": False, "reason": f"{riders} distinct riders across {trips} trips is impossible"}

    return {"ok": True, "reason": None}

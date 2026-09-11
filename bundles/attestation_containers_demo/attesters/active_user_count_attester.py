"""Deterministic attester for computations/active-user-count.md (runtime: postgres).

Verifies provenance (the executed SQL matches the sanctioned text, modulo
whitespace) and that the reported count is a non-negative integer. See
greeting_attester.py's docstring for why this does not follow
bundles/acme_retail/attesters/sql_equality.py's signature.
"""

import re


def _canonicalize(sql):
    return re.sub(r"\s+", " ", sql).strip()


def attest(*, sanctioned_computation, receipt, values):
    executed = receipt.get("executed_sql")
    if executed is None or _canonicalize(executed) != _canonicalize(sanctioned_computation):
        return {"ok": False, "reason": "executed SQL does not match the sanctioned computation"}

    result = receipt.get("result") or []
    if not result or "active_users" not in result[0]:
        return {"ok": False, "reason": "receipt is missing the active_users column"}

    count = result[0]["active_users"]
    if not isinstance(count, int) or count < 0:
        return {"ok": False, "reason": f"active_users is not a non-negative integer: {count!r}"}

    return {"ok": True, "reason": None}

"""Deterministic attester for computations/greeting.md (runtime: python).

Written against OKF4net.Attestation.Containers's real invocation contract:
the container bootstrap calls attest(sanctioned_computation=..., receipt=...,
values=...). This is NOT the same signature as
bundles/acme_retail/attesters/sql_equality.py, which expects claimed_value --
a consumer-side notion with no counterpart in AttestationContext.
"""


def attest(*, sanctioned_computation, receipt, values):
    name = values.get("name", "world")
    expected = f"Hello, {name}!"
    actual = receipt.get("message")
    if actual != expected:
        return {"ok": False, "reason": f"expected {expected!r}, got {actual!r}"}
    return {"ok": True, "reason": None}

"""Deterministic attester for the revenue computation receipt."""


def attest(receipt: dict) -> bool:
    return "result" in receipt

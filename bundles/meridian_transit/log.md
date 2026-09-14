---
type: Log
title: Meridian Transit bundle history
---

# Bundle history

## 2026-09-14

- **Update**: the [capped fare](/computations/capped-fare.md) attester now rejects a negative `cap_cents` or fare as unusable input. It checked only that they were integers, so a cap of `-1` recomputed to an all-zero split and a receipt matching it passed. The sanctioned computation itself is unchanged.
- **Update**: the [capped fare](/computations/capped-fare.md) attester now recomputes the sequential per-trip split and requires the receipt to match it element for element. It previously checked only order-blind properties (total, reconciliation, per-trip bounds), so a split with the right charges on the wrong trips — `[0, 250, 250, 200]` instead of `[250, 250, 200, 0]` — passed. The sanctioned computation itself is unchanged.

## 2026-09-11

- **Creation**: bundle established with two Attested Computations, one per runtime — [daily ridership](/computations/daily-ridership.md) on `postgres`, [capped fare](/computations/capped-fare.md) on `python`.
- **Verified**: `human:dlemoine@meridian` reviewed the ridership computation against the Trip Counting Policy; `human:rkaur@meridian` reviewed the capped-fare computation against the Fare Capping Policy.

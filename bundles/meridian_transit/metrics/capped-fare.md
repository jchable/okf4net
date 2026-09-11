---
type: Metric
title: Capped fare per rider-day
description: What a rider is actually charged for one operating day once the daily cap is applied, and what the cap waived. Backed by an Attested Computation.
tags: [fares, headline-metric]
generated: { by: human:rkaur@meridian, at: 2026-09-11T09:00:00Z }
verified:
  - { by: human:rkaur@meridian, at: 2026-09-11T10:15:00Z }
status: stable
stale_after: 2027-06-30T00:00:00Z
sources:
  - id: fare-capping
    resource: policies/fare-capping.md
    title: Daily Fare Capping Policy (2026)
    author: human:rkaur@meridian
    last_modified: 2026-08-20T00:00:00Z
---

# Definition

The capped fare is what a rider owes for one operating day after the daily cap is
applied to their trips in order, together with the amount the cap waived. [^fare-capping]

Produced by [the capped-fare computation](../computations/capped-fare.md).

# Why it is not "sum of fares"

Summing [`trips.fare_cents`](../tables/trips.md) gives the **uncapped** total and
overstates revenue for every rider who reached the cap. The difference is the waived
amount, which is a reported figure in its own right — it is what the capping policy
costs.

# The per-trip split matters

The computation returns what each individual trip was charged, not only the day's
total. That breakdown is what appears on a rider's statement and what a rider
disputes, so it is part of the sanctioned output rather than something a consumer
reconstructs.

[^fare-capping]: Daily Fare Capping Policy (2026)

---
type: Policy
title: Daily Fare Capping Policy (2026)
description: A rider is never charged more in one operating day than the daily cap; trips beyond it travel free.
tags: [fares, policy]
generated: { by: human:rkaur@meridian, at: 2026-08-20T00:00:00Z }
verified:
  - { by: human:rkaur@meridian, at: 2026-08-20T00:00:00Z }
status: stable
stale_after: 2027-06-30T00:00:00Z
---

# Rule

Within one operating day, a rider pays the ordinary fare for each trip **until their
cumulative charge reaches the daily cap**. The trip that crosses the cap is charged
only the remaining amount. Every trip after it is charged nothing.

The cap is a board-set figure, passed to the computation rather than hard-coded, so
a change of cap does not change the sanctioned computation.

# Why the order matters

The rule is procedural: what a trip costs depends on every trip before it that day.
Two riders with the same trips in a different order pay the same total, but the
*per-trip* breakdown differs — and the breakdown is what appears on a statement and
what a rider disputes. The sanctioned computation therefore returns the per-trip
split, not only the total.

# Examples

A cap of 700, with fares of 250, 250, 250, 250:

| Trip | Fare | Charged | Running total |
|------|------|---------|---------------|
| 1    | 250  | 250     | 250           |
| 2    | 250  | 250     | 500           |
| 3    | 250  | **200** | 700           |
| 4    | 250  | **0**   | 700           |

Charged 700, waived 300.

# Boundary cases, stated so they are not left to judgement

- A rider whose fares total **below** the cap pays them all; nothing is waived.
- A single trip priced **above** the cap is charged the cap, not the fare.
- A day with no trips charges nothing; it is not an error.

# Implemented by

[`computations/capped-fare.md`](../computations/capped-fare.md), whose attester
recomputes this rule from the same inputs rather than trusting the run's own report.

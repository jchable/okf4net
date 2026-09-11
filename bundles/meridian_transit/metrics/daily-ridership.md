---
type: Metric
title: Daily ridership
description: Completed trips and distinct fare media for one operating day. Backed by an Attested Computation.
tags: [ridership, headline-metric]
generated: { by: human:dlemoine@meridian, at: 2026-09-11T09:00:00Z }
verified:
  - { by: human:dlemoine@meridian, at: 2026-09-11T09:30:00Z }
status: stable
stale_after: 2027-06-30T00:00:00Z
sources:
  - id: trip-counting
    resource: policies/trip-counting.md
    title: Trip Counting Policy (2026)
    author: human:dlemoine@meridian
    last_modified: 2026-08-20T00:00:00Z
---

# Definition

Daily ridership is the number of **completed** trips on an operating day, reported
alongside the number of distinct fare media that made them. [^trip-counting]

The figure is produced by
[the daily-ridership computation](../computations/daily-ridership.md); this concept
only narrates it. Trust lives on the computation, not here.

# How to read it

"Distinct riders" counts **fare media, not people** — a household sharing one card
is one. Treat it as a lower bound on unique travellers, and never as a population
figure.

The two numbers move differently: a strike-day drop shows up in trips immediately,
while distinct riders falls more slowly, because occasional riders keep making one
trip each.

[^trip-counting]: Trip Counting Policy (2026)

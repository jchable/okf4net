---
type: Postgres Table
title: trips
description: One row per fare-system trip, closed or abandoned, with the operating day it belongs to.
resource: postgres://meridian/public/trips
tags: [ridership, fares, source-table]
generated: { by: process:fare-system-export, at: 2026-09-01T00:00:00Z }
verified:
  - { by: human:dlemoine@meridian, at: 2026-09-01T00:00:00Z }
status: stable
stale_after: 2027-06-30T00:00:00Z
---

# Schema

| Column         | Type          | Notes                                                        |
|----------------|---------------|--------------------------------------------------------------|
| `trip_id`      | `bigint`      | Primary key, minted by the fare system.                       |
| `rider_id`     | `bigint`      | Stable per fare medium, not per person.                       |
| `service_date` | `date`        | The **operating day**, written at trip close. See the policy. |
| `status`       | `text`        | `completed` or `abandoned`.                                   |
| `fare_cents`   | `integer`     | The ordinary fare, **before** any daily cap is applied.       |

# Notes that change how it is read

- `rider_id` identifies a **fare medium**, not a person: a household sharing one
  card is one rider here. Distinct-rider counts are therefore a lower bound on
  people.
- `fare_cents` is the uncapped fare. The amount actually charged is the capping
  policy's output, not this column — a sum over `fare_cents` overstates revenue for
  any rider who reached the cap.
- A row is written when a trip closes, so the table is not a live view of trips in
  progress.

# Used by

- [`computations/daily-ridership.md`](../computations/daily-ridership.md) reads
  `service_date` and `status`.
- The fares feeding [`computations/capped-fare.md`](../computations/capped-fare.md)
  come from `fare_cents`, assembled per rider-day by the caller.

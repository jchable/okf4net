---
type: Attested Computation
title: Daily ridership for a service date
description: Sanctioned SQL producing completed trips and distinct riders for one service date, per Meridian's trip-counting policy.
tags: [ridership, attested, postgres]
runtime: postgres
parameters:
  - { name: service_date, type: string, required: true }
executor:
  receipt: [executed_sql, result]
attester:
  resource: attesters/ridership_shape.py
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
  - id: trips-table
    resource: tables/trips.md
    title: trips (Postgres table)
    author: team:data-platform
    last_modified: 2026-09-01T00:00:00Z
---

# Computation

```sql
SELECT count(*) AS completed_trips, count(DISTINCT rider_id) AS distinct_riders FROM trips WHERE service_date = :service_date::date AND status = 'completed'
```

This implements the two rules of the Trip Counting Policy: [^trip-counting]

1. **A trip counts once it completes.** `status = 'completed'` — a trip abandoned at
   the gate, or still in progress at the cut-off, is not ridership.
2. **A service date is the operating day, not the calendar day.** `service_date` is
   written by the fare system when the trip closes, and already accounts for
   post-midnight service belonging to the previous operating day. The computation
   never derives it from a timestamp.

The parameter is bound by the driver, never interpolated: the placeholder
`:service_date` survives verbatim into the receipt's `executed_sql`, and the
`::date` cast is part of the sanctioned text rather than something a caller
supplies.

# What the attester checks

`attesters/ridership_shape.py` receives the receipt and verifies three things that
do not depend on trusting the executor:

1. **Both declared fields are present**, and `result` holds exactly one row.
2. **`distinct_riders <= completed_trips`** — an invariant of the query's own shape.
   A result violating it means the rows were not produced by this query, whatever
   `executed_sql` says.
3. **Neither count is negative.**

It deliberately does **not** treat `executed_sql == sanctioned` as proof of
provenance. Under a container host, `executed_sql` is echoed back by the wrapper
from the string the host itself sent, so that comparison can only fail if the host
is broken — it is a convention check, not evidence the database ran this text. Real
provenance needs an identifier the engine produces, which Postgres does not expose
the way BigQuery's `job_id` does. See [`references/running.md`](../references/running.md).

# Freshness

`stale_after` mirrors the policy's annual review. A consumer running this after the
review date should re-verify before serving the figure.

[^trip-counting]: Trip Counting Policy (2026)

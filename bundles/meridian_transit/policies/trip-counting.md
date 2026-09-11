---
type: Policy
title: Trip Counting Policy (2026)
description: What counts as a trip for ridership reporting, and which operating day it belongs to.
tags: [ridership, policy]
generated: { by: human:dlemoine@meridian, at: 2026-08-20T00:00:00Z }
verified:
  - { by: human:dlemoine@meridian, at: 2026-08-20T00:00:00Z }
status: stable
stale_after: 2027-06-30T00:00:00Z
---

# Rule

1. **A trip counts once it completes.** A trip abandoned at the gate, or still open
   at the reporting cut-off, is not ridership. In the data this is
   `status = 'completed'`.
2. **A trip belongs to its operating day, not its calendar day.** The fare system
   writes `service_date` when the trip closes, and post-midnight service is already
   attributed to the day whose timetable it belongs to. Ridership reporting reads
   that column and never derives a day from a timestamp.

# Why rule 2 exists

Deriving the day from a timestamp splits a single night's service across two
reported days, which makes a Friday look light and a Saturday look heavy every
week. The fare system already resolves this at the source; reporting must not
re-resolve it differently.

# Implemented by

[`computations/daily-ridership.md`](../computations/daily-ridership.md).

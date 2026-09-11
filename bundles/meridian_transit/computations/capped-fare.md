---
type: Attested Computation
title: Capped fare for one rider-day
description: Sanctioned Python applying Meridian's daily fare cap to one rider's ordered trip fares, returning what is charged and what is waived.
tags: [fares, attested, python]
runtime: python
parameters:
  - { name: fares_cents, type: string, required: true }
  - { name: cap_cents, type: integer, required: true }
executor:
  receipt: [charged_cents, waived_cents, per_trip_cents]
attester:
  resource: attesters/fare_cap.py
generated: { by: human:dlemoine@meridian, at: 2026-09-11T09:00:00Z }
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

# Computation

```python
import json, os
params = json.loads(os.environ['OKF_PARAMS_JSON'])
fares = json.loads(params['fares_cents'])
cap = params['cap_cents']
charged = 0
per_trip = []
for fare in fares:
    take = max(0, min(fare, cap - charged))
    charged += take
    per_trip.append(take)
print(json.dumps({
    'charged_cents': charged,
    'waived_cents': sum(fares) - charged,
    'per_trip_cents': per_trip,
}))
```

# Why this one is not SQL

Fare capping is **order-dependent and procedural**: each trip is charged only up to
whatever remains of the cap, so the amount a given trip costs depends on every trip
before it that day. [^fare-capping] A window function can express it, but the policy
is written as a procedure and Finance reviews it as one — and the point of §10 is
that the sanctioned artefact is the thing that runs, not a translation of it.

It is also **pure**: it reads no database. That is what lets it run with
`--network none`, and why its parameters arrive as values rather than as a query.
`fares_cents` is a JSON array carried in a `string` parameter, because §10.2's
parameter families are scalar; the array is data the caller assembles, never
computation code.

# What the attester checks

`attesters/fare_cap.py` **independently recomputes the invariant** from the same
inputs, rather than comparing an echo:

1. `charged_cents == min(sum(fares), cap)` — the policy's defining property.
2. `sum(per_trip_cents) == charged_cents` — the per-trip split is consistent with
   the total.
3. `charged_cents + waived_cents == sum(fares)` — nothing is created or lost.
4. No per-trip charge exceeds its own fare.

None of those can pass by accident if the executed script differs from the
sanctioned one in any way that matters. This is the check the SQL sibling *cannot*
make, and the reason this bundle carries both kinds.

[^fare-capping]: Daily Fare Capping Policy (2026)

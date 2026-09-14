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

`attesters/fare_cap.py` **independently recomputes the policy** from the same
inputs, rather than comparing an echo. It walks the fares in the order presented,
charging each trip `max(0, min(fare, cap - charged_so_far))` — the rule above — and
then requires the receipt to match that recomputation:

1. Every receipt field is an integer, and `per_trip_cents` a list of integers.
2. `charged_cents` equals the recomputed total — for non-negative fares,
   `min(sum(fares), cap)`, the policy's defining property.
3. `charged_cents + waived_cents == sum(fares)` — nothing is created or lost.
4. `per_trip_cents` has one charge per trip, sums to `charged_cents`, and no charge
   is negative or exceeds its own fare.
5. **`per_trip_cents` equals the recomputed sequential split, element for element.**

Checks 1–4 are all order-blind, and a wrong statement can pass every one of them:
four 250 fares against a 700 cap split as `[0, 250, 250, 200]` charges 700, waives
300, and keeps each charge within its fare, yet the policy charges
`[250, 250, 200, 0]`. Check 5 is the one that rejects it, and it matters because the
per-trip split is what a rider sees and disputes. Each failure names what differed.

So a pass means the receipt is exactly what the sanctioned computation produces for
these inputs. It does not prove *which* code ran — a different script giving the same
numbers would pass — but it does prove the numbers displayed are the policy's. This
is the check the SQL sibling *cannot* make, and the reason this bundle carries both
kinds.

[^fare-capping]: Daily Fare Capping Policy (2026)

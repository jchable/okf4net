---
type: Attested Computation
title: Revenue Computation
description: Computes total revenue for a given year via BigQuery.
resource: https://example.com/computations/revenue
tags: [finance, revenue]
runtime: bigquery
parameters:
  - { name: year, type: integer, required: true }
executor: { resource: references/skills/run-on-bq.md, receipt: [job_id, executed_sql, result] }
attester: { resource: references/attesters/revenue.py }
generated: { by: process:nightly, at: 2026-07-20T00:00:00Z }
verified:
  - { by: human:ada, at: 2026-07-21T00:00:00Z }
sources:
  - { id: bq-ledger, resource: https://example.com/ledger, usage_count: 120, last_modified: 2026-07-15 }
stale_after: 2099-01-01
---

# Computation

```sql
SELECT SUM(amount) AS revenue
FROM sales
WHERE year = @year
```

Computes total revenue for the given fiscal year.

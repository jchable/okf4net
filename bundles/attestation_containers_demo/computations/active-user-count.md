---
type: Attested Computation
title: Active user count
description: Sanctioned SQL that counts active users at or above a given id, demonstrating the SqlClient container runtime against a real Postgres database.
tags: [demo, attestation, containers]
runtime: postgres
parameters:
  - { name: min_id, type: integer, required: true }
executor:
  receipt: [executed_sql, result]
attester:
  resource: /attesters/active_user_count_attester.py
---

# Computation

```sql
SELECT count(*) AS active_users FROM users WHERE active = true AND id >= :min_id
```

Placeholder syntax is `:name` (pg8000's native binding style), not BigQuery's
`@name` — the two demo runtimes each use the placeholder syntax their own
driver actually binds; there is no single universal OKF placeholder syntax.

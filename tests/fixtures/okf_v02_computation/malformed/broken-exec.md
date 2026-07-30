---
type: Attested Computation
title: Broken Executor Resource (malformed)
description: References an executor resource that does not exist on disk.
resource: https://example.com/computations/malformed-broken-exec
tags: [malformed]
runtime: bigquery
executor: { resource: does-not-exist.md, receipt: [job_id] }
---

# Computation

```sql
SELECT 1
```

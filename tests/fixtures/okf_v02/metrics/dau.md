---
type: Metric
title: Daily Active Users
description: Count of distinct active users per day.
resource: https://example.com/metrics/dau
tags: [engagement]
generated: { by: okf4net/0.3.0, at: 2026-07-01T00:00:00Z }
verified:
  - { by: process:nightly, at: 2026-07-02T00:00:00Z }
  - { by: human:ada, at: 2026-07-03T00:00:00Z }
sources:
  - id: ga4
    resource: https://example.com/ga4
    usage_count: 5000
    last_modified: 2026-06-30
usage_window: { from: 2026-06-01, to: 2026-06-30 }
status: stable
stale_after: 2099-01-01T00:00:00Z
---

Daily active users.

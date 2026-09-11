---
type: Attested Computation
title: Greeting message
description: Sanctioned Python script that builds a deterministic greeting for a name, demonstrating the Script container runtime.
tags: [demo, attestation, containers]
runtime: python
parameters:
  - { name: name, type: string, required: true }
executor:
  receipt: [message]
attester:
  resource: /attesters/greeting_attester.py
---

# Computation

```python
import json, os

params = json.loads(os.environ["OKF_PARAMS_JSON"])
name = params.get("name", "world")
print(json.dumps({"message": f"Hello, {name}!"}))
```

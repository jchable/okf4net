---
type: Reference
title: Attestation Containers Demo
description: A validation bundle for OKF4net.Attestation.Containers demonstrating Script (python) and SqlClient (postgres) container runtimes.
tags: [demo, attestation, containers]
---

# attestation_containers_demo

A small OKF v0.2 bundle, written from scratch (not copied from any
upstream source), to validate `OKF4net.Attestation.Containers`'s invocation
convention end to end. Its attesters are written against
`AttestationContext`'s real shape (`sanctioned_computation`, `receipt`,
`values`) — deliberately **not** copying `bundles/acme_retail/attesters/sql_equality.py`'s
signature, which expects a `claimed_value` that has no counterpart in
`AttestationContext` (see
`docs/superpowers/specs/2026-09-07-attestation-containers-design.md`'s
Attester section for why).

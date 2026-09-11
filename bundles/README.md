# Sample bundles

Sample [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
v0.2 bundles used in this repo for manual testing and samples — distinct
from [`tests/fixtures/`](../tests/fixtures/README.md), which stays
byte-exact golden CLI captures. Consumed together by
[`samples/catalog-explorer/`](../samples/catalog-explorer/README.md);
`acme_retail` alone is also consumed by
[`samples/acme-retail-agent/`](../samples/acme-retail-agent/README.md).

## Acme Retail

A fictional retail company's bundle. It exercises parts of the spec a
minimal synthetic bundle can't: `Metric` and `Policy` concepts, a `Skill`,
an `Attested Computation` pair (`runtime: bigquery`) with its executor and
attester, trust tiers (`verified`), staleness (`stale_after`), and a
deprecated concept kept for historical reproducibility.

### Provenance

Copied verbatim from `okf/bundles/acme_retail` in
[`GoogleCloudPlatform/knowledge-catalog`](https://github.com/GoogleCloudPlatform/knowledge-catalog),
commit [`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96`](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/3fcbb9f828c2f23d109c855ee403c3a4c81f3a96/okf/bundles/acme_retail),
licensed under the Apache License, Version 2.0 — see `LICENSE.Apache-2.0` at
the repo root and the attribution entry in `NOTICE`.

### What's different from upstream

- `viz.html` was **not** carried over: it's a generated artifact of the
  upstream Python `reference_agent` visualizer (Cytoscape JS/CSS tied to
  that toolchain), not OKF bundle content — nothing in this repo generates
  or keeps it in sync.
- `attesters/sql_equality.py` **is** carried over, untouched, as a plain
  reference resource (the `attester.resource` target for
  `computations/*.md`). Nothing in this repo ports or reimplements its logic
  in C# — that part has never changed, and is the whole point. What has
  changed is the first half of this sentence: `OKF4net.Attestation.Containers`
  now runs an attester script like this one *as itself*, inside a container,
  rather than not at all. See
  [`samples/acme-retail-agent/README.md`](../samples/acme-retail-agent/README.md)
  for why, and what actually running an Attested Computation against this
  bundle would require.

### Validating

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail
```

Exits `0` (conformant): 9 concepts, 0 errors, 22 warnings, 0 info. The
warnings are expected and harmless:

- 18 of the 22 are `LegacyDateOnlyTimestamp`: 7 `stale_after` values plus
  §5.1 `sources[].last_modified` and `usage_window` bounds written as bare
  `YYYY-MM-DD`. OKF v0.2 §5 requires "an ISO 8601 datetime with an explicit
  UTC offset" for every timestamp-valued key, so the upstream sample is in
  drift with its own spec. The values are still read (normalized to midnight
  UTC); this is upstream drift to report upstream, **not** something to patch
  locally — the bundle is a verbatim copy (see `NOTICE`).
- The remaining 4 of the 22 are "missing recommended frontmatter field
  `resource`" on `Metric` and `Skill` concepts — abstract concepts, where
  §4.1 says a `resource` URI is expected to be absent rather than missing.
  OKF4net still warns, because §4.1 draws that line by meaning and leaves the
  type vocabulary open, so nothing syntactic decides it. The two
  `Attested Computation` concepts used to warn here too and no longer do:
  §10.1 names that type normatively and every example the spec gives of it
  omits `resource`, which is the one case a rule can be keyed on. See
  **S4.1-8** in `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.

This file previously recorded a third group — twelve `sources[].resource` /
`executor.resource` / `attester.resource` paths reported as "not found" — and
explained them as upstream writing bundle-root-relative paths where §6.2
wanted concept-relative ones. **That explanation was wrong, and the twelve
warnings were ours, not upstream's.** §6.2 never says what base a relative
path resolves against; OKF4net now reads it as bundle-rooted, because the
spec's own Appendix A only works that way — it lays out
`computations/revenue.md` beside a bundle-root `references/` directory that
the concept names as a bare `references/skills/run-on-bq.md`. `acme_retail`
is laid out exactly that way and was conformant all along. `Bundle.TryResolveResource`
now resolves a bare path from the bundle root, an explicit `./` or `../` from
the concept's directory, and the twelve warnings are gone. See **S6.2-1** in
`docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.

## Attestation Containers demo

A small, self-authored bundle (`attestation_containers_demo/`) whose two
Attested Computations exist to be *run*, not just read — one `python` runtime
and one `postgres`, each with its own attester script. It is the fixture
`samples/attestation-containers-demo/` drives through
`OKF4net.Attestation.Containers` against a real container engine.

Unlike `acme_retail` and `ga4` below, this one is **not** an upstream copy, so
it is free to be shaped for the demonstration: its paths use the explicit
`/attesters/…` bundle-root form, and its computations are deliberately trivial
so that what the sample shows is the *pipeline*, not the query.

One honest caveat, worth knowing before reading its attester: the SQL
computation's attester compares `receipt.executed_sql` against the sanctioned
text, and under this host that comparison can never fail — `executed_sql` is
echoed back by our own wrapper from the very string we sent it. It demonstrates
the *convention* an attester follows; it is not evidence that the database
executed the sanctioned text. Real provenance needs a receipt field the engine
itself produces, such as a BigQuery `job_id` resolved against the job's own
recorded SQL.

## GA4

Google's public GA4 ecommerce reference docs bundle, used in this repo as
a second knowledge source alongside `acme_retail` — see
[`samples/catalog-explorer/`](../samples/catalog-explorer/README.md). It
exercises concept types `acme_retail` doesn't: a `BigQuery Dataset`, and a
set of `Reference` concepts documenting ecommerce audience metrics
(`purchasers`, `n_day_active_users`, and others).

### Provenance

Copied verbatim from `okf/bundles/ga4` in
[`GoogleCloudPlatform/knowledge-catalog`](https://github.com/GoogleCloudPlatform/knowledge-catalog),
commit [`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96`](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/3fcbb9f828c2f23d109c855ee403c3a4c81f3a96/okf/bundles/ga4),
licensed under the Apache License, Version 2.0 — see `LICENSE.Apache-2.0` at
the repo root and the attribution entry in `NOTICE`.

### What's different from upstream

- `viz.html` was **not** carried over: it's a generated artifact of the
  upstream Python `reference_agent` visualizer (Cytoscape JS/CSS tied to
  that toolchain), not OKF bundle content — nothing in this repo generates
  or keeps it in sync (same as `acme_retail`).

### Validating

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/ga4
```

Exits `0` (conformant): 9 concepts, 0 errors, 0 warnings, 0 info.

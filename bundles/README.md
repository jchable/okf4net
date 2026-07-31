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
  `computations/*.md`). OKF4net does not execute Python, and nothing in
  this repo ports or reimplements its logic in C# — see
  [`samples/acme-retail-agent/README.md`](../samples/acme-retail-agent/README.md)
  for why, and what actually running an Attested Computation against this
  bundle would require.

### Validating

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail
```

Exits `0` (conformant): 9 concepts, 0 errors, 18 warnings, 0 info. The
warnings are expected and harmless:

- 12 of the 18 are `sources[].resource` / `executor.resource` /
  `attester.resource` frontmatter paths reported as "not found". OKF v0.2
  §6.2 resolves a plain relative path (no leading `/`) against the
  **referencing concept's own directory**, not the bundle root — e.g.
  `computations/gross-margin-period.md`'s `sources[0].resource:
  policies/margin-standard.md` resolves to
  `computations/policies/margin-standard.md` (which doesn't exist); the
  real file is one level up, at `../policies/margin-standard.md` from that
  concept. The upstream bundle writes these paths bundle-root-relative
  instead. This affects only frontmatter-path *resolution* diagnostics —
  reading, browsing, and searching the bundle are unaffected.
- The remaining 6 are "missing recommended frontmatter field `resource`"
  on concept types where a `resource` URI doesn't apply (`Metric`, `Skill`,
  and `Attested Computation`).

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
```

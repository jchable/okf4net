# Acme Retail test bundle + Agent Framework sample

Date: 2026-07-30

## Motivation

OKF4net has no bundle in-repo that exercises §10 Attested Computation,
provenance/trust tiers, and staleness together, and no example showing
`OKF4net.Agents` driving a real chat agent (as opposed to unit tests calling
tool methods directly). The upstream `GoogleCloudPlatform/knowledge-catalog`
repo ships exactly such a bundle — `okf/bundles/acme_retail`, a fictional
retail company with Metrics, Policies, an Attested Computation pair
(`runtime: bigquery`, a Skill executor, a Python attester), and a BigQuery
Table concept. This spec brings that bundle into OKF4net for manual
testing/demos, and builds a standalone sample console app that consumes it
through Microsoft Agent Framework end to end, including running the attested
computations for real (mocked backend, real bind→execute→attest→gate
pipeline).

## Part A — `bundles/acme_retail`

### Contents

Copy verbatim (byte-for-byte) from `okf/bundles/acme_retail` at
`GoogleCloudPlatform/knowledge-catalog` commit
`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96` (`main`, as of 2026-07-30),
Apache-2.0:

```text
bundles/acme_retail/
  index.md
  log.md
  attesters/
    index.md
    sql_equality.py        # kept as a plain resource, not executed by OKF4net
  computations/
    index.md
    gross-margin-period.md
    revenue-ytd.md
  metrics/
    index.md
    revenue.md
    gross-margin.md
    gross-margin-legacy.md
  policies/
    index.md
    revenue-recognition.md
    margin-standard.md
  skills/
    index.md
    run-on-bq.md
  tables/
    index.md
    orders.md
  README.md               # new — provenance & license
```

`viz.html` is intentionally **not** carried over — it's a generated artifact
of the upstream Python `reference_agent` visualizer (Cytoscape JS/CSS tied to
that toolchain), not OKF bundle content, and OKF4net has no equivalent
generator to keep it in sync with.

### `bundles/acme_retail/README.md`

Documents:

- What the bundle is (fictional Acme Retail company; showcases OKF v0.2
  Metric/Policy/Attested Computation/Skill/BigQuery Table types, trust tiers,
  staleness, deprecation).
- Provenance: source repo, path, commit SHA pinned at copy time, Apache-2.0.
- That `sql_equality.py` is preserved as-is for reference; OKF4net does not
  execute Python, so `samples/acme-retail-agent` reimplements its logic in
  C# (Part B).
- That `viz.html` was intentionally omitted, and why.

### Other doc updates

- One `NOTICE` entry recording this bundle's attribution, alongside the
  existing OKF-reference-implementation lineage entry.
- A short `CLAUDE.md` note that `bundles/` holds sample bundles for manual
  testing/demos (distinct from `tests/fixtures/`, which stays golden/byte-exact).

## Part B — `samples/acme-retail-agent`

Kebab-case directory; standard .NET naming inside (project `AcmeRetailAgent`,
namespace `OKF4net.Samples.AcmeRetailAgent`). Own `AcmeRetailAgent.sln` —
**not** added to `OKF4net.sln`, **not** wired into `ci.yml`. Project
references: `OKF4net`, `OKF4net.Agents`, `OKF4net.Attestation`. Inherits the
repo's `Directory.Build.props` (nullable, warnings-as-errors, LangVersion 14)
automatically since MSBuild walks up the directory tree regardless of
solution membership. New `.cs` files carry the same
`// SPDX-License-Identifier: LGPL-3.0-or-later` header as the rest of the repo.

```text
samples/acme-retail-agent/
  AcmeRetailAgent.sln
  README.md
  src/AcmeRetailAgent/
    AcmeRetailAgent.csproj
    Program.cs                      # CLI entry: REPL + --prompt one-shot
    ChatClientFactory.cs            # builds IChatClient from env vars
    Attestation/
      BigQueryRuntime.cs            # IAttestationRuntime for "bigquery"
      MockParameterBinder.cs
      MockBigQueryExecutor.cs
      FakeData.cs                   # in-memory orders/products/fx/etc.
      SqlEqualityAttester.cs        # port of sql_equality.py
  tests/AcmeRetailAgent.Tests/
    AcmeRetailAgent.Tests.csproj
    SqlEqualityAttesterTests.cs
    MockBigQueryExecutorTests.cs
```

### 1. Chat client (multi-provider)

One `IChatClient` (`Microsoft.Extensions.AI.OpenAI`) built from an
OpenAI-compatible base URL:

- `OKF_CHAT_BASE_URL` — e.g. `https://api.openai.com/v1`,
  `http://localhost:11434/v1` (Ollama), or any OpenAI-compatible gateway
  (Claude/Copilot-fronting proxies included).
- `OKF_CHAT_API_KEY` — bearer key; not required for local Ollama.
- `OKF_CHAT_MODEL` — model id understood by that endpoint.

Missing/invalid config prints a one-line usage error and exits non-zero,
mirroring `OkfMcpConfig`'s pattern in `OKF4net.Mcp` — no partial startup.

### 2. Agent wiring

- `OkfBundleTools` rooted at `bundles/acme_retail`, constructed with the
  `AttestationOrchestrator` from §3 below (so `okf_run_computation` is part
  of the exposed tool set, not just `okf_get_computation`).
- `OkfContextProvider` registered via `ChatClientAgentOptions.AIContextProviders`
  for automatic budget-bounded context injection — the sample exercises both
  `OKF4net.Agents` integration surfaces (explicit tools + ambient context),
  not just one.
- System instructions: short, tells the agent it's grounded in the Acme
  Retail OKF bundle and should use the tools rather than guessing.

### 3. The `bigquery` attestation runtime (mock, real pipeline)

Registered into `AttestationRuntimeRegistry(["bigquery"] = ...)`. This is a
**mock backend with a real pipeline** — no rigged pass-through:

- **`MockParameterBinder : IParameterBinder`** — binds `{year}` (for
  `revenue-ytd`) or `{period_start, period_end}` (for `gross-margin-period`)
  into `BoundComputation`. `BoundText` is the sanctioned SQL **unchanged**
  (keeps `@name` placeholders — the Skill doc's own "do not
  string-interpolate" rule); `Values` carries the supplied parameters.
- **`FakeData.cs`** — small in-memory tables sized just for these two
  computations: `orders`, `order_lines`, `products`, `fx_daily_rates`,
  `fulfillment_cost`, `shipment_cost`, `payment_fees`. Order dates are fixed
  within FY2026 Q2 (matching `tables/orders.md`'s own
  `usage_window: 2026-04-01..2026-06-30`), `order_status = 'delivered'` —
  comfortably past the 30-day recognition window both now and indefinitely
  into the future (2026 dates only get older). At least one non-USD order
  exercises the `fx_daily_rates` conversion leg.
- **`MockBigQueryExecutor : IComputationExecutor`** — dispatches on which
  parameter names were bound (`year` present → revenue-ytd logic;
  `period_start`/`period_end` present → gross-margin-period logic), computes
  the real result via LINQ over `FakeData` applying the same business rules
  as the sanctioned SQL (delivered + 30-day window, USD conversion, full
  COGS for margin), and returns `Receipt{job_id, executed_sql, result}` with
  `executed_sql` = the sanctioned SQL text (what a real executor would echo
  back) and `result` = the computed figure — the executor does not know the
  "expected" answer in advance, it derives it.
- **`SqlEqualityAttester : IAttester`** — direct port of
  `attesters/sql_equality.py`: canonicalize (strip `--`/`/* */` comments,
  collapse whitespace, uppercase only recognized SQL keywords, leave
  identifiers alone), compare `receipt.executed_sql`'s canonical form to the
  sanctioned computation's, then compare `receipt.result[0]` to the caller's
  claimed value. Both checks must pass for `AttestationVerdict.Passed`.

Because `stale_after: 2026-12-31` on both computations postdates "today"
(2026-07-30) and the FakeData dates, a demo run through the REPL produces a
genuine `Displayable: true` outcome without any clock overrides.

### 4. CLI UX

- `dotnet run --project src/AcmeRetailAgent` — interactive REPL: reads a
  line, runs one agent turn, prints the response (and, for visibility, a
  compact log of which tool(s) were called), loops; `exit`/`quit` leaves.
- `dotnet run --project src/AcmeRetailAgent -- --prompt "..."` (or piped
  stdin) — runs exactly one turn non-interactively and exits 0, for smoke
  testing / demo scripting.

### 5. Testing

`tests/AcmeRetailAgent.Tests` (xunit, matching the main repo's test
framework), runnable manually (`dotnet test` from
`samples/acme-retail-agent/`), **not** wired into root CI:

- `SqlEqualityAttesterTests` — canonicalization behavior (comments,
  whitespace, keyword casing), a positive match, and a **tampered receipt**
  case (mismatched `executed_sql` and a mismatched `result[0]`) proving the
  attester actually rejects — not just that it accepts the happy path.
- `MockBigQueryExecutorTests` — both computations against hand-computed
  expected totals from `FakeData`, so the executor's LINQ logic is verified
  independent of the LLM/agent loop.
- No test depends on a live chat-provider endpoint; the REPL/agent loop
  itself is manually smoke-tested, not covered by automated tests (would
  require a real or fake LLM backend, out of scope here).

## Out of scope

- Adding `samples/` to `OKF4net.sln` or `ci.yml`.
- A general-purpose `Source`/BigQuery-ingestion pipeline (the earlier,
  larger "port the reference_agent enrichment tool" idea) — this spec is
  scoped to the bundle + one consuming sample, not a content-generation tool.
- A web-ingestion crawler, HTML→OKF visualizer, or LLM-based index-summary
  synthesis.
- Native (non-OpenAI-compatible) per-provider connectors.

## Open risks

- If `Microsoft.Extensions.AI.OpenAI`'s API shape has moved since
  `OKF4net.Agents`'s own dependency version, the sample must pin/match a
  compatible version — checked during implementation, not assumed here.
- FakeData's fixed FY2026 dates are a deliberate simplification; if the
  sample is revisited far in the future and someone wants to demo other
  fiscal years, the dataset would need extending (not a correctness bug,
  just a demo-data limitation, noted in the sample's own README).

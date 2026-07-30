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
through Microsoft Agent Framework — reading, browsing, searching, and
inspecting the bundle's contract for the Attested Computations (but not
running them; see "Future work").

**Why not port the attester to C#:** §10's whole trust model rests on
executing the *actual* sanctioned script referenced by `attester.resource` —
not a reimplementation of what someone believes it does. A C# port of
`sql_equality.py` would be exactly the kind of divergence risk attestation
exists to eliminate: the port could silently drift from the real script over
time, and the thing actually being trusted would quietly become "our reading
of the Python," not the Python itself. So `sql_equality.py` stays
untouched in the bundle, and this sample does not attempt to execute it.

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
- That `sql_equality.py` is preserved as-is, untouched; OKF4net does not
  execute Python and `samples/acme-retail-agent` does not port or reimplement
  it (see that sample's own README and "Future work" below).
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
references: `OKF4net`, `OKF4net.Agents` (not `OKF4net.Attestation` — this
sample never constructs an `AttestationOrchestrator`, see "Future work").
Inherits the
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

- `OkfBundleTools` rooted at `bundles/acme_retail`, constructed **without**
  an `AttestationOrchestrator` — per `OkfBundleTools`'s own documented
  behavior this exposes `okf_read_concept`, `okf_browse`, `okf_graph`,
  `okf_search`, `okf_validate_bundle`, `okf_changes_since`, and
  `okf_get_computation` (read-only: the §10 contract and sanctioned SQL
  text), while omitting `okf_run_computation` entirely rather than exposing
  an always-erroring tool. The write tools (`okf_write_concept`,
  `okf_append_log`, `okf_regenerate_indexes`) are technically present but
  not the point of this demo against a fixed sample bundle.
- `OkfContextProvider` registered via `ChatClientAgentOptions.AIContextProviders`
  for automatic budget-bounded context injection — the sample exercises both
  `OKF4net.Agents` integration surfaces (explicit tools + ambient context),
  not just one.
- System instructions: short, tells the agent it's grounded in the Acme
  Retail OKF bundle and should use the tools rather than guessing. Explicitly
  notes that Attested Computations can be inspected (`okf_get_computation`)
  but not run through this sample.

### 3. CLI UX

- `dotnet run --project src/AcmeRetailAgent` — interactive REPL: reads a
  line, runs one agent turn, prints the response (and, for visibility, a
  compact log of which tool(s) were called), loops; `exit`/`quit` leaves.
- `dotnet run --project src/AcmeRetailAgent -- --prompt "..."` (or piped
  stdin) — runs exactly one turn non-interactively and exits 0, for smoke
  testing / demo scripting.

### 4. Testing

No dedicated test project for this sample: there is no bespoke logic to
unit-test (tool wiring and context injection are already covered by
`OKF4net.Agents`'s own test suite) beyond the chat-client env-var config
parsing, which is small enough to sanity-check by manual smoke test
(`dotnet run -- --prompt "..."` with valid/invalid env vars). If that parsing
grows non-trivial during implementation, add a minimal test project then
rather than speculatively now.

## Future work: a container-based execution runtime

Out of scope for this spec, noted here so it isn't lost: a follow-on
initiative to actually run §10 Attested Computations end to end — executing
the *real* referenced scripts (e.g. `attesters/sql_equality.py`,
`skills/run-on-bq.md`'s executor logic) inside a sandboxed container, rather
than a native C# reimplementation. This would plug into `OKF4net.Attestation`
as `IComputationExecutor`/`IAttester` implementations that shell out to a
container runtime (Docker or similar) per `runtime:`/language, keeping the
sanctioned script itself as the thing that actually runs — preserving the
trust property a port would undermine. Needs its own design pass (sandboxing
model, how the container gets the script + inputs, how a receipt/verdict
comes back out, resource/time limits) before implementation; will likely use
`acme_retail`'s `computations/revenue-ytd.md` and `gross-margin-period.md` as
its worked example once it exists.

## Out of scope

- Adding `samples/` to `OKF4net.sln` or `ci.yml`.
- Running Attested Computations for real (`okf_run_computation`) — see
  "Future work" above.
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

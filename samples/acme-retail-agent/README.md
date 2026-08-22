# Acme Retail agent sample

A standalone console app demonstrating [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
driving [OKF4net.Agents](../../src/OKF4net.Agents/README.md)'s tools and
context provider against the [`bundles/acme_retail`](../../bundles/README.md)
sample bundle. Read-only: browse/read/search/graph the bundle, and inspect
(but not run) its Attested Computations. See
[the design spec](../../docs/superpowers/specs/2026-07-30-acme-retail-bundle-and-agent-sample-design.md)
for the full rationale, including why this sample does not run
`attesters/sql_equality.py` or wire `okf_run_computation`.

Standalone: this project has its own `AcmeRetailAgent.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI.

## Setup

Point the agent at any OpenAI-compatible chat endpoint via environment
variables:

- `OKF_CHAT_BASE_URL` (required) — e.g. `https://api.openai.com/v1`,
  `http://localhost:11434/v1` (Ollama), or any OpenAI-compatible gateway
  (including Claude- or Copilot-fronting proxies).
- `OKF_CHAT_MODEL` (required) — a model id understood by that endpoint.
- `OKF_CHAT_API_KEY` (optional) — bearer key; not required for local
  Ollama.
- `OKF_BUNDLE_ROOT` (optional) — overrides the bundle path; defaults to
  `bundles/acme_retail` at this repo's root (located by walking up from
  the running assembly to `OKF4net.sln`). A relative override resolves
  against your shell's working directory when you ran `dotnet run` — see
  the note in `Program.cs`.

## Run

Run from `samples/acme-retail-agent/` (the commands below are relative to
that directory).

Interactive:

```bash
OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project src/AcmeRetailAgent
```

One-shot:

```bash
OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project src/AcmeRetailAgent -- --prompt "What is Acme's FY2026 revenue recognition policy?"
```

(or pipe a prompt via stdin instead of `--prompt`). Type `exit` or `quit` to
leave the interactive REPL.

## Questions worth asking

Grounded answers about one concept — these go through
`okf_search`/`okf_read_concept`:

- *What is Acme's FY2026 revenue recognition policy?*
- *How is gross margin defined, and what does it depend on?*

Questions about the bundle **as a whole** — these go through `okf_audit`, in
one call rather than by opening concepts one at a time:

- *Which concepts have never been verified by a human?* — the interesting
  one today: eight of the nine concepts carry a `human:` verifier, so the
  answer is `skills/run-on-bq`. **Watch what the model passes**: `okf_audit`
  defaults `stale` to `true`, so a call that leaves it at the default asks
  "stale AND unverified" and correctly returns nothing, since that concept
  has no `stale_after` at all. The question as posed is
  `okf_audit(stale: false, trust: "unverified")` — the equivalent CLI call
  being `okf audit bundles/acme_retail --trust unverified`, with no
  `--stale`. If the agent answers "none", check the `[tools: ...]` line and
  ask it again without the staleness constraint.
- *How healthy is this knowledge base — how much of it is human-reviewed,
  and how much is stale?*
- *Is anything deprecated?*

Note on the freshness question specifically: no concept in this bundle is
past its `stale_after` date *yet*, so "what is stale?" correctly answers
"nothing" today. Most concepts carry `stale_after: 2026-12-31`, so this
sample starts reporting seven stale concepts on 2027-01-01 — which is the
point the bundle is making, not a bug in it. To see the stale path before
then, the CLI can pin the date: `okf audit bundles/acme_retail --as-of
2027-06-01`.

## What it does

Wires `OkfBundleTools` (constructed without an `AttestationOrchestrator`, so
`okf_run_computation` is not exposed — only `okf_get_computation`, which
reads a §10 contract and its sanctioned SQL, is available for the two
Attested Computations in the bundle) and `OkfContextProvider` (ambient
budget-bounded context injection) into one `ChatClientAgent`. The
interactive mode keeps one `AgentSession` across turns; one-shot mode runs a
single turn and exits. Each response prints a `[tools: ...]` line naming any
`okf_*` tools the agent called, for visibility into what it did.

Two kinds of question are demonstrated, and they use the tools differently.
Retrieval questions (`okf_search`, `okf_read_concept`, `okf_graph`) pull one
concept, or a few. Corpus-level questions about trust, freshness and
lifecycle go to `okf_audit`, which answers them in one call by reading every
concept's §5.3–§5.5 frontmatter — the `[tools: ...]` line is what shows you
which path the model actually took.

"Read-only" is enforced by construction, not just by convention:
`Program.cs` filters `okf_write_concept`, `okf_append_log`, and
`okf_regenerate_indexes` out of the tool list before it ever reaches the
agent (mirroring `OkfMcpToolset`'s write-tool filter in `src/OKF4net.Mcp`),
so the model has no way to mutate the bundle's byte-exact, license-attributed
upstream copy — even a model that tries to claim it made a change (small
local models sometimes fabricate a plausible-looking "done" response) cannot
actually write to disk, because the tool call it would need simply isn't in
its tool list.

## Why no attested-computation execution

`bundles/acme_retail/attesters/sql_equality.py` is kept untouched, not
ported to C#: §10's attestation trust model depends on running the *actual*
sanctioned script, not a reimplementation that could silently diverge from
it. Real execution is planned as a separate, later container-based
execution runtime that runs the sanctioned scripts themselves — see the
design spec's "Future work" section.

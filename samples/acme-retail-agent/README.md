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

## What it does

Wires `OkfBundleTools` (constructed without an `AttestationOrchestrator`, so
`okf_run_computation` is not exposed — only `okf_get_computation`, which
reads a §10 contract and its sanctioned SQL, is available for the two
Attested Computations in the bundle) and `OkfContextProvider` (ambient
budget-bounded context injection) into one `ChatClientAgent`. The
interactive mode keeps one `AgentSession` across turns; one-shot mode runs a
single turn and exits. Each response prints a `[tools: ...]` line naming any
`okf_*` tools the agent called, for visibility into what it did.

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

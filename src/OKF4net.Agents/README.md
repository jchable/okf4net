# OKF4net.Agents

[Microsoft Agent Framework](https://github.com/microsoft/agent-framework) function
tools for [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf)
knowledge bundles. This package exposes the operations of the
[OKF4net](https://www.nuget.org/packages/OKF4net) library — reading, browsing,
searching, writing, indexing and validating OKF bundles — as `AIFunction` tools
that AI agents can call.

It depends on `Microsoft.Agents.AI` (this is the integration layer; the
underlying `OKF4net` core library remains dependency-free).

## Quick start

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

var tools = new OkfBundleTools("./my_bundle");

AIAgent agent = chatClient.AsAIAgent(
    instructions: "You manage an OKF knowledge bundle.",
    tools: tools.GetTools());

var response = await agent.RunAsync("Summarize the concepts in this bundle.");
```

## The twelve tools

`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`, `okf_audit`,
`okf_write_concept`, `okf_verify`, `okf_append_log`, `okf_regenerate_indexes`,
`okf_validate_bundle`, `okf_changes_since`, `okf_get_computation` — plus a
thirteenth, `okf_run_computation`, only when the tool set is constructed with an
`OKF4net.Attestation` orchestrator wired in.

All tools return agent-friendly markdown/plain text and never throw for
expected errors (unknown ids, invalid paths, malformed input) — the agent
receives an explanatory message instead. All four write tools
(`okf_write_concept`, `okf_verify`, `okf_append_log`, `okf_regenerate_indexes`)
serialize their writes and rely on the Agent Framework's tool-approval
mechanism for gating. Their validation levels differ, though: only
`okf_write_concept` checks a document against the stricter producer-grade
OKF rules (non-empty `type`, `title`, `description`) before touching disk;
`okf_verify` deliberately enforces only §11 conformance (a non-empty `type`)
instead — recording a review is not producing content, and refusing a
reviewer because a concept is missing a `description` would make precisely
the concepts an audit surfaces unstampable. `okf_append_log` validates only
its own `kind`/`text` arguments (non-empty, no embedded newline or null
byte) and re-renders `log.md` through the §9 model — it does not touch
concept documents at all. `okf_regenerate_indexes` performs no document
validation whatsoever; it only rebuilds `index.md` listings from whatever is
already on disk. It records a `{by, at}` review stamp (§5.2) — a dated
declaration, not a proof; see the project README's `okf verify` section for
what it does and doesn't guarantee. Bundle content is treated as untrusted
and is never injected as a system message.

`OkfContextProvider` (an `AIContextProvider`, registered via
`ChatClientAgentOptions.AIContextProviders`) layers on top of the same
`OkfBundleTools` instance to automatically inject budget-bounded bundle
context into each invocation (as a message, never as system instructions).
Memory capture into deterministic, per-day memory concepts is **off by
default** (`MemoryCaptureMode.Disabled`) — no LLM call, no extra tool
round-trip — and only happens once a caller explicitly opts in via
`OkfContextProviderOptions.MemoryCapture = MemoryCaptureMode.Enabled`,
which writes captured exchanges into the same bundle the agent reads from.
Note: the token budget is a soft chars/4 estimate
(can be exceeded slightly), its `<okf-context>` fences are readability
markers rather than a security boundary, and same-day memory capture is safe
across concurrent sessions **within one process** — `OkfBundleTools` shares
its write lock across every instance pointed at the same canonicalized
bundle path via a process-wide registry — but not across separate processes
sharing a bundle path, and the reparse-point guard write tools rely on is a
best-effort check-then-write, not a guarantee against a concurrent local
actor substituting a path component mid-write (see the project README's
concurrency caveats for the full scope).

See the [project README](https://github.com/jchable/okf4net) for the full
documentation, and NOTICE/LICENSE.Apache-2.0 for the attribution chain of the
underlying OKF implementation.

Licensed LGPL-3.0-or-later.

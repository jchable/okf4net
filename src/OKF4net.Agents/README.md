# OKF4net.Agents

[Microsoft Agent Framework](https://github.com/microsoft/agent-framework) function
tools for [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf)
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

## The nine tools

`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`,
`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`,
`okf_validate_bundle`, `okf_changes_since`.

All tools return agent-friendly markdown/plain text and never throw for
expected errors (unknown ids, invalid paths, malformed input) — the agent
receives an explanatory message instead. Write tools validate documents
(producer-grade OKF rules) before touching disk, serialize their writes, and
rely on the Agent Framework's tool-approval mechanism for gating. Bundle
content is treated as untrusted and is never injected as a system message.

See the [project README](https://github.com/jchable/okf4net) for the full
documentation, and NOTICE/LICENSE.Apache-2.0 for the attribution chain of the
underlying OKF implementation.

Licensed LGPL-3.0-or-later.

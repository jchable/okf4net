# Agents quickstart sample

The shortest way to see [OKF4net.Agents](../../src/OKF4net.Agents/README.md)
working: a console app that wraps the [`bundles/ga4`](../../bundles/README.md#ga4)
sample bundle in `OkfBundleTools` and drives it through a
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
agent — with **no LLM endpoint, no API key and no network access**. The
"model" is a small scripted `IChatClient` that replays fixed turns; the tools
and the Agent Framework's tool-calling pipeline are the real ones.

Standalone: this project has its own `AgentsQuickstart.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI. Its only
reference is a `ProjectReference` to `src/OKF4net.Agents`; `Microsoft.Agents.AI`
comes through it, so the project has no `PackageReference` of its own.

## Run

```bash
dotnet run --project samples/agents-quickstart/src/AgentsQuickstart
```

Optional: `OKF_BUNDLE_ROOT` points it at another bundle (a relative value
resolves against the current directory). By default it uses `bundles/ga4`,
located by walking up from the running assembly to `OKF4net.sln`. The search
query and concept id in steps 2 and 3 are written for `ga4`, so on another
bundle those tools will likely answer `No results for query 'purchaser'.` and
`Concept 'references/metrics/purchasers' not found. …` — as a message, not an
exception — and the scripted final answer, which is fixed text, no longer
matches the bundle.

## What it does

Three steps, each narrated on the console:

1. **Wrap the bundle and list its tools.** Constructs `OkfBundleTools` and
   prints the name and first sentence of every tool returned by
   `GetTools(OkfToolMode.ReadOnly)` — every tool except the four that write
   (`OkfBundleTools.WriteToolNames`), which are listed as left out.
2. **Call two tools directly from C#.** `tools.Search("purchaser")` and
   `tools.ReadConcept("references/metrics/purchasers")` — the methods behind
   `okf_search` and `okf_read_concept` — with their output printed (the
   concept is cut after 16 lines).
3. **The same tools behind an agent.** `chatClient.AsAIAgent(instructions,
   tools)` with the read-only tool list, where `chatClient` is a
   `ScriptedChatClient` (in this project — a copy of the pattern the test
   suite uses, not a reference to it). Its script asks for `okf_search`, then
   `okf_read_concept`, then gives a fixed final answer. Each turn prints what
   the "model" received; the tool results it receives were produced by the
   Agent Framework's function-invoking pipeline actually executing the tools
   against the bundle.

The bundle is never written: only read-only tools are handed to the agent, and
the two direct calls are reads. Running the sample leaves `git status` clean.

## Using a real model

The script cannot react to what the tools return — that is the part a real
model supplies. To use one, assign any real `IChatClient` to `chatClient` in
`Program.cs` instead of the `ScriptedChatClient`; the `AsAIAgent` call, the
tools and `RunAsync` stay as they are. That needs a provider package (for
example `Microsoft.Extensions.AI.OpenAI`), which this sample deliberately
does not carry. [`samples/acme-retail-agent/`](../acme-retail-agent/README.md)
builds this kind of agent over any OpenAI-compatible endpoint, and adds
`OkfContextProvider` for automatic context injection.

---
title: I ported a Rust knowledge-format library to zero-dependency .NET — here's what I learned
published: false
tags: dotnet, csharp, opensource, ai
canonical_url: https://REPLACE-WITH-PERSONAL-SITE/okf4net-launch
---

<!-- NOTE: replace canonical_url above with the real URL once this is published on the personal site. -->

If you can `cat` a file, you can read the knowledge base. If you can `git clone` a repo, you can ship it. No vector database to stand up, no proprietary export format, no vendor lock-in — just a directory of markdown files with YAML frontmatter that a human can open in any editor and an agent can read with `ReadFile`. That's the whole pitch, and it's the reason I spent the last few weeks porting a Rust library to C# to get it onto .NET.

The format behind that pitch is Google's [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md), and the library is [**OKF4net**](https://github.com/jchable/okf4net) — a zero-dependency .NET (C#, net10.0) implementation, plus an optional layer for wiring OKF bundles straight into agents built on the Microsoft Agent Framework. This post is the launch story: what OKF actually is, why I ported it instead of writing a wrapper, and what "zero dependency" really costs and buys you.

## What OKF is

OKF defines a **bundle**: a directory tree of UTF-8 markdown files, where each file is a **concept** — a YAML frontmatter block followed by a markdown body. Concepts cross-link each other with ordinary markdown links, `index.md` files give you progressive-disclosure directory listings, and `log.md` files record date-grouped change history. The only hard conformance requirement is a non-empty `type` field on every concept; everything else — unknown types, unknown keys, broken links — has to be tolerated by a conformant consumer. It's deliberately boring as a format, which is the point: the format is context here, not the pitch. The pitch is what you get to do with plain files — diff them, review them in a PR, grep them, back them up with nothing but `git`.

## The port story

This repository used to ship a Rust implementation of OKF. I removed it at commit `d20343c` — but only after proving, file by file and command by command, that the C# port produced byte-identical output. `tests/fixtures/golden/` holds five golden captures taken directly from the Rust binary's stdout — `validate`, `info`, `graph --dot`, `fmt`, and `index` — against a shared example bundle, and the C# CLI is diffed byte-for-byte against every one of them in CI. As of today the full suite passes end-to-end, including five byte-exact golden CLI comparisons against the original captures.

I'll say the quiet part out loud: this port was AI-assisted, done largely with Claude Code driving the migration file by file, spec section by spec section, with the golden fixtures as the ground truth it had to match exactly. I think that's worth stating plainly rather than glossing over — a byte-exact port across languages is a fairly mechanical, well-specified translation task with an unambiguous pass/fail signal (does the output match the captured bytes, yes or no), which is exactly the kind of task where an AI pair-programmer earns its keep and where you can trust the result *because* you can verify it byte-for-byte rather than having to take anyone's word for it. The interesting design decisions — the YAML subset, the permissive-loading philosophy, the two-tier validation split — came from following the spec and the Python reference implementation; the AI assistance was in the grinding, get-every-byte-right execution, not the architecture.

## Show, don't tell

Here's the library, loading a bundle and running a conformance check:

```csharp
using OKF4net;

var bundle = Bundle.Load("./my_bundle");
Console.WriteLine($"{bundle.Count} concepts");

// Conformance check (§9).
var report = BundleValidator.Validate(bundle);
if (report.IsConformant)
{
    Console.WriteLine($"conformant with OKF v{OkfSpec.Version}");
}

// Traverse the cross-link graph.
var id = ConceptId.Parse("tables/orders");
foreach (var link in bundle.LinksFrom(id))
{
    Console.WriteLine($"{id} -> {link.Target} (exists: {link.Exists})");
}
```

`Bundle.Load` never aborts on a malformed concept file — it collects parse failures into `bundle.ParseErrors` and keeps walking the tree, because a knowledge base that one bad file can take down entirely is a bad knowledge base.

And here's the CLI, which is the same tool the Rust binary used to be, invocation-for-invocation:

```sh
okf validate ./bundles/ga4
okf graph ./bundles/ga4 --dot | dot -Tsvg > graph.svg
```

`okf validate` exits non-zero on a non-conformant bundle, so it drops straight into a CI step. The CLI ships as a self-contained, Native AOT single-file binary — no .NET runtime install required on the machine that runs it.

## The agents angle

The reason I care about this format enough to port a whole library for it is `OKF4net.Agents`, which turns an OKF bundle into tools and context for the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework). `OkfBundleTools` wraps one bundle root and exposes nine function tools — read, browse, graph, search, write, append-log, regenerate-indexes, validate, changes-since — that an `AIAgent` can call directly:

```csharp
var tools = new OkfBundleTools("./my_bundle");
AIAgent agent = chatClient.AsAIAgent(tools: tools.GetTools());
var response = await agent.RunAsync("Search the bundle for concepts about refunds.");
```

Layer `OkfContextProvider` onto the same tools instance and, opted in explicitly, an agent's exchanges get captured as long-term memory — one markdown concept per UTC day, written through the same validated, lock-protected write path the tools use, plus a matching `log.md` entry. That's the part I think is genuinely different from the usual answer to "give my agent memory": instead of an opaque vector store you can't audit, memory is a markdown file in a git-tracked directory. You can open it, diff it across commits, redact a line, or point a second agent at the exact same directory with no export step. It's not a fit for every use case — the README is upfront that v1 memory is bundle-global and unscoped, so it's opt-in and meant for a shared, non-sensitive bundle rather than a multi-tenant deployment — but for a single team's shared knowledge base, "memory you can `git blame`" is a real capability, not a slogan.

## Design choices

The whole library — `OKF4net` and `OKF4net.Cli` — has zero third-party runtime dependencies: no YAML library, no CLI-parsing package, nothing. It has its own documented YAML *subset* parser (frontmatter is scalars, lists, and shallow maps — no anchors, no tags, no multi-document files, and it says so with a clear error if you hand it those), its own markdown link scanner, and its own argument parsing, all on top of the .NET base class library. That constraint is what makes the CLI publishable as a single-file Native AOT binary with no runtime to install, and it's what keeps the barrier to contributing low — there's no framework to learn before you can read the code. `OKF4net.Agents` is the one exception, since talking to `Microsoft.Agents.AI` requires depending on it; everything else stays dependency-free by design, enforced project by project. The project also ships `OKF4net.Catalog`, a local multi-bundle catalog with search-by-source resolution, and `OKF4net.Mcp`, an MCP server that plugs a bundle straight into Claude Desktop or Claude Code — so agents and tools have a ready path to discover and query bundles without writing that plumbing themselves.

## Come contribute

OKF4net is young and I'd rather it stay welcoming than gate-kept. You don't need any prior OKF knowledge to help — the [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue) label names the files to touch and the test that should go green when you're done, [`ROADMAP.md`](https://github.com/jchable/okf4net/blob/main/ROADMAP.md) lays out where the project is headed, and [Discussions](https://github.com/jchable/okf4net/discussions) is the place to ask a question before you write any code. The project is licensed **LGPL-3.0-or-later**, and the bar to your first PR is exactly three commands: `dotnet build`, `dotnet test`, `dotnet format`. If any part of "knowledge bundles you can `cat` and agents that remember things in files you can read" sounds useful to you, I'd love the help — and the feedback.

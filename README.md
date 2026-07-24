# OKF4net

[![CI](https://github.com/jchable/okf4net/actions/workflows/ci.yml/badge.svg)](https://github.com/jchable/okf4net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/OKF4net.svg)](https://www.nuget.org/packages/OKF4net)
[![License: LGPL-3.0-or-later](https://img.shields.io/badge/License-LGPL--3.0--or--later-blue.svg)](LICENSE)

A **zero-dependency .NET (C#) implementation** of the [Open Knowledge Format
(OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) —
Google's open, human- and agent-friendly format for representing *knowledge* as
a directory of markdown files with YAML frontmatter.

> OKF is intentionally minimal: "if you can `cat` a file, you can read OKF; if
> you can `git clone` a repo, you can ship it." This project honors that
> spirit — it is implemented entirely on the .NET **base class library**, with
> **no third-party dependencies** (it includes its own YAML-subset parser,
> markdown link scanner, directory walker, and CLI argument parsing).
>
> OKF4net is a from-scratch C# port of this repository's former Rust `okf`
> implementation, itself a port of the OKF reference implementation. The Rust
> sources were removed once the port was proven byte-exact (182/182 tests,
> including 5 byte-exact golden CLI comparisons — see
> [`tests/fixtures/`](tests/fixtures/README.md)); OKF4net is now the sole
> implementation in this repository.

## What OKF is

- A **bundle** is a directory tree of UTF-8 markdown files (the unit of
  distribution).
- A **concept** is one markdown document: a YAML **frontmatter** block delimited
  by `---`, followed by a markdown **body**.
- A **concept id** is the file's path within the bundle with `.md` removed
  (`tables/users.md` → `tables/users`).
- Concepts **cross-link** via ordinary markdown links — absolute
  (`/tables/users.md`, bundle-relative) or relative (`./other.md`).
- `index.md` files provide directory listings for *progressive disclosure*;
  `log.md` files record date-grouped change history. Both are **reserved**
  filenames.
- The only hard requirement for **conformance** is a non-empty `type` field on
  every concept; consumers must otherwise be permissive (unknown types, unknown
  keys, broken links, and missing optional fields are all tolerated).

See [mapping to the spec](#mapping-to-the-spec) below for the section-by-section
mapping.

## Library overview

| Type / namespace                          | Responsibility                                                            |
|--------------------------------------------|----------------------------------------------------------------------------|
| `OKF4net.Yaml.YamlValue` / `YamlMapping`   | A YAML-*subset* value/mapping model for frontmatter                        |
| `OKF4net.Yaml.YamlValue.Parse` / `YamlEmitter` | Parser entry point and emitter for the same YAML subset                |
| `OKF4net.OkfDocument`                      | Frontmatter + body; parse / serialize / validate (§4)                      |
| `OKF4net.Frontmatter`                      | Typed accessors over an order-preserving mapping (§4.1)                    |
| `OKF4net.ConceptId`                        | `ConceptId` ↔ path conversion and segment validation (§2)                  |
| `OKF4net.LinkScanner`                      | Markdown link extraction, classification, citations (§5, §8)               |
| `OKF4net.Bundle`                           | `Bundle.Load` — walk a tree, build the concept graph + backlinks (§3, §5)  |
| `OKF4net.IndexGenerator`                   | Generate `index.md` directory listings (§6)                                |
| `OKF4net.ChangeLog`                        | Parse / build `log.md` update histories (§7)                               |
| `OKF4net.BundleValidator`                  | §9 conformance checking with severity-tagged diagnostics                   |

The split mirrors the reference Python implementation's `bundle/` package
(`document.py`, `index.py`, `paths.py`) — and the Rust `okf` crate that
preceded this port — so behaviour stays compatible: the document parser,
validator, and index generator are faithful ports, verified by tests adapted
from the reference test suite and, for the CLI, by byte-exact comparison
against the removed Rust binary's captured output.

### Design choices

- **Frontmatter preserves everything.** Rather than deserializing into a fixed
  type (which would drop producer-defined keys), `Frontmatter` keeps the full
  ordered mapping and layers typed getters (`Type`, `Title`, `Tags`, …) on
  top. This satisfies the spec's requirement that consumers preserve unknown
  keys when round-tripping.
- **Permissive loading.** `Bundle.Load` never aborts on a bad concept file; it
  collects parse failures in `ParseErrors` and keeps going. Broken
  cross-links are retained as graph edges to non-existent concepts.
- **Two levels of validation.** `OkfDocument.ValidateConformance()` enforces
  only what §9 requires (a non-empty `type`). `OkfDocument.Validate()` matches
  the stricter producer-side check from the reference agent (`type`, `title`,
  `description`, `timestamp`).
- **A documented YAML subset.** Real OKF frontmatter is scalars, lists, and
  shallow maps. The parser handles block/flow collections, quoted/plain
  scalars, `|`/`>` block scalars, and comments; it rejects (with a clear error)
  the YAML features that never appear in frontmatter — anchors, tags, multiple
  documents.

## Usage

### As a library

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
foreach (var backlink in bundle.Backlinks(id))
{
    Console.WriteLine($"cited by {backlink}");
}
```

Parsing and round-tripping a single document:

```csharp
using OKF4net;

var doc = OkfDocument.Parse("---\ntype: Metric\ntitle: DAU\n---\n\n# Body\n");
Console.WriteLine(doc.Frontmatter.Type); // "Metric"
doc.ValidateConformance(); // throws DocumentValidationException on failure

// Serialize() preserves frontmatter key order and the body.
var text = doc.Serialize();
```

### As a CLI

```
okf validate <bundle>    Check a bundle against OKF v0.1 conformance (§9)
okf info     <bundle>    Summarize a bundle (concepts, types, links, version)
okf index    <bundle>    (Re)generate every index.md in the bundle
okf graph    <bundle>    Print the cross-link graph (--dot for Graphviz DOT)
okf parse    <file>      Parse one concept document and print its structure
okf fmt      <file>      Normalize a document by parse + re-serialize (-w writes)
```

`okf validate` exits non-zero when a bundle is not conformant, so it drops
straight into CI:

```sh
okf validate ./bundles/ga4
okf graph ./bundles/ga4 --dot | dot -Tsvg > graph.svg
```

`okf` is `OKF4net.Cli`, published as a self-contained, Native AOT
single-file binary — no .NET runtime installation required on the target
machine (see [Building & testing](#building--testing)). Invocations are
unchanged from the Rust binary it replaces.

### Using OKF4net with Microsoft Agent Framework

`src/OKF4net.Agents/` exposes bundle operations as function tools for the
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework):
`OkfBundleTools` wraps one bundle root and its `GetTools()` method returns
nine ready-to-use `AITool`s, which `AsAIAgent` turns into an agent's tool list.

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

IChatClient chatClient = /* your IChatClient, e.g. from an OpenAI/Azure client */;
var tools = new OkfBundleTools("./my_bundle");

AIAgent agent = chatClient.AsAIAgent(tools: tools.GetTools());
var response = await agent.RunAsync("Search the bundle for concepts about refunds.");
Console.WriteLine(response.Text);
```

The nine tools (read → browse → graph → search → write → append →
regenerate → validate → changes-since):

| Tool                     | Description                                                                                                                                                                                                    |
|--------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `okf_read_concept`       | Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.                                                                                                                    |
| `okf_browse`             | Browse the bundle via its index files (progressive disclosure). Without a path, lists the bundle root.                                                                                                        |
| `okf_graph`              | Inspect the cross-link graph. With a concept id: its outgoing links, backlinks and broken links. Without: bundle-wide stats.                                                                                  |
| `okf_search`             | Full-text search across concept titles, descriptions, tags and bodies. Returns matching concept ids ranked by relevance.                                                                                      |
| `okf_write_concept`      | Create or update a concept document. The frontmatter must contain non-empty type, title, description and timestamp (producer-grade validation is enforced before writing).                                   |
| `okf_append_log`         | Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §7 model, so non-conforming prose or comments in a hand-authored log.md are not preserved. |
| `okf_regenerate_indexes` | Regenerate every index.md in the bundle (progressive-disclosure listings). Run after adding or changing concepts.                                                                                             |
| `okf_validate_bundle`    | Validate the bundle against OKF v0.1 conformance (§9). Returns the diagnostics report.                                                                                                                        |
| `okf_changes_since`      | Summarize bundle changes since a given ISO date, aggregated from every log.md in the bundle.                                                                                                                  |

**Security note:** bundle content (concept bodies, frontmatter, log entries)
is untrusted — it comes from files on disk that may have been written by
another agent or a human contributor — and is never injected into the
conversation with a `system` role; it only ever reaches the model as tool
output. The three write-capable tools (`okf_write_concept`, `okf_append_log`
and `okf_regenerate_indexes`)
rely entirely on the Agent Framework's own tool-approval mechanism to gate
execution — `OkfBundleTools` performs no additional confirmation step of its
own.

The core `OKF4net` library stays dependency-free (BCL only); only
`OKF4net.Agents` references `Microsoft.Agents.AI` (see
[Hard rules](CLAUDE.md) for the per-project dependency policy).

#### Automatic context & memory (OkfContextProvider)

`OkfContextProvider` is an `AIContextProvider` that, layered onto the same
`OkfBundleTools` instance as the tools above, automatically injects relevant
bundle context into each invocation and — when explicitly enabled — captures
the exchange back into the bundle as long-term memory, no extra tool calls
required from the model. Register it via `ChatClientAgentOptions.AIContextProviders`
(the tools + providers convenience overload doesn't exist; this is the one
API surface that wires both):

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

var tools = new OkfBundleTools("./my_bundle");
// EnableMemoryCapture defaults to false; opt in explicitly (see the memory
// trust model caveat below) to get the capture behavior shown here.
var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { EnableMemoryCapture = true });

AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions { Tools = tools.GetTools() },
    AIContextProviders = [provider],
});

var response = await agent.RunAsync("What do we know about orders?");
```

`OkfContextProviderOptions`:

| Option                | Default    | Meaning                                                                                                  |
|-----------------------|------------|----------------------------------------------------------------------------------------------------------|
| `TokenBudget`         | `2000`     | Approximate token budget (chars/4 estimate) for context injected per invocation.                         |
| `EnableMemoryCapture` | `false`    | Opt-in: whether exchanges are captured as long-term memory concepts in the bundle after each invocation. |
| `MemoryDirectory`     | `"memory"` | Bundle subdirectory holding memory concepts, as a single `ConceptId` segment (no `/`).                   |
| `MaxConceptsInjected` | `5`        | Maximum number of scored concepts injected into a single invocation's context.                           |

**Security note:** as with the tools above, bundle content is untrusted.
`ProvideAIContextAsync` injects the bundle root index plus the top scored
concepts (progressive disclosure, budget-bounded) as reference **data in a
message** — it is never written into `AIContext.Instructions`, so a
prompt-injection payload smuggled into a concept body cannot reach the
instructions channel.

**Memory design (v1, deterministic):** `StoreAIContextAsync` captures each
exchange with no LLM call — the last user message and the agent's final
response are appended to one memory concept per UTC day
(`<MemoryDirectory>/<yyyy-MM-dd>`), plus a matching `log.md` entry. Captured
text is blockquote-neutralized (each line prefixed with `>`) so injected markdown
structure (`---`, headings, a fake `# Citations` section) can't be mistaken
for genuine document structure. Writes go through the same
`OkfBundleTools.WriteConcept`/`AppendLog` calls — and therefore the same
producer-grade validation, write lock and reparse-point guards — any other
caller would use, and the provider never throws toward the invocation
pipeline.

A few known v1 caveats:

- **Budget is approximate:** `TokenBudget` uses a crude chars/4 estimate and
  does not charge the per-block `<okf-context>` framing overhead, so the
  injected message can exceed it by a small margin (~4% at defaults) — a soft
  budget, not a hard cap.
- **The `<okf-context id="…">` fences are readability markers, not a security
  boundary:** the whole injected message is untrusted user-role reference
  data (a concept body containing a literal `</okf-context>` could visually
  break out of its fence); this doesn't matter because nothing in that
  message is ever treated as instructions in the first place (see the
  security note above).
- **Memory is bundle-global, unscoped, and opt-in:** captured memory carries
  no session/user/tenant key, so a scored recall in `ProvideAIContextAsync`
  can surface one session's captured exchange in a completely different
  session sharing the same bundle. That's why `EnableMemoryCapture` defaults
  to `false` — enable it only for a bundle that's intended to be a shared,
  non-sensitive memory across those sessions. It also means same-day capture
  is a read-modify-write on one concept file: two truly concurrent
  `StoreAIContextAsync` calls sharing a provider across sessions can lose the
  earlier section (last-writer-wins on the day's concept) while `log.md`
  still records both `Memory` entries — a same-day count divergence between
  `log.md` and the memory concept is a known v1 limitation. Turns within a
  single session are sequential, so the concurrency half of this only affects
  cross-session sharing.

## Mapping to the spec

| Spec section                 | Implemented by                                                 |
|-------------------------------|-------------------------------------------------------------------|
| §2 Terminology / concept id  | `OKF4net.ConceptId`                                            |
| §3 Bundle structure          | `OKF4net.Bundle`, `Bundle.ReservedFilenames`                   |
| §4 Concept documents         | `OKF4net.OkfDocument`, `OKF4net.Frontmatter`                   |
| §5 Cross-linking             | `OKF4net.LinkScanner`, `Bundle.LinksFrom` / `Bundle.Backlinks` |
| §6 Index files                | `OKF4net.IndexGenerator`                                       |
| §7 Log files                  | `OKF4net.ChangeLog`                                            |
| §8 Citations                  | `LinkScanner`, `OkfDocument.Citations()`                       |
| §9 Conformance                | `OKF4net.BundleValidator`                                      |
| §11 Versioning                | `Bundle.OkfVersion`, `OKF4net.OkfSpec.Version`                 |

## Building & testing

```sh
dotnet build OKF4net.sln           # core library + okf CLI + test project
dotnet test OKF4net.sln            # unit + integration tests (incl. golden CLI comparisons)
dotnet publish src/OKF4net.Cli -c Release  # Native AOT, self-contained okf binary
```

## License

OKF4net is licensed under the **GNU Lesser General Public License v3.0 or
later (LGPL-3.0-or-later)** — see [`LICENSE`](LICENSE) for the full LGPLv3
text and [`LICENSE.GPL-3.0`](LICENSE.GPL-3.0) for the GPLv3 text it
incorporates by reference.

This is a derivative work: its document parser, concept-id conventions, and
index generator are ports of the Apache-2.0-licensed
[OKF reference implementation](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf)
by Google LLC, by way of this repository's former Rust implementation `okf`
by Walter van der Giessen (also Apache-2.0, removed from this repository at
commit `d20343c` once byte-exact parity with this C# port was proven).
Portions derived from those upstream works remain subject to the Apache
License, Version 2.0 — see [`LICENSE.Apache-2.0`](LICENSE.Apache-2.0). Full
attribution is in [`NOTICE`](NOTICE).

This is an independent implementation and is not affiliated with or endorsed by
Google.

# OKF4net

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
> sources were removed once the port was proven byte-exact (181/181 tests,
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
| `OKF4net.Yaml.YamlParser` / `YamlEmitter`  | Parser and emitter for the same YAML subset                                |
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
    Console.WriteLine($"conformant with OKF v{OkfSpecVersion}");
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
| §11 Versioning                | `Bundle.OkfVersion`                                            |

## Building & testing

```sh
dotnet build OKF4net.sln           # core library + okf CLI + test project
dotnet test OKF4net.sln            # unit + integration tests (incl. golden CLI comparisons)
dotnet publish src/OKF4net.Cli -c Release  # Native AOT, self-contained okf binary
```

## License

Licensed under the **Apache License, Version 2.0** — the same license as the
upstream [OKF project](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf).
This is a derivative work: its document parser, concept-id conventions, and
index generator are ports of the OKF reference implementation, by way of this
repository's former Rust implementation. See [`LICENSE`](LICENSE) for the
full terms and [`NOTICE`](NOTICE) for attribution.

This is an independent implementation and is not affiliated with or endorsed by
Google.

# `okfgen` — the OKF producer

A standalone CLI that reads a repository and writes an OKF v0.2 bundle describing it:
a repository `overview`, one concept per detected package and doc, and — for C# —
one concept per namespace, type and member, with `## Calls` links between them.

It lives in its own solution (`producers/OkfProducer.sln`), references `src/OKF4net`
by project reference, is **not** part of `OKF4net.sln`, is **not** published to NuGet
today, and is exempt from the repository's zero-third-party-dependency rule
(`Microsoft.Extensions.Hosting`, `System.CommandLine`, `Microsoft.CodeAnalysis.CSharp`,
`TreeSitter.DotNet`). `OkfProducer.Core` itself still references only `OKF4net`.

## Before you touch this code

```sh
dotnet test producers/OkfProducer.sln
```

**Run it. Nothing else will.** `producers/` is deliberately outside CI — the decision
was taken on 2026-08-01 and it is not an open question (see `ROADMAP.md`). Nothing on
a pull request builds this solution, so the guarantee has to be local and it has to be
one command. That command carries, among 400-odd tests:

- the **golden bundle** (`tests/OkfProducer.Tests/fixtures/golden/`), regenerated from
  the committed fixture repository beside it and compared file by file. Unlike the
  root `tests/fixtures/`, this golden captures **our own** output: it is meant to be
  regenerated whenever the generator changes on purpose (`OKFGEN_UPDATE_GOLDEN=1`,
  then review the diff — the test fails loudly in that mode rather than passing on a
  tautology);
- the **blast-radius** tests, which pin how far one source edit is allowed to ripple
  through the bundle;
- the **CLI tests**, which drive the shipped composition in-process — the flags below,
  and the three wiring properties that are invisible to a test that assembles its own
  pipeline (field preservation supplied, no delete-licence on a `--no-code` run, the
  run's notes actually reaching stderr).

## Using it

```sh
dotnet run --project producers/src/OkfProducer.Cli -- generate --repo . --out ./bundle
dotnet run --project producers/src/OkfProducer.Cli -- validate --okf ./bundle
```

`generate` flags:

| Flag | Default | What it does |
|---|---|---|
| `--repo <path>` | required | Repository to scan. |
| `--out <path>` | required | Bundle to write. |
| `--update` | off | Write into a non-empty `--out`. Concepts this run does not generate are preserved — except under `code`, where a concept the previous run claimed and this one no longer produces is pruned. |
| `--reset` / `--force` | off | Delete and recreate `--out` first. Refused when `--out` is, or contains, `--repo`. |
| `--repo-url <url>` | absent | Permalink base, e.g. `https://github.com/owner/repo`. With a ref, every code concept gets a `resource` link to its declaration. Without both, **no `resource` is emitted at all** — see below. |
| `--rev <ref>` | current branch | The ref permalinks point at. Never a commit sha by default: a sha would rewrite every code concept's `resource` on the next commit. On a detached HEAD there is no branch name, so this becomes required for permalinks. |
| `--check` | off | Regenerate over a copy of the bundle and exit non-zero if anything differs. Never writes to `--out`. |
| `--include-tests` | off | Walk test projects and `test`/`tests`/`spec` directories too. |
| `--include-internal` | off | Emit `internal` declarations, not only public ones. |
| `--no-code` | off | Skip the code-graph stage entirely: `overview`, `packages/` and `docs/` only. |
| `--max-file-size <bytes>` | 2 MiB | Largest source file the code stage will read. A larger one is skipped and counted, which makes the run partial — the concepts it owned are then not pruned. |

Notes — what a run could not do — go to **stderr**, prefixed `note: `; results go to
stdout. A note never changes the exit code. The ones worth knowing:

- a project the exact (Roslyn) resolver could not compile, so calls in its files fell
  back to name matching;
- code containers no package's `Compile` item set claims, so they hang off their own
  parent but off no package concept;
- no source-ownership map at all (no `dotnet` on `PATH`, or an unrestored repository),
  so the package → namespace level of the containment spine is missing entirely.
  Nothing is guessed from the directory tree instead: a project can add, remove and
  link sources across directories, so a directory-derived link would attribute a
  namespace to the wrong package.

### Why `--repo-url` is all-or-nothing

Without both a repo URL and a ref, code concepts carry **no** `resource` field rather
than a repository-relative path. That is not a shortcut. `BundleValidator` resolves a
bare relative `resource` against the **concept's own directory** — so
`src/Links.cs` on `code/csharp/okf4net/link-scanner/scan` would be looked for under
`<bundle>/code/csharp/okf4net/link-scanner/src/Links.cs`, a miss for every code
concept and one warning apiece. Omitting the field costs exactly the same number of
warnings, and only one of the two is honest. (The `packages/` and `docs/` families do
still carry repo-relative paths that miss this way — a pre-existing, deliberately
accepted limitation recorded in `ROADMAP.md`.)

## Packaging — a manual release step, not a guarantee

`okfgen` packs as a **RID-specific `dotnet tool`**: one pointer package plus one
package per RID (`win-x64`, `linux-x64`, `osx-arm64`). A portable tool package would
carry every RID's tree-sitter natives at once (~590 MB), and they cannot be trimmed by
hand — `deps.json` is what feeds `NATIVE_DLL_SEARCH_DIRECTORIES`, not the contents of
the folder.

```sh
dotnet pack producers/src/OkfProducer.Cli -c Release
```

Expect four packages in `producers/src/OkfProducer.Cli/bin/Release/`. Measured on
2026-09-01, version 0.1.0:

| Package | Compressed | Uncompressed |
|---|---|---|
| `OkfProducer.Cli.0.1.0.nupkg` (pointer) | 2 KB | 5 entries, no payload |
| `OkfProducer.Cli.win-x64.0.1.0.nupkg` | 13.3 MB | 87.6 MB |
| `OkfProducer.Cli.linux-x64.0.1.0.nupkg` | 11.6 MB | 83.7 MB |
| `OkfProducer.Cli.osx-arm64.0.1.0.nupkg` | 11.5 MB | 80.7 MB |

**Every sub-package must be pushed, not only the pointer** — the pointer contains
nothing but `DotnetToolSettings.xml`, and an install resolves the RID package from the
feed. `ToolPackageRuntimeIdentifiers` **without** `RuntimeIdentifiers` fails
`dotnet pack` with `NETSDK1047`; both properties are set in the `.csproj` and the
comment there says why.

**This is a manual step performed at release time, and it is not covered by any
test.** `producers/` is outside CI, so a per-RID `dotnet tool install` smoke test
cannot be honest: nothing would run it. Verifying the pack means running the command
above and, for a real release, installing the resulting package on each target OS by
hand. Saying otherwise would claim coverage that does not exist.

Two related facts, recorded rather than fixed: the per-RID payload is dominated by
grammars this producer never uses (`verilog` 17.3 MB, `razor` 10.5 MB, `cpp` 5.1 MB
on `linux-x64`), and getting below ~40 MB is a documented follow-up, not a v1 promise.
None of this is applied to `src/OKF4net.Mcp`, which has real users and declares no
`RuntimeIdentifier(s)` — so .NET 10's RID-specific tool behaviour does not touch it.

## Layout

| Project | Depends on | Holds |
|---|---|---|
| `src/OkfProducer.Core` | `OKF4net` only | Scanning, the language-agnostic code-graph contracts, concept generation, the bundle writer, `--check`, the generation manifest. |
| `src/OkfProducer.CodeGraph.TreeSitter` | Core + `TreeSitter.DotNet` | The `ILanguageExtractor` and the C# profile. |
| `src/OkfProducer.CodeGraph.Roslyn` | Core + `Microsoft.CodeAnalysis.CSharp` | The exact `ISymbolResolver`, and the `dotnet msbuild` query behind it. |
| `src/OkfProducer.Cli` | all of the above | The composition root — the only project that can assemble the pipeline — and the `okfgen` command surface. |
| `tests/OkfProducer.Tests` | all of the above | xunit, the fixture repository and the golden bundle. |

Design: [`docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md`](../docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md).

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
| `--reset` / `--force` | off | Delete and recreate `--out` first. Refused when `--out` is, or contains, `--repo`, and refused when `--out` holds a symbolic link or junction (see below). The delete happens at the commit boundary, so a run that fails while *generating* leaves the old bundle — but a run interrupted during the commit itself leaves an empty or half-repopulated directory, and unlike every other operation here that costs the hand-written concepts outside `code` too. `--reset` means "throw this bundle away and write it again"; `--update` is the flag with no such window. |
| `--repo-url <url>` | absent | Permalink base, e.g. `https://github.com/owner/repo`. With a ref, every code concept gets a `resource` link to its declaration. Without both, **no `resource` is emitted at all** — see below. |
| `--rev <ref>` | current branch | The ref permalinks point at. Never a commit sha by default: a sha would rewrite every code concept's `resource` on the next commit. On a detached HEAD there is no branch name, so this becomes required for permalinks. |
| `--check` | off | Regenerate over a copy of the bundle and exit non-zero if anything differs. Never writes to `--out`. Refused with `--reset`/`--force` (it would delete nothing while the operator believed a reset happened) and with `--no-code` (see below). |
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
  namespace to the wrong package;
- `--repo-url` on a detached HEAD, where there is no branch name to build permalinks
  from and a sha is refused as a default;
- `--rev` supplied without `--repo-url`, which does nothing;
- **a file under `code` that this run took ownership of.** §6.3 stops this producer
  *deleting* a concept no manifest claims; nothing stops it *overwriting* one, because
  the moment the generator produces the same id the staged file is moved over it. A
  concept you wrote by hand at an id the producer later generates is replaced — body and
  all — and this note, naming the file, is the only signal there is. The note says which
  of the two happened to the *description*: a concept carrying a `description_source` this
  producer does not derive (`manual`, `llm`, anything it never wrote) keeps its
  description under §4.2 and the note says so; anything else loses it with the body. Field
  preservation applies to concepts this producer wrote before, so the way to keep a
  hand-written concept whole is to give it an id the producer does not generate;
- **a scope narrower than the run that wrote the manifest**, e.g. dropping
  `--include-internal`. Nothing is pruned in that run: a concept missing from it may
  simply be out of scope rather than gone from the repository, and the two are
  indistinguishable from this run's own output alone. Re-run with the flag to prune
  again. The refusal **persists**: the manifest that run writes records the widest scope
  covering the concepts it kept, not its own narrow one, so running the same narrowed
  command a second and a third time refuses again rather than deleting on the next pass;
- **a manifest this build cannot read**, which is a corrupt one or one written by a
  producer whose schema version this build does not know — including every bundle
  produced before schema 2. That run prunes nothing and cannot say which files under
  `code` it had written before, so it reports the manifest once instead of reporting
  every file. It writes a manifest this build does read, so the run after it is normal
  again;
- **a concept whose destination leaves the bundle through a link**, which is refused
  rather than written (see the warning below).

`--check` forwards these too, including the writer's reconciliation notes. For one case
they are the only signal that exists: a hand-written concept under the owned prefix that
no manifest claims is copied forward untouched and never regenerated, so it *cannot*
differ. `--check` exits 0 and prints `No drift` for ever, while the bundle carries a
concept this producer will never prune.

A degraded run is the milder case, not that one. A source file over `--max-file-size`, or
a traversal that timed out, does leave stale `code/` concepts *held back* rather than
pruned — but the file also drops out of the manifest's extracted-file list, so
`.okfgen-manifest.json` differs and `--check` exits 1 with a drift line naming it. There
the notes explain *why* the run was degraded rather than *whether* anything is wrong.

`--check --no-code` is **rejected**, not noted. `--check` regenerates over a copy of the
bundle; with `--no-code` that regeneration produces no `code` concept and no manifest, so
every `code/` file is copied forward untouched, cannot differ, and the copy's
`.okfgen-manifest.json` stays byte-identical. The run would exit 0 and print `No drift`
over a `code/` family of any age — and since a note never changes the exit code, a CI gate
keyed on `--check` would stay green for ever. Drop one of the two flags.

A **malformed** `--repo-url` is not a note but an error: anything that is not an absolute
`http`/`https` URL (`github.com/o/r`, `git@github.com:o/r` — the two forms a forge shows
and a user pastes) is rejected before any work, because the alternative is a
successful-looking run containing not one `resource`.

### Symbolic links and junctions inside the bundle

`File.Move`, `File.Delete`, `File.WriteAllBytes` and `Directory.Delete` all **follow** a
symbolic link or a junction, and `Path.GetFullPath` resolves neither — so no comparison of
path strings can tell that `<bundle>/code/x/report.md` is really `~/notes/report.md`. The
threat is not an `--out` you chose; it is a link inside a bundle somebody else wrote, and
a bundle committed beside a repository is content a clone brings with it.

`okfgen` asks the **filesystem** where a path lands, not the path string: it walks the
candidate component by component, follows every reparse point it meets, and refuses
anything landing outside the bundle root.

**This section gives no total, on purpose.** Two rounds of review put a number here and
both were wrong — one invented an ungated hole in `OKF4net`'s `IndexGenerator` that does
not exist, the next declared every write gated while two were not. A count in prose is a
claim nobody re-derives; each gate is instead documented in the code that has it
(`BundleWriter.CommitStaging`, the prune and the directory cleanup in `BundleWriter`,
`GenerationManifest.WriteTo`, and the two reporting walks), where a reader can check the
gate against the call it guards.

The `IndexGenerator` correction is kept rather than quietly dropped: it gates itself. It
collects markdown with `Directory.GetFileSystemEntries` and tests for a reparse point
*before* it recurses, applies the same skip when listing a directory's children, and
immediately before each write re-checks both the directory's ancestor chain and the
`index.md` node itself. Junctions and symbolic links are both covered.

What this means for you:

- **Do not generate into a bundle that contains a symbolic link or a junction.** The gates
  make the refusals safe, not the arrangement workable. A run against such a bundle keeps
  concepts it would otherwise prune, and — if the link is `.okfgen-manifest.json` itself —
  leaves its ownership record unwritten and says so. A concept whose path crosses the link
  is refused and recorded as a **write failure**; which of the two gates refuses it depends
  on where the link points, and so does the sentence you get — see the `generate` bullet
  below. Remove the link and re-run.
- **`--reset` refuses outright** rather than emptying such a bundle. Measured on Windows:
  a recursive delete over a tree holding a junction removes the real files, unlinks the
  junction, and *then* fails — so the unguarded reset destroyed the bundle and returned an
  error, leaving neither the old bundle nor the new one. The refusal happens before the
  first file is deleted; the message names the link. Remove it, or use `--update`.
- **`--check` neither copies nor compares what a link points at,** and emits one note per
  link it skipped that the differences do not already name. A clean result there is a
  statement about everything else. Earlier it copied *through* a link, which flattened the
  far side into the comparison and produced notes about files that were never in the bundle.
  A link is **not** automatically invisible to the check, and which of the two shapes you
  have decides what you read:
  - a link where the generator writes a **directory** of concepts (`code/<namespace>`): the
    copy fills that path with real concepts while your bundle has nothing there, so each
    displaced concept is reported as drift, the check exits 1, and the note names the link.
    Two paths, two true statements.
  - a link where the generator writes a **file** (`code/<namespace>/<type>.md`): the drift
    line names the link's own path and says a link is what the bundle holds there. No note is
    emitted for it — the drift line carries the whole story, and printing both handed you one
    line calling the path missing from the bundle and another saying it was never compared.

  Either way the exit code is 1, and that is the right answer: the concepts this producer
  writes at those paths are not in your bundle.
- **`generate` refuses to write a concept whose path crosses the link**, and reports it as a
  **write failure naming that one concept** while the run carries on: the id is left out of
  the manifest, and the failure count disqualifies the run from pruning. Two gates do it, and
  which one fires decides what you read. `CommitStaging` resolves the destination through the
  filesystem, then asks (1) does it leave the bundle root, and (2) is the path it really
  arrives at the path that was asked for. Measured on Windows 11 build 26200 / .NET 10.0.8:
  a junction at a concept file's path pointing **outside** the bundle fails (1) — "the path
  leaves the bundle root"; a junction at a concept file's path, or at a concept *directory*,
  pointing back **inside** the bundle passes (1), because nothing is escaping, and fails (2)
  — the message names the path the concept would otherwise have landed at.

  Both inward shapes used to get past the gates. The file-path one threw
  `UnauthorizedAccessException` out of the whole run, taking every concept already committed
  with it. The directory one was worse: measured on the same host, nothing failed at all —
  `CreateDirectory` succeeded on the junction, `File.Move` wrote through it, a hand-written
  file at the far end was replaced by the generated concept, and the manifest claimed an id
  whose file was not at the path the id names. Both are fixed. Remove the link.
- **A bundle root that is itself a link stays fine** — a symlinked project directory, a
  container or WSL bind mount. Only the root's own link is followed, and every path below
  it is measured against the resolved root.

None of this makes an untrusted bundle safe to generate into in general. It bounds the
paths *this producer* builds from concept ids, and that is the whole claim.

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
2026-09-01, version 0.1.0. **The size that matters is the installed one** — what a
`dotnet tool install` leaves on disk; the download column is given because it is what
the feed serves, not as a second target:

| Package | Installed | Download |
|---|---|---|
| `OkfProducer.Cli.0.1.0.nupkg` (pointer) | — | 2 KB (5 entries, no payload) |
| `OkfProducer.Cli.win-x64.0.1.0.nupkg` | 87.6 MB | 13.3 MB |
| `OkfProducer.Cli.linux-x64.0.1.0.nupkg` | 83.7 MB | 11.6 MB |
| `OkfProducer.Cli.osx-arm64.0.1.0.nupkg` | 80.7 MB | 11.5 MB |

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

Two related facts, recorded rather than fixed. The installed size is dominated by
grammars this producer never loads (`verilog` 17.3 MB, `razor` 10.5 MB, `cpp` 5.1 MB
on `linux-x64`), which cannot be removed one file at a time — `deps.json` is what feeds
`NATIVE_DLL_SEARCH_DIRECTORIES`, not the contents of the folder. Getting the
**installed** size below ~40 MB is a documented follow-up, not a v1 promise; the design
spec's "~69 MB" was an estimate of that same installed dimension and came in low.

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

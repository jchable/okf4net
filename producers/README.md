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
| `--no-msbuild` | off | Do not run `dotnet msbuild` on the scanned repository, and skip the Roslyn resolver built on it. Costs **two** things: call links then come from name matching alone, so an ambiguous name is left unlinked instead of resolved exactly; and the run has no source-ownership map, so **no `packages` → namespace containment link is emitted at all** — under `--update` that overwrites the ones a previous run wrote. **Read the section below before deciding you do not need this.** |
| `--max-file-size <bytes>` | 2 MiB | Largest source file the code stage will read — by **both** engines. The tree-sitter engine skips a larger one *and counts it*, which makes the run partial: the concepts it owned are then not pruned. The Roslyn engine applies the same cap to the `Compile` items MSBuild reports, but drops an over-cap item **silently** — for a file the scan also walked the counted skip covers it, and for one it did not (a linked out-of-repository source, a generated file under `obj/`) nothing reports it: the project simply fails to compile and is named as such. |

### Generating from a repository runs that repository's build logic

Only point `okfgen` at a repository you would be willing to **build**.

The exact (Roslyn) resolver gets its reference set by spawning `dotnet msbuild` once per
project, in the project's own directory. An MSBuild *evaluation* is the execution of
repository-authored logic — there is no read-only mode of it to ask for — so
`Directory.Build.props` and `Directory.Build.targets`, everything they `Import`, any target
hooked on `BeforeTargets="ResolveReferences"`, and a `RoslynCodeTaskFactory` inline `<Code>`
task all run, as the user running `okfgen`. That is a wider door than anything else in this
producer: the tree-sitter extractor only parses, and the Roslyn stage deliberately does not
run source generators.

**The repository also adds to the invocation itself.** `dotnet msbuild` auto-applies a
`Directory.Build.rsp` found in the project's directory — the directory the query deliberately
runs in — and that file holds command-line switches, not properties. Measured on this host: a
one-line `Directory.Build.rsp` containing `-t:Pwn` made the producer's own query run a `Pwn`
target it never requested, alongside `-t:ResolveReferences`. So a repository can turn the
query into `-t:Build`, or add any other switch. Two things were measured to still hold: an
explicit command-line switch wins on conflict, so the producer's own `-nodeReuse:false`
cannot be flipped from the rsp; and the mitigation above is unchanged, because it never
rested on which targets were asked for.

`--no-msbuild` is the way out. It skips the whole stage: **no `dotnet msbuild` is spawned and
no MSBuild logic from the scanned tree is evaluated**, and calls are resolved by the
name-matching baseline — which refuses an ambiguous name rather than guessing it, so the call
links you lose are edges, not correctness. The run says so in a note.

Two things it is **not**. It does not make the run process-free: `okfgen` runs `git` in the
scanned tree, with the repository as the working directory, reading its `.git/config`. How
many times depends on the flags, so here is the whole of it rather than a number. `git show
-s` and `git rev-parse` run on *every* generate — they stamp `overview`'s `generated.at`
and `revision`. `git symbolic-ref` runs as well, **unless `--rev` already named the ref**, in
which case the branch is never read. And `--check` runs one further `git rev-parse` in the
scanned tree before the regeneration it compares against, on top of that regeneration's own.
Two to four invocations, then. Far less exposure than MSBuild — none of them triggers a hook,
an fsmonitor, or a pager with stdout redirected — but it is not nothing, and this section
used to say "no process is spawned". And it is not free of structural cost: the source-ownership
map comes out of the same MSBuild query, so with the flag on there is **no `packages` →
namespace containment link at all**, and under `--update` those links are overwritten.

It is **off by default on purpose**. Turning it on by default would silently degrade the
resolution quality of every run that exists today, which is a worse trade than a documented
hazard with a lever next to it.

Notes — what a run could not do — go to **stderr**, prefixed `note: `; results go to
stdout. A note never changes the exit code. The ones worth knowing:

Each bullet names the site that emits it, so a documented note can be checked against
the emitted one by grep rather than from memory.

- (`GenerateRun.ReportProjects`) a project the exact (Roslyn) resolver could not compile,
  so calls in its files fell back to name matching;
- (`GenerateRun.ReportProjects`) a project that *did* compile with zero errors but of
  which the run owns no file — the same degradation under a report that says otherwise.
  It happens when every one of a project's `Compile` items was refused before the
  compilation was built (each over `--max-file-size`, each behind a link, each absent, or
  none of them inside the repository): the compilation is then empty, and an empty
  *library* compilation has no errors to report, so `Compiled` overstates it. A project
  that declares no `Compile` item at all is **not** this case and is not reported — it
  owns nothing because it has nothing, with no refusal behind it and no links to lose;
- code containers no package's `Compile` item set claims, so they hang off their own
  parent but off no package concept;
- (`ConceptGenerator.AttributePackages`) no source-ownership map at all, so the
  package → namespace level of the containment spine is missing entirely. This note is
  gated on nothing but the loss itself — no map supplied, at least one package concept,
  at least one namespace group — so it is what **every** shape of the loss has in common,
  and it says the same thing in each. Three run shapes leave the map unsupplied
  (`GenerateRun.Execute` assigns it at exactly one site, inside the branch that queries
  MSBuild): `--no-msbuild`, which additionally prints its own note naming this loss
  beside the call-resolution one; the stage running but MSBuild able to answer for no
  project at all (no `dotnet` on `PATH`, a repository that was never restored), where
  `GenerateRun.Attribution` additionally points at `dotnet restore`; and a scan that
  detected no `.csproj` to query at all, where neither of those branches runs and this
  note is the only signal there is. (`--no-code` leaves the map unsupplied too, but
  analyses no source, so there is no namespace to attribute and no note.)
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
  concepts it would otherwise prune, and — if the link is `.okfgen-manifest.json` itself,
  pointing out of the bundle *or* back inside it — leaves its ownership record unwritten and
  says so. A concept whose path crosses the link
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
paths *this producer* builds from concept ids, and that is the whole claim. Neither does it
bound a bundle that is being *changed while a run is in progress*: the gates resolve a path
and the write acts on it a moment later, with no handle held across that window.

#### An open question the writes and the prune answer differently

**Recorded, not settled.** As of this round the write side refuses a link that points back
*inside* the bundle: `CommitStaging` will not move a concept through one, and
`GenerationManifest.WriteTo` will not write the manifest through one. The reason given at
both sites is that following it "would write a concept at a path that no longer matches its
id and destroy whatever the far end holds".

The **prune** does the opposite on the same shape. `TryResolveConceptFile` resolves a
manifest id through an inward junction, finds the far end inside the bundle, and
`File.Delete` removes it — the far-side file, not the link. This is deliberate and pinned by
a test (`The_directory_cleanup_after_a_prune_removes_no_link_the_bundle_merely_holds`), which
asserts that the id *is* pruned while the link itself survives. It predates the write-side
gates and was not changed by them.

So the two sites are:

- `BundleWriter.CommitStaging` + `GenerationManifest.WriteTo` — an inward link is a **refusal**.
- `BundleWriter.Reconcile` → `TryResolveConceptFile` → `File.Delete` — an inward link is
  **followed**, and the file at the far end is deleted.

The question, stated without an answer: if writing through an inward link is damage because
the file at the far end was never this run's to touch, is deleting through one damage for the
same reason — or is a manifest id a standing claim on wherever that id resolves, which is
precisely what the prune's three-check design already assumes? Choosing "refuse" makes a
bundle that deliberately links a concept directory un-prunable and leaves stale concepts
behind; choosing "follow" keeps the current behaviour and the current exposure. Both readings
have a real cost and neither was picked here. A maintainer changing either site should settle
this first, and change both.

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

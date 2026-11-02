# Bundle provider, lot D: catalogue, viewer and breaking changes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the provider seam where lots A–C left it: the catalogue owns one `BundleHandle` per knowledge source (created from a `catalog.json` source's provider, reused across reloads while the source's configuration is unchanged, searched through by every resolver, `OnAccess` by default), `OkfBundleKnowledgeSource` serves an image from a handle, `FileMemoryStore` reads, enumerates and deletes through the provider and reports a `MemoryDeleteResult` per tier, the viewer and the CLI read `Location` and the §8 display path instead of the path-based members, those members become `[Obsolete]` with a migration message, `Diagnostic` and `AuditFinding` carry their `Key`, memory scope segments with a reserved base are prefixed, and the docs say all of it — with no byte of `okf` or `okf-render` output moving and every existing caller behaving as before, except what §10 lists and the CHANGELOG records.

**Architecture:** Three seams change and nothing else. In `src/OKF4net`: `Bundle.DisplayPath(BundleParseError)` is the one public text of a load problem's display path; `BundleValidator` and `ConceptAudit` attach the key beside the path they already print; the path-based members gain `[Obsolete]` and lose their last readers in `src/`. In `src/OKF4net.Catalog`: `KnowledgeCatalogSnapshot.Handles` holds the handles `FileKnowledgeCatalog` builds (filesystem provider confined to the catalog root, or an `IBundleProviderFactory` registered for the source's `provider` type) under `KnowledgeCatalogOptions.LoadOptions`/`RefreshPolicy`; `CatalogSourceOpener` is the single place every resolver opens a source (the snapshot's handle, else the per-request path resolution of before, for a hand-built snapshot); `FileMemoryStore` holds one `FileSystemBundleProvider` per tier root and opens each scope through a provider rooted at the scope and confined to the tier root. In `src/OKF4net.Viewer` and `src/OKF4net.Cli`: `SiteModel`, `okf info` and its JSON read `Location`, `IndexKeys`/`LogKeys` and `ParseProblems` with the display path — the same strings by construction.

**Tech Stack:** C# 14 / .NET 10, BCL only in `src/OKF4net`, `src/OKF4net.Catalog`, `src/OKF4net.Viewer`, `src/OKF4net.Cli` (zero new `PackageReference`, no P/Invoke); `Microsoft.Extensions.DependencyInjection.Abstractions` stays the one package of `src/OKF4net.Catalog.Hosting`; xunit + `Xunit.SkippableFact` in `tests/OKF4net.Tests`; node 22 for one line-ending-safe script (Task 9) and the differential of Task 11; Docker image `mcr.microsoft.com/dotnet/sdk:10.0` for the Linux runs.

**Spec:** `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (revision 6 with its dated maintainer notes), sections §1 (`BundleWriteReport`, `MemoryDeleteResult`), §3 (`FileMemoryStore`, `okf parse`/`fmt` out of the seam), §5.2–§5.3 (handles, policies, the two defaults), §7 (no credential through `catalog.json`), §8 (the display rule, the confinement boundary), §9 (the consumers table: `Bundle`, `BundleValidator`, `ConceptAudit`, `OkfBundleKnowledgeSource`, Catalog, `FileMemoryStore`, Viewer, `okf`/`okf-render`; "Catalog : qui crée et conserve les handles"; "nommer un provider dans `catalog.json`"; the memory key rule D4), §10 (the breaking list) and §11 (verification). The maintainer's governing rule for every lot: everything works as before for existing callers beyond what §10 announces, each change recorded in the CHANGELOG by the task that makes it. Lots A (`fa983e4`), B and C (`feat/bundle-provider-c`, tip `3ad76b8`) are its base.

## Replay

Every code block of this plan was taken from a spike built one commit per task on branch `spike/lotd-1` (worktree `E:\Sources\okf\.claude\worktrees\bundle-provider-d-spike`, from `3ad76b8`, upstream unset; commits `a31f478` → `e09bf4a`), rebuilt and tested as the steps say; the blocks were then re-applied mechanically on a second worktree from `3ad76b8` (`spike/lotd-2`, see the end of this section) and the result compared with the spike tree. The counts below are the spike's, on Windows 11 (.NET SDK 10.0.204) unless stated. Base of the plan, Windows full suite: `Passed: 3536, Skipped: 36, Total: 3572`. End of the plan: **Windows `Passed: 3596, Skipped: 36, Total: 3632`** (+60 tests), `dotnet build OKF4net.sln` with 0 warnings, `dotnet format OKF4net.sln --verify-no-changes` clean; `dotnet test producers/OkfProducer.sln` ``Passed: 826, Skipped: 15, Total: 841`` (the base's own counts, after the one producers edit of Task 9); Native AOT publish of `okf` (4 845 056 bytes) and `okf-render` (4 603 904 bytes) succeeds and `okf validate bundles/acme_retail` prints `✓ conformant with OKF v0.2`; the viewer JS harness (`tools/viewer-security-check`, `npm test`) `453 passed, 0 failed`; Linux (container) as root, full suite `Passed: 3559, Skipped: 73, Total: 3632`; as an unprivileged user over the Providers, Agents, Attestation and Catalog classes `Passed: 1424, Skipped: 46, Total: 1470`; the read-only `okf` verbs (`validate`, `validate --json`, `info`, `info --json`, `audit`, `audit --json`, `audit --json --type Table`, `graph`, `graph --dot`) and `okf-render` give byte-identical transcripts and sites on six repository bundles against `3ad76b8` (477 transcript lines and 185 site files; Task 11). **The mechanical replay** (`spike/lotd-2`, from `3ad76b8`): every code block of Tasks 1–10 applied in order by exact single-occurrence replacement (1 + 4, 1 + 8, 2 + 5, 2 + 16, 1 + 14, 1 + 6, 5 + 10, 2 + 3, 16 + 19 and 0 + 7 blocks for the test and source sides of Tasks 1 to 10), each task's build at 0 warnings and its test command at the counts the task states, every touched `.cs` file CR = LF, the full suite `3596 / 36 / 3632`, `dotnet format` clean, and the resulting tree identical to the spike's (`diff -rq`, the hook's `graphify-out/` excluded). The red steps of Tasks 2, 6 and 8 were replayed the same way on the previous task's tree with the task's test blocks alone.

## Scope and cut

The design says four lots: A read side (merged), B write side (done), C handles, observations and pinned attestation (done, `3ad76b8`), D catalogue, viewer and breaking changes. This plan implements **lot D**, cut as follows.

**Already done by lots A–C** (verified on `3ad76b8`; nothing below is planned again):

- `Concept.Key` (init-only, beside the positional `Path`), `Bundle.Provider`, `Bundle.Location`, `Bundle.IndexKeys`/`LogKeys`, `Bundle.ParseProblems` (`BundleParseError(RawName, Key?, Error)`), `Bundle.TryResolveResourceKey`, `Bundle.ReadResourceText(BundleKey)`, `ConceptId.ToKey`/`FromKey`, `BundleDisplayPath.Compose` — lot A (`src/OKF4net/Bundle.cs:46`, `:604-625`, `Providers/BundleDisplayPath.cs`). The §9 rows `Bundle` and `ConceptId` are complete except for the obsolescence of the old members.
- `BundleWriteReport`, `VerificationOutcome.Report`, `BundleConceptWriter.BundleRoot` throwing `NotSupportedException` off the filesystem (`BundleConceptWriter.cs:334`) — lot B. `BundleValidator` already reads `index.md`/`log.md` and tests resources through the provider.
- `BundleHandle`, the four policies, `OkfBundleTools(BundleHandle)` with `Handle`/`Location` and `BundleRoot` throwing off the filesystem (`OkfBundleTools.cs:249`), `OkfContextProvider` sharing the handle, `AttestationOrchestrator` on keys, D6, the `okf-mcp` bound — lot C. The §9 rows `BundleConceptWriter`, `IndexGenerator`, `OkfBundleTools`, `OkfContextProvider`, `AttestationOrchestrator`, `okf-mcp` are complete.
- `okf parse` and `okf fmt` already read and write their one file with `System.IO` (`OkfCli.ReadFileStrict`/`WriteFileStrict`), outside the seam by design (§3): nothing to do but say so (Task 10).

**In lot D** (this plan):

- §9 Viewer: `SiteModel` on `Location` and the display path (Task 1). §9 `okf`: `info`, `info --json` and the validator's three readers on keys (Task 2).
- §9 `OkfBundleKnowledgeSource` on a `BundleHandle` (Task 3). §9 "nommer un provider": `provider`/`settings`, `IBundleProviderFactory`, the registration (Task 4). §9 "qui crée et conserve les handles": the snapshot's handles, reuse on identical configuration, disposal (Task 5); the resolvers through them, deduplication by `IBundleProvider.Id` (Task 6).
- §9 `FileMemoryStore` through the provider, `MemoryDeleteResult` replaced (§1, §10) (Task 7). §9 D4: the `_` prefix with its migration note (Task 8).
- §10: the path-based members `[Obsolete]`, `Diagnostic.Key` and `AuditFinding.Key`, the one producers reader migrated (Task 9). Docs: CHANGELOG (per task, consolidated), both READMEs (#143, "Local filesystem bundles only"), CLAUDE.md, the design's dated notes (Task 10). Verification (Task 11).

**Not in lot D** (the design's "Travaux futurs", none needed by a lot-D piece): #140 end-to-end revalidation, #141 embedded provider, #142 S3 (the factory seam is here, no S3 factory ships), #151 memory ceiling, #152 async contract (the removal of the obsolete members goes with it), #153 beyond-memory bundles, #154 attestation allowlist, #178 cross-process lock, #179 incremental image, #181 frozen model. Also out: a remote memory provider (§9: memory stays filesystem), a trust level per source, `okf serve`.

## Open decisions (the controller rules; the plan proceeds on the recommendation)

1. **Obsolete, not removed (L2).** §10 lists `Concept.Path`, `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText`, `Diagnostic.Path`, `AuditFinding.Path` as breaking and only `BundleRoot` as "`[Obsolete]` pendant une version"; the brief says `[Obsolete]` on `BundleRoot` *and* the path-based members whose last reader is gone; the maintainer's rule is "tout doit fonctionner de façon identique à avant". The plan marks `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)` and the two `BundleRoot` obsolete (a warning naming the replacement, removal with #152), and keeps `Concept.Path`, `Diagnostic.Path`, `AuditFinding.Path` as they are — positional members whose value *is* the §8 display rule every output prints — beside a new `Key`. Recommendation: keep; a removal is one more major bump away and costs every host now.
2. **`new OkfBundleKnowledgeSource(id, directory)` under `OnAccess` (L5).** §5.2 says a string constructor "crée son propre handle" and §5.3 says the general default is `Manual`; but that source reloaded at every search, and `Manual` would make a host-built one never see a change. `OnAccess` (the catalogue's default, §5.3) keeps it fresh at the cost of a token read, which §10 lists. Recommendation: `OnAccess`.
3. **A source root replaced by a junction after the handle exists (L13).** §11 says "refusée au prochain accès"; §5.2 says a failed reload keeps the previous image served. The provider refuses the token read, the handle serves the image it had (`IsStale`, `LastLoadError` wrapping `BundleAccessRefusedException`) and nothing under the junction is ever read; lot C's C18 took the same reading. Recommendation: follow §5.2 and pin it (Task 6).
4. **Non-positional `ProviderType`/`Settings` on `KnowledgeCatalogSource` (L7).** §10 lists the record as breaking ("gagne le type de provider et ses réglages"); init-only properties with defaults add them without breaking a constructor call. Recommendation: init-only (less breakage than announced is fine).
5. **A scope's directories are left after a deletion (L10).** §8 says a delete leaves empty directories, the brief says no direct `Directory.Delete`; the old code removed the scope directory. The plan deletes object by object and leaves directories (a scope exists while it holds a key, decision 14), listed in the CHANGELOG. Recommendation: keep; removing directories would reopen direct I/O.
6. **`path` refused on a non-filesystem source (L8).** §9 says a source's `Path` "ne vaut plus que pour le type filesystem"; a silently accepted but unused `path` is a trap. Recommendation: `IllegalPath`.
7. **The diagnostic of a failing factory names the exception type only (L9)**, as every provider failure is translated (§7). Recommendation: keep; the raw text is for the host's log, and the factory is the host's code.
8. **Tests that pin the obsolete projections suppress CS0618 file-wide (L12)** (fourteen files, one line after the SPDX header) rather than migrate: they pin that the projections are the strings they always were. Recommendation: keep.

Found in the design, resolved as above: §11's "refusée au prochain accès" against §5.2's served-stale image (decision 3); §10's removal list against the maintainer's rule and the brief's `[Obsolete]` (decision 1); §5.2's "crée son propre handle" with §5.3's `Manual` default against the per-call reload the string constructor had (decision 2). Nothing in the design was found impossible.

## Decisions taken by this plan (L1–L14, for the maintainer to confirm)

**L1. Scope.** As cut above; no public signature of lots A–C changes, no fixture is touched.

**L2. Obsolescence.** `[Obsolete("… use … (#152).")]` on `Bundle.Root`, `Bundle.IndexFiles`, `Bundle.LogFiles`, `Bundle.ParseErrors`, `Bundle.TryResolveResource`, `Bundle.ReadResourceText(string)`, `BundleConceptWriter.BundleRoot`, `OkfBundleTools.BundleRoot`; `ObsoleteMembersTests` is the inventory (the two members obsolete before lot D, `OkfBundleTools.RunComputation` and `OkfContextProviderOptions.MemoryDirectory`, listed as they are). `Concept.Path`, `Diagnostic.Path`, `AuditFinding.Path` stay; `Diagnostic.Key` and `AuditFinding.Key` are added, attached once after validation from the display path (`BundleValidator.AttachKeys`) and at the one audit construction site. `BundleKey` has no `==` operator: compare keys with `Equals`.

**L3. `Bundle.DisplayPath(BundleParseError)`** is public; `okf info`, `info --json`, `validate`, the viewer and the tests print a problem through it. `ConceptId.FromKey(indexKey)` replaces `ConceptId.FromPath(bundle.Root, indexPath)`: same segments, same validation, same exception.

**L4. The viewer.** `ViewerSite.BundleRoot` keeps its name and takes `Bundle.Location`; `ViewerParseError.Path` is `bundle.DisplayPath(problem)`. The site's bytes do not change (Task 1's test, Task 11's differential).

**L5. `OkfBundleKnowledgeSource`.** `(string id, BundleHandle handle)` and `Handle`; the directory constructor builds `new BundleHandle(new FileSystemBundleProvider(dir), refreshPolicy: BundleRefreshPolicy.OnAccess)`; `BundleRelativePath` is `concept.Key.ToString()` (the relativised `Path` only for a hand-built concept, `RelativePathOf`); a first load that fails is `SourceUnavailable` as before, a failed reload serves the stale image.

**L6. `ProviderSettings`** is a value-equal read-only map (sorted ordinal, `From`, `Empty`) so that two sources with the same settings are equal and a reload can compare configurations.

**L7. `KnowledgeCatalogSource.ProviderType`** (`"filesystem"` by default, `FileSystemProviderType`), `.Settings`, `.IsFileSystem`, init-only. The parser accepts `provider` (non-empty string) and `settings` (object of strings, each name once); `path` is required for `filesystem` and refused otherwise; `filesystem` accepts no setting; a memory source must be `filesystem`. New codes: `MalformedProvider`, `MalformedSettings`, `IllegalSetting`, `IllegalPath`, `IllegalMemoryProvider`, `UnknownProvider`, `ProviderUnavailable`.

**L8. `IBundleProviderFactory`** (`ProviderType`, `AcceptedSettings`, `Create(KnowledgeCatalogSource)`) in `OKF4net.Catalog`; `KnowledgeCatalogOptions.ProviderFactories`; `KnowledgeOptions.AddProviderFactory` in Hosting. `FileKnowledgeCatalog`'s constructor refuses a factory for `filesystem`, a duplicate type or an empty type with `ArgumentException`; a source naming an unregistered type or an unaccepted setting rejects the load (`UnknownProvider`, `IllegalSetting`), consistent with the all-or-nothing reload.

**L9. Handles on the snapshot.** `KnowledgeCatalogSnapshot.Handles : IReadOnlyDictionary<string, BundleHandle>` (init, empty by default). `FileKnowledgeCatalog.TryBuildHandles` runs after the path checks: per enabled knowledge source, the `HandleConfiguration(ProviderType, ResolvedDirectory, Settings, RefreshPolicy, LoadOptions)` is compared with the one the current handle was built for; equal → the handle is reused; else a provider is created (`new FileSystemBundleProvider(dir, confinementBoundary: CatalogRoot)` or `factory.Create(source)`) and `new BundleHandle(provider, LoadOptions, policy)`; a factory or handle constructor that throws (anything but `OutOfMemoryException`) rejects the load as `ProviderUnavailable` naming the exception type, and the handles built in that attempt are disposed. On a successful publish, the previous snapshot's handles not carried over are disposed (`DisposeHandlesLeft`); `Dispose()` disposes the current ones. `KnowledgeCatalogOptions.RefreshPolicy` (`OnAccess`), `RefreshPolicyFor`, `LoadOptions` (`BundleLoadOptions.Default`); `KnowledgeOptions.RefreshPolicy` in Hosting.

**L10. `FileMemoryStore`.** One `FileSystemBundleProvider` per tier root (`_tierProviders`); a scope provider `new FileSystemBundleProvider(Path.Combine([tierRoot, .. prefix.Segments]), confinementBoundary: tierRoot)`; `Probe` = `scopeProvider.Exists(index.md)` (the boundary check, which answers an absent root without throwing) then `tierProvider.Enumerate(prefix).Any()`; `BundleAccessRefusedException` → `Refused`, with the old diagnostic texts. Reads load `Bundle.Load(scopeProvider)` (ids and links as before). Deletion: `tierProvider.Enumerate(prefix)`, `Delete(key, None)` per object, a raw name that is no key reported under the prefix; directories left. `MemoryDeleteResult(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error)`, `MemoryTierDeletion(MemoryTier Tier, BundleWriteReport Report)`; a tier whose scope holds no key is absent from `Tiers`; `Error` joins the tiers' failures as before. Writes use `new BundleConceptWriter(tierProvider)`.

**L11. D4.** `BundleKeyValidator.HasWindowsReservedBase(segment)` (internal, the one list) and `MemoryPath.Encode` prefixing `_` when the encoded segment's base is reserved; the hash unchanged. A scope id is ASCII, so the superscript names never reach it.

**L12. The resolvers.** `CatalogSourceOpener.TryOpen(catalog, snapshot, source, out bundleSource, out identity, out location, out diagnostic)`: the handle when the snapshot has one (identity `Provider.Id`, location `handle.Location`), else the per-request `CatalogPathResolver.TryResolve` + directory constructor of before (identity and location the directory). `GroupedKnowledgeResolver` and `FusedResolverEngine` call it; the `DuplicateDirectory` message keeps its wording with the location.

**L13. Confinement.** A catalogue handle's provider carries `confinementBoundary: CatalogRoot`; the chain root→source is re-checked STRICTly before every access (lot A). A root replaced by a junction after the handle exists fails the token read; the image is served stale (open decision 3).

**L14. Documentation per task.** Each task carries its CHANGELOG entry text; Task 10 writes them consolidated (the spike wrote them in one commit, so the CHANGELOG hunks are in Task 10) and the READMEs, CLAUDE.md and the design notes.

## Global Constraints

- **Branch and worktree.** Execute in a dedicated worktree on branch `feat/bundle-provider-d`, created from `feat/bundle-provider-c` at `3ad76b8`: `git worktree add .claude/worktrees/bundle-provider-d -b feat/bundle-provider-d 3ad76b8`, then `git branch --unset-upstream` — never in `E:\Sources\okf` (the shared checkout, where `dev` is extracted; never `git checkout dev`). Do not push before the lot is reviewed.
- **Zero third-party runtime dependencies** in `src/OKF4net`, `src/OKF4net.Catalog`, `src/OKF4net.Viewer`, `src/OKF4net.Cli`, `src/OKF4net.Render`: BCL only, no `PackageReference`, **no P/Invoke**. `src/OKF4net.Catalog.Hosting` keeps `Microsoft.Extensions.DependencyInjection.Abstractions` only. `InvariantGlobalization` stays on in `OKF4net.Cli` and `OKF4net.Render`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on every public member (missing docs fail the build), nullable on, `TreatWarningsAsErrors`, LangVersion 14. **An XML `cref` may only name a type or member that exists at that task** (CS1574 is an error: Task 3's class remarks say "the snapshot's handles" in plain text because `KnowledgeCatalogSnapshot.Handles` arrives in Task 5). A `cref` to an obsolete member raises no warning (measured), but Task 9 replaces them anyway so the docs point at the replacements.
- **Source files are CRLF in the working tree** (`core.autocrlf`; the index holds LF). After writing a **new** `.cs` file, convert it with `unix2dos -q <file>`; the file-editing tool keeps CRLF on an existing CRLF file. **Never run MSYS `sed -i` on a `.cs` file** (it strips every CR). Before each commit, for every touched `.cs` file, `tr -cd '\r' < f | wc -c` must equal `wc -l < f` (the shell's `$'\r'` in a `grep -c` miscounts: use `tr`). Task 9's node script preserves line endings by construction.
- **Tool-layer pitfalls.** The file-writing tool decodes `\uXXXX` escapes and collapses `\\`: every block of this plan writes `\n` and `\t` inside C# string literals only (they survive), builds no `\\` sequence and no `\uXXXX` escape (Task 8's theory rows are plain ASCII for that reason). Check any doubtful byte with `cat -A`.
- **`tests/fixtures/` is never edited and no snapshot is regenerated.** Lot D promises byte-identical CLI and viewer output; if a golden comparison or Task 11's differential turns red, STOP and report.
- **Path-safety guards use the STRICT predicates.** This lot adds no `ReparsePoints` call: `FileMemoryStore`'s old `IsReparseEscaped` walk is replaced by the provider's confinement boundary and per-key guards (`CheckBoundary`, `GuardStrict`), and `CatalogPathResolver` keeps its STRICT walk. The two lenient sites named in CLAUDE.md are unchanged.
- **Provider errors are translated at the core boundary** (§7): a factory's exception is reported by type name only; the memory store's per-object failure keeps the runtime's text as the old `could not be deleted: …` did (#211).
- Each task's **CHANGELOG entry** is given in the task and written in Task 10 (`### Added` after lot C's last entry, a new `### Changed (breaking)` between `### Added` and `### Changed`, the Changed entries at the top of `### Changed`).
- Commit messages say only what the code does. End each with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Stage files by name — **never `git add -A`** (the graphify hook rebuilds in the background after each commit; it writes nothing into the worktree). `tools/lotd-pragma.js` (Task 9) is never committed.
- **Tests.** `dotnet build OKF4net.sln` with 0 warnings, `dotnet format OKF4net.sln --verify-no-changes`, and `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` green at the end of every task (the counts in each task are what to expect; the test runner prints in the machine's language — `Réussi!`/`Échoué!` here — so grep for both). The junction tests run on Windows; the file-symlink ones skip without Developer Mode. **The Linux container run is mandatory at Task 11** (advisable after Tasks 6 and 7), as root for the full suite and as an unprivileged user for the Providers, Agents, Attestation and Catalog classes, from the worktree root in Git Bash:

  ```bash
  SRC="$(pwd -W)"   # Windows path of the worktree, for Docker Desktop
  MSYS_NO_PATHCONV=1 docker run --rm -v "$SRC:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -e; mkdir -p /work; cd /src
    tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" --exclude=./producers --exclude=./tools/viewer-security-check/node_modules -cf - . | (cd /work && tar xf -)
    cd /work && dotnet test tests/OKF4net.Tests --filter "Category!=ContainerIntegration" 2>&1 | grep -E "Passed!|Failed!|^\s+Failed |Error Message" -A3
    useradd -m tester && chown -R tester /work
    su tester -c "cd /work && dotnet test tests/OKF4net.Tests --filter \"FullyQualifiedName~OKF4net.Tests.Providers|FullyQualifiedName~OKF4net.Tests.Agents|FullyQualifiedName~OKF4net.Tests.Attestation|FullyQualifiedName~OKF4net.Tests.Catalog\" 2>&1 | grep -E \"Passed!|Failed!|^\s+Failed \" -A3"'
  ```

  Expected at Task 11: root `Passed: 3559, Skipped: 73, Total: 3632`; tester `Passed: 1424, Skipped: 46, Total: 1470`. (Do not pass `--no-build` or override `HOME` for the tester: the SDK's workload check then fails before any test runs.)
- **Timing-dependent tests outside lot D.** If exactly `AttestationOrchestratorTests.A_token_ignoring_stage_is_abandoned_when_the_token_fires("blocking")`, `OkfComputationToolsTests.A_token_ignoring_attester_cannot_outlast_the_timeout_or_display_its_result("blocking")` or `CliContainerEngineRunTests.A_hung_engine_kill_does_not_hang_the_timed_out_run` fails alone in a full run, rerun its class before concluding anything (none failed in this plan's replays).
- **Native AOT publish on this machine** needs `vswhere.exe` on `PATH` when run from Git Bash; run it from PowerShell with `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH` first (Task 11). Do not run a Release publish while another `dotnet` build runs in the same worktree.
- `dotnet test producers/OkfProducer.sln` green locally before the lot is declared done (`producers/` is outside CI by decision); Task 9 migrates its one reader of `Bundle.ParseErrors` (`PruningTests.cs:1363`), the only producers edit of the lot.

## Review Focus

1. **A catalogue source serving a stale image after an external edit.** Under `OnAccess` the token decides at every request; a request must never serve an image older than the storage when the token changed, and a failed token read must serve the previous image stale rather than nothing. Pinned by `Two_searches_over_an_unchanged_bundle_load_it_once_under_OnAccess`, `A_change_in_storage_is_seen_at_the_next_search_under_OnAccess`, `A_failed_reload_keeps_the_previous_image_served_and_a_failed_first_load_is_SourceUnavailable` (Task 3) and, through the resolvers, `Two_searches_over_an_unchanged_source_load_it_once_and_a_change_is_seen_at_the_next_search` for the three strategies (Task 6).
2. **A handle leaked, or disposed under a live request.** A reload must reuse an unchanged source's handle (no second image), rebuild a changed one, dispose what leaves without breaking a request that still holds the old snapshot, and dispose nothing of the current snapshot on a rejected reload. Pinned by `A_reload_with_an_unchanged_source_keeps_its_handle_and_its_image`, `A_reload_that_changes_a_sources_path_builds_a_new_handle_and_the_old_one_still_serves_its_image`, `A_rejected_reload_keeps_every_handle`, `A_factory_source_gets_a_handle_under_its_own_policy_and_leaves_the_catalog_disposed`, `Disposing_the_catalog_disposes_its_handles` (Task 5).
3. **A changed `okf`/`okf-render` byte.** The display path of a rejected raw name (no key), of a nested key and of an `index.md`/`log.md` finding must be the `Path.Combine` string of before. Pinned by `Build_takes_the_bundle_location_and_the_display_path_of_every_problem` (Task 1), `Info_json_and_the_reserved_file_findings_print_the_display_path_composed_from_the_key` (Task 2), the golden tests and Task 11's differential on six bundles.
4. **A memory scope deletion removing more than the scope, or following a junction.** Pinned by `Delete_removes_only_the_target_scope_subtree` (the sibling scope's file intact), `DeleteScope_refuses_a_scope_directory_reached_through_a_junction_whose_link_status_cannot_be_inspected`, `A_scope_directory_that_is_itself_a_junction_is_refused_by_every_operation`, `A_object_that_cannot_be_deleted_is_reported_and_the_deletion_can_be_run_again` (Task 7) and the hosting test `…outside the catalog root…` already in place.
5. **A breaking change without a migration, or a credential through the manifest.** Pinned by `Exactly_the_path_based_members_are_obsolete_each_naming_its_replacement` (every lot-D obsolete message says "use …" and "(#152)", none is an error; the inventory is closed) (Task 9), `The_parser_rejects_a_malformed_or_illegal_provider_declaration` and `A_registered_factory_accepts_the_source_and_a_setting_it_does_not_list_is_refused` (Task 4), and the CHANGELOG's `### Changed (breaking)` entries with their migration sentences (Task 10).

---


### Task 1: The viewer reads `Location` and the display path of each load problem

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (after the private `DisplayPathOf`, `:171-179`)
- Modify: `src/OKF4net.Viewer/SiteModel.cs:82-91`
- Modify: `src/OKF4net.Viewer/ViewerModel.cs:70-71`, `:103`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelLocationTests.cs` (create)

**Interfaces:**
- Consumes: lot A's `Bundle.Location`, `Bundle.ParseProblems`, `BundleParseError(RawName, Key?, Error)`, `BundleDisplayPath.Compose`; the test project's `InMemoryBundleProvider(id:)` whose `Location` is `memory://<id>` and `Add(rawName, text)`.
- Produces: `public string Bundle.DisplayPath(BundleParseError problem)` — the location, then the key's segments with the OS separator when the name is a key, else the raw name with `/` turned into the OS separator (`ArgumentNullException` on `null`); every later task that prints a problem uses it (Tasks 2 and 9).

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Viewer/SiteModelLocationTests.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// <see cref="SiteModel.Build"/> reads the bundle's <see cref="Bundle.Location"/> and the §8
/// display path of each <see cref="Bundle.ParseProblems"/> entry (spec §9, Viewer row), so the
/// strings a site carries are the ones the path-based projections printed: the location as
/// given, and for a problem the location plus the name with the OS separator — a rejected raw
/// name (no key) included. Driven by an <see cref="InMemoryBundleProvider"/> so the rejected name
/// needs no filesystem that can hold it.
/// </summary>
public class SiteModelLocationTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n";

    [Fact]
    public void Build_takes_the_bundle_location_and_the_display_path_of_every_problem()
    {
        var storage = new InMemoryBundleProvider(id: "site")
            .Add("a.md", Doc)
            .Add("broken.md", "---\ntype: Note\ntitle: never closed\n")
            .Add("bad\tname.md", Doc);
        var bundle = Bundle.Load(storage);

        var site = SiteModel.Build(bundle);

        Assert.Equal("memory://site", bundle.Location);
        Assert.Equal(bundle.Location, site.BundleRoot);
        Assert.Equal("site", site.BundleName);
        Assert.Equal(2, bundle.ParseProblems.Count);
        Assert.Equal(
            bundle.ParseProblems.Select(p => (bundle.DisplayPath(p), p.Error)).ToList(),
            site.ParseErrors.Select(e => (e.Path, e.Error)).ToList());

        // The rejected raw name has no key: its display path is the raw name under the location.
        var rejected = Assert.Single(bundle.ParseProblems, p => p.Key is null);
        Assert.Equal("bad\tname.md", rejected.RawName);
        Assert.Equal(Path.Combine("memory://site", "bad\tname.md"), bundle.DisplayPath(rejected));
        Assert.Contains(site.ParseErrors, e => e.Path == Path.Combine("memory://site", "bad\tname.md"));

        // The unparseable concept has a key: its display path is the key's segments under the location.
        var broken = Assert.Single(bundle.ParseProblems, p => p.Key is not null);
        Assert.Equal(Path.Combine("memory://site", "broken.md"), bundle.DisplayPath(broken));
    }

    [Fact]
    public void DisplayPath_joins_a_nested_key_with_the_OS_separator()
    {
        var storage = new InMemoryBundleProvider(id: "site").Add("a/b/c.md", "---\ntype: Note\ntitle: never closed\n");
        var bundle = Bundle.Load(storage);

        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(Path.Combine("memory://site", "a", "b", "c.md"), bundle.DisplayPath(problem));
        Assert.Throws<ArgumentNullException>(() => bundle.DisplayPath(null!));
    }
}
`````

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~SiteModelLocationTests" -nologo -v q`
Expected: the build fails with `error CS1061: 'Bundle' does not contain a definition for 'DisplayPath'` (two sites).

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Viewer/SiteModel.cs`, find:

`````csharp
                Link: page.RelativeHtmlPath,
                Description: concept.Document.Frontmatter.Description ?? string.Empty));
        }

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList())
        {
            Index = index,
            BundleName = BundleNameOf(bundle.Root),
            GraphPagePath = FreeGraphPagePath(pages),
        };
    }

`````

and replace it with:

`````csharp
                Link: page.RelativeHtmlPath,
                Description: concept.Document.Frontmatter.Description ?? string.Empty));
        }

        // The location and the §8 display path of each problem (spec §9, Viewer row): the
        // same strings the path-based members projected, so the generated HTML does not change.
        return new ViewerSite(
            bundle.Location,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseProblems.Select(p => new ViewerParseError(bundle.DisplayPath(p), p.Error)).ToList())
        {
            Index = index,
            BundleName = BundleNameOf(bundle.Location),
            GraphPagePath = FreeGraphPagePath(pages),
        };
    }

`````

In `src/OKF4net.Viewer/ViewerModel.cs`, find:

`````csharp
/// <param name="Exists">Whether the target concept exists in the bundle.</param>
public sealed record ViewerLink(string RawTarget, string Href, bool Exists);

/// <summary>One unparseable file, surfaced on the generated index page.</summary>
/// <param name="Path">The offending file's path.</param>
/// <param name="Error">The parse error reported by <see cref="Bundle"/>.</param>
public sealed record ViewerParseError(string Path, string Error);

/// <summary>One generated concept page.</summary>
`````

and replace it with:

`````csharp
/// <param name="Exists">Whether the target concept exists in the bundle.</param>
public sealed record ViewerLink(string RawTarget, string Href, bool Exists);

/// <summary>One unparseable file, surfaced on the generated index page.</summary>
/// <param name="Path">The offending file's display path (<see cref="Bundle.DisplayPath"/>: the location, then the name with the OS separator).</param>
/// <param name="Error">The parse error reported by <see cref="Bundle"/>.</param>
public sealed record ViewerParseError(string Path, string Error);

/// <summary>One generated concept page.</summary>
`````

In `src/OKF4net.Viewer/ViewerModel.cs`, find:

`````csharp
    public ViewerPageHead? Head { get; init; }
}

/// <summary>The whole generated site, as a pure model.</summary>
/// <param name="BundleRoot">The source bundle's root directory.</param>
/// <param name="Pages">One entry per concept.</param>
/// <param name="IndexMarkdown">The index page's markdown, rendered by the same client-side path as concept bodies.</param>
/// <param name="ParseErrors">Files the bundle could not parse.</param>
public sealed record ViewerSite(
`````

and replace it with:

`````csharp
    public ViewerPageHead? Head { get; init; }
}

/// <summary>The whole generated site, as a pure model.</summary>
/// <param name="BundleRoot">The source bundle's displayable location (<c>Bundle.Location</c>: the root directory of a filesystem bundle, as given).</param>
/// <param name="Pages">One entry per concept.</param>
/// <param name="IndexMarkdown">The index page's markdown, rendered by the same client-side path as concept bodies.</param>
/// <param name="ParseErrors">Files the bundle could not parse.</param>
public sealed record ViewerSite(
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp

        return System.IO.Path.Combine(location, problem.RawName.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Loads a bundle from a directory tree through the default
    /// <see cref="FileSystemBundleProvider"/> (spec §8).
    ///
`````

and replace it with:

`````csharp

        return System.IO.Path.Combine(location, problem.RawName.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The display path of one of this bundle's <see cref="ParseProblems"/> (spec §8): the
    /// <see cref="Location"/>, then the key's segments joined by the OS separator when the name
    /// is a key, else the raw name as listed with <c>/</c> turned into the OS separator — the
    /// same string the loader printed for an unparseable file before the provider seam. The one
    /// text every output (the CLI, its JSON, the viewer's index page) prints for a problem; it
    /// escapes nothing, that is each output's own job.
    /// </summary>
    /// <param name="problem">One of <see cref="ParseProblems"/> (or any problem to display under this bundle's location).</param>
    public string DisplayPath(BundleParseError problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return DisplayPathOf(Location, problem);
    }

    /// <summary>
    /// Loads a bundle from a directory tree through the default
    /// <see cref="FileSystemBundleProvider"/> (spec §8).
    ///
`````

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~SiteModelLocationTests|FullyQualifiedName~OKF4net.Tests.Viewer" -nologo -v q`
Expected: 0 failed; `SiteModelLocationTests` 2 passed (the Viewer group gains those 2).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Bundle.cs src/OKF4net.Viewer/SiteModel.cs src/OKF4net.Viewer/ViewerModel.cs tests/OKF4net.Tests/Viewer/SiteModelLocationTests.cs
git commit -m "feat(viewer): SiteModel reads Bundle.Location and the display path of each load problem

Bundle.DisplayPath(BundleParseError) is the one public text of a load problem's display path (spec §8).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (written in Task 10): the `### Added` bullet "**`Bundle.DisplayPath(BundleParseError)`** (#139, lot D; spec §8) …".

### Task 2: `okf info`, its JSON and the validator read keys and the display path

The three remaining readers of the path-based members in `src/OKF4net` and `src/OKF4net.Cli` move onto `Location`, `IndexKeys`/`LogKeys`, `ParseProblems` + `DisplayPath` and `ConceptId.FromKey`. A behaviour-preserving refactor: the strings are the same by construction, so the pinning test is green before and after — the red it gives is Task 9's obsolete warning. The golden snapshots and `MachineOutputTests` pin the bytes.

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs:850`, `:858-859`, `:881-887`
- Modify: `src/OKF4net.Cli/JsonOutput.cs:164-173`
- Modify: `src/OKF4net/Validate.cs:348-356`, `:838-846`, `:878-879`, `:907`, `:957-958`
- Test: `tests/OKF4net.Tests/CliDisplayPathTests.cs` (one Fact added before `A_rejected_name_keeps_its_unquoted_display_path_on_every_os`)

**Interfaces:**
- Consumes: Task 1's `Bundle.DisplayPath`; lot A's `IndexKeys`, `LogKeys`, `ParseProblems`, `BundleDisplayPath.Compose`, `ConceptId.FromKey(BundleKey)`.
- Produces: nothing new; `CheckIndexEntryDescriptions(Bundle, BundleKey indexKey, string indexPath, string body, List<Diagnostic>)` is private.

- [ ] **Step 1: Write the pinning test**

In `tests/OKF4net.Tests/CliDisplayPathTests.cs`, find:

`````csharp
        var jsonEscaped = expectedOrders.Replace("\\", "\\\\");
        Assert.Contains("\"path\":\"" + jsonEscaped + "\"", audit.Out);
    }

    /// <summary>
    /// The display path of a name the loader rejects is the one <c>dev</c> printed before the
    /// provider seam: <c>Path.Combine(root, …)</c> over the name as found on disk, unquoted —
    /// in <see cref="Bundle.ParseErrors"/>, in <see cref="Diagnostic.Path"/>, in
`````

and replace it with:

`````csharp
        var jsonEscaped = expectedOrders.Replace("\\", "\\\\");
        Assert.Contains("\"path\":\"" + jsonEscaped + "\"", audit.Out);
    }

    /// <summary>
    /// The readers that moved from the path-based members to the keys (lot D): <c>okf info</c>
    /// and <c>info --json</c> count <c>IndexKeys</c>/<c>LogKeys</c> and print each load problem
    /// under <see cref="Bundle.DisplayPath"/>; <c>validate</c> prints an <c>index.md</c> and a
    /// <c>log.md</c> finding under the §8 display path composed from the key. All unnormalised:
    /// the OS separator, byte for byte what the path-based members projected.
    /// </summary>
    [Fact]
    public void Info_json_and_the_reserved_file_findings_print_the_display_path_composed_from_the_key()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "# Index\n\n- [sub](sub/index.md)\n");
        tmp.Write("sub/index.md", "---\nokf_version: 0.2\n---\n# Sub\n");
        tmp.Write("sub/log.md", "## not-a-date\n\n- x\n");
        tmp.Write("sub/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n");
        tmp.Write("sub/a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\nA.\n");
        var expectedIndex = Path.Combine(tmp.Path, "sub", "index.md");
        var expectedLog = Path.Combine(tmp.Path, "sub", "log.md");
        var expectedBroken = Path.Combine(tmp.Path, "sub", "broken.md");

        var validate = Run("validate", tmp.Path);
        Assert.Contains("[error] " + expectedIndex + ": index.md must not contain frontmatter (§8)", validate.Out);
        Assert.Contains("[error] " + expectedLog + ": log date heading is not ISO-8601", validate.Out);
        Assert.Contains("[error] " + expectedBroken + ": unparseable concept document:", validate.Out);

        var info = Run("info", tmp.Path);
        Assert.Contains("index.md:   2\n", info.Out);
        Assert.Contains("log.md:     1\n", info.Out);
        Assert.Contains("unparseable files:\n  " + expectedBroken + ": ", info.Out);

        var json = Run("info", tmp.Path, "--json");
        Assert.Equal(0, json.Code);
        Assert.Contains("\"indexFileCount\":2", json.Out);
        Assert.Contains("\"logFileCount\":1", json.Out);
        Assert.Contains("\"path\":\"" + expectedBroken.Replace("\\", "\\\\") + "\"", json.Out);
    }

    /// <summary>
    /// The display path of a name the loader rejects is the one <c>dev</c> printed before the
    /// provider seam: <c>Path.Combine(root, …)</c> over the name as found on disk, unquoted —
    /// in <see cref="Bundle.ParseErrors"/>, in <see cref="Diagnostic.Path"/>, in
`````

- [ ] **Step 2: Run it**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CliDisplayPathTests" -nologo -v q`
Expected: 0 failed (the test pins the strings the refactor must preserve; it is green on the base as well — the obsolete members are the same strings).

- [ ] **Step 3: Move the readers onto the keys**

In `src/OKF4net.Cli/JsonOutput.cs`, find:

`````csharp
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        var parseErrors = bundle.ParseErrors
            .Select(pe => new ParseErrorJson(pe.Path, pe.Error))
            .ToList();

        var result = new InfoJsonResult(
            bundlePath,
            bundle.OkfVersion,
            bundle.Count,
            bundle.IndexFiles.Count,
            bundle.LogFiles.Count,
            byType,
            totalLinks,
            bundle.BrokenLinks().Count,
            parseErrors);
`````

and replace it with:

`````csharp
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        var parseErrors = bundle.ParseProblems
            .Select(p => new ParseErrorJson(bundle.DisplayPath(p), p.Error))
            .ToList();

        var result = new InfoJsonResult(
            bundlePath,
            bundle.OkfVersion,
            bundle.Count,
            bundle.IndexKeys.Count,
            bundle.LogKeys.Count,
            byType,
            totalLinks,
            bundle.BrokenLinks().Count,
            parseErrors);
`````

In `src/OKF4net.Cli/OkfCli.cs`, find:

`````csharp
            JsonOutput.WriteInfo(stdout, path, bundle);
            return 0;
        }

        stdout.Write($"bundle:     {bundle.Root}\n");
        var okfVersion = bundle.OkfVersion;
        if (okfVersion is not null)
        {
            stdout.Write($"okf_version: {okfVersion}\n");
        }

        stdout.Write($"concepts:   {bundle.Count}\n");
        stdout.Write($"index.md:   {bundle.IndexFiles.Count}\n");
        stdout.Write($"log.md:     {bundle.LogFiles.Count}\n");

        var byType = JsonOutput.BuildTypeHistogram(bundle);

        if (byType.Count > 0)
`````

and replace it with:

`````csharp
            JsonOutput.WriteInfo(stdout, path, bundle);
            return 0;
        }

        stdout.Write($"bundle:     {bundle.Location}\n");
        var okfVersion = bundle.OkfVersion;
        if (okfVersion is not null)
        {
            stdout.Write($"okf_version: {okfVersion}\n");
        }

        stdout.Write($"concepts:   {bundle.Count}\n");
        stdout.Write($"index.md:   {bundle.IndexKeys.Count}\n");
        stdout.Write($"log.md:     {bundle.LogKeys.Count}\n");

        var byType = JsonOutput.BuildTypeHistogram(bundle);

        if (byType.Count > 0)
`````

In `src/OKF4net.Cli/OkfCli.cs`, find:

`````csharp
        }

        stdout.Write($"\nlinks:      {totalLinks} internal ({broken.Count} broken)\n");

        if (bundle.ParseErrors.Count > 0)
        {
            stdout.Write("\nunparseable files:\n");
            foreach (var (p, e) in bundle.ParseErrors)
            {
                stdout.Write($"  {p}: {e}\n");
            }
        }

        return 0;
`````

and replace it with:

`````csharp
        }

        stdout.Write($"\nlinks:      {totalLinks} internal ({broken.Count} broken)\n");

        if (bundle.ParseProblems.Count > 0)
        {
            stdout.Write("\nunparseable files:\n");
            foreach (var problem in bundle.ParseProblems)
            {
                stdout.Write($"  {bundle.DisplayPath(problem)}: {problem.Error}\n");
            }
        }

        return 0;
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
        var now = (clock ?? new SystemClock()).Now;
        var diagnostics = new List<Diagnostic>();

        // (1) Files whose frontmatter could not be parsed are conformance errors.
        foreach (var (path, error) in bundle.ParseErrors)
        {
            diagnostics.Add(new Diagnostic(
                Severity.Error,
                path,
                null,
                $"unparseable concept document: {error}",
                DiagnosticCode.UnparseableDocument));
        }

        // (1b) Names the load accepted but the portable profile would refuse (spec §2,
`````

and replace it with:

`````csharp
        var now = (clock ?? new SystemClock()).Now;
        var diagnostics = new List<Diagnostic>();

        // (1) Files whose frontmatter could not be parsed are conformance errors.
        foreach (var problem in bundle.ParseProblems)
        {
            diagnostics.Add(new Diagnostic(
                Severity.Error,
                bundle.DisplayPath(problem),
                null,
                $"unparseable concept document: {problem.Error}",
                DiagnosticCode.UnparseableDocument));
        }

        // (1b) Names the load accepted but the portable profile would refuse (spec §2,
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
    /// <see cref="ConceptLink.Resolve"/> with the index's own location as the source,
    /// so a relative entry resolves from the index's directory and a <c>/</c> entry
    /// from the bundle root — no second implementation of link resolution.
    /// </summary>
    private static void CheckIndexEntryDescriptions(Bundle bundle, string indexPath, string body, List<Diagnostic> diagnostics)
    {
        ConceptId source;
        try
        {
            // `metrics/index.md` becomes `metrics/index`, whose parent is `metrics` --
            // the directory a relative entry resolves against.
            source = ConceptId.FromPath(bundle.Root, indexPath);
        }
        catch (ConceptIdException)
        {
            return;
`````

and replace it with:

`````csharp
    /// <see cref="ConceptLink.Resolve"/> with the index's own location as the source,
    /// so a relative entry resolves from the index's directory and a <c>/</c> entry
    /// from the bundle root — no second implementation of link resolution.
    /// </summary>
    private static void CheckIndexEntryDescriptions(Bundle bundle, BundleKey indexKey, string indexPath, string body, List<Diagnostic> diagnostics)
    {
        ConceptId source;
        try
        {
            // `metrics/index.md` becomes `metrics/index`, whose parent is `metrics` --
            // the directory a relative entry resolves against. From the key, as the
            // display path was derived from it (spec §9): the same segments, validated the same way.
            source = ConceptId.FromKey(indexKey);
        }
        catch (ConceptIdException)
        {
            return;
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
    {
        for (var i = 0; i < bundle.IndexKeys.Count; i++)
        {
            var key = bundle.IndexKeys[i];
            var path = bundle.IndexFiles[i];
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
`````

and replace it with:

`````csharp
    {
        for (var i = 0; i < bundle.IndexKeys.Count; i++)
        {
            var key = bundle.IndexKeys[i];
            var path = BundleDisplayPath.Compose(bundle.Location, key);
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
            // below. That early-return used to be the first thing after parsing, and a
            // well-formed index has no frontmatter at all -- so the body of every
            // conformant index was skipped, and an entry could omit its description
            // with nothing to say so.
            CheckIndexEntryDescriptions(bundle, path, doc.Body, diagnostics);
            CheckIndexEntriesAreLinks(path, doc.Body, diagnostics);

            if (doc.Frontmatter.IsEmpty)
            {
`````

and replace it with:

`````csharp
            // below. That early-return used to be the first thing after parsing, and a
            // well-formed index has no frontmatter at all -- so the body of every
            // conformant index was skipped, and an entry could omit its description
            // with nothing to say so.
            CheckIndexEntryDescriptions(bundle, key, path, doc.Body, diagnostics);
            CheckIndexEntriesAreLinks(path, doc.Body, diagnostics);

            if (doc.Frontmatter.IsEmpty)
            {
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp

        for (var i = 0; i < bundle.LogKeys.Count; i++)
        {
            var key = bundle.LogKeys[i];
            var path = bundle.LogFiles[i];
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
`````

and replace it with:

`````csharp

        for (var i = 0; i < bundle.LogKeys.Count; i++)
        {
            var key = bundle.LogKeys[i];
            var path = BundleDisplayPath.Compose(bundle.Location, key);
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
`````

- [ ] **Step 4: Run the CLI, golden, validator and machine-output groups**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CliDisplayPathTests|FullyQualifiedName~GoldenParityTests|FullyQualifiedName~ValidateTests|FullyQualifiedName~MachineOutputTests|FullyQualifiedName~CliTests" -nologo -v q`
Expected: `Passed: 415, Skipped: 1, Total: 416` (the base's 414 + the new Fact).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Cli/OkfCli.cs src/OKF4net.Cli/JsonOutput.cs src/OKF4net/Validate.cs tests/OKF4net.Tests/CliDisplayPathTests.cs
git commit -m "refactor(cli): okf info, its JSON and the validator read keys and the display path, not the path-based members

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (Task 10): the `### Changed` bullet "**`okf info`, `okf info --json`, `okf validate` and the viewer read the keys and the display path** …".

### Task 3: `OkfBundleKnowledgeSource` serves its image from a `BundleHandle`

**Files:**
- Modify: `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs` (whole file replaced)
- Modify: `src/OKF4net.Catalog/KnowledgePassage.cs:24-27`
- Test: `tests/OKF4net.Tests/Catalog/OkfBundleKnowledgeSourceTests.cs` (header, six tests, one assertion)

**Interfaces:**
- Consumes: lot C's `BundleHandle(IBundleProvider, BundleLoadOptions?, BundleRefreshPolicy?, TimeProvider?)`, `Current`, `Refresh()`, `IsStale`, `RefreshPolicy`, `Location`; `BundleRefreshPolicy.OnAccess`, `BundleRefreshMode`; `FileSystemBundleProvider(root)` with `Root`, `ConfinementBoundary`; `Concept.Key`; the test project's `InMemoryBundleProvider` (`Calls`, `FailEnumerateWith`).
- Produces: `public OkfBundleKnowledgeSource(string id, BundleHandle handle)`, `public BundleHandle Handle { get; }`, `internal static string RelativePathOf(Bundle bundle, Concept concept)` (used by Task 7's memory store). The directory constructor builds its own `OnAccess` handle.

- [ ] **Step 1: Write the failing tests**

In `tests/OKF4net.Tests/Catalog/OkfBundleKnowledgeSourceTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="OkfBundleKnowledgeSource"/>: stateless per-call loading,
/// mapping of <see cref="ScoredConcept"/> to <see cref="KnowledgePassage"/>,
/// scoring/ordering parity with the core <see cref="ConceptSearch"/>, and
/// never-throws behaviour on a bad bundle directory.
/// </summary>
public class OkfBundleKnowledgeSourceTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="OkfBundleKnowledgeSource"/>: one image served from a <see cref="BundleHandle"/>
/// under its refresh policy (spec §5.2, §9), mapping of <see cref="ScoredConcept"/> to
/// <see cref="KnowledgePassage"/>, scoring/ordering parity with the core
/// <see cref="ConceptSearch"/>, and never-throws behaviour on a bad bundle directory.
/// </summary>
public class OkfBundleKnowledgeSourceTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private const string Doc = "---\ntype: Note\ntitle: Orders\ndescription: d\n---\n\nOrders and refunds.\n";

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    // ---- The image comes from the handle (lot D) ---------------------------

    [Fact]
    public async Task Two_searches_over_an_unchanged_bundle_load_it_once_under_OnAccess()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);

        var first = await source.SearchAsync(new KnowledgeQuery("orders"));
        var second = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Single(first.Passages);
        Assert.Single(second.Passages);
        Assert.Equal(1, Loads(storage));
        Assert.Same(handle, source.Handle);
    }

    [Fact]
    public async Task A_change_in_storage_is_seen_at_the_next_search_under_OnAccess()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);

        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        storage.Add("b.md", Doc);

        var after = await source.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, after.Passages.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public async Task Under_Manual_the_image_is_served_until_the_host_refreshes()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage);
        var source = new OkfBundleKnowledgeSource("k", handle);

        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        storage.Add("b.md", Doc);
        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        handle.Refresh();
        Assert.Equal(2, (await source.SearchAsync(new KnowledgeQuery("orders"))).Passages.Count);
    }

    [Fact]
    public async Task A_failed_reload_keeps_the_previous_image_served_and_a_failed_first_load_is_SourceUnavailable()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);
        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        storage.Add("b.md", Doc);
        storage.FailEnumerateWith = new IOException("storage down");
        var stale = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Null(stale.Diagnostic);
        Assert.Single(stale.Passages);
        Assert.True(handle.IsStale);

        var broken = new InMemoryBundleProvider(id: "never") { FailEnumerateWith = new IOException("storage down") };
        using var neverLoaded = new BundleHandle(broken);
        var result = await new OkfBundleKnowledgeSource("never", neverLoaded).SearchAsync(new KnowledgeQuery("orders"));
        Assert.Empty(result.Passages);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, result.Diagnostic!.Code);
        Assert.Equal("never", result.Diagnostic.SourceId);
        Assert.Contains("bundle at 'memory://never' could not be loaded", result.Diagnostic.Message);
    }

    [Fact]
    public void The_directory_constructor_builds_its_own_OnAccess_handle_over_the_filesystem()
    {
        using var tmp = new TempDir();
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        Assert.Equal(BundleRefreshMode.OnAccess, source.Handle.RefreshPolicy.Mode);
        var provider = Assert.IsType<FileSystemBundleProvider>(source.Handle.Provider);
        Assert.Equal(tmp.Path, provider.Root);
        Assert.Null(provider.ConfinementBoundary);
        Assert.NotSame(source.Handle, new OkfBundleKnowledgeSource("other", tmp.Path).Handle);
        Assert.Throws<ArgumentNullException>(() => new OkfBundleKnowledgeSource("x", (BundleHandle)null!));
    }

    [Fact]
    public void BundleRelativePath_is_the_key_or_the_relativised_path_of_a_hand_built_concept()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("tables/orders.md", Doc);
        var bundle = Bundle.Load(storage);
        var loaded = Assert.Single(bundle.Concepts);
        Assert.Equal("tables/orders.md", OkfBundleKnowledgeSource.RelativePathOf(bundle, loaded));

        var handBuilt = new Concept(loaded.Id, Path.Combine(bundle.Location, "tables", "orders.md"), loaded.Document);
        Assert.Null(handBuilt.Key);
        Assert.Equal("tables/orders.md", OkfBundleKnowledgeSource.RelativePathOf(bundle, handBuilt));
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
`````

In `tests/OKF4net.Tests/Catalog/OkfBundleKnowledgeSourceTests.cs`, find:

`````csharp
            Assert.Equal(e.Concept.Id.ToString(), p.ConceptId);
            Assert.Equal(e.Concept.Document.Frontmatter.Title, p.Title);
            Assert.Equal(e.Score, p.Score);
            Assert.Equal(ConceptSearch.Excerpt(e.Concept.Document.Body, query.Text) ?? string.Empty, p.Excerpt);
            Assert.Equal(Path.GetRelativePath(bundle.Root, e.Concept.Path).Replace(Path.DirectorySeparatorChar, '/'), p.BundleRelativePath);
        }

        // The scorer's own contract: descending score, ties broken by ascending concept id --
        // assert the passage order itself is non-increasing in score as an extra sanity check.
`````

and replace it with:

`````csharp
            Assert.Equal(e.Concept.Id.ToString(), p.ConceptId);
            Assert.Equal(e.Concept.Document.Frontmatter.Title, p.Title);
            Assert.Equal(e.Score, p.Score);
            Assert.Equal(ConceptSearch.Excerpt(e.Concept.Document.Body, query.Text) ?? string.Empty, p.Excerpt);
            Assert.Equal(e.Concept.Key!.ToString(), p.BundleRelativePath);
        }

        // The scorer's own contract: descending score, ties broken by ascending concept id --
        // assert the passage order itself is non-increasing in score as an extra sanity check.
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OkfBundleKnowledgeSourceTests" -nologo -v q`
Expected: the build fails: `error CS1503` (no constructor takes a `BundleHandle`), `error CS1061: 'OkfBundleKnowledgeSource' does not contain a definition for 'Handle'` and `'RelativePathOf'`.

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Catalog/KnowledgePassage.cs`, find:

`````csharp
/// body line, or the empty string if the core scorer found none.
/// </param>
/// <param name="Score">The core scorer's relevance score for this concept (higher is more relevant).</param>
/// <param name="BundleRelativePath">
/// The concept file's path relative to its bundle's root directory (the
/// absolute <c>Concept.Path</c> relativized against <c>Bundle.Root</c>).
/// </param>
/// <param name="TrustTier">
/// The matching concept's derived trust tier (<c>Frontmatter.TrustTier</c>,
/// §5.3) -- defaults to <see cref="OKF4net.TrustTier.Unverified"/> so
`````

and replace it with:

`````csharp
/// body line, or the empty string if the core scorer found none.
/// </param>
/// <param name="Score">The core scorer's relevance score for this concept (higher is more relevant).</param>
/// <param name="BundleRelativePath">
/// The concept file's path relative to its bundle's root: its key
/// (<c>Concept.Key</c>), '/'-separated on every OS.
/// </param>
/// <param name="TrustTier">
/// The matching concept's derived trust tier (<c>Frontmatter.TrustTier</c>,
/// §5.3) -- defaults to <see cref="OKF4net.TrustTier.Unverified"/> so
`````

In `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// An <see cref="IKnowledgeSource"/> over a single, already-resolved OKF
/// bundle directory on disk.
/// </summary>
/// <remarks>
/// <b>Stateless by design.</b> Each <see cref="SearchAsync"/> call performs
/// its own <see cref="Bundle.Load(string)"/> (permissive, §11) and searches
/// via the core <see cref="ConceptSearch.Search"/> -- the same scorer
/// <c>okf search</c>/<c>OkfBundleTools.Search</c> use, so ranking and scoring
/// are identical by construction rather than by parallel re-implementation.
/// Loading per call is simple and correct but not free; a bundle cache with
/// its own invalidation policy is an explicit V2 concern and is deliberately
/// not attempted here (mirrors <see cref="IKnowledgeCatalog.ReloadAsync"/>
/// being "the reliable path" rather than relying on implicit caching).
/// </remarks>
public sealed class OkfBundleKnowledgeSource : IKnowledgeSource
{
    private readonly string _bundleDirectory;

    /// <summary>
    /// Creates a source over <paramref name="bundleDirectory"/>, an already
    /// safety-validated, resolved absolute directory (see
    /// <see cref="CatalogPathResolver.TryResolve"/>) -- this type does not
    /// itself re-derive or re-validate a path from a manifest-relative one.
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="bundleDirectory">The resolved, absolute bundle root directory to load and search.</param>
    public OkfBundleKnowledgeSource(string id, string bundleDirectory)
    {
        Id = id;
        _bundleDirectory = bundleDirectory;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// A null/blank <see cref="KnowledgeQuery.Text"/> is handled defensively
    /// here, before it ever reaches <see cref="ConceptSearch.Search"/>: the
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// An <see cref="IKnowledgeSource"/> over one OKF bundle, served from a
/// <see cref="BundleHandle"/> (spec §5.2, §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>One image, refreshed under the handle's policy.</b> Each <see cref="SearchAsync"/>
/// call reads <see cref="BundleHandle.Current"/> and searches it with the core
/// <see cref="ConceptSearch.Search"/> -- the same scorer <c>okf search</c>/<c>OkfBundleTools.Search</c>
/// use, so ranking and scoring are identical by construction rather than by parallel
/// re-implementation. The handle decides when the image is reloaded: a catalog source
/// is served from the handle its catalog keeps across requests (the snapshot's handles,
/// <see cref="BundleRefreshPolicy.OnAccess"/> by default), so an unchanged bundle is
/// loaded once and a changed one is seen at the next search. A reload that fails keeps
/// the previous image served (<see cref="BundleHandle.IsStale"/>); only a bundle that
/// never loaded is reported as <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>.
/// </para>
/// <para>
/// <b>The sharing is explicit.</b> The directory constructor builds a handle of its own
/// over a <see cref="FileSystemBundleProvider"/>, under <see cref="BundleRefreshPolicy.OnAccess"/>
/// (the policy of a catalog source: the bundle's token is compared at every search, so an
/// external change is still seen, where the source used to reload at every call). Two
/// sources built that way over one directory have two images; a host that wants one image
/// per source passes the same <see cref="BundleHandle"/> to both.
/// </para>
/// </remarks>
public sealed class OkfBundleKnowledgeSource : IKnowledgeSource
{
    /// <summary>
    /// Creates a source over <paramref name="bundleDirectory"/>, an already
    /// safety-validated, resolved absolute directory (see
    /// <see cref="CatalogPathResolver.TryResolve"/>) -- this type does not
    /// itself re-derive or re-validate a path from a manifest-relative one.
    /// The source owns a handle of its own over that directory, refreshed on
    /// access (see the type remarks).
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="bundleDirectory">The resolved, absolute bundle root directory to load and search.</param>
    public OkfBundleKnowledgeSource(string id, string bundleDirectory)
        : this(id, new BundleHandle(new FileSystemBundleProvider(bundleDirectory), refreshPolicy: BundleRefreshPolicy.OnAccess))
    {
    }

    /// <summary>
    /// Creates a source served from <paramref name="handle"/>, whose image, options and
    /// refresh policy it shares with every other consumer of that handle.
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="handle">The handle the bundle is read from.</param>
    public OkfBundleKnowledgeSource(string id, BundleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(handle);
        Id = id;
        Handle = handle;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <summary>The handle the bundle is read from.</summary>
    public BundleHandle Handle { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// A null/blank <see cref="KnowledgeQuery.Text"/> is handled defensively
    /// here, before it ever reaches <see cref="ConceptSearch.Search"/>: the
`````

In `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs`, find:

`````csharp

        Bundle bundle;
        try
        {
            bundle = Bundle.Load(_bundleDirectory);
        }
        catch (OkfException e)
        {
            return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(
                Array.Empty<KnowledgePassage>(),
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{_bundleDirectory}' could not be loaded: {e.Message}")));
        }

        var scored = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);
`````

and replace it with:

`````csharp

        Bundle bundle;
        try
        {
            bundle = Handle.Current;
        }
        catch (OkfException e)
        {
            return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(
                Array.Empty<KnowledgePassage>(),
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{Handle.Location}' could not be loaded: {e.Message}")));
        }

        var scored = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);
`````

In `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs`, find:

`````csharp
                    ConceptId: hit.Concept.Id.ToString(),
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    // Normalized to '/' regardless of OS -- matches ConceptId's
                    // '/' segment convention and travels correctly for a future
                    // <okf-context> adapter, rather than leaking a Windows
                    // backslash-separated path into cross-platform output.
                    BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path).Replace(Path.DirectorySeparatorChar, '/'),
                    TrustTier: fm.TrustTier,
                    Lifecycle: lc);
            })
            .ToList();
`````

and replace it with:

`````csharp
                    ConceptId: hit.Concept.Id.ToString(),
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: RelativePathOf(bundle, hit.Concept),
                    TrustTier: fm.TrustTier,
                    Lifecycle: lc);
            })
            .ToList();
`````

In `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs`, find:

`````csharp
        // KnowledgeSearchResult (same reasoning as KnowledgeContext's own
        // passages/diagnostics; see GroupedKnowledgeResolver).
        return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(passages.AsReadOnly(), null));
    }
}
`````

and replace it with:

`````csharp
        // KnowledgeSearchResult (same reasoning as KnowledgeContext's own
        // passages/diagnostics; see GroupedKnowledgeResolver).
        return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(passages.AsReadOnly(), null));
    }

    /// <summary>
    /// The passage's bundle-relative path: the concept's key, '/'-separated on every OS --
    /// the same text <c>Path.GetRelativePath(root, concept.Path)</c> gave with the separator
    /// normalised, which stays the fallback for a concept built by hand (no key).
    /// </summary>
    internal static string RelativePathOf(Bundle bundle, Concept concept) =>
        concept.Key is { } key
            ? key.ToString()
            : Path.GetRelativePath(bundle.Location, concept.Path).Replace(Path.DirectorySeparatorChar, '/');
}
`````

- [ ] **Step 4: Run the catalog group**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog" -nologo -v q`
Expected: `Passed: 243, Skipped: 1, Total: 244` (the base's 237 + 6).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs src/OKF4net.Catalog/KnowledgePassage.cs tests/OKF4net.Tests/Catalog/OkfBundleKnowledgeSourceTests.cs
git commit -m "feat(catalog): OkfBundleKnowledgeSource serves its image from a BundleHandle

The directory constructor builds its own handle under OnAccess; the passage's relative path is the concept's key.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entries (Task 10): `### Added` "**`OkfBundleKnowledgeSource(string id, BundleHandle handle)`** …"; `### Changed` "**`new OkfBundleKnowledgeSource(id, directory)` builds its own `OnAccess` handle** …".

### Task 4: A `catalog.json` source names its provider; hosts register provider factories

**Files:**
- Create: `src/OKF4net.Catalog/ProviderSettings.cs`, `src/OKF4net.Catalog/IBundleProviderFactory.cs`
- Modify: `src/OKF4net.Catalog/KnowledgeCatalogSource.cs` (whole file), `CatalogManifestParser.cs` (`:27-29`, `:183-201`, `:228-242`), `CatalogDiagnosticCode.cs:19-20`, `KnowledgeCatalogOptions.cs:36-43`, `FileKnowledgeCatalog.cs` (`:54-56`, `:72-82`, `:262-274`, `:358-362`)
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs:17`, `:89`; `KnowledgeServiceCollectionExtensions.cs:80-84`
- Test: `tests/OKF4net.Tests/Catalog/TestProviderFactory.cs` (create; reused by Tasks 5 and 6), `tests/OKF4net.Tests/Catalog/CatalogProviderTests.cs` (create)

**Interfaces:**
- Consumes: `IBundleProvider`; `KnowledgeCatalogSource(Id, Path, Priority, Enabled, Role, Tier)`; `CatalogManifestParser.TryParse`; `FileKnowledgeCatalog(KnowledgeCatalogOptions)`; Hosting's `KnowledgeOptions.AddCatalogFile`, `AddKnowledge`.
- Produces: `public sealed class ProviderSettings : IReadOnlyDictionary<string,string>, IEquatable<ProviderSettings>` (`Empty`, `From(IEnumerable<KeyValuePair<string,string>>)`); `public interface IBundleProviderFactory { string ProviderType; IReadOnlySet<string> AcceptedSettings; IBundleProvider Create(KnowledgeCatalogSource source); }`; on `KnowledgeCatalogSource`: `const string FileSystemProviderType = "filesystem"`, `string ProviderType { get; init; }`, `ProviderSettings Settings { get; init; }`, `bool IsFileSystem`; `CatalogDiagnosticCode.MalformedProvider`, `MalformedSettings`, `IllegalSetting`, `IllegalPath`, `IllegalMemoryProvider`, `UnknownProvider`, `ProviderUnavailable`; `KnowledgeCatalogOptions.ProviderFactories`; `KnowledgeOptions.AddProviderFactory(IBundleProviderFactory)`; the test helper `TestProviderFactory(string providerType, Func<KnowledgeCatalogSource, IBundleProvider>? create = null, params string[] acceptedSettings)` with `Created`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/CatalogProviderTests.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// A <c>catalog.json</c> source names its provider (spec §9): the <c>provider</c> and
/// <c>settings</c> fields of the manifest, their parse rules, the factories a host registers
/// (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>, <see cref="KnowledgeOptions.AddProviderFactory"/>)
/// and what the catalog refuses — an unknown type, a setting the factory does not accept, a
/// memory source off the filesystem — before any provider is created.
/// </summary>
public class CatalogProviderTests
{
    private static KnowledgeCatalogSnapshot Parse(string json)
    {
        Assert.True(CatalogManifestParser.TryParse(json, "/m", out var snapshot, out var diagnostics), string.Join("; ", diagnostics.Select(d => d.Message)));
        return snapshot!;
    }

    private static IReadOnlyList<CatalogDiagnostic> Reject(string json)
    {
        Assert.False(CatalogManifestParser.TryParse(json, "/m", out _, out var diagnostics));
        return diagnostics;
    }

    // ---- The parser --------------------------------------------------------

    [Fact]
    public void A_source_without_provider_is_a_filesystem_source_with_no_setting()
    {
        var source = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""").Sources);

        Assert.Equal(KnowledgeCatalogSource.FileSystemProviderType, source.ProviderType);
        Assert.True(source.IsFileSystem);
        Assert.Same(ProviderSettings.Empty, source.Settings);
        Assert.Equal("./a", source.Path);
    }

    [Fact]
    public void A_source_may_name_a_provider_and_its_settings_and_then_carries_no_path()
    {
        var source = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": "b", "prefix": "p/" } } ] }""").Sources);

        Assert.Equal("s3", source.ProviderType);
        Assert.False(source.IsFileSystem);
        Assert.Equal(string.Empty, source.Path);
        Assert.Equal(ProviderSettings.From([new("prefix", "p/"), new("bucket", "b")]), source.Settings);
        Assert.Equal(["bucket", "prefix"], source.Settings.Keys.ToList());
    }

    [Theory]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "" , "path": "./a" } ] }""", CatalogDiagnosticCode.MalformedProvider)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": 3, "path": "./a" } ] }""", CatalogDiagnosticCode.MalformedProvider)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": [ "bucket" ] } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": 1 } } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": "b", "bucket": "c" } } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "path": "./a", "settings": { "bucket": "b" } } ] }""", CatalogDiagnosticCode.IllegalSetting)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "path": "./a" } ] }""", CatalogDiagnosticCode.IllegalPath)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "role": "memory", "tier": "user" } ] }""", CatalogDiagnosticCode.IllegalMemoryProvider)]
    public void The_parser_rejects_a_malformed_or_illegal_provider_declaration(string json, CatalogDiagnosticCode expected)
    {
        var diagnostics = Reject(json);

        Assert.Contains(diagnostics, d => d.Code == expected);
    }

    [Fact]
    public void A_filesystem_source_still_needs_a_path_and_a_memory_source_may_say_filesystem()
    {
        Assert.Contains(Reject("""{ "version": 1, "sources": [ { "id": "a", "provider": "filesystem" } ] }"""), d => d.Code == CatalogDiagnosticCode.EmptyPath);

        var memory = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "m", "path": "./m", "role": "memory", "tier": "user", "provider": "filesystem" } ] }""").Sources);
        Assert.True(memory.IsFileSystem);
        Assert.Equal(SourceRole.Memory, memory.Role);
    }

    [Fact]
    public void ProviderSettings_compare_by_value_and_refuse_a_duplicate_name()
    {
        var a = ProviderSettings.From([new("x", "1"), new("y", "2")]);
        var b = ProviderSettings.From([new("y", "2"), new("x", "1")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, ProviderSettings.From([new("x", "1")]));
        Assert.Equal("1", a["x"]);
        Assert.False(a.TryGetValue("z", out _));
        Assert.Equal("x=1;y=2", a.ToString());
        Assert.Same(ProviderSettings.Empty, ProviderSettings.From([]));
        Assert.Throws<ArgumentException>(() => ProviderSettings.From([new("x", "1"), new("x", "2")]));
        Assert.Throws<KeyNotFoundException>(() => a["z"]);
    }

    // ---- The catalog -------------------------------------------------------

    private static (TempDir Root, string Manifest) CatalogWith(string json)
    {
        var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "docs"));
        return (root, root.Write("catalog.json", json));
    }

    private const string UnknownProviderJson = """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" }, { "id": "remote", "provider": "s3", "settings": { "bucket": "b" } } ] }""";

    [Fact]
    public void A_source_naming_a_provider_with_no_factory_is_refused_at_construction()
    {
        var (root, manifest) = CatalogWith(UnknownProviderJson);
        using var disposable = root;

        var error = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
        }));

        Assert.Contains("[UnknownProvider]", error.Message);
        Assert.Contains("'s3'", error.Message);
    }

    [Fact]
    public void A_registered_factory_accepts_the_source_and_a_setting_it_does_not_list_is_refused()
    {
        var (root, manifest) = CatalogWith(UnknownProviderJson);
        using var disposable = root;
        var accepting = new TestProviderFactory("s3", acceptedSettings: ["bucket", "prefix"]);
        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [accepting],
        };

        using var catalog = new FileKnowledgeCatalog(options);
        Assert.Equal(2, catalog.Current.Sources.Count);

        var refusing = new TestProviderFactory("s3", acceptedSettings: ["prefix"]);
        var error = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [refusing],
        }));
        Assert.Contains("[IllegalSetting]", error.Message);
        Assert.Contains("'bucket'", error.Message);
    }

    [Fact]
    public async Task A_reload_naming_an_unknown_provider_is_rejected_and_keeps_the_snapshot()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var before = catalog.Current;

        File.WriteAllText(manifest, UnknownProviderJson);
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        Assert.Contains(catalog.LastReloadDiagnostics, d => d.Code == CatalogDiagnosticCode.UnknownProvider);
    }

    [Fact]
    public void A_factory_for_the_built_in_type_or_a_duplicate_type_is_a_configuration_error()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        KnowledgeCatalogOptions Options(params IBundleProviderFactory[] factories) => new()
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
        };

        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory("filesystem"))));
        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory("s3"), new TestProviderFactory("s3"))));
        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory(string.Empty))));
        using var fine = new FileKnowledgeCatalog(Options(new TestProviderFactory("s3"), new TestProviderFactory("blob")));
    }

    // ---- Hosting -----------------------------------------------------------

    [Fact]
    public void AddProviderFactory_hands_the_factories_to_the_catalog_options()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        var factory = new TestProviderFactory("s3");
        var services = new ServiceCollection();

        services.AddKnowledge(o =>
        {
            o.AddCatalogFile(manifest);
            o.AddProviderFactory(factory);
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<KnowledgeCatalogOptions>();
        Assert.Same(factory, Assert.Single(options.ProviderFactories));
        Assert.Throws<ArgumentNullException>(() => new KnowledgeOptions().AddProviderFactory(null!));
    }
}
`````

Create `tests/OKF4net.Tests/Catalog/TestProviderFactory.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// An <see cref="IBundleProviderFactory"/> for the catalog tests: serves one provider type,
/// accepts the settings it is given, and creates providers through a callback (by default an
/// <see cref="InMemoryBundleProvider"/> named after the source's id), recording every source
/// it was asked to create a provider for.
/// </summary>
public sealed class TestProviderFactory(string providerType, Func<KnowledgeCatalogSource, IBundleProvider>? create = null, params string[] acceptedSettings) : IBundleProviderFactory
{
    private readonly List<KnowledgeCatalogSource> _created = [];

    public string ProviderType { get; } = providerType;

    public IReadOnlySet<string> AcceptedSettings { get; } = new HashSet<string>(acceptedSettings, StringComparer.Ordinal);

    /// <summary>Every source this factory created a provider for, in order.</summary>
    public IReadOnlyList<KnowledgeCatalogSource> Created => _created;

    public IBundleProvider Create(KnowledgeCatalogSource source)
    {
        _created.Add(source);
        return create is null ? new Tests.Providers.InMemoryBundleProvider(id: source.Id) : create(source);
    }
}
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CatalogProviderTests" -nologo -v q`
Expected: the build fails (`error CS0246: 'IBundleProviderFactory'`, `'ProviderSettings'`; `error CS1061` for `ProviderType`, `Settings`, `IsFileSystem`, `ProviderFactories`, `AddProviderFactory`; `error CS0117` for the new `CatalogDiagnosticCode` members).

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`, find:

`````csharp
/// </remarks>
public sealed class KnowledgeOptions
{
    private int _catalogFileCallCount;

    /// <summary>
    /// The <see cref="KnowledgeResolverStrategy"/> used for searches whose
    /// query leaves <see cref="KnowledgeQuery.ResolverStrategy"/> unset.
`````

and replace it with:

`````csharp
/// </remarks>
public sealed class KnowledgeOptions
{
    private int _catalogFileCallCount;
    private readonly List<IBundleProviderFactory> _providerFactories = [];

    /// <summary>
    /// The <see cref="KnowledgeResolverStrategy"/> used for searches whose
    /// query leaves <see cref="KnowledgeQuery.ResolverStrategy"/> unset.
`````

In `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`, find:

`````csharp
        CatalogFilePath = fullPath;
        CatalogRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
    }

    /// <summary>
    /// Validates that exactly one <see cref="AddCatalogFile"/> call was made.
    /// Called by <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>
    /// immediately after running the <c>configure</c> callback, so a
`````

and replace it with:

`````csharp
        CatalogFilePath = fullPath;
        CatalogRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
    }

    /// <summary>
    /// The provider factories registered by <see cref="AddProviderFactory"/>, in order,
    /// handed to <see cref="KnowledgeCatalogOptions.ProviderFactories"/>.
    /// </summary>
    internal IReadOnlyList<IBundleProviderFactory> ProviderFactories => _providerFactories;

    /// <summary>
    /// Registers a factory for the <c>provider</c> type it names
    /// (<see cref="IBundleProviderFactory.ProviderType"/>), so a <c>catalog.json</c> source may
    /// name that type. The credentials the provider needs are the factory's own (injected by
    /// the host that built it), never read from the manifest. The built-in <c>filesystem</c>
    /// type needs no factory and cannot be registered: <see cref="FileKnowledgeCatalog"/>
    /// refuses it, as it refuses two factories for one type, on first resolve.
    /// </summary>
    /// <param name="factory">The factory to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void AddProviderFactory(IBundleProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _providerFactories.Add(factory);
    }

    /// <summary>
    /// Validates that exactly one <see cref="AddCatalogFile"/> call was made.
    /// Called by <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>
    /// immediately after running the <c>configure</c> callback, so a
`````

In `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`, find:

`````csharp
        var catalogOptions = new KnowledgeCatalogOptions
        {
            CatalogFilePath = options.CatalogFilePath!,
            CatalogRoot = options.CatalogRoot!,
        };

        services.TryAddSingleton(catalogOptions);
        services.TryAddSingleton<IKnowledgeCatalog>(_ => new FileKnowledgeCatalog(catalogOptions));
`````

and replace it with:

`````csharp
        var catalogOptions = new KnowledgeCatalogOptions
        {
            CatalogFilePath = options.CatalogFilePath!,
            CatalogRoot = options.CatalogRoot!,
            ProviderFactories = [.. options.ProviderFactories],
        };

        services.TryAddSingleton(catalogOptions);
        services.TryAddSingleton<IKnowledgeCatalog>(_ => new FileKnowledgeCatalog(catalogOptions));
`````

In `src/OKF4net.Catalog/CatalogDiagnosticCode.cs`, find:

`````csharp

    /// <summary>The manifest root object contains a property other than <c>version</c> or <c>sources</c>.</summary>
    UnknownRootProperty,

    /// <summary>A source entry contains a property other than <c>id</c>, <c>path</c>, <c>priority</c>, <c>enabled</c>, <c>role</c>, or <c>tier</c>.</summary>
    UnknownSourceProperty,

    /// <summary>The root <c>version</c> property is missing, not a number, or not exactly <c>1</c>.</summary>
    WrongVersion,

    /// <summary>The root <c>sources</c> property is missing, not an array, or an empty array.</summary>
`````

and replace it with:

`````csharp

    /// <summary>The manifest root object contains a property other than <c>version</c> or <c>sources</c>.</summary>
    UnknownRootProperty,

    /// <summary>A source entry contains a property other than <c>id</c>, <c>path</c>, <c>priority</c>, <c>enabled</c>, <c>role</c>, <c>tier</c>, <c>provider</c>, or <c>settings</c>.</summary>
    UnknownSourceProperty,

    /// <summary>A source's <c>provider</c> is present but not a non-empty string.</summary>
    MalformedProvider,

    /// <summary>A source's <c>settings</c> is present but not a JSON object whose values are all strings, or names a setting twice.</summary>
    MalformedSettings,

    /// <summary>A source carries a setting its provider does not accept: any setting for the built-in <c>filesystem</c> provider, or a name the provider's factory does not list in <see cref="IBundleProviderFactory.AcceptedSettings"/>.</summary>
    IllegalSetting,

    /// <summary>A source whose <c>provider</c> is not <c>filesystem</c> carries a <c>path</c>, which names a directory only for the filesystem provider.</summary>
    IllegalPath,

    /// <summary>A <c>role:"memory"</c> source names a <c>provider</c> other than <c>filesystem</c>: memory stays on the filesystem (spec §9).</summary>
    IllegalMemoryProvider,

    /// <summary>A source names a <c>provider</c> for which the host registered no <see cref="IBundleProviderFactory"/> (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>).</summary>
    UnknownProvider,

    /// <summary>A source's provider could not be created: its factory's <see cref="IBundleProviderFactory.Create"/>, or the handle over the provider it returned, threw.</summary>
    ProviderUnavailable,

    /// <summary>The root <c>version</c> property is missing, not a number, or not exactly <c>1</c>.</summary>
    WrongVersion,

    /// <summary>The root <c>sources</c> property is missing, not an array, or an empty array.</summary>
`````

In `src/OKF4net.Catalog/CatalogManifestParser.cs`, find:

`````csharp
    private const string PriorityProperty = "priority";
    private const string EnabledProperty = "enabled";
    private const string RoleProperty = "role";
    private const string TierProperty = "tier";
    private const string KnowledgeRoleValue = "knowledge";
    private const string MemoryRoleValue = "memory";

    /// <summary>
`````

and replace it with:

`````csharp
    private const string PriorityProperty = "priority";
    private const string EnabledProperty = "enabled";
    private const string RoleProperty = "role";
    private const string TierProperty = "tier";
    private const string ProviderProperty = "provider";
    private const string SettingsProperty = "settings";
    private const string KnowledgeRoleValue = "knowledge";
    private const string MemoryRoleValue = "memory";

    /// <summary>
`````

In `src/OKF4net.Catalog/CatalogManifestParser.cs`, find:

`````csharp
    private static KnowledgeCatalogSource ParseSource(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name is not (IdProperty or PathProperty or PriorityProperty or EnabledProperty or RoleProperty or TierProperty))
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.UnknownSourceProperty,
                    $"Unknown source property '{property.Name}'."));
            }
        }

        var id = ParseId(source, seenIds, diags);
        var path = ParsePath(source, diags);
        var priority = ParsePriority(source, diags);
        var enabled = ParseEnabled(source, diags);
        var (role, roleWasValid) = ParseRole(source, diags);
        var tier = ParseTier(source, role, roleWasValid, diags);

        return new KnowledgeCatalogSource(id, path, priority, enabled, role, tier);
    }

    private static string ParseId(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
`````

and replace it with:

`````csharp
    private static KnowledgeCatalogSource ParseSource(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name is not (IdProperty or PathProperty or PriorityProperty or EnabledProperty or RoleProperty or TierProperty or ProviderProperty or SettingsProperty))
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.UnknownSourceProperty,
                    $"Unknown source property '{property.Name}'."));
            }
        }

        var id = ParseId(source, seenIds, diags);
        var (role, roleWasValid) = ParseRole(source, diags);
        var providerType = ParseProvider(source, role, roleWasValid, diags);
        var isFileSystem = string.Equals(providerType, KnowledgeCatalogSource.FileSystemProviderType, StringComparison.Ordinal);
        var path = ParsePath(source, isFileSystem, diags);
        var settings = ParseSettings(source, isFileSystem, diags);
        var priority = ParsePriority(source, diags);
        var enabled = ParseEnabled(source, diags);
        var tier = ParseTier(source, role, roleWasValid, diags);

        return new KnowledgeCatalogSource(id, path, priority, enabled, role, tier) { ProviderType = providerType, Settings = settings };
    }

    /// <summary>
    /// Parses the source's <c>provider</c> (spec §9): absent is the built-in
    /// <see cref="KnowledgeCatalogSource.FileSystemProviderType"/>; present, it must be a
    /// non-empty string, and a <c>role:"memory"</c> source may only name the filesystem
    /// (memory stays local). Whether a named type has a factory is the catalog's check,
    /// not the parser's, which knows no factory.
    /// </summary>
    private static string ParseProvider(JsonElement source, SourceRole role, bool roleWasValid, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(ProviderProperty, out var providerProperty))
        {
            return KnowledgeCatalogSource.FileSystemProviderType;
        }

        var value = providerProperty.ValueKind == JsonValueKind.String ? providerProperty.GetString() : null;
        if (string.IsNullOrEmpty(value))
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedProvider, "Source 'provider' must be a non-empty string."));
            return KnowledgeCatalogSource.FileSystemProviderType;
        }

        if (role == SourceRole.Memory && roleWasValid && !string.Equals(value, KnowledgeCatalogSource.FileSystemProviderType, StringComparison.Ordinal))
        {
            diags.Add(new CatalogDiagnostic(
                CatalogDiagnosticCode.IllegalMemoryProvider,
                $"A role:\"memory\" source must use the \"{KnowledgeCatalogSource.FileSystemProviderType}\" provider, not '{value}': memory stays on the local filesystem."));
        }

        return value;
    }

    /// <summary>
    /// Parses the source's <c>settings</c>: absent is <see cref="ProviderSettings.Empty"/>;
    /// present, it must be an object whose values are all strings, each name once. The
    /// built-in filesystem provider accepts none (its storage is named by <c>path</c>); which
    /// names another provider accepts is its factory's declaration, checked by the catalog.
    /// </summary>
    private static ProviderSettings ParseSettings(JsonElement source, bool isFileSystem, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(SettingsProperty, out var settingsProperty))
        {
            return ProviderSettings.Empty;
        }

        if (settingsProperty.ValueKind != JsonValueKind.Object)
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedSettings, "Source 'settings' must be a JSON object of string values."));
            return ProviderSettings.Empty;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settingsProperty.EnumerateObject())
        {
            if (setting.Value.ValueKind != JsonValueKind.String)
            {
                diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedSettings, $"Source setting '{setting.Name}' must be a string."));
                return ProviderSettings.Empty;
            }

            if (!names.Add(setting.Name))
            {
                diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedSettings, $"Source setting '{setting.Name}' appears twice."));
                return ProviderSettings.Empty;
            }

            pairs.Add(new KeyValuePair<string, string>(setting.Name, setting.Value.GetString() ?? string.Empty));
        }

        if (isFileSystem && pairs.Count > 0)
        {
            diags.Add(new CatalogDiagnostic(
                CatalogDiagnosticCode.IllegalSetting,
                $"The \"{KnowledgeCatalogSource.FileSystemProviderType}\" provider accepts no setting ('{pairs[0].Key}'); its bundle is named by 'path'."));
            return ProviderSettings.Empty;
        }

        return ProviderSettings.From(pairs);
    }

    private static string ParseId(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
`````

In `src/OKF4net.Catalog/CatalogManifestParser.cs`, find:

`````csharp

        return id;
    }

    private static string ParsePath(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (source.TryGetProperty(PathProperty, out var pathProperty) &&
            pathProperty.ValueKind == JsonValueKind.String)
        {
            var path = pathProperty.GetString() ?? string.Empty;
            if (path.Length > 0)
            {
`````

and replace it with:

`````csharp

        return id;
    }

    /// <summary>
    /// Parses the source's <c>path</c>: required, non-empty, for a filesystem source; refused
    /// on any other provider type, whose storage is named by <c>settings</c> (spec §9: the
    /// path "ne vaut plus que pour le type filesystem").
    /// </summary>
    private static string ParsePath(JsonElement source, bool isFileSystem, List<CatalogDiagnostic> diags)
    {
        var hasPath = source.TryGetProperty(PathProperty, out var pathProperty);
        if (!isFileSystem)
        {
            if (hasPath)
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.IllegalPath,
                    $"Source 'path' is only valid on a \"{KnowledgeCatalogSource.FileSystemProviderType}\" source; a provider's storage is named by its 'settings'."));
            }

            return string.Empty;
        }

        if (hasPath && pathProperty.ValueKind == JsonValueKind.String)
        {
            var path = pathProperty.GetString() ?? string.Empty;
            if (path.Length > 0)
            {
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
`````

and replace it with:

`````csharp
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Dictionary<string, IBundleProviderFactory> _factories;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
    /// The initial <see cref="KnowledgeCatalogOptions.CatalogFilePath"/> could
    /// not be read, parsed, or path-validated. The exception message
    /// aggregates every underlying <see cref="CatalogDiagnostic"/>.
    /// </exception>
    public FileKnowledgeCatalog(KnowledgeCatalogOptions options)
    {
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);

        if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
`````

and replace it with:

`````csharp
    /// The initial <see cref="KnowledgeCatalogOptions.CatalogFilePath"/> could
    /// not be read, parsed, or path-validated. The exception message
    /// aggregates every underlying <see cref="CatalogDiagnostic"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="KnowledgeCatalogOptions.ProviderFactories"/> holds two factories for one
    /// provider type, or one for the built-in <c>filesystem</c> type: a host configuration
    /// error, refused before any manifest is read.
    /// </exception>
    public FileKnowledgeCatalog(KnowledgeCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);
        _factories = IndexFactories(options.ProviderFactories);

        if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
            {
                continue;
            }

            if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out _, out var pathDiagnostic))
            {
                pathDiagnostics.Add(pathDiagnostic!);
            }
        }

        if (pathDiagnostics.Count > 0)
`````

and replace it with:

`````csharp
            {
                continue;
            }

            // A filesystem source's path is checked as before; another provider type needs a
            // registered factory that accepts every setting the source carries (spec §9).
            if (source.IsFileSystem)
            {
                if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out _, out var pathDiagnostic))
                {
                    pathDiagnostics.Add(pathDiagnostic!);
                }
            }
            else if (!_factories.TryGetValue(source.ProviderType, out var factory))
            {
                pathDiagnostics.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.UnknownProvider,
                    $"Source '{source.Id}' names the provider '{source.ProviderType}', for which no factory is registered (KnowledgeCatalogOptions.ProviderFactories)."));
            }
            else
            {
                foreach (var name in source.Settings.Keys)
                {
                    if (!factory.AcceptedSettings.Contains(name))
                    {
                        pathDiagnostics.Add(new CatalogDiagnostic(
                            CatalogDiagnosticCode.IllegalSetting,
                            $"Source '{source.Id}' carries the setting '{name}', which the '{source.ProviderType}' provider does not accept."));
                    }
                }
            }
        }

        if (pathDiagnostics.Count > 0)
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
        var full = Path.GetFullPath(catalogFilePath);
        return Path.GetDirectoryName(full) ?? full;
    }

    private static string FormatDiagnostics(IReadOnlyList<CatalogDiagnostic> diagnostics) =>
        "Invalid catalog manifest:" + string.Concat(diagnostics.Select(d => $"{Environment.NewLine}  [{d.Code}] {d.Message}"));
}
`````

and replace it with:

`````csharp
        var full = Path.GetFullPath(catalogFilePath);
        return Path.GetDirectoryName(full) ?? full;
    }

    /// <summary>The factories by provider type; a duplicate type or the built-in <c>filesystem</c> type is a configuration error.</summary>
    private static Dictionary<string, IBundleProviderFactory> IndexFactories(IReadOnlyList<IBundleProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        var indexed = new Dictionary<string, IBundleProviderFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory, nameof(factories));
            if (string.IsNullOrEmpty(factory.ProviderType))
            {
                throw new ArgumentException("a provider factory needs a non-empty ProviderType", nameof(factories));
            }

            if (string.Equals(factory.ProviderType, KnowledgeCatalogSource.FileSystemProviderType, StringComparison.Ordinal))
            {
                throw new ArgumentException($"the provider type '{KnowledgeCatalogSource.FileSystemProviderType}' is built in and cannot be registered", nameof(factories));
            }

            if (!indexed.TryAdd(factory.ProviderType, factory))
            {
                throw new ArgumentException($"two provider factories claim the type '{factory.ProviderType}'", nameof(factories));
            }
        }

        return indexed;
    }

    private static string FormatDiagnostics(IReadOnlyList<CatalogDiagnostic> diagnostics) =>
        "Invalid catalog manifest:" + string.Concat(diagnostics.Select(d => $"{Environment.NewLine}  [{d.Code}] {d.Message}"));
}
`````

Create `src/OKF4net.Catalog/IBundleProviderFactory.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Builds the <see cref="IBundleProvider"/> of a <c>catalog.json</c> source whose
/// <c>provider</c> names this factory's <see cref="ProviderType"/> (spec §9, "nommer un
/// provider dans catalog.json"). A host registers one factory per provider type it offers
/// (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>); the type <c>filesystem</c> is
/// built in and cannot be registered. Each factory declares the settings it accepts, so a
/// manifest cannot smuggle a credential in: a setting the factory does not list is rejected
/// with <see cref="CatalogDiagnosticCode.IllegalSetting"/> before anything is created, and the
/// credentials a provider needs (a client, a key) are given to the factory by the host, never
/// read from the manifest (spec §7).
/// </summary>
public interface IBundleProviderFactory
{
    /// <summary>The <c>provider</c> value this factory serves; never <c>filesystem</c>.</summary>
    string ProviderType { get; }

    /// <summary>The names a source's <c>settings</c> may carry for this provider type; any other name is rejected.</summary>
    IReadOnlySet<string> AcceptedSettings { get; }

    /// <summary>
    /// Creates the provider of <paramref name="source"/>, whose
    /// <see cref="KnowledgeCatalogSource.Settings"/> carry only accepted names. Called when
    /// the catalog builds the source's handle (at construction, and at a reload that changed
    /// the source's effective configuration); what it throws rejects that load as
    /// <see cref="CatalogDiagnosticCode.ProviderUnavailable"/>.
    /// </summary>
    /// <param name="source">The validated source.</param>
    IBundleProvider Create(KnowledgeCatalogSource source);
}
`````

In `src/OKF4net.Catalog/KnowledgeCatalogOptions.cs`, find:

`````csharp
    /// <see cref="IKnowledgeCatalog.ReloadAsync"/> is the only way to pick up
    /// changes. Defaults to <see langword="true"/>.
    /// </summary>
    public bool WatchForChanges { get; init; } = true;
}
`````

and replace it with:

`````csharp
    /// <see cref="IKnowledgeCatalog.ReloadAsync"/> is the only way to pick up
    /// changes. Defaults to <see langword="true"/>.
    /// </summary>
    public bool WatchForChanges { get; init; } = true;

    /// <summary>
    /// The provider factories the manifest may name by <c>provider</c> (spec §9): one per
    /// provider type, none for the built-in <c>filesystem</c>. A source naming a type with no
    /// factory here is rejected (<see cref="CatalogDiagnosticCode.UnknownProvider"/>); two
    /// factories with the same type, or one claiming <c>filesystem</c>, are a configuration
    /// error <see cref="FileKnowledgeCatalog"/>'s constructor refuses. Empty by default.
    /// </summary>
    public IReadOnlyList<IBundleProviderFactory> ProviderFactories { get; init; } = [];
}
`````

In `src/OKF4net.Catalog/KnowledgeCatalogSource.cs`, find:

`````csharp
/// The source's unique identifier; validated as a single OKF concept-id
/// segment (<see cref="OKF4net.ConceptId.ValidateSegment"/>) and unique
/// within the owning <see cref="KnowledgeCatalogSnapshot"/>.
/// </param>
/// <param name="Path">The non-empty, manifest-relative path to the source bundle.</param>
/// <param name="Priority">
/// Ordering priority among sources; defaults to <c>0</c> when omitted from the manifest.
/// </param>
/// <param name="Enabled">Whether the source is active; defaults to <c>true</c> when omitted.</param>
`````

and replace it with:

`````csharp
/// The source's unique identifier; validated as a single OKF concept-id
/// segment (<see cref="OKF4net.ConceptId.ValidateSegment"/>) and unique
/// within the owning <see cref="KnowledgeCatalogSnapshot"/>.
/// </param>
/// <param name="Path">
/// The non-empty, manifest-relative path to the source bundle, for a
/// <see cref="FileSystemProviderType"/> source; the empty string for any other
/// <see cref="ProviderType"/>, whose storage is named by <see cref="Settings"/>.
/// </param>
/// <param name="Priority">
/// Ordering priority among sources; defaults to <c>0</c> when omitted from the manifest.
/// </param>
/// <param name="Enabled">Whether the source is active; defaults to <c>true</c> when omitted.</param>
`````

In `src/OKF4net.Catalog/KnowledgeCatalogSource.cs`, find:

`````csharp
/// The memory tier, for a <see cref="SourceRole.Memory"/> source; <see langword="null"/>
/// for a <see cref="SourceRole.Knowledge"/> source.
/// </param>
public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role, MemoryTier? Tier = null);
`````

and replace it with:

`````csharp
/// The memory tier, for a <see cref="SourceRole.Memory"/> source; <see langword="null"/>
/// for a <see cref="SourceRole.Knowledge"/> source.
/// </param>
public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role, MemoryTier? Tier = null)
{
    /// <summary>The built-in provider type: a bundle directory under the catalog root, named by <see cref="Path"/>.</summary>
    public const string FileSystemProviderType = "filesystem";

    /// <summary>
    /// The source's <c>provider</c> (spec §9): <see cref="FileSystemProviderType"/> when the
    /// manifest names none, else the type of a factory the host registered
    /// (<see cref="IBundleProviderFactory.ProviderType"/>). A <see cref="SourceRole.Memory"/>
    /// source is always a filesystem one.
    /// </summary>
    public string ProviderType { get; init; } = FileSystemProviderType;

    /// <summary>The source's <c>settings</c>, accepted by its provider's factory; empty for a filesystem source.</summary>
    public ProviderSettings Settings { get; init; } = ProviderSettings.Empty;

    /// <summary><see langword="true"/> for a <see cref="FileSystemProviderType"/> source.</summary>
    public bool IsFileSystem => string.Equals(ProviderType, FileSystemProviderType, StringComparison.Ordinal);
}
`````

Create `src/OKF4net.Catalog/ProviderSettings.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections;

namespace OKF4net.Catalog;

/// <summary>
/// The <c>settings</c> of a <c>catalog.json</c> source (spec §9, "nommer un provider"): a
/// read-only map of string values, compared by value so that two sources with the same
/// settings are equal and a reload can tell whether a source's configuration changed.
/// Which names are accepted is the provider factory's declaration
/// (<see cref="IBundleProviderFactory.AcceptedSettings"/>); a credential never travels here,
/// it is injected into the factory by the host.
/// </summary>
public sealed class ProviderSettings : IReadOnlyDictionary<string, string>, IEquatable<ProviderSettings>
{
    private readonly KeyValuePair<string, string>[] _pairs;

    private ProviderSettings(KeyValuePair<string, string>[] pairs)
    {
        _pairs = pairs;
    }

    /// <summary>No setting.</summary>
    public static ProviderSettings Empty { get; } = new([]);

    /// <summary>Builds a settings map from <paramref name="pairs"/>, ordered by name (ordinal).</summary>
    /// <exception cref="ArgumentException">A name or a value is <see langword="null"/>, or a name appears twice.</exception>
    public static ProviderSettings From(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs)
        {
            if (name is null || value is null)
            {
                throw new ArgumentException("a setting needs a name and a value", nameof(pairs));
            }

            if (!sorted.TryAdd(name, value))
            {
                throw new ArgumentException($"the setting '{name}' appears twice", nameof(pairs));
            }
        }

        return sorted.Count == 0 ? Empty : new([.. sorted]);
    }

    /// <inheritdoc/>
    public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _pairs.Select(p => p.Key);

    /// <inheritdoc/>
    public IEnumerable<string> Values => _pairs.Select(p => p.Value);

    /// <inheritdoc/>
    public int Count => _pairs.Length;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out string value)
    {
        foreach (var pair in _pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)_pairs).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Value equality: the same names with the same values (ordinal).</summary>
    public bool Equals(ProviderSettings? other)
    {
        if (other is null || other._pairs.Length != _pairs.Length)
        {
            return false;
        }

        for (var i = 0; i < _pairs.Length; i++)
        {
            if (!string.Equals(_pairs[i].Key, other._pairs[i].Key, StringComparison.Ordinal)
                || !string.Equals(_pairs[i].Value, other._pairs[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ProviderSettings);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in _pairs)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>The settings as <c>name=value</c> pairs joined by <c>;</c>, for diagnostics.</summary>
    public override string ToString() => string.Join(";", _pairs.Select(p => $"{p.Key}={p.Value}"));
}
`````

- [ ] **Step 4: Run the catalog group**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog" -nologo -v q`
Expected: `Passed: 260, Skipped: 1, Total: 261` (+17: 8 theory rows and 9 facts).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/ProviderSettings.cs src/OKF4net.Catalog/IBundleProviderFactory.cs src/OKF4net.Catalog/KnowledgeCatalogSource.cs src/OKF4net.Catalog/CatalogDiagnosticCode.cs src/OKF4net.Catalog/KnowledgeCatalogOptions.cs src/OKF4net.Catalog/CatalogManifestParser.cs src/OKF4net.Catalog/FileKnowledgeCatalog.cs src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/TestProviderFactory.cs tests/OKF4net.Tests/Catalog/CatalogProviderTests.cs
git commit -m "feat(catalog): a catalog.json source names its provider and settings; hosts register provider factories

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (Task 10): `### Added` "**A `catalog.json` source names its provider** …".


### Task 5: The catalog snapshot owns one `BundleHandle` per enabled knowledge source

**Files:**
- Modify: `src/OKF4net.Catalog/KnowledgeCatalogSnapshot.cs` (whole file), `KnowledgeCatalogOptions.cs` (`using`, after `ProviderFactories`), `FileKnowledgeCatalog.cs` (`using`, class remarks, fields, constructor, `ReloadAsync`, `Dispose`, new private members `HandleConfiguration`, `DisposeHandlesLeft`, `TryBuildHandles`, `TryLoadSnapshot` signature and tail)
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs` (`RefreshPolicy`), `KnowledgeServiceCollectionExtensions.cs:84-85`
- Test: `tests/OKF4net.Tests/Catalog/CatalogHandleTests.cs` (create)

**Interfaces:**
- Consumes: Task 4's `IBundleProviderFactory`, `KnowledgeCatalogSource.IsFileSystem`/`ProviderType`/`Settings`, `TestProviderFactory`; lot C's `BundleHandle`, `BundleRefreshPolicy` (`OnAccess`, `Notify`), `BundleLoadOptions.Default`; `FileSystemBundleProvider(root, confinementBoundary:)` and its `ConfinementBoundary`; `ReparsePoints.CanonicalizeRoot` (internal, visible to the tests); the test project's `NotifyingBundleProvider(inner)` with `Subscribers`.
- Produces: `KnowledgeCatalogSnapshot.Handles : IReadOnlyDictionary<string, BundleHandle>` (init, empty by default); `KnowledgeCatalogOptions.RefreshPolicy` (`BundleRefreshPolicy`, `OnAccess`), `RefreshPolicyFor` (`Func<KnowledgeCatalogSource, BundleRefreshPolicy?>?`), `LoadOptions` (`BundleLoadOptions`); `KnowledgeOptions.RefreshPolicy` (Hosting). `FileKnowledgeCatalog` builds, reuses and disposes the handles (decision L9); Task 6's resolvers read `snapshot.Handles`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/CatalogHandleTests.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Internal;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The catalog snapshot owns the handles (spec §5.2, §9): one per enabled knowledge source,
/// built by its provider under the catalog's options and the source's policy, confined to the
/// catalog root; reused across a reload only when the source's effective configuration is
/// unchanged; dropped and disposed when the source changes or leaves, while a request that
/// still holds the old snapshot keeps reading its image; disposed with the catalog.
/// </summary>
public class CatalogHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: d\n---\nAlpha.\n";

    private static FileKnowledgeCatalog Catalog(
        TempDir root,
        string json,
        BundleRefreshPolicy? policy = null,
        Func<KnowledgeCatalogSource, BundleRefreshPolicy?>? policyFor = null,
        params IBundleProviderFactory[] factories)
    {
        root.Write("catalog.json", json);
        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
            RefreshPolicy = policy ?? BundleRefreshPolicy.OnAccess,
            RefreshPolicyFor = policyFor,
        });
    }

    private const string TwoSources = """
        {
          "version": 1,
          "sources": [
            { "id": "a", "path": "./a" },
            { "id": "b", "path": "./b", "enabled": false },
            { "id": "m", "path": "./m", "role": "memory", "tier": "user" }
          ]
        }
        """;

    [Fact]
    public void Construction_builds_one_confined_OnAccess_handle_per_enabled_knowledge_source()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        root.Write("b/x.md", Doc);
        Directory.CreateDirectory(Path.Combine(root.Path, "m"));
        using var catalog = Catalog(root, TwoSources);

        var handles = catalog.Current.Handles;
        var handle = Assert.Single(handles).Value;
        Assert.True(handles.ContainsKey("a"));
        Assert.Equal(BundleRefreshMode.OnAccess, handle.RefreshPolicy.Mode);
        Assert.Same(BundleLoadOptions.Default, handle.Options);
        var provider = Assert.IsType<FileSystemBundleProvider>(handle.Provider);
        Assert.Equal(Path.Combine(root.Path, "a"), provider.Root);
        Assert.Equal(ReparsePoints.CanonicalizeRoot(root.Path), provider.ConfinementBoundary);
        Assert.Equal(1, handle.Current.Count);
    }

    [Fact]
    public async Task A_reload_with_an_unchanged_source_keeps_its_handle_and_its_image()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var before = catalog.Current.Handles["a"];
        var image = before.Current;

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a", "priority": 5 } ] }""");
        var reloaded = await catalog.ReloadAsync();

        Assert.Equal(2, reloaded.Generation);
        Assert.Same(before, reloaded.Handles["a"]);
        Assert.Same(image, reloaded.Handles["a"].Current);
    }

    [Fact]
    public async Task A_reload_that_changes_a_sources_path_builds_a_new_handle_and_the_old_one_still_serves_its_image()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        root.Write("a2/x.md", Doc);
        root.Write("a2/y.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var old = catalog.Current.Handles["a"];
        var oldImage = old.Current;

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a2" } ] }""");
        var reloaded = await catalog.ReloadAsync();

        var fresh = reloaded.Handles["a"];
        Assert.NotSame(old, fresh);
        Assert.Equal(2, fresh.Current.Count);
        Assert.Equal(Path.Combine(root.Path, "a2"), fresh.Location);
        Assert.Same(oldImage, old.Current); // disposed by the catalog, still readable by a request that holds the old snapshot
    }

    [Theory]
    [InlineData("""{ this is not json""")]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "path": "./gone" } ] }""")]
    public async Task A_rejected_reload_keeps_every_handle(string broken)
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var before = catalog.Current;

        root.Write("catalog.json", broken);
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        Assert.NotEmpty(catalog.LastReloadDiagnostics);
        Assert.Equal(1, before.Handles["a"].Current.Count);
    }

    [Fact]
    public async Task A_factory_source_gets_a_handle_under_its_own_policy_and_leaves_the_catalog_disposed()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var notifying = new NotifyingBundleProvider(new InMemoryBundleProvider(id: "mem").Add("n.md", Doc));
        var factory = new TestProviderFactory("memory", _ => notifying);
        const string withMemory = """{ "version": 1, "sources": [ { "id": "a", "path": "./a" }, { "id": "mem", "provider": "memory" } ] }""";
        using var catalog = Catalog(
            root,
            withMemory,
            policyFor: s => s.Id == "mem" ? BundleRefreshPolicy.Notify : null,
            factories: factory);

        var handle = catalog.Current.Handles["mem"];
        Assert.Same(notifying, handle.Provider);
        Assert.Equal(BundleRefreshMode.Notify, handle.RefreshPolicy.Mode);
        Assert.Equal(BundleRefreshMode.OnAccess, catalog.Current.Handles["a"].RefreshPolicy.Mode);
        Assert.Equal(1, notifying.Subscribers);
        Assert.Single(factory.Created);

        var reloaded = await catalog.ReloadAsync();
        Assert.Same(handle, reloaded.Handles["mem"]);
        Assert.Single(factory.Created); // reused, not created again

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var without = await catalog.ReloadAsync();
        Assert.False(without.Handles.ContainsKey("mem"));
        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_factory_that_throws_or_a_Notify_policy_over_a_silent_provider_rejects_the_load_and_builds_nothing()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var throwing = new TestProviderFactory("memory", _ => throw new InvalidOperationException("bucket s3://secret/path unreachable"));
        const string withMemory = """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" }, { "id": "a", "path": "./a" } ] }""";

        var error = Assert.Throws<CatalogException>(() => Catalog(root, withMemory, factories: throwing));
        Assert.Contains("[ProviderUnavailable]", error.Message);
        Assert.Contains("(InvalidOperationException)", error.Message);
        Assert.DoesNotContain("secret", error.Message);

        var silent = new TestProviderFactory("memory");
        var notify = Assert.Throws<CatalogException>(() => Catalog(root, withMemory, policy: BundleRefreshPolicy.Notify, factories: silent));
        Assert.Contains("[ProviderUnavailable]", notify.Message);
        Assert.Contains("(ArgumentException)", notify.Message);
    }

    [Fact]
    public void Disposing_the_catalog_disposes_its_handles()
    {
        using var root = new TempDir();
        var notifying = new NotifyingBundleProvider(new InMemoryBundleProvider(id: "mem").Add("n.md", Doc));
        var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" } ] }""",
            policy: BundleRefreshPolicy.Notify,
            factories: new TestProviderFactory("memory", _ => notifying));
        Assert.Equal(1, notifying.Subscribers);

        catalog.Dispose();
        catalog.Dispose();

        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_hand_built_snapshot_carries_no_handle()
    {
        Assert.True(CatalogManifestParser.TryParse("""{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""", "/m", out var snapshot, out _));

        Assert.Empty(snapshot!.Handles);
        Assert.Empty((snapshot with { Generation = 3 }).Handles);
    }
}
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CatalogHandleTests" -nologo -v q`
Expected: the build fails: `error CS1061: 'KnowledgeCatalogSnapshot' does not contain a definition for 'Handles'`, `error CS0117: 'KnowledgeCatalogOptions' does not contain a definition for 'RefreshPolicy'` / `'RefreshPolicyFor'`.

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`, find:

`````csharp
        CatalogFilePath = fullPath;
        CatalogRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
    }

    /// <summary>
    /// The provider factories registered by <see cref="AddProviderFactory"/>, in order,
    /// handed to <see cref="KnowledgeCatalogOptions.ProviderFactories"/>.
    /// </summary>
`````

and replace it with:

`````csharp
        CatalogFilePath = fullPath;
        CatalogRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
    }

    /// <summary>
    /// The refresh policy of every knowledge source's handle, handed to
    /// <see cref="KnowledgeCatalogOptions.RefreshPolicy"/>: the bundle's token is compared
    /// before each use by default (<see cref="OKF4net.Providers.BundleRefreshPolicy.OnAccess"/>),
    /// so a source stays as fresh as when it was reloaded at every request.
    /// </summary>
    public OKF4net.Providers.BundleRefreshPolicy RefreshPolicy { get; set; } = OKF4net.Providers.BundleRefreshPolicy.OnAccess;

    /// <summary>
    /// The provider factories registered by <see cref="AddProviderFactory"/>, in order,
    /// handed to <see cref="KnowledgeCatalogOptions.ProviderFactories"/>.
    /// </summary>
`````

In `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`, find:

`````csharp
        {
            CatalogFilePath = options.CatalogFilePath!,
            CatalogRoot = options.CatalogRoot!,
            ProviderFactories = [.. options.ProviderFactories],
        };

        services.TryAddSingleton(catalogOptions);
        services.TryAddSingleton<IKnowledgeCatalog>(_ => new FileKnowledgeCatalog(catalogOptions));
`````

and replace it with:

`````csharp
        {
            CatalogFilePath = options.CatalogFilePath!,
            CatalogRoot = options.CatalogRoot!,
            ProviderFactories = [.. options.ProviderFactories],
            RefreshPolicy = options.RefreshPolicy ?? throw new ArgumentException("KnowledgeOptions.RefreshPolicy must not be null."),
        };

        services.TryAddSingleton(catalogOptions);
        services.TryAddSingleton<IKnowledgeCatalog>(_ => new FileKnowledgeCatalog(catalogOptions));
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
/// reliable, synchronous guarantee that a specific edit has been picked up
/// should call <see cref="ReloadAsync"/> explicitly rather than rely on the
/// watcher.
/// </para>
/// </remarks>
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Dictionary<string, IBundleProviderFactory> _factories;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;

    private KnowledgeCatalogSnapshot _current;
`````

and replace it with:

`````csharp
/// reliable, synchronous guarantee that a specific edit has been picked up
/// should call <see cref="ReloadAsync"/> explicitly rather than rely on the
/// watcher.
/// </para>
/// <para>
/// <b>The snapshot owns the handles</b> (spec §5.2, §9). Each published snapshot carries one
/// <see cref="BundleHandle"/> per enabled knowledge source (<see cref="KnowledgeCatalogSnapshot.Handles"/>),
/// built by the source's provider — the built-in filesystem one, confined to
/// <see cref="KnowledgeCatalogOptions.CatalogRoot"/> and re-checked against it before every
/// access, or the factory registered for the source's <c>provider</c> — under
/// <see cref="KnowledgeCatalogOptions.LoadOptions"/> and the source's refresh policy
/// (<see cref="KnowledgeCatalogOptions.RefreshPolicy"/>, <see cref="BundleRefreshPolicy.OnAccess"/>
/// by default). A reload reuses a source's handle, image included, only when its effective
/// configuration is identical in every point (provider type, resolved directory, settings,
/// refresh policy, load options); any difference, a changed <c>path</c> for instance, builds
/// a new handle and the old one is no longer served by the new snapshot. A handle that
/// leaves the catalog is disposed (which only ends its change subscription: a request still
/// holding the old snapshot keeps reading its image); <see cref="Dispose"/> disposes them all.
/// A rejected reload builds nothing that outlives it and keeps every current handle.
/// </para>
/// </remarks>
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Dictionary<string, IBundleProviderFactory> _factories;
    private readonly Lock _gate = new();

    /// <summary>The effective configuration each current handle was built for, by source id (guarded by <see cref="_gate"/>).</summary>
    private Dictionary<string, HandleConfiguration> _handleConfigurations = new(StringComparer.Ordinal);
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;

    private KnowledgeCatalogSnapshot _current;
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);
        _factories = IndexFactories(options.ProviderFactories);

        if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
        }

        _current = snapshot! with { Generation = 1 };

        if (options.WatchForChanges)
        {
            _debounceTimer = new Timer(FireDebouncedReload, state: null, Timeout.Infinite, Timeout.Infinite);
`````

and replace it with:

`````csharp
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);
        _factories = IndexFactories(options.ProviderFactories);

        if (!TryLoadSnapshot(previous: null, out var snapshot, out var configurations, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
        }

        _current = snapshot! with { Generation = 1 };
        _handleConfigurations = configurations!;

        if (options.WatchForChanges)
        {
            _debounceTimer = new Timer(FireDebouncedReload, state: null, Timeout.Infinite, Timeout.Infinite);
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
            {
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
            {
                _lastReloadDiagnostics = diagnostics;
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            _current = snapshot! with { Generation = _current.Generation + 1 };
            _lastReloadDiagnostics = [];
            return new ValueTask<KnowledgeCatalogSnapshot>(_current);
        }
    }

    /// <summary>
    /// Stops the watcher and debounce timer (if any) and prevents any future
    /// reload from applying. Cancellation-safe (never leaves a torn snapshot)
    /// and idempotent -- safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
`````

and replace it with:

`````csharp
            {
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            if (!TryLoadSnapshot(_current, out var snapshot, out var configurations, out var diagnostics))
            {
                _lastReloadDiagnostics = diagnostics;
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            var previous = _current;
            _current = snapshot! with { Generation = _current.Generation + 1 };
            _handleConfigurations = configurations!;
            _lastReloadDiagnostics = [];
            DisposeHandlesLeft(previous, _current);
            return new ValueTask<KnowledgeCatalogSnapshot>(_current);
        }
    }

    /// <summary>
    /// Stops the watcher and debounce timer (if any), disposes the current snapshot's
    /// handles (their change subscriptions end; an image already handed out stays readable)
    /// and prevents any future reload from applying. Cancellation-safe (never leaves a torn
    /// snapshot) and idempotent -- safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp

            _disposed = true;
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
        }
    }

    private bool TryLoadSnapshot(out KnowledgeCatalogSnapshot? snapshot, out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        string json;
        try
        {
            // Strict UTF-8, matching every other read in this codebase
`````

and replace it with:

`````csharp

            _disposed = true;
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
            foreach (var handle in _current.Handles.Values)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// The effective configuration a source's handle was built for (spec §9): a handle is
    /// reused across reloads only when every point is equal, so a changed <c>path</c> (its
    /// resolved directory), setting, policy or option rebuilds it. The load options are
    /// compared as the record they are (the catalog hands every handle its own instance).
    /// </summary>
    private sealed record HandleConfiguration(string ProviderType, string? ResolvedDirectory, ProviderSettings Settings, BundleRefreshPolicy RefreshPolicy, BundleLoadOptions LoadOptions);

    /// <summary>Disposes the handles of <paramref name="previous"/> that <paramref name="current"/> does not serve any more.</summary>
    private static void DisposeHandlesLeft(KnowledgeCatalogSnapshot previous, KnowledgeCatalogSnapshot current)
    {
        foreach (var (id, handle) in previous.Handles)
        {
            if (!current.Handles.TryGetValue(id, out var kept) || !ReferenceEquals(kept, handle))
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the handles of <paramref name="snapshot"/>'s enabled knowledge sources, reusing
    /// a handle of <paramref name="previous"/> whose effective configuration is unchanged.
    /// On failure nothing created here survives: the handles built so far are disposed and
    /// every current handle is untouched.
    /// </summary>
    private bool TryBuildHandles(
        KnowledgeCatalogSnapshot snapshot,
        KnowledgeCatalogSnapshot? previous,
        IReadOnlyDictionary<string, string> resolvedDirectories,
        out IReadOnlyDictionary<string, BundleHandle>? handles,
        out Dictionary<string, HandleConfiguration>? configurations,
        out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        var built = new Dictionary<string, BundleHandle>(StringComparer.Ordinal);
        var created = new List<BundleHandle>();
        var keys = new Dictionary<string, HandleConfiguration>(StringComparer.Ordinal);
        var problems = new List<CatalogDiagnostic>();

        foreach (var source in snapshot.Sources)
        {
            if (!source.Enabled || source.Role != SourceRole.Knowledge)
            {
                continue;
            }

            var policy = _options.RefreshPolicyFor?.Invoke(source) ?? _options.RefreshPolicy;
            var configuration = new HandleConfiguration(
                source.ProviderType,
                source.IsFileSystem ? resolvedDirectories[source.Id] : null,
                source.Settings,
                policy,
                _options.LoadOptions);

            if (previous is not null
                && previous.Handles.TryGetValue(source.Id, out var existing)
                && _handleConfigurations.TryGetValue(source.Id, out var existingConfiguration)
                && existingConfiguration == configuration)
            {
                built[source.Id] = existing;
                keys[source.Id] = configuration;
                continue;
            }

            try
            {
                var provider = source.IsFileSystem
                    ? new FileSystemBundleProvider(resolvedDirectories[source.Id], confinementBoundary: _options.CatalogRoot)
                    : _factories[source.ProviderType].Create(source);
                var handle = new BundleHandle(provider, _options.LoadOptions, policy);
                created.Add(handle);
                built[source.Id] = handle;
                keys[source.Id] = configuration;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Translated (spec §7): the factory's own text may name a bucket or an endpoint,
                // which stays with the host's log, never in a diagnostic a consumer may print.
                problems.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.ProviderUnavailable,
                    $"Source '{source.Id}' provider '{source.ProviderType}' could not be created ({e.GetType().Name})."));
            }
        }

        if (problems.Count > 0)
        {
            foreach (var handle in created)
            {
                handle.Dispose();
            }

            handles = null;
            configurations = null;
            diagnostics = problems.AsReadOnly();
            return false;
        }

        handles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, BundleHandle>(built);
        configurations = keys;
        diagnostics = [];
        return true;
    }

    private bool TryLoadSnapshot(
        KnowledgeCatalogSnapshot? previous,
        out KnowledgeCatalogSnapshot? snapshot,
        out Dictionary<string, HandleConfiguration>? configurations,
        out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        configurations = null;
        string json;
        try
        {
            // Strict UTF-8, matching every other read in this codebase
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
            return false;
        }

        var pathDiagnostics = new List<CatalogDiagnostic>();
        foreach (var source in snapshot!.Sources)
        {
            if (!source.Enabled)
            {
`````

and replace it with:

`````csharp
            return false;
        }

        var pathDiagnostics = new List<CatalogDiagnostic>();
        var resolvedDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in snapshot!.Sources)
        {
            if (!source.Enabled)
            {
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
            // A filesystem source's path is checked as before; another provider type needs a
            // registered factory that accepts every setting the source carries (spec §9).
            if (source.IsFileSystem)
            {
                if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out _, out var pathDiagnostic))
                {
                    pathDiagnostics.Add(pathDiagnostic!);
                }
            }
            else if (!_factories.TryGetValue(source.ProviderType, out var factory))
            {
                pathDiagnostics.Add(new CatalogDiagnostic(
`````

and replace it with:

`````csharp
            // A filesystem source's path is checked as before; another provider type needs a
            // registered factory that accepts every setting the source carries (spec §9).
            if (source.IsFileSystem)
            {
                if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out var resolved, out var pathDiagnostic))
                {
                    pathDiagnostics.Add(pathDiagnostic!);
                }
                else
                {
                    resolvedDirectories[source.Id] = resolved!;
                }
            }
            else if (!_factories.TryGetValue(source.ProviderType, out var factory))
            {
                pathDiagnostics.Add(new CatalogDiagnostic(
`````

In `src/OKF4net.Catalog/FileKnowledgeCatalog.cs`, find:

`````csharp
            diagnostics = pathDiagnostics.AsReadOnly();
            return false;
        }

        return true;
    }

    private FileSystemWatcher CreateWatcher()
`````

and replace it with:

`````csharp
            diagnostics = pathDiagnostics.AsReadOnly();
            return false;
        }

        if (!TryBuildHandles(snapshot, previous, resolvedDirectories, out var handles, out configurations, out diagnostics))
        {
            snapshot = null;
            return false;
        }

        snapshot = snapshot with { Handles = handles! };
        return true;
    }

    private FileSystemWatcher CreateWatcher()
`````

In `src/OKF4net.Catalog/KnowledgeCatalogOptions.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Configuration for a <see cref="FileKnowledgeCatalog"/>.
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Configuration for a <see cref="FileKnowledgeCatalog"/>.
`````

In `src/OKF4net.Catalog/KnowledgeCatalogOptions.cs`, find:

`````csharp
    /// factories with the same type, or one claiming <c>filesystem</c>, are a configuration
    /// error <see cref="FileKnowledgeCatalog"/>'s constructor refuses. Empty by default.
    /// </summary>
    public IReadOnlyList<IBundleProviderFactory> ProviderFactories { get; init; } = [];
}
`````

and replace it with:

`````csharp
    /// factories with the same type, or one claiming <c>filesystem</c>, are a configuration
    /// error <see cref="FileKnowledgeCatalog"/>'s constructor refuses. Empty by default.
    /// </summary>
    public IReadOnlyList<IBundleProviderFactory> ProviderFactories { get; init; } = [];

    /// <summary>
    /// The refresh policy of every knowledge source's handle (spec §5.3): the bundle's token is
    /// compared before each use by default (<see cref="BundleRefreshPolicy.OnAccess"/>), so a
    /// source stays as fresh as when it was reloaded at every request, at the cost of a token
    /// read instead of a load. <see cref="RefreshPolicyFor"/> overrides it per source.
    /// </summary>
    public BundleRefreshPolicy RefreshPolicy { get; init; } = BundleRefreshPolicy.OnAccess;

    /// <summary>
    /// Chooses a source's refresh policy; <see langword="null"/> (the default), or a
    /// <see langword="null"/> answer for a source, means <see cref="RefreshPolicy"/>. A
    /// <see cref="BundleRefreshPolicy.Notify"/> answer needs a provider that implements
    /// <see cref="IBundleChangeNotifier"/>, else the source is rejected as
    /// <see cref="CatalogDiagnosticCode.ProviderUnavailable"/>.
    /// </summary>
    public Func<KnowledgeCatalogSource, BundleRefreshPolicy?>? RefreshPolicyFor { get; init; }

    /// <summary>The options every handle loads its bundle with (bounds, key policy); <see cref="BundleLoadOptions.Default"/> by default.</summary>
    public BundleLoadOptions LoadOptions { get; init; } = BundleLoadOptions.Default;
}
`````

In `src/OKF4net.Catalog/KnowledgeCatalogSnapshot.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// An immutable, validated catalog manifest snapshot (no filesystem access yet).
/// </summary>
/// <param name="Version">The manifest schema version; always <c>1</c> for a successfully parsed snapshot.</param>
/// <param name="Sources">
/// The manifest's sources, in the order they appeared in the manifest (ordinal-stable).
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// An immutable, validated catalog manifest snapshot.
/// </summary>
/// <param name="Version">The manifest schema version; always <c>1</c> for a successfully parsed snapshot.</param>
/// <param name="Sources">
/// The manifest's sources, in the order they appeared in the manifest (ordinal-stable).
`````

In `src/OKF4net.Catalog/KnowledgeCatalogSnapshot.cs`, find:

`````csharp
/// Task 4); the parser produces snapshots with <c>Generation 0</c> and the
/// catalog stamps the real value on publish.
/// </param>
public sealed record KnowledgeCatalogSnapshot(
    int Version, IReadOnlyList<KnowledgeCatalogSource> Sources, string ManifestDirectory, long Generation);
`````

and replace it with:

`````csharp
/// Task 4); the parser produces snapshots with <c>Generation 0</c> and the
/// catalog stamps the real value on publish.
/// </param>
public sealed record KnowledgeCatalogSnapshot(
    int Version, IReadOnlyList<KnowledgeCatalogSource> Sources, string ManifestDirectory, long Generation)
{
    private static readonly IReadOnlyDictionary<string, BundleHandle> NoHandles =
        new ReadOnlyDictionary<string, BundleHandle>(new Dictionary<string, BundleHandle>(StringComparer.Ordinal));

    /// <summary>
    /// The handle of every enabled <see cref="SourceRole.Knowledge"/> source, by source id
    /// (spec §5.2, §9): the catalog that published this snapshot created them, keeps each one
    /// across reloads while the source's effective configuration is unchanged, and the
    /// resolvers search through them, so a source's image survives from one request to the
    /// next and is refreshed under the source's policy. Empty for a snapshot the parser built
    /// (<see cref="CatalogManifestParser.TryParse"/>) or one built by hand: a resolver then
    /// resolves the source's path at each request, as before the seam. Never
    /// <see langword="null"/>; a genuine read-only view.
    /// </summary>
    public IReadOnlyDictionary<string, BundleHandle> Handles { get; init; } = NoHandles;
}
`````

- [ ] **Step 4: Run the catalog group**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog" -nologo -v q`
Expected: `Passed: 269, Skipped: 1, Total: 270` (+9: 7 facts and a 2-row theory).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeCatalogSnapshot.cs src/OKF4net.Catalog/KnowledgeCatalogOptions.cs src/OKF4net.Catalog/FileKnowledgeCatalog.cs src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/CatalogHandleTests.cs
git commit -m "feat(catalog): the snapshot owns one BundleHandle per enabled knowledge source, reused across reloads while the source's configuration is unchanged

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (Task 10): `### Added` "**The catalog snapshot owns the handles** …".

### Task 6: The resolvers search through the snapshot's handles

**Files:**
- Create: `src/OKF4net.Catalog/CatalogSourceOpener.cs`
- Modify: `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs` (class remarks `:14-34`, the loop `:136-146`), `FusedResolverEngine.cs` (class summary `:17-20`, the open/dedup and search loops `:118-162`), `IKnowledgeCatalog.cs:22-28`
- Test: `tests/OKF4net.Tests/Catalog/CatalogResolverHandleTests.cs` (create)

**Interfaces:**
- Consumes: Task 5's `KnowledgeCatalogSnapshot.Handles`; Task 3's `OkfBundleKnowledgeSource(id, handle)`; Task 4's `TestProviderFactory`; `CatalogPathResolver.TryResolve`, `CatalogPathResolver.PathComparison` (internal); `KnowledgeResolverRouter(catalog, strategy, fairnessQuota, visibilityPolicy)`; the test project's `TempDir.TryCreateJunctionToExternalDir`.
- Produces: `internal static bool CatalogSourceOpener.TryOpen(IKnowledgeCatalog catalog, KnowledgeCatalogSnapshot snapshot, KnowledgeCatalogSource source, out OkfBundleKnowledgeSource? bundleSource, out string? identity, out string? location, out KnowledgeDiagnostic? diagnostic)` (decision L12).

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/CatalogResolverHandleTests.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The resolvers search through the handles the snapshot keeps (spec §9, §11 "Catalogue"):
/// two searches over an unchanged source reuse one image; a change in storage is seen at the
/// next search (<see cref="BundleRefreshPolicy.OnAccess"/>); two sources over one storage are
/// deduplicated by the provider's id; a snapshot without handles still resolves each source's
/// path at every request; and a source root replaced by a junction after the handle was
/// created is refused by the provider at the next access, the previous image served stale.
/// </summary>
public class CatalogResolverHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders and refunds.\n";

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    private static FileKnowledgeCatalog Catalog(TempDir root, string json, params IBundleProviderFactory[] factories)
    {
        root.Write("catalog.json", json);
        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
        });
    }

    private const string OneMemorySource = """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" } ] }""";

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task Two_searches_over_an_unchanged_source_load_it_once_and_a_change_is_seen_at_the_next_search(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        var storage = new InMemoryBundleProvider(id: "mem").Add("a.md", Doc);
        using var catalog = Catalog(root, OneMemorySource, new TestProviderFactory("memory", _ => storage));
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);

        var first = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var second = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Single(first.Passages);
        Assert.Single(second.Passages);
        Assert.Equal(1, Loads(storage));

        storage.Add("b.md", Doc);
        var third = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, third.Passages.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task Two_sources_over_one_storage_are_deduplicated_by_the_providers_id(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        var storage = new InMemoryBundleProvider(id: "shared").Add("a.md", Doc);
        const string two = """{ "version": 1, "sources": [ { "id": "x", "provider": "memory", "priority": 2 }, { "id": "y", "provider": "memory", "priority": 1 } ] }""";
        using var catalog = Catalog(root, two, new TestProviderFactory("memory", _ => storage));
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        var passage = Assert.Single(context.Passages);
        Assert.Equal("x", passage.SourceId);
        var duplicate = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.DuplicateDirectory);
        Assert.Equal("y", duplicate.SourceId);
        Assert.Contains("('memory://shared')", duplicate.Message);
        Assert.Contains("as source 'x'", duplicate.Message);
    }

    [Fact]
    public async Task Two_filesystem_sources_over_one_directory_are_still_reported_under_the_resolved_directory()
    {
        using var root = new TempDir();
        root.Write("docs/a.md", Doc);
        const string two = """{ "version": 1, "sources": [ { "id": "x", "path": "./docs", "priority": 2 }, { "id": "y", "path": "docs", "priority": 1 } ] }""";
        using var catalog = Catalog(root, two);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Single(context.Passages);
        var duplicate = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.DuplicateDirectory);
        Assert.Contains($"('{Path.Combine(root.Path, "docs")}')", duplicate.Message);
    }

    /// <summary>A catalog whose snapshot carries no handle: the parser's own, as a host or a test may build one.</summary>
    private sealed class HandlelessCatalog(string catalogRoot, KnowledgeCatalogSnapshot snapshot) : IKnowledgeCatalog
    {
        public KnowledgeCatalogSnapshot Current { get; } = snapshot;

        public string CatalogRoot { get; } = catalogRoot;

        public IReadOnlyList<CatalogDiagnostic> LastReloadDiagnostics => [];

        public ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default) => new(Current);
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    public async Task A_snapshot_without_handles_resolves_each_sources_path_at_every_search(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        root.Write("docs/a.md", Doc);
        Assert.True(CatalogManifestParser.TryParse("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" }, { "id": "gone", "path": "./gone" } ] }""", root.Path, out var snapshot, out _));
        var resolver = new KnowledgeResolverRouter(new HandlelessCatalog(root.Path, snapshot!), strategy, null, null);

        Assert.Single((await resolver.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        root.Write("docs/b.md", Doc);
        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        var unavailable = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.SourceUnavailable);
        Assert.Equal("gone", unavailable.SourceId);
        Assert.Contains("could not be re-resolved", unavailable.Message);
    }

    [SkippableFact]
    public async Task A_source_root_replaced_by_a_junction_after_the_handle_was_created_is_refused_at_the_next_access()
    {
        using var root = new TempDir();
        using var external = new TempDir();
        root.Write("docs/a.md", Doc);
        external.Write("a.md", Doc);
        external.Write("b.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        var resolver = new GroupedKnowledgeResolver(catalog);
        var handle = catalog.Current.Handles["docs"];
        Assert.Single((await resolver.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        Directory.Delete(Path.Combine(root.Path, "docs"), recursive: true);
        Skip.IfNot(root.TryCreateJunctionToExternalDir("docs", external.Path), "needs a junction or a directory symlink");

        // The provider re-checks the chain from the catalog root before every access (spec §8):
        // the token read is refused, the reload fails and the previous image is served stale
        // (spec §5.2); nothing under the junction is ever read.
        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Single(context.Passages);
        Assert.Empty(context.Diagnostics);
        Assert.True(handle.IsStale);
        var failure = Assert.IsType<BundleLoadException>(handle.LastLoadError);
        Assert.IsType<BundleAccessRefusedException>(failure.InnerException);
        Assert.Throws<BundleAccessRefusedException>(() => handle.Provider.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => handle.Provider.Read(BundleKey.Parse("b.md")));
    }
}
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CatalogResolverHandleTests" -nologo -v q`
Expected: `Failed: 6, Passed: 3, Total: 9` — the three `Two_searches_over_an_unchanged_source…` rows (the resolvers still build a fresh source per request, so `Loads` is 2 where 1 is expected), the two `Two_sources_over_one_storage_are_deduplicated_by_the_providers_id` rows (a source with no `path` cannot be resolved per request: `SourceUnavailable` where the deduplication is expected) and `A_source_root_replaced_by_a_junction…` (the per-request path check reports `SourceUnavailable` where the handle's stale image is expected); `Two_filesystem_sources_over_one_directory…` and the two `A_snapshot_without_handles…` rows pass before as after.

- [ ] **Step 3: Write the implementation**

Create `src/OKF4net.Catalog/CatalogSourceOpener.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// How every resolver opens a knowledge source of a snapshot (spec §9, "Catalog : qui crée
/// et conserve les handles"): from the handle the catalog keeps for it, so that the image
/// survives from one request to the next and is refreshed under the source's policy; or,
/// for a snapshot that carries no handle for the source (one the parser or a test built by
/// hand), by resolving its path at this request and loading at each search, as every
/// resolver did before the seam.
/// </summary>
internal static class CatalogSourceOpener
{
    /// <summary>
    /// Opens <paramref name="source"/>. On success <paramref name="identity"/> is what two
    /// sources over one storage share — the provider's <see cref="Providers.IBundleProvider.Id"/>
    /// (the canonical root for the filesystem), or the resolved directory for a source
    /// without a handle — and <paramref name="location"/> is the displayable location; on
    /// failure <paramref name="diagnostic"/> says why the source is unavailable.
    /// </summary>
    internal static bool TryOpen(
        IKnowledgeCatalog catalog,
        KnowledgeCatalogSnapshot snapshot,
        KnowledgeCatalogSource source,
        out OkfBundleKnowledgeSource? bundleSource,
        out string? identity,
        out string? location,
        out KnowledgeDiagnostic? diagnostic)
    {
        if (snapshot.Handles.TryGetValue(source.Id, out var handle))
        {
            bundleSource = new OkfBundleKnowledgeSource(source.Id, handle);
            identity = handle.Provider.Id;
            location = handle.Location;
            diagnostic = null;
            return true;
        }

        if (!CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var directory, out var pathDiagnostic))
        {
            bundleSource = null;
            identity = null;
            location = null;
            diagnostic = new KnowledgeDiagnostic(
                KnowledgeDiagnosticCode.SourceUnavailable,
                source.Id,
                $"Source '{source.Id}' path could not be re-resolved: {pathDiagnostic!.Message}");
            return false;
        }

        bundleSource = new OkfBundleKnowledgeSource(source.Id, directory!);
        identity = directory;
        location = directory;
        diagnostic = null;
        return true;
    }
}
`````

In `src/OKF4net.Catalog/FusedResolverEngine.cs`, find:

`````csharp
internal readonly record struct RankedPassage(KnowledgePassage Passage, int Priority);

/// <summary>
/// The shared pipeline behind every fusing <see cref="IKnowledgeResolver"/>
/// strategy: resolve and dedup the enabled sources, fan out, apply the
/// query's <see cref="StalePolicy"/>, then rank with a caller-supplied
/// comparer. <see cref="MergedKnowledgeResolver"/> and
/// <see cref="PriorityWeightedKnowledgeResolver"/> differ ONLY in that
/// comparer -- keeping the rest here means dedup semantics, diagnostics, and
/// the never-throw contract cannot drift between the two.
/// </summary>
`````

and replace it with:

`````csharp
internal readonly record struct RankedPassage(KnowledgePassage Passage, int Priority);

/// <summary>
/// The shared pipeline behind every fusing <see cref="IKnowledgeResolver"/>
/// strategy: open (<see cref="CatalogSourceOpener"/>) and dedup the enabled
/// sources, fan out, apply the query's <see cref="StalePolicy"/>, then rank
/// with a caller-supplied comparer. <see cref="MergedKnowledgeResolver"/> and
/// <see cref="PriorityWeightedKnowledgeResolver"/> differ ONLY in that
/// comparer -- keeping the rest here means dedup semantics, diagnostics, and
/// the never-throw contract cannot drift between the two.
/// </summary>
`````

In `src/OKF4net.Catalog/FusedResolverEngine.cs`, find:

`````csharp
        }

        var diagnostics = new List<KnowledgeDiagnostic>();

        // Resolve + dedup BEFORE searching. Two manifest entries pointing at
        // the same directory are the same bundle: searching both would load
        // and score it twice only to discard half the results. enabledSources
        // is already in priority-then-id order, so the first entry reaching a
        // given directory is the survivor by construction; a later duplicate
        // is skipped and reported (see below), not just silently dropped.
        var seenDirectories = new Dictionary<string, string>(StringComparer.FromComparison(CatalogPathResolver.PathComparison));
        var resolved = new List<(KnowledgeCatalogSource Source, string Directory)>();

        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var directory, out var pathDiagnostic))
            {
                diagnostics.Add(new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    source.Id,
                    $"Source '{source.Id}' path could not be re-resolved: {pathDiagnostic!.Message}"));
                continue;
            }

            if (seenDirectories.TryGetValue(directory!, out var survivingSourceId))
            {
                diagnostics.Add(new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.DuplicateDirectory,
                    source.Id,
                    $"Source '{source.Id}' resolves to the same directory ('{directory}') as source '{survivingSourceId}', which was already resolved; skipped to avoid searching the same bundle twice."));
                continue;
            }

            seenDirectories.Add(directory!, source.Id);
            resolved.Add((source, directory!));
        }

        var ranked = new List<RankedPassage>();
        var anySourceSearchedSuccessfully = false;
        var now = clock.Now;

        foreach (var (source, directory) in resolved)
        {
            ct.ThrowIfCancellationRequested();

            var bundleSource = new OkfBundleKnowledgeSource(source.Id, directory);
            var result = await bundleSource.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
`````

and replace it with:

`````csharp
        }

        var diagnostics = new List<KnowledgeDiagnostic>();

        // Open + dedup BEFORE searching. Two manifest entries over the same
        // storage are the same bundle: searching both would load and score it
        // twice only to discard half the results. The identity is the
        // provider's Id (spec §9; the canonical root for the filesystem, so
        // the result is the one the resolved directory gave), or that
        // directory for a source without a handle. enabledSources is already
        // in priority-then-id order, so the first entry reaching a given
        // storage is the survivor by construction; a later duplicate is
        // skipped and reported (see below), not just silently dropped.
        var seenIdentities = new Dictionary<string, string>(StringComparer.FromComparison(CatalogPathResolver.PathComparison));
        var opened = new List<(KnowledgeCatalogSource Source, OkfBundleKnowledgeSource Bundle)>();

        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogSourceOpener.TryOpen(catalog, snapshot, source, out var bundleSource, out var identity, out var location, out var unavailable))
            {
                diagnostics.Add(unavailable!);
                continue;
            }

            if (seenIdentities.TryGetValue(identity!, out var survivingSourceId))
            {
                diagnostics.Add(new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.DuplicateDirectory,
                    source.Id,
                    $"Source '{source.Id}' resolves to the same directory ('{location}') as source '{survivingSourceId}', which was already resolved; skipped to avoid searching the same bundle twice."));
                continue;
            }

            seenIdentities.Add(identity!, source.Id);
            opened.Add((source, bundleSource!));
        }

        var ranked = new List<RankedPassage>();
        var anySourceSearchedSuccessfully = false;
        var now = clock.Now;

        foreach (var (source, bundleSource) in opened)
        {
            ct.ThrowIfCancellationRequested();

            var result = await bundleSource.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
`````

In `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`, find:

`````csharp
/// are never searched here; they feed <c>IMemoryStore</c> instead (spec §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Each enabled <see cref="KnowledgeCatalogSource"/> only ever carries a
/// manifest-relative <see cref="KnowledgeCatalogSource.Path"/> (see Task 4);
/// this resolver re-derives each source's resolved, absolute bundle
/// directory itself, on every search, via <see cref="CatalogPathResolver.TryResolve"/>
/// using <see cref="IKnowledgeCatalog.CatalogRoot"/> and the current
/// snapshot's <see cref="KnowledgeCatalogSnapshot.ManifestDirectory"/> --
/// the same containment check <see cref="FileKnowledgeCatalog"/> already
/// applies to every enabled source at load/reload time. A source that fails
/// that re-resolution (e.g. its directory was deleted after the catalog last
/// loaded) yields a <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>
/// diagnostic for that source alone; the remaining sources are still
/// searched.
/// </para>
/// <para>
/// A fresh, stateless <see cref="OkfBundleKnowledgeSource"/> is constructed
/// per enabled source per search -- consistent with that type's own
/// per-call <see cref="Bundle.Load(string)"/> design; see its remarks.
/// </para>
/// </remarks>
public sealed class GroupedKnowledgeResolver : IKnowledgeResolver
{
`````

and replace it with:

`````csharp
/// are never searched here; they feed <c>IMemoryStore</c> instead (spec §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Each enabled source is searched through the <see cref="Providers.BundleHandle"/> the
/// catalog keeps for it (<see cref="KnowledgeCatalogSnapshot.Handles"/>, spec §9): the
/// image survives from one search to the next and is refreshed under the source's policy
/// (<see cref="KnowledgeCatalogOptions.RefreshPolicy"/>), and the catalog root's confinement
/// is re-checked by the provider before every access. A source whose bundle never loaded
/// yields a <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/> diagnostic for that
/// source alone; the remaining sources are still searched.
/// </para>
/// <para>
/// A snapshot that carries no handle for a source (one built by hand, or by
/// <see cref="CatalogManifestParser.TryParse"/>) is served as before the seam: the source's
/// manifest-relative <see cref="KnowledgeCatalogSource.Path"/> is re-resolved at every
/// search via <see cref="CatalogPathResolver.TryResolve"/>, with the same containment check,
/// and a fresh <see cref="OkfBundleKnowledgeSource"/> loads the bundle for that search
/// (<see cref="CatalogSourceOpener"/>).
/// </para>
/// </remarks>
public sealed class GroupedKnowledgeResolver : IKnowledgeResolver
{
`````

In `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`, find:

`````csharp
        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogPathResolver.TryResolve(_catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var resolvedDirectory, out var pathDiagnostic))
            {
                diagnostics.Add(new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    source.Id,
                    $"Source '{source.Id}' path could not be re-resolved: {pathDiagnostic!.Message}"));
                continue;
            }

            var bundleSource = new OkfBundleKnowledgeSource(source.Id, resolvedDirectory!);
            var result = await bundleSource.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
                diagnostics.Add(result.Diagnostic);
`````

and replace it with:

`````csharp
        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogSourceOpener.TryOpen(_catalog, snapshot, source, out var bundleSource, out _, out _, out var unavailable))
            {
                diagnostics.Add(unavailable!);
                continue;
            }

            var result = await bundleSource!.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
                diagnostics.Add(result.Diagnostic);
`````

In `src/OKF4net.Catalog/IKnowledgeCatalog.cs`, find:

`````csharp
    /// every enabled source's <c>path</c> was (and, on reload, is re-)
    /// validated against via <see cref="CatalogPathResolver.TryResolve"/>
    /// (see <see cref="KnowledgeCatalogOptions.CatalogRoot"/>). Exposed so a
    /// caller holding only an <see cref="IKnowledgeCatalog"/> -- e.g. a
    /// <see cref="GroupedKnowledgeResolver"/> -- can re-resolve a source's
    /// resolved bundle directory from <see cref="KnowledgeCatalogSource.Path"/>
    /// and <see cref="KnowledgeCatalogSnapshot.ManifestDirectory"/> without
    /// the snapshot itself having to carry resolved, absolute paths.
    /// </summary>
    string CatalogRoot { get; }

    /// <summary>
`````

and replace it with:

`````csharp
    /// every enabled source's <c>path</c> was (and, on reload, is re-)
    /// validated against via <see cref="CatalogPathResolver.TryResolve"/>
    /// (see <see cref="KnowledgeCatalogOptions.CatalogRoot"/>). Exposed so a
    /// caller holding only an <see cref="IKnowledgeCatalog"/> -- e.g. a
    /// <see cref="GroupedKnowledgeResolver"/> over a snapshot that carries no
    /// handle for a source -- can re-resolve that source's bundle directory
    /// from <see cref="KnowledgeCatalogSource.Path"/> and
    /// <see cref="KnowledgeCatalogSnapshot.ManifestDirectory"/>; a source with
    /// a handle (<see cref="KnowledgeCatalogSnapshot.Handles"/>) is served from
    /// it, its provider confined to this root.
    /// </summary>
    string CatalogRoot { get; }

    /// <summary>
`````

- [ ] **Step 4: Run the catalog group**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog" -nologo -v q`
Expected: `Passed: 278, Skipped: 1, Total: 279` (+9; the junction fact runs on Windows, skips without a junction or symlink).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/CatalogSourceOpener.cs src/OKF4net.Catalog/GroupedKnowledgeResolver.cs src/OKF4net.Catalog/FusedResolverEngine.cs src/OKF4net.Catalog/IKnowledgeCatalog.cs tests/OKF4net.Tests/Catalog/CatalogResolverHandleTests.cs
git commit -m "feat(catalog): the resolvers search through the snapshot's handles and deduplicate by provider id

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (Task 10): `### Changed` "**A catalog source no longer reloads its bundle at every request** …".

### Task 7: `FileMemoryStore` through the provider; `DeleteScopeAsync` reports per tier

**Files:**
- Modify: `src/OKF4net.Catalog/FileMemoryStore.cs` (whole file), `MemoryTypes.cs:20-21`, `IMemoryStore.cs:33-34`
- Modify: `samples/catalog-explorer/src/CatalogExplorer/Program.cs:105` (the sample prints the new result; `samples/` is outside `OKF4net.sln`, so only its own build tells)
- Test: `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs` (three deletion tests re-pinned, the junction test extended, three tests added), `tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs:280`

**Interfaces:**
- Consumes: `FileSystemBundleProvider(root, confinementBoundary:)`, `Exists`, `Enumerate`, `Delete(key, BundleWriteCondition.None)`, `BundleAccessRefusedException`, `BundleProviderException`; `BundleKey.Parse`/`TryParse(raw, out key, out reason)`/`Segments`; `BundleWriteReport(Written, Deleted, Failed)`, `BundleWriteFailure(Key, Reason)`; `BundleConceptWriter(IBundleProvider)`; `DebugQuote.Quote` (internal); Task 3's `OkfBundleKnowledgeSource.RelativePathOf`.
- Produces: `public sealed record MemoryDeleteResult(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error)` (**breaking**: `TiersDeleted` is gone), `public sealed record MemoryTierDeletion(MemoryTier Tier, BundleWriteReport Report)`; `IMemoryStore.DeleteScopeAsync` returns the new record (decision L10).

- [ ] **Step 1: Write the failing tests**

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, find:

`````csharp
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(b, Entry("bob data"), MemoryTier.User);

        var del = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, b)));
    }

    [Fact]
    public async Task Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

and replace it with:

`````csharp
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(b, Entry("bob data"), MemoryTier.User);

        var del = await store.DeleteScopeAsync(a, MemoryTier.User);
        var deletion = Assert.Single(del.Tiers);
        Assert.Equal(MemoryTier.User, deletion.Tier);
        Assert.Equal([$"{MemoryPath.For(MemoryTier.User, a)}/2026-07-27.md"], deletion.Report.Deleted.Select(k => k.ToString()).ToList());
        Assert.True(deletion.Report.Succeeded);
        Assert.Null(del.Error);

        // The scope's objects are gone and its directory is left in place (spec §8): empty, it
        // holds no key, so the scope no longer exists for the store; the sibling scope is intact.
        Assert.Empty(Directory.EnumerateFiles(MemPath(tmp.Path, MemoryTier.User, a), "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, b, "2026-07-27.md")));
        Assert.Empty(await store.EnumerateAsync(a));
        Assert.Single(await store.EnumerateAsync(b));
    }

    [Fact]
    public async Task Deleting_an_absent_or_already_emptied_scope_touches_no_tier()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");

        var absent = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Empty(absent.Tiers);
        Assert.Null(absent.Error);

        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        Assert.Single((await store.DeleteScopeAsync(a, MemoryTier.User)).Tiers);
        var again = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Empty(again.Tiers);
        Assert.Null(again.Error);
        Assert.Empty((await store.ReadAsync(a, new KnowledgeQuery("alice"))).Passages);
    }

    [SkippableFact]
    public async Task A_object_that_cannot_be_deleted_is_reported_and_the_deletion_can_be_run_again()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "holding a file open with FileShare.None blocks its deletion on Windows only");
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(a, new MemoryEntry("2026-07-28", Frontmatter, "## 10:00:00 UTC\n\nmore\n"), MemoryTier.User);
        var heldPath = MemPath(tmp.Path, MemoryTier.User, a, "2026-07-27.md");

        MemoryDeleteResult partial;
        using (new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            partial = await store.DeleteScopeAsync(a, MemoryTier.User);
        }

        var deletion = Assert.Single(partial.Tiers);
        Assert.Equal([$"{MemoryPath.For(MemoryTier.User, a)}/2026-07-28.md"], deletion.Report.Deleted.Select(k => k.ToString()).ToList());
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal($"{MemoryPath.For(MemoryTier.User, a)}/2026-07-27.md", failure.Key.ToString());
        Assert.StartsWith("Memory tier 'User' subtree could not be deleted: ", partial.Error);
        Assert.True(File.Exists(heldPath));

        var again = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.True(Assert.Single(again.Tiers).Report.Succeeded);
        Assert.Null(again.Error);
        Assert.False(File.Exists(heldPath));
    }

    [Fact]
    public async Task Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, find:

`````csharp
        await store.WriteAsync(a, Entry("session a data"), MemoryTier.Session);
        await store.WriteAsync(b, Entry("session b data"), MemoryTier.Session);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Session);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Session, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.Session, b)));
    }

    [Fact]
    public async Task Session_Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

and replace it with:

`````csharp
        await store.WriteAsync(a, Entry("session a data"), MemoryTier.Session);
        await store.WriteAsync(b, Entry("session b data"), MemoryTier.Session);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Session);
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Null(del.Error);

        Assert.Empty(Directory.EnumerateFiles(MemPath(tmp.Path, MemoryTier.Session, a), "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Session, b, "2026-07-27.md")));
    }

    [Fact]
    public async Task Session_Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, find:

`````csharp
        await store.WriteAsync(a, Entry("tenant a data"), MemoryTier.Tenant);
        await store.WriteAsync(b, Entry("tenant b data"), MemoryTier.Tenant);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Tenant);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Tenant, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.Tenant, b)));
    }

    [Fact]
    public async Task Tenant_Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

and replace it with:

`````csharp
        await store.WriteAsync(a, Entry("tenant a data"), MemoryTier.Tenant);
        await store.WriteAsync(b, Entry("tenant b data"), MemoryTier.Tenant);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Tenant);
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Null(del.Error);

        Assert.Empty(Directory.EnumerateFiles(MemPath(tmp.Path, MemoryTier.Tenant, a), "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Tenant, b, "2026-07-27.md")));
    }

    [Fact]
    public async Task Tenant_Read_ConceptId_is_fully_qualified_matching_Enumerate()
`````

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, find:

`````csharp
        var store = UserStore(tmp);

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        Assert.Equal(0, delete.TiersDeleted);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete.", delete.Error);
        Assert.True(File.Exists(outsideFile));
    }
}
`````

and replace it with:

`````csharp
        var store = UserStore(tmp);

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        var deletion = Assert.Single(delete.Tiers);
        Assert.Empty(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete.", delete.Error);
        Assert.Equal(delete.Error, failure.Reason);
        Assert.True(File.Exists(outsideFile));

        // The same chain refuses a read and an enumeration, with the read's diagnostic.
        var read = await store.ReadAsync(scope, new KnowledgeQuery("survive"));
        Assert.Empty(read.Passages);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to read.", Assert.Single(read.Diagnostics).Message);
        Assert.Empty(await store.EnumerateAsync(scope));
    }

    [SkippableFact]
    public async Task A_scope_directory_that_is_itself_a_junction_is_refused_by_every_operation()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        external.Write("2026-07-27.md", "---\ntype: AgentMemory\ntitle: t\ndescription: d\n---\nsecret outside\n");
        var scopeDir = Path.Combine(MemoryPath.For(MemoryTier.User, scope).Split('/'));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(scopeDir, external.Path), "needs a junction or a directory symlink");
        var store = UserStore(tmp);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("secret"));
        Assert.Empty(read.Passages);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to read.", Assert.Single(read.Diagnostics).Message);
        Assert.Empty(await store.EnumerateAsync(scope));

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete.", delete.Error);
        Assert.True(File.Exists(Path.Combine(external.Path, "2026-07-27.md")));
    }
}
`````

In `tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs`, find:

`````csharp
            var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

            Assert.False(write.Written);
            Assert.Equal([keep], outsideAfterWrite);
            Assert.Equal(0, delete.TiersDeleted);
        }

        var catalogError = Assert.IsType<CatalogException>(error);
        Assert.Contains("or an entry that could not be inspected", catalogError.Message, StringComparison.Ordinal);
`````

and replace it with:

`````csharp
            var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

            Assert.False(write.Written);
            Assert.Equal([keep], outsideAfterWrite);
            Assert.All(delete.Tiers, t => Assert.Empty(t.Report.Deleted));
        }

        var catalogError = Assert.IsType<CatalogException>(error);
        Assert.Contains("or an entry that could not be inspected", catalogError.Message, StringComparison.Ordinal);
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~FileMemoryStoreTests" -nologo -v q`
Expected: the build fails: `error CS1061: 'MemoryDeleteResult' does not contain a definition for 'Tiers'` (and `'TiersDeleted'` once the record changes, hence the hosting test's one-line edit in this task).

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// Filesystem <see cref="IMemoryStore"/>. Path derivation is isolated in
/// <see cref="MemoryPath.For"/>; writes reuse the core
/// <see cref="OKF4net.BundleConceptWriter"/> (producer validation + per-path
/// lock + reparse guards) over the tier's memory source root. Read/write/
/// delete/enumerate are all generic over <see cref="MemoryTier"/> -- a tier
/// absent from the configured roots is simply treated as "no source
/// configured" (errors-as-data), not a special case per tier.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    /// <summary>
    /// The comparison used by <see cref="IsReparseEscaped"/>'s ancestor walk.
    /// <see cref="StringComparison.Ordinal"/> on every platform: case-sensitivity
    /// is a runtime property of the specific volume, not of the OS, and every
    /// scoped subdirectory this class walks is built via
    /// <see cref="Path.Combine(string[])"/> from the same tier root passed to
    /// <see cref="IsReparseEscaped"/>, so its prefix always keeps that root's
    /// exact casing -- <c>Ordinal</c> has no cost for legitimate input.
    /// </summary>
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    private readonly IReadOnlyDictionary<MemoryTier, string> _tierRoots;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierRoots = tierRoots;
    }

    /// <inheritdoc/>
    public ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default)
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Filesystem <see cref="IMemoryStore"/>, through the provider seam (spec §9). Path
/// derivation is isolated in <see cref="MemoryPath.For"/>; every read, write, deletion and
/// enumeration goes through a <see cref="FileSystemBundleProvider"/>: one per tier root,
/// and, for a scope, one rooted at the scope's directory and confined to the tier root, so
/// the chain from the tier root to the scope is re-checked STRICTly before every access (a
/// reparse point or an entry that cannot be inspected on it refuses the scope: a guard
/// fails closed). Writes reuse the core <see cref="OKF4net.BundleConceptWriter"/>
/// (producer validation + per-path lock + the provider's guards) over the tier's provider.
/// Read/write/delete/enumerate are all generic over <see cref="MemoryTier"/> -- a tier
/// absent from the configured roots is simply treated as "no source configured"
/// (errors-as-data), not a special case per tier.
/// </summary>
/// <remarks>
/// <para><b>A scope exists while it holds a key</b> (spec §9, decision 14): a scope whose
/// directory is empty, or absent, is read and enumerated as nothing and deleted as a no-op.
/// A scope is loaded at every read, as before (no <see cref="BundleHandle"/>; spec §5.3).</para>
/// <para><b>Deleting a scope</b> enumerates the keys under its prefix and deletes each object
/// on its own (<see cref="IBundleProvider.Delete"/>, unconditional); there is no multi-object
/// transaction, and the result says per tier what was deleted and what failed
/// (<see cref="MemoryTierDeletion.Report"/>), so a failed deletion can be run again. The
/// directories are left in place (spec §8): empty, they hold no key, so the scope no longer
/// exists for the store.</para>
/// </remarks>
public sealed class FileMemoryStore : IMemoryStore
{
    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    /// <summary>Any key: <see cref="IBundleProvider.Exists"/> on it runs the scope provider's boundary check before anything is listed.</summary>
    private static readonly BundleKey ProbeKey = BundleKey.Parse("index.md");

    private readonly IReadOnlyDictionary<MemoryTier, string> _tierRoots;
    private readonly Dictionary<MemoryTier, FileSystemBundleProvider> _tierProviders;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierRoots = tierRoots;
        _tierProviders = tierRoots.ToDictionary(pair => pair.Key, pair => new FileSystemBundleProvider(pair.Value));
    }

    private enum ScopeState
    {
        /// <summary>No key under the scope's prefix.</summary>
        Absent,

        /// <summary>At least one key under the scope's prefix.</summary>
        Present,

        /// <summary>The provider refuses the chain from the tier root to the scope (a reparse point, or an entry that cannot be inspected).</summary>
        Refused,
    }

    /// <inheritdoc/>
    public ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default)
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            // Computed once per tier -- MemoryPath.For hashes a segment per
            // call -- and reused both to build subDir below and as every
            // hit's ConceptId prefix further down.
            var conceptIdPrefix = MemoryPath.For(tier, scope);
            var subDir = ScopedDir(root, conceptIdPrefix);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point or could not be inspected; refusing to read."));
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
            }
            catch (OkfException e)
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' could not be loaded: {e.Message}"));
`````

and replace it with:

`````csharp

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierProviders.TryGetValue(tier, out var tierProvider))
            {
                continue;
            }

            // Computed once per tier -- MemoryPath.For hashes a segment per
            // call -- and reused both to open the scope below and as every
            // hit's ConceptId prefix further down.
            var conceptIdPrefix = MemoryPath.For(tier, scope);
            var prefix = BundleKey.Parse(conceptIdPrefix);
            var scopeProvider = ScopeProvider(tier, prefix);
            switch (Probe(tierProvider, scopeProvider, prefix))
            {
                case ScopeState.Absent:
                    continue;
                case ScopeState.Refused:
                    diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point or could not be inspected; refusing to read."));
                    continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(scopeProvider);
            }
            catch (OkfException e)
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' could not be loaded: {e.Message}"));
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp
                    ConceptId: $"{conceptIdPrefix}/{hit.Concept.Id}",
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path).Replace(Path.DirectorySeparatorChar, '/'),
                    Lifecycle: fm.Lifecycle));
            }
        }
`````

and replace it with:

`````csharp
                    ConceptId: $"{conceptIdPrefix}/{hit.Concept.Id}",
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: OkfBundleKnowledgeSource.RelativePathOf(bundle, hit.Concept),
                    Lifecycle: fm.Lifecycle));
            }
        }
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        if (!_tierRoots.TryGetValue(tier, out var root))
        {
            return new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, $"No memory source configured for tier '{tier}'."));
        }

        var conceptId = $"{MemoryPath.For(tier, scope)}/{entry.ConceptName}";
        var writer = new BundleConceptWriter(root);
        var section = entry.SectionMarkdown;
        var result = writer.AppendToConceptAtomic(
            conceptId,
            entry.FrontmatterYamlIfCreating,
`````

and replace it with:

`````csharp
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        if (!_tierProviders.TryGetValue(tier, out var tierProvider))
        {
            return new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, $"No memory source configured for tier '{tier}'."));
        }

        var conceptId = $"{MemoryPath.For(tier, scope)}/{entry.ConceptName}";
        var writer = new BundleConceptWriter(tierProvider);
        var section = entry.SectionMarkdown;
        var result = writer.AppendToConceptAtomic(
            conceptId,
            entry.FrontmatterYamlIfCreating,
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tiers = tier is { } t ? [t] : ReadOrder.Where(x => IsApplicable(x, scope));
        var deleted = 0;
        var errors = new List<string>();

        foreach (var currentTier in tiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tierRoots.TryGetValue(currentTier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, MemoryPath.For(currentTier, scope));
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                errors.Add($"Memory tier '{currentTier}' path is a reparse point or could not be inspected; refusing to delete.");
                continue;
            }

            try
            {
                Directory.Delete(subDir, recursive: true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {e.Message}");
            }
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return new ValueTask<MemoryDeleteResult>(new MemoryDeleteResult(deleted, error));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default)
`````

and replace it with:

`````csharp
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tiers = tier is { } t ? [t] : ReadOrder.Where(x => IsApplicable(x, scope));
        var deletions = new List<MemoryTierDeletion>();
        var errors = new List<string>();

        foreach (var currentTier in tiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tierProviders.TryGetValue(currentTier, out var tierProvider))
            {
                continue;
            }

            var prefix = BundleKey.Parse(MemoryPath.For(currentTier, scope));
            switch (Probe(tierProvider, ScopeProvider(currentTier, prefix), prefix))
            {
                case ScopeState.Absent:
                    continue;
                case ScopeState.Refused:
                    var refusal = $"Memory tier '{currentTier}' path is a reparse point or could not be inspected; refusing to delete.";
                    errors.Add(refusal);
                    deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], [], [new BundleWriteFailure(prefix, refusal)])));
                    continue;
            }

            var deleted = new List<BundleKey>();
            var failed = new List<BundleWriteFailure>();
            foreach (var listing in tierProvider.Enumerate(prefix, BundleLimits.Default).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!BundleKey.TryParse(listing.RawName, out var key, out var why))
                {
                    failed.Add(new BundleWriteFailure(prefix, $"{DebugQuote.Quote(listing.RawName)} is not a key ({why}), so it cannot be deleted through the provider."));
                    continue;
                }

                try
                {
                    tierProvider.Delete(key, BundleWriteCondition.None);
                    deleted.Add(key);
                }
                catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
                {
                    failed.Add(new BundleWriteFailure(key, e.Message));
                }
            }

            if (failed.Count > 0)
            {
                errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {string.Join("; ", failed.Select(f => f.Reason))}");
            }

            deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], deleted.AsReadOnly(), failed.AsReadOnly())));
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return new ValueTask<MemoryDeleteResult>(new MemoryDeleteResult(deletions.AsReadOnly(), error));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default)
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            var prefix = MemoryPath.For(tier, scope);
            var subDir = ScopedDir(root, prefix);
            if (!Directory.Exists(subDir) || IsReparseEscaped(root, subDir))
            {
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
            }
            catch (OkfException)
            {
                continue;
            }

            foreach (var concept in bundle.Concepts)
            {
                concepts.Add(new MemoryConcept(tier, $"{prefix}/{concept.Id}", concept.Document.Frontmatter.Title));
            }
        }

        return new ValueTask<IReadOnlyList<MemoryConcept>>(concepts.AsReadOnly());
`````

and replace it with:

`````csharp

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierProviders.TryGetValue(tier, out var tierProvider))
            {
                continue;
            }

            var conceptIdPrefix = MemoryPath.For(tier, scope);
            var prefix = BundleKey.Parse(conceptIdPrefix);
            var scopeProvider = ScopeProvider(tier, prefix);
            if (Probe(tierProvider, scopeProvider, prefix) != ScopeState.Present)
            {
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(scopeProvider);
            }
            catch (OkfException)
            {
                continue;
            }

            foreach (var concept in bundle.Concepts)
            {
                concepts.Add(new MemoryConcept(tier, $"{conceptIdPrefix}/{concept.Id}", concept.Document.Frontmatter.Title));
            }
        }

        return new ValueTask<IReadOnlyList<MemoryConcept>>(concepts.AsReadOnly());
`````

In `src/OKF4net.Catalog/FileMemoryStore.cs`, find:

`````csharp
        _ => false,
    };

    /// <summary>
    /// Combines <paramref name="root"/> with an already-computed
    /// <see cref="MemoryPath.For"/> prefix. Takes the prefix rather than
    /// <c>(tier, scope)</c> so callers that also need the prefix for another
    /// purpose (e.g. a concept id) compute it once and pass it in, instead
    /// of this method silently recomputing it (<see cref="MemoryPath.For"/>
    /// hashes a segment per call).
    /// </summary>
    private static string ScopedDir(string root, string prefix) =>
        Path.Combine([root, .. prefix.Split('/')]);

    private static bool IsReparseEscaped(string root, string subDir)
    {
        // CanonicalizeRoot matters here: a host-configured tier root with a
        // trailing separator would otherwise defeat the ancestor walk -- see
        // its remarks. Strict predicates: this guards reads, enumeration and a
        // recursive DELETE of the scope subtree, so a directory whose link
        // status cannot be read is refused like a link (a guard fails closed --
        // see ReparsePoints.IsReparsePointOrUninspectable).
        var fullRoot = ReparsePoints.CanonicalizeRoot(root);
        var full = Path.GetFullPath(subDir);
        return ReparsePoints.IsReparsePointOrUninspectable(full) || ReparsePoints.HasReparsePointOrUninspectableAncestor(fullRoot, full, PathComparison);
    }
}
`````

and replace it with:

`````csharp
        _ => false,
    };

    /// <summary>
    /// A provider rooted at the scope's directory (the tier root plus the prefix's segments,
    /// the same string <c>Path.Combine</c> gave before the seam) and confined to the tier
    /// root: the chain from that root to the scope is re-checked STRICTly before every
    /// access (spec §8), so the scope's concepts load with the ids and links a bundle rooted
    /// there has, as before.
    /// </summary>
    private FileSystemBundleProvider ScopeProvider(MemoryTier tier, BundleKey prefix) =>
        new(Path.Combine([_tierRoots[tier], .. prefix.Segments]), confinementBoundary: _tierRoots[tier]);

    /// <summary>
    /// The scope's state: the chain from the tier root to the scope is checked first
    /// (<see cref="IBundleProvider.Exists"/> on the scope provider runs its boundary check
    /// before anything is listed, and answers for an absent root without throwing), then the
    /// tier provider lists the keys under the scope's prefix.
    /// </summary>
    private static ScopeState Probe(FileSystemBundleProvider tierProvider, FileSystemBundleProvider scopeProvider, BundleKey prefix)
    {
        try
        {
            scopeProvider.Exists(ProbeKey);
            return tierProvider.Enumerate(prefix, BundleLimits.Default).Any() ? ScopeState.Present : ScopeState.Absent;
        }
        catch (BundleAccessRefusedException)
        {
            return ScopeState.Refused;
        }
    }
}
`````

In `src/OKF4net.Catalog/IMemoryStore.cs`, find:

`````csharp

    /// <summary>Writes <paramref name="entry"/> into the scope's <paramref name="tier"/> memory (create-or-append, atomic).</summary>
    ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default);

    /// <summary>Deletes a scope's memory subtree for one tier, or (when <paramref name="tier"/> is null) every applicable configured tier.</summary>
    ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default);

    /// <summary>Lists the concepts stored for a scope across its applicable configured tiers (audit / DSAR).</summary>
    ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default);
`````

and replace it with:

`````csharp

    /// <summary>Writes <paramref name="entry"/> into the scope's <paramref name="tier"/> memory (create-or-append, atomic).</summary>
    ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default);

    /// <summary>Deletes a scope's memory for one tier, or (when <paramref name="tier"/> is null) every applicable configured tier: each object under the scope on its own, with a report per tier of what was deleted and what failed (spec §1, §9).</summary>
    ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default);

    /// <summary>Lists the concepts stored for a scope across its applicable configured tiers (audit / DSAR).</summary>
    ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default);
`````

In `src/OKF4net.Catalog/MemoryTypes.cs`, find:

`````csharp

/// <summary>The result of a scoped memory write: whether it was written, and the error text if not.</summary>
public sealed record MemoryWriteResult(bool Written, string? Error);

/// <summary>The result of a scoped memory deletion: how many tier subtrees were removed, and the error text if any.</summary>
public sealed record MemoryDeleteResult(int TiersDeleted, string? Error);

/// <summary>One stored memory concept, for enumeration/audit.</summary>
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
`````

and replace it with:

`````csharp

/// <summary>The result of a scoped memory write: whether it was written, and the error text if not.</summary>
public sealed record MemoryWriteResult(bool Written, string? Error);

/// <summary>
/// The result of a scoped memory deletion (spec §1, §9): one <see cref="MemoryTierDeletion"/>
/// per tier whose scope held something or was refused, in read order, and the error text
/// when any tier failed. A tier whose scope held no key is absent from <see cref="Tiers"/>.
/// </summary>
/// <param name="Tiers">What each tier's deletion did, with the keys deleted and those that failed.</param>
/// <param name="Error">The failures of every tier joined by <c>; </c>, or <see langword="null"/> when nothing failed.</param>
public sealed record MemoryDeleteResult(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error);

/// <summary>
/// What deleting one tier's scope did: every object under the scope's prefix is deleted on
/// its own, so the <see cref="OKF4net.Providers.BundleWriteReport"/> says exactly which keys
/// were deleted and which failed, with why; a scope the provider refused (a reparse point
/// on its path) reports one failure under the scope's prefix and nothing deleted.
/// </summary>
/// <param name="Tier">The tier.</param>
/// <param name="Report">The keys deleted, in order, and the failures.</param>
public sealed record MemoryTierDeletion(MemoryTier Tier, OKF4net.Providers.BundleWriteReport Report);

/// <summary>One stored memory concept, for enumeration/audit.</summary>
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
`````

In `samples/catalog-explorer/src/CatalogExplorer/Program.cs`, find:

`````csharp
    Console.WriteLine($"  read diagnostic: [{diagnostic.Code}] source={diagnostic.SourceId} {diagnostic.Message}");
}

var deleteResult = await memoryStore.DeleteScopeAsync(employeeScope);
Console.WriteLine($"  cleanup: tiersDeleted={deleteResult.TiersDeleted} error={deleteResult.Error}");

return 0;

static string? FindRepoRoot()
`````

and replace it with:

`````csharp
    Console.WriteLine($"  read diagnostic: [{diagnostic.Code}] source={diagnostic.SourceId} {diagnostic.Message}");
}

var deleteResult = await memoryStore.DeleteScopeAsync(employeeScope);
Console.WriteLine($"  cleanup: tiersDeleted={deleteResult.Tiers.Count(t => t.Report.Succeeded)} objectsDeleted={deleteResult.Tiers.Sum(t => t.Report.Deleted.Count)} error={deleteResult.Error}");

return 0;

static string? FindRepoRoot()
`````

- [ ] **Step 4: Run the catalog group**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog" -nologo -v q`
Expected: `Passed: 281, Skipped: 1, Total: 282` (+3; the two junction facts and the held-open fact run on Windows, skip elsewhere).
Run: `dotnet build samples/catalog-explorer/src/CatalogExplorer/CatalogExplorer.csproj -nologo -v q` — expected 0 errors (without the sample's one-line edit: `error CS1061: 'MemoryDeleteResult' does not contain a definition for 'TiersDeleted'`).

- [ ] **Step 5: Commit** (two commits, the sample on its own)

```bash
git add src/OKF4net.Catalog/FileMemoryStore.cs src/OKF4net.Catalog/MemoryTypes.cs src/OKF4net.Catalog/IMemoryStore.cs tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs
git commit -m "feat(catalog): FileMemoryStore reads, enumerates and deletes through the provider; DeleteScopeAsync reports per tier

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git add samples/catalog-explorer/src/CatalogExplorer/Program.cs
git commit -m "samples(catalog-explorer): print the per-tier memory deletion report

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entries (Task 10): `### Added` "**`MemoryTierDeletion`** …"; `### Changed (breaking)` "**`MemoryDeleteResult` is `(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error)`** …" with its migration sentence; `### Changed` "**`FileMemoryStore` reads, enumerates and deletes through the provider** …".

### Task 8: A memory scope segment whose base is a Windows reserved device name is prefixed with `_` (D4)

**Files:**
- Modify: `src/OKF4net/Providers/BundleKeyValidator.cs` (after `ReservedBases`, `:29-34`; the reserved-name check `:408-419`)
- Modify: `src/OKF4net.Catalog/MemoryPath.cs:82-94`
- Test: `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs` (two theories before `Tenant_tier_keeps_its_literal_prefix…`), `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs` (one round-trip fact)

**Interfaces:**
- Consumes: `BundleKeyValidator`'s private `ReservedBases`; `MemoryPath.For`; `KnowledgeAccessScope` (segments are `[A-Za-z0-9_][A-Za-z0-9_.-]*`: ASCII, so `COM¹`-style names never reach the encoder).
- Produces: `internal static bool BundleKeyValidator.HasWindowsReservedBase(string segment)` (visible to `OKF4net.Catalog`), used by `MemoryPath.Encode` and by the validator's own reserved-name rule (one list, decision L11).

- [ ] **Step 1: Write the failing tests**

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, find:

`````csharp
        Assert.Empty(await store.EnumerateAsync(a));
        Assert.Single(await store.EnumerateAsync(b));
    }

    [Fact]
    public async Task Deleting_an_absent_or_already_emptied_scope_touches_no_tier()
    {
        using var tmp = new TempDir();
`````

and replace it with:

`````csharp
        Assert.Empty(await store.EnumerateAsync(a));
        Assert.Single(await store.EnumerateAsync(b));
    }

    [Fact]
    public async Task A_scope_whose_identifier_has_a_reserved_base_is_written_read_and_deleted_under_its_prefixed_segment()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "con.foo", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("reserved-base notes"), MemoryTier.User);
        Assert.True(write.Written, write.Error);
        Assert.StartsWith("memory-user/_con.foo-", MemoryPath.For(MemoryTier.User, scope));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, scope, "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("reserved-base"));
        Assert.Single(read.Passages);
        Assert.Single(await store.EnumerateAsync(scope));

        var del = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Empty(await store.EnumerateAsync(scope));
    }

    [Fact]
    public async Task Deleting_an_absent_or_already_emptied_scope_touches_no_tier()
    {
        using var tmp = new TempDir();
`````

In `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs`, find:

`````csharp
        Assert.NotEmpty(hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"hash char '{c}' is not lowercase hex"));
    }

    [Fact]
    public void Tenant_tier_keeps_its_literal_prefix_with_an_encoded_segment()
    {
        var segments = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme")).Split('/');
`````

and replace it with:

`````csharp
        Assert.NotEmpty(hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"hash char '{c}' is not lowercase hex"));
    }

    // ---- Reserved Windows device names (spec §9, D4) ----------------------

    [Theory]
    [InlineData("con.foo", "_con.foo")]
    [InlineData("CON.Foo", "_con.foo")]
    [InlineData("com1.logs", "_com1.logs")]
    [InlineData("LPT9.x", "_lpt9.x")]
    [InlineData("aux.a.b", "_aux.a.b")]
    public void An_identifier_whose_base_is_a_reserved_device_name_is_prefixed_with_an_underscore(string tenant, string expectedReadable)
    {
        var segment = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: tenant)).Split('/')[1];

        AssertEncoded(segment, expectedReadable);

        // The hash is that of the raw identifier, prefix or not: the same as for an unreserved name.
        var hash = segment[(segment.LastIndexOf('-') + 1)..];
        Assert.Equal(16, hash.Length);
    }

    [Theory]
    [InlineData("con")]
    [InlineData("console.foo")]
    [InlineData("com10.x")]
    [InlineData("acme")]
    [InlineData("con_foo")]
    public void An_identifier_without_a_reserved_base_is_not_prefixed(string tenant)
    {
        var segment = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: tenant)).Split('/')[1];

        AssertEncoded(segment, tenant.ToLowerInvariant());
        Assert.False(segment.StartsWith('_'));
    }

    [Fact]
    public void Tenant_tier_keeps_its_literal_prefix_with_an_encoded_segment()
    {
        var segments = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme")).Split('/');
`````

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~MemoryPathTests|FullyQualifiedName~FileMemoryStoreTests" -nologo -v q`
Expected on Windows: `Failed: 6, Passed: 42, Skipped: 1, Total: 49` — the five rows of `An_identifier_whose_base_is_a_reserved_device_name_is_prefixed_with_an_underscore` (the readable part is `con.foo` where `_con.foo` is expected) and `A_scope_whose_identifier_has_a_reserved_base…` (the write lands on a device name); on Linux and macOS the round trip passes and 5 fail. Every other test of the two classes passes before as after.

- [ ] **Step 3: Write the implementation**

In `src/OKF4net.Catalog/MemoryPath.cs`, find:

`````csharp
    /// injective under case-folding: the <c>hash</c> is a 64-bit (16 lowercase
    /// hex chars) truncation of the SHA-256 of the <b>case-sensitive</b> raw
    /// bytes, so two case-variant segments produce case-insensitively distinct
    /// results. See the type remarks for why (case-insensitive-FS isolation).
    /// </summary>
    private static string Encode(string raw)
    {
        var readable = raw.ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        return $"{readable}-{hash}";
    }
}
`````

and replace it with:

`````csharp
    /// injective under case-folding: the <c>hash</c> is a 64-bit (16 lowercase
    /// hex chars) truncation of the SHA-256 of the <b>case-sensitive</b> raw
    /// bytes, so two case-variant segments produce case-insensitively distinct
    /// results. See the type remarks for why (case-insensitive-FS isolation).
    /// <para>
    /// <b>Reserved names</b> (spec §9, D4): the encoded segment must be a key the portable
    /// profile accepts, and the one rule it could break is a Windows reserved device name as
    /// its base — an identifier whose part before its first <c>.</c> is one (<c>con.foo</c>
    /// gives <c>con.foo-{hash}</c>, whose base is <c>con</c>; the extension does not lift the
    /// reservation). Such a segment is prefixed with <c>_</c> (<c>_con.foo-{hash}</c>); the
    /// hash, computed on the raw identifier, is unchanged and still makes the segment unique.
    /// No other identifier changes.
    /// </para>
    /// </summary>
    private static string Encode(string raw)
    {
        var readable = raw.ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        var segment = $"{readable}-{hash}";
        return OKF4net.Providers.BundleKeyValidator.HasWindowsReservedBase(segment) ? "_" + segment : segment;
    }
}
`````

In `src/OKF4net/Providers/BundleKeyValidator.cs`, find:

`````csharp
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    private static readonly char[] WindowsIllegal = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly IReadOnlyList<Func<BundleKey, string?>> _extra;
`````

and replace it with:

`````csharp
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    /// <summary>
    /// <see langword="true"/> when <paramref name="segment"/>'s base \u2014 the part before its
    /// first <c>.</c>, or the whole segment \u2014 is a reserved Windows device name, whatever the
    /// case: the one check <see cref="BundleKeyConstraints.WindowsReservedNames"/> applies,
    /// for a caller that must avoid such a name before building a key (the memory store's
    /// scope segments, spec \u00a79 D4).
    /// </summary>
    internal static bool HasWindowsReservedBase(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var dot = segment.IndexOf('.');
        var baseName = dot < 0 ? segment : segment[..dot];
        foreach (var reserved in ReservedBases)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly char[] WindowsIllegal = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly IReadOnlyList<Func<BundleKey, string?>> _extra;
`````

In `src/OKF4net/Providers/BundleKeyValidator.cs`, find:

`````csharp
        {
            return "contains a character Windows forbids (< > : \" | ? *)";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames))
        {
            var dot = segment.IndexOf('.');
            var baseName = dot < 0 ? segment : segment[..dot];
            foreach (var reserved in ReservedBases)
            {
                if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return $"has the reserved Windows device name '{reserved}' as its base";
                }
            }
        }

        if (Effective.HasFlag(BundleKeyConstraints.RequireNfc))
        {
`````

and replace it with:

`````csharp
        {
            return "contains a character Windows forbids (< > : \" | ? *)";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames) && HasWindowsReservedBase(segment))
        {
            var dot = segment.IndexOf('.');
            var baseName = dot < 0 ? segment : segment[..dot];
            return $"has the reserved Windows device name '{baseName.ToUpperInvariant()}' as its base";
        }

        if (Effective.HasFlag(BundleKeyConstraints.RequireNfc))
        {
`````

- [ ] **Step 4: Run the catalog, key and portability groups**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Catalog|FullyQualifiedName~BundleKey|FullyQualifiedName~Portability" -nologo -v q`
Expected: `Passed: 416, Skipped: 2, Total: 418` (the catalog group alone: 292 / 1 / 293, +11).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/BundleKeyValidator.cs src/OKF4net.Catalog/MemoryPath.cs tests/OKF4net.Tests/Catalog/MemoryPathTests.cs tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs
git commit -m "feat(catalog): a memory scope segment whose base is a Windows reserved device name is prefixed with an underscore (D4)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entry (Task 10): `### Changed` "**A memory scope segment whose base is a Windows reserved device name is prefixed with `_`** …" with the migration paragraph (also in the Catalog README, Task 10).


### Task 9: The path-based members are obsolete; `Diagnostic` and `AuditFinding` carry their key

The last readers of the path-based members in `src/` are gone (Tasks 1, 2, 3, 7); this task marks them `[Obsolete]`, adds `Key` beside the display path on `Diagnostic` and `AuditFinding`, suppresses the warning in the fourteen test files that pin the projections (one line after each SPDX header: the fourteen hunks below), migrates the one reader in `producers/`, and pins the inventory of obsolete members by reflection.

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (constructor assignments `:149-154`; `Root` `:603-610`; `IndexKeys`/`LogKeys`/`ParseProblems` docs `:618-625`; `IndexFiles`/`LogFiles`/`ParseErrors` `:725-732`; `TryResolveResource` `:869-872`; `ReadResourceText(string)` `:980-982`; three `cref`s `:18`, `:89`, `:205`)
- Modify: `src/OKF4net/BundleConceptWriter.cs:219`, `:233`, `:332-334`; `src/OKF4net.Agents/OkfBundleTools.cs:247-249`; `src/OKF4net/FrontmatterResource.cs:8`, `:38`
- Modify: `src/OKF4net/Validate.cs` (`Diagnostic` record `:261-262`, `return new ValidationReport(…)` `:691` + `AttachKeys`); `src/OKF4net/Audit.cs:46`, `:58-65`, `:315-322`
- Modify (one `#pragma` line each, after the SPDX line): `tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs`, `AuditTests.cs`, `BundleConceptWriterProviderTests.cs`, `BundleLoadPerformanceTests.cs`, `BundleTests.cs`, `CliDisplayPathTests.cs`, `FrontmatterResourceTests.cs`, `Providers/BundleExecutableResourceTests.cs`, `Providers/BundleHandleTests.cs`, `Providers/BundlePortabilityTests.cs`, `Providers/BundleProviderLoadTests.cs`, `Providers/FileSystemBundleProviderScopeTests.cs`, `ValidateTests.cs`, `Yaml/YamlDepthSymmetryTests.cs`
- Modify: `producers/tests/OkfProducer.Tests/Generation/PruningTests.cs:1363`
- Test: `tests/OKF4net.Tests/ObsoleteMembersTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's `Bundle.DisplayPath`; lot A's `Concept.Key`, `IndexKeys`, `LogKeys`, `ParseProblems`, `BundleDisplayPath.Compose`; `ConceptAudit.Run(bundle, AuditQuery, clock)`, `AuditQuery(Type:)`; the two members obsolete before this lot (`OkfBundleTools.RunComputation(string, IReadOnlyDictionary<string, object?>)`, `OkfContextProviderOptions.MemoryDirectory`), listed as they are.
- Produces: `[Obsolete]` (warning) on `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)`, `BundleConceptWriter.BundleRoot`, `OkfBundleTools.BundleRoot`, each message containing "use " and "(#152)"; `public BundleKey? Diagnostic.Key { get; init; }`; `public BundleKey? AuditFinding.Key { get; init; }`; `private static List<Diagnostic> BundleValidator.AttachKeys(Bundle, List<Diagnostic>)`. `BundleKey` has no `==`: compare with `Equals`/`Assert.Equal`.

- [ ] **Step 1: Write the failing test and the fourteen suppressions**

In `tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
`````

In `tests/OKF4net.Tests/AuditTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Unit tests for <see cref="ConceptAudit"/>: tier and status counting,
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
namespace OKF4net.Tests;

/// <summary>
/// Unit tests for <see cref="ConceptAudit"/>: tier and status counting,
`````

In `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;
`````

In `tests/OKF4net.Tests/BundleLoadPerformanceTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;
`````

In `tests/OKF4net.Tests/BundleTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for bundle loading and the cross-link graph, exercised against the
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
namespace OKF4net.Tests;

/// <summary>
/// Tests for bundle loading and the cross-link graph, exercised against the
`````

In `tests/OKF4net.Tests/CliDisplayPathTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using OKF4net.Cli;

namespace OKF4net.Tests;

`````

In `tests/OKF4net.Tests/FrontmatterResourceTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using OKF4net;
using Xunit;

`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Linq;
using OKF4net;
using Xunit;

`````

Create `tests/OKF4net.Tests/ObsoleteMembersTests.cs` (then `unix2dos -q` it):

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// The path-based members the provider seam replaced are kept one version, marked obsolete
/// (spec §9, §10): exactly these, each with a message that names its replacement and the
/// version that removes them, as a warning (never an error, so a host upgrading keeps
/// building); and the key-based members they project are populated.
/// </summary>
public class ObsoleteMembersTests
{
    private static readonly Assembly[] Assemblies = [typeof(Bundle).Assembly, typeof(OkfBundleTools).Assembly];

    private static IEnumerable<(string Member, ObsoleteAttribute Attribute)> ObsoleteMembers() =>
        Assemblies
            .SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (Member: $"{t.Name}.{m.Name}{Signature(m)}", Attribute: m.GetCustomAttribute<ObsoleteAttribute>()!)))
            .Where(x => x.Attribute is not null);

    private static string Signature(MemberInfo member) =>
        member is MethodInfo method ? $"({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})" : string.Empty;

    /// <summary>The members lot D marked obsolete: their message names a replacement ("use …") and the version that removes them (#152).</summary>
    private static readonly string[] PathBasedMembers =
    [
        "Bundle.Root",
        "Bundle.IndexFiles",
        "Bundle.LogFiles",
        "Bundle.ParseErrors",
        "Bundle.TryResolveResource(Concept, String, String&, ResourceResolutionStatus&)",
        "Bundle.ReadResourceText(String)",
        "BundleConceptWriter.BundleRoot",
        "OkfBundleTools.BundleRoot",
    ];

    /// <summary>Obsolete before lot D, kept as they were: listed so that the inventory below is the whole public surface.</summary>
    private static readonly string[] EarlierObsoleteMembers =
    [
        "OkfBundleTools.RunComputation(String, IReadOnlyDictionary`2)",
        "OkfContextProviderOptions.MemoryDirectory",
    ];

    [Fact]
    public void Exactly_the_path_based_members_are_obsolete_each_naming_its_replacement()
    {
        var members = ObsoleteMembers().ToList();

        Assert.Equal(
            PathBasedMembers.Concat(EarlierObsoleteMembers).Order(StringComparer.Ordinal),
            members.Select(m => m.Member).Order(StringComparer.Ordinal));

        foreach (var (member, attribute) in members.Where(m => PathBasedMembers.Contains(m.Member, StringComparer.Ordinal)))
        {
            Assert.False(attribute.IsError, member);
            Assert.Contains("use ", attribute.Message, StringComparison.Ordinal);
            Assert.Contains("(#152)", attribute.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Diagnostics_and_audit_findings_carry_the_key_their_display_path_projects()
    {
        var storage = new InMemoryBundleProvider(id: "k")
            .Add("index.md", "---\nokf_version: 0.2\n---\n# Root\n\n- [a](tables/a.md)\n")
            .Add("tables/index.md", "---\nextra: 1\n---\n# Tables\n")
            .Add("tables/a.md", "---\ntype: Note\ntitle: A\ndescription: d\nresource: missing.sql\n---\nSee [b](b.md).\n")
            .Add("tables/broken.md", "---\ntype: Note\nk: *a\n---\n")
            .Add("bad\tname.md", "---\ntype: Note\n---\n")
            .Add("log.md", "## not-a-date\n\n- x\n");
        var bundle = Bundle.Load(storage);

        var report = BundleValidator.Validate(bundle);

        foreach (var diagnostic in report.Diagnostics)
        {
            var expected = diagnostic.Path is { } path
                ? KeyOf(bundle, path)
                : diagnostic.Concept is { } id ? bundle.Get(id)?.Key : null;
            Assert.Equal(expected, diagnostic.Key);
        }

        Assert.Equal(BundleKey.Parse("tables/a.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing).Key);
        Assert.Equal(BundleKey.Parse("tables/index.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexHasFrontmatter).Key);
        Assert.Equal(BundleKey.Parse("log.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid).Key);
        Assert.Equal(BundleKey.Parse("tables/broken.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableDocument && d.Path!.EndsWith("broken.md", StringComparison.Ordinal)).Key);
        Assert.Null(Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableDocument && d.Path!.EndsWith("bad\tname.md", StringComparison.Ordinal)).Key);
        var broken = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.BrokenLink);
        Assert.Null(broken.Path);
        Assert.Equal(BundleKey.Parse("tables/a.md"), broken.Key);

        var audit = ConceptAudit.Run(bundle, new AuditQuery { Type = "Note" }, new SystemClock());
        var finding = Assert.Single(audit.Findings);
        Assert.Equal(BundleKey.Parse("tables/a.md"), finding.Key);
        Assert.Equal(bundle.Get(finding.Id)!.Path, finding.Path);
    }

    private static BundleKey? KeyOf(Bundle bundle, string displayPath)
    {
        foreach (var concept in bundle.Concepts)
        {
            if (concept.Path == displayPath)
            {
                return concept.Key;
            }
        }

        foreach (var key in bundle.IndexKeys.Concat(bundle.LogKeys))
        {
            if (BundleDisplayPath.Compose(bundle.Location, key) == displayPath)
            {
                return key;
            }
        }

        foreach (var problem in bundle.ParseProblems)
        {
            if (bundle.DisplayPath(problem) == displayPath)
            {
                return problem.Key;
            }
        }

        return null;
    }
}
`````

In `tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

`````

In `tests/OKF4net.Tests/Providers/BundleHandleTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

In `tests/OKF4net.Tests/Providers/BundlePortabilityTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using OKF4net.Cli;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

In `tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

`````

In `tests/OKF4net.Tests/Providers/FileSystemBundleProviderScopeTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Globalization;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;
`````

In `tests/OKF4net.Tests/ValidateTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Conformance-checking tests, exercised rule-by-rule against
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
namespace OKF4net.Tests;

/// <summary>
/// Conformance-checking tests, exercised rule-by-rule against
`````

In `tests/OKF4net.Tests/Yaml/YamlDepthSymmetryTests.cs`, find:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;
`````

and replace it with:

`````csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Text;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;
`````

In `producers/tests/OkfProducer.Tests/Generation/PruningTests.cs`, find:

`````csharp
        // bundle model" is. Name the manifest `.md` and it stops being a concept only by failing to
        // parse as one, which is a diagnostic in every `okf validate` run from then on.
        var bundle = Bundle.Load(tmp.Path);
        Assert.DoesNotContain(bundle.Concepts, c => c.Path.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
        Assert.DoesNotContain(bundle.ParseErrors, e => e.Path.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_manifest_is_written_after_the_concepts_it_describes()
`````

and replace it with:

`````csharp
        // bundle model" is. Name the manifest `.md` and it stops being a concept only by failing to
        // parse as one, which is a diagnostic in every `okf validate` run from then on.
        var bundle = Bundle.Load(tmp.Path);
        Assert.DoesNotContain(bundle.Concepts, c => c.Path.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
        Assert.DoesNotContain(bundle.ParseProblems, p => p.RawName.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_manifest_is_written_after_the_concepts_it_describes()
`````

- [ ] **Step 2: Run the new test to verify it fails**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ObsoleteMembersTests" -nologo -v q`
Expected: the build fails: `error CS1061: 'Diagnostic' does not contain a definition for 'Key'` and the same for `'AuditFinding'`. (The `#pragma` lines are harmless before the attributes exist.)

- [ ] **Step 3: Mark the members obsolete, attach the keys, migrate the producers reader**

In `src/OKF4net.Agents/OkfBundleTools.cs`, find:

`````csharp

    /// <summary>The bundle's displayable location (the root directory for the filesystem provider).</summary>
    public string Location => _handle.Location;

    /// <summary>The bundle's root directory: the string a root-based constructor was given, or the filesystem provider's root.</summary>
    /// <exception cref="NotSupportedException">The tool set is over another provider: a path has no meaning for it (spec §9).</exception>
    public string BundleRoot => _root
        ?? throw new NotSupportedException("this tool set is not over a filesystem provider, so it has no bundle root");

    /// <summary>
`````

and replace it with:

`````csharp

    /// <summary>The bundle's displayable location (the root directory for the filesystem provider).</summary>
    public string Location => _handle.Location;

    /// <summary>The bundle's root directory: the string a root-based constructor was given, or the filesystem provider's root. Obsolete (spec §9, §10): a tool set over another provider has no root.</summary>
    /// <exception cref="NotSupportedException">The tool set is over another provider: a path has no meaning for it (spec §9).</exception>
    [Obsolete("OkfBundleTools.BundleRoot has no meaning for a tool set over a provider other than the filesystem: use Location (the provider's displayable location), or Handle.Provider as a FileSystemBundleProvider for its Root. Removed with the asynchronous provider contract (#152).")]
    public string BundleRoot => _root
        ?? throw new NotSupportedException("this tool set is not over a filesystem provider, so it has no bundle root");

    /// <summary>
`````

In `src/OKF4net/Audit.cs`, find:

`````csharp
}

/// <summary>One concept selected by an audit, with its signals already derived.</summary>
/// <param name="Id">The concept id (§2).</param>
/// <param name="Path">The concept's file path, as built from the bundle root.</param>
/// <param name="Type">The frontmatter <c>type</c>, or null when absent.</param>
/// <param name="Title">The frontmatter <c>title</c>, or null when absent.</param>
/// <param name="Trust">The derived trust tier (§5.3).</param>
/// <param name="Lifecycle">The lifecycle fields (§5.4/§5.5).</param>
`````

and replace it with:

`````csharp
}

/// <summary>One concept selected by an audit, with its signals already derived.</summary>
/// <param name="Id">The concept id (§2).</param>
/// <param name="Path">The concept's display path (spec §8): the bundle's location, then the key's segments with the OS separator — what <c>okf audit --json</c> prints.</param>
/// <param name="Type">The frontmatter <c>type</c>, or null when absent.</param>
/// <param name="Title">The frontmatter <c>title</c>, or null when absent.</param>
/// <param name="Trust">The derived trust tier (§5.3).</param>
/// <param name="Lifecycle">The lifecycle fields (§5.4/§5.5).</param>
`````

In `src/OKF4net/Audit.cs`, find:

`````csharp
    string? Type,
    string? Title,
    TrustTier Trust,
    Lifecycle Lifecycle,
    bool IsStale);

/// <summary>
/// The result of an audit: counts over the whole bundle, plus the concepts the
/// query selected. The counts never narrow with the query -- the denominator
`````

and replace it with:

`````csharp
    string? Type,
    string? Title,
    TrustTier Trust,
    Lifecycle Lifecycle,
    bool IsStale)
{
    /// <summary>The concept's key in its provider (spec §9); <see langword="null"/> for a finding built by hand over a concept without one.</summary>
    public Providers.BundleKey? Key { get; init; }
}

/// <summary>
/// The result of an audit: counts over the whole bundle, plus the concepts the
/// query selected. The counts never narrow with the query -- the denominator
`````

In `src/OKF4net/Audit.cs`, find:

`````csharp
                frontmatter.Type,
                frontmatter.Title,
                tier,
                lifecycle,
                isStale));
        }

        findings.Sort(static (a, b) => a.Id.CompareTo(b.Id));

`````

and replace it with:

`````csharp
                frontmatter.Type,
                frontmatter.Title,
                tier,
                lifecycle,
                isStale)
            {
                Key = concept.Key,
            });
        }

        findings.Sort(static (a, b) => a.Id.CompareTo(b.Id));

`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
/// inspected, a root outside its confinement boundary, an object gone or changed while it
/// was read); or a global bound exceeded (entries, depth). The message is library-authored
/// (spec §7); the raw cause, when there is one, is the inner exception. Per-file problems —
/// a rejected name, an oversized or unparseable concept — are recorded in
/// <see cref="Bundle.ParseErrors"/> instead (loading is permissive, §11).
/// </summary>
public sealed class BundleLoadException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
`````

and replace it with:

`````csharp
/// inspected, a root outside its confinement boundary, an object gone or changed while it
/// was read); or a global bound exceeded (entries, depth). The message is library-authored
/// (spec §7); the raw cause, when there is one, is the inner exception. Per-file problems —
/// a rejected name, an oversized or unparseable concept — are recorded in
/// <see cref="Bundle.ParseProblems"/> instead (loading is permissive, §11).
/// </summary>
public sealed class BundleLoadException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
/// <see cref="Load(string)"/> walks a directory, parses every non-reserved
/// <c>.md</c> file into a <see cref="Concept"/>, records the reserved
/// <c>index.md</c> / <c>log.md</c> files, and builds the cross-link graph
/// (§6). Loading is **permissive** by design (§11): files whose frontmatter
/// cannot be parsed are collected into <see cref="ParseErrors"/> rather than
/// aborting the load, and broken links are retained as edges to
/// non-existent concepts.
/// </summary>
public sealed class Bundle
`````

and replace it with:

`````csharp
/// <see cref="Load(string)"/> walks a directory, parses every non-reserved
/// <c>.md</c> file into a <see cref="Concept"/>, records the reserved
/// <c>index.md</c> / <c>log.md</c> files, and builds the cross-link graph
/// (§6). Loading is **permissive** by design (§11): files whose frontmatter
/// cannot be parsed are collected into <see cref="ParseProblems"/> rather than
/// aborting the load, and broken links are retained as edges to
/// non-existent concepts.
/// </summary>
public sealed class Bundle
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
        Concepts = concepts;
        _index = index;
        IndexKeys = indexKeys;
        LogKeys = logKeys;
        IndexFiles = indexKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        LogFiles = logKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        ParseProblems = parseProblems;
        ParseErrors = parseProblems.Select(p => (DisplayPathOf(provider.Location, p), p.Error)).ToList();
        RejectedKeys = rejectedKeys;
        RejectedPrefixes = rejectedPrefixes;
        _rejectedPrefixFolds = rejectedPrefixes.Select(validator.FoldKeyText).ToHashSet(StringComparer.Ordinal);
        PortabilityIssues = portabilityIssues;
`````

and replace it with:

`````csharp
        Concepts = concepts;
        _index = index;
        IndexKeys = indexKeys;
        LogKeys = logKeys;
        ParseProblems = parseProblems;
#pragma warning disable CS0618 // the display-path projections are kept, obsolete, until their removal (spec §10)
        IndexFiles = indexKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        LogFiles = logKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        ParseErrors = parseProblems.Select(p => (DisplayPathOf(provider.Location, p), p.Error)).ToList();
#pragma warning restore CS0618
        RejectedKeys = rejectedKeys;
        RejectedPrefixes = rejectedPrefixes;
        _rejectedPrefixFolds = rejectedPrefixes.Select(validator.FoldKeyText).ToHashSet(StringComparer.Ordinal);
        PortabilityIssues = portabilityIssues;
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
    /// global bound exceeded (<see cref="BundleLimits.Default"/>: entries, depth) and a refusal
    /// by the provider's STRICT guards — a directory under the root whose link status cannot be
    /// inspected aborts the load before it is listed. A symlink or junction under the root is
    /// skipped, never followed. Per-file problems — a rejected name, an oversized or
    /// unparseable concept — are recorded in <see cref="ParseErrors"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, an I/O error occurred, a concept is not valid UTF-8, a global bound was exceeded, or the provider refused an access.</exception>
    public static Bundle Load(string root)
    {
`````

and replace it with:

`````csharp
    /// global bound exceeded (<see cref="BundleLimits.Default"/>: entries, depth) and a refusal
    /// by the provider's STRICT guards — a directory under the root whose link status cannot be
    /// inspected aborts the load before it is listed. A symlink or junction under the root is
    /// skipped, never followed. Per-file problems — a rejected name, an oversized or
    /// unparseable concept — are recorded in <see cref="ParseProblems"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, an I/O error occurred, a concept is not valid UTF-8, a global bound was exceeded, or the provider refused an access.</exception>
    public static Bundle Load(string root)
    {
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp

        return OkfEncodings.Strict.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The bundle's root: the provider's displayable location (the root directory for the filesystem).</summary>
    public string Root => Provider.Location;

    /// <summary>The provider this bundle was loaded from (spec §1).</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The provider's displayable location; <see cref="Root"/> is the same string.</summary>
    public string Location => Provider.Location;

    /// <summary>The options the bundle was loaded with.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>The provider's bundle token at load time (spec §5.1).</summary>
    public BundleVersion Version { get; }

    /// <summary>The keys of every <c>index.md</c>, in load order; <see cref="IndexFiles"/> holds their display paths.</summary>
    public IReadOnlyList<BundleKey> IndexKeys { get; }

    /// <summary>The keys of every <c>log.md</c>, in load order; <see cref="LogFiles"/> holds their display paths.</summary>
    public IReadOnlyList<BundleKey> LogKeys { get; }

    /// <summary>Every problem met while loading (spec §2): rejected <c>.md</c> names, oversized or unparseable concepts. <see cref="ParseErrors"/> is its display-path projection.</summary>
    public IReadOnlyList<BundleParseError> ParseProblems { get; }

    /// <summary>Keys the tree validation or a bound rejected (spec §2), whatever their extension: they stay rejected for <see cref="IsKeyAllowed"/> even though the provider may still hold them.</summary>
    public IReadOnlySet<BundleKey> RejectedKeys { get; }
`````

and replace it with:

`````csharp

        return OkfEncodings.Strict.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The bundle's root: the provider's displayable location (the root directory for the filesystem). Obsolete: the same string as <see cref="Location"/>.</summary>
    [Obsolete("Bundle.Root is the provider's displayable location: use Bundle.Location (or Bundle.Provider). The path-based members are removed with the asynchronous provider contract (#152).")]
    public string Root => Provider.Location;

    /// <summary>The provider this bundle was loaded from (spec §1).</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The provider's displayable location; the obsolete <c>Root</c> is the same string.</summary>
    public string Location => Provider.Location;

    /// <summary>The options the bundle was loaded with.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>The provider's bundle token at load time (spec §5.1).</summary>
    public BundleVersion Version { get; }

    /// <summary>The keys of every <c>index.md</c>, in load order; <see cref="BundleDisplayPath.Compose"/> with <see cref="Location"/> gives their display paths (the obsolete <c>IndexFiles</c>).</summary>
    public IReadOnlyList<BundleKey> IndexKeys { get; }

    /// <summary>The keys of every <c>log.md</c>, in load order; <see cref="BundleDisplayPath.Compose"/> with <see cref="Location"/> gives their display paths (the obsolete <c>LogFiles</c>).</summary>
    public IReadOnlyList<BundleKey> LogKeys { get; }

    /// <summary>Every problem met while loading (spec §2): rejected <c>.md</c> names, oversized or unparseable concepts. <see cref="DisplayPath"/> gives each one's display path (the obsolete <c>ParseErrors</c> is that projection).</summary>
    public IReadOnlyList<BundleParseError> ParseProblems { get; }

    /// <summary>Keys the tree validation or a bound rejected (spec §2), whatever their extension: they stay rejected for <see cref="IsKeyAllowed"/> even though the provider may still hold them.</summary>
    public IReadOnlySet<BundleKey> RejectedKeys { get; }
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp

    /// <summary><c>true</c> if a concept with this id exists.</summary>
    public bool Contains(ConceptId id) => _index.ContainsKey(id);

    /// <summary>Paths of all <c>index.md</c> files found (§8).</summary>
    public IReadOnlyList<string> IndexFiles { get; }

    /// <summary>Paths of all <c>log.md</c> files found (§9).</summary>
    public IReadOnlyList<string> LogFiles { get; }

    /// <summary>Files whose frontmatter could not be parsed during loading, as (path, error message) pairs.</summary>
    public IReadOnlyList<(string Path, string Error)> ParseErrors { get; }

    /// <summary>The resolved outbound cross-links from a concept.</summary>
    public IReadOnlyList<ResolvedLink> LinksFrom(ConceptId id) =>
`````

and replace it with:

`````csharp

    /// <summary><c>true</c> if a concept with this id exists.</summary>
    public bool Contains(ConceptId id) => _index.ContainsKey(id);

    /// <summary>Paths of all <c>index.md</c> files found (§8). Obsolete: the display paths of <see cref="IndexKeys"/>.</summary>
    [Obsolete("Bundle.IndexFiles projects Bundle.IndexKeys through the display rule: use IndexKeys, and BundleDisplayPath.Compose(bundle.Location, key) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<string> IndexFiles { get; }

    /// <summary>Paths of all <c>log.md</c> files found (§9). Obsolete: the display paths of <see cref="LogKeys"/>.</summary>
    [Obsolete("Bundle.LogFiles projects Bundle.LogKeys through the display rule: use LogKeys, and BundleDisplayPath.Compose(bundle.Location, key) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<string> LogFiles { get; }

    /// <summary>Files whose frontmatter could not be parsed during loading, as (path, error message) pairs. Obsolete: <see cref="ParseProblems"/> with <see cref="DisplayPath"/>.</summary>
    [Obsolete("Bundle.ParseErrors projects Bundle.ParseProblems through the display rule: use ParseProblems (the raw name, the key when the name is one, the error), and bundle.DisplayPath(problem) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<(string Path, string Error)> ParseErrors { get; }

    /// <summary>The resolved outbound cross-links from a concept.</summary>
    public IReadOnlyList<ResolvedLink> LinksFrom(ConceptId id) =>
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
    /// does not.
    ///
    /// Always returns <c>true</c>: resolution never fails outright (§11,
    /// permissive), it only reports which of the above statuses applies.
    /// </summary>
    public bool TryResolveResource(Concept concept, string rawPath, out string? absolutePath, out ResourceResolutionStatus status)
    {
        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
`````

and replace it with:

`````csharp
    /// does not.
    ///
    /// Always returns <c>true</c>: resolution never fails outright (§11,
    /// permissive), it only reports which of the above statuses applies.
    /// Obsolete: it resolves through the OS filesystem, outside the provider
    /// seam, so it applies neither the key policy nor the exact-name rules
    /// <see cref="TryResolveResourceKey"/> applies; no code of this repository
    /// calls it.
    /// </summary>
    [Obsolete("Bundle.TryResolveResource resolves a §6.2 path on the OS filesystem, outside the provider seam: use Bundle.TryResolveResourceKey, which resolves on keys under the key policy, then Bundle.ReadResourceText(BundleKey). The path-based members are removed with the asynchronous provider contract (#152).")]
    public bool TryResolveResource(Concept concept, string rawPath, out string? absolutePath, out ResourceResolutionStatus status)
    {
        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
`````

In `src/OKF4net/Bundle.cs`, find:

`````csharp
    /// not be inspected; or the OS denies the read.
    /// </exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public string ReadResourceText(string absolutePath)
    {
        var fullRoot = ReparsePoints.CanonicalizeRoot(Root);
        var fullPath = System.IO.Path.GetFullPath(absolutePath);
`````

and replace it with:

`````csharp
    /// not be inspected; or the OS denies the read.
    /// </exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    [Obsolete("Bundle.ReadResourceText(string) reads a host path, outside the provider seam: use Bundle.ReadResourceText(BundleKey) on the key Bundle.TryResolveResourceKey resolved. The path-based members are removed with the asynchronous provider contract (#152).")]
    public string ReadResourceText(string absolutePath)
    {
        var fullRoot = ReparsePoints.CanonicalizeRoot(Root);
        var fullPath = System.IO.Path.GetFullPath(absolutePath);
`````

In `src/OKF4net/BundleConceptWriter.cs`, find:

`````csharp
    [ThreadStatic]
    private static Dictionary<string, HeldLock>? t_heldLocks;

    /// <summary>
    /// Over the filesystem provider, <see cref="BundleRoot"/> through
    /// <see cref="ReparsePoints.CanonicalizeRoot"/>, with Windows namespace forms
    /// normalized, computed once: the key of <see cref="t_heldLocks"/>, and the
    /// starting point <see cref="ReparsePoints.ResolveLockKey"/> resolves from.
    /// Over any other provider, <see cref="ProviderLockPrefix"/> and its
`````

and replace it with:

`````csharp
    [ThreadStatic]
    private static Dictionary<string, HeldLock>? t_heldLocks;

    /// <summary>
    /// Over the filesystem provider, its <see cref="FileSystemBundleProvider.Root"/> through
    /// <see cref="ReparsePoints.CanonicalizeRoot"/>, with Windows namespace forms
    /// normalized, computed once: the key of <see cref="t_heldLocks"/>, and the
    /// starting point <see cref="ReparsePoints.ResolveLockKey"/> resolves from.
    /// Over any other provider, <see cref="ProviderLockPrefix"/> and its
`````

In `src/OKF4net/BundleConceptWriter.cs`, find:

`````csharp

    /// <summary>Where every byte is read and written.</summary>
    private readonly IBundleProvider _provider;

    /// <summary><see cref="_provider"/> when it is the filesystem provider: its root keys the lock registry and backs <see cref="BundleRoot"/>, and its test hooks back the writer's.</summary>
    private readonly FileSystemBundleProvider? _fileSystem;

    /// <summary>The key policy every write is checked against: the provider's native constraints under the options' policy, exactly as a load builds it (spec §2, decision B2).</summary>
    private readonly BundleKeyValidator _validator;
`````

and replace it with:

`````csharp

    /// <summary>Where every byte is read and written.</summary>
    private readonly IBundleProvider _provider;

    /// <summary><see cref="_provider"/> when it is the filesystem provider: its root keys the lock registry and backs the obsolete <c>BundleRoot</c>, and its test hooks back the writer's.</summary>
    private readonly FileSystemBundleProvider? _fileSystem;

    /// <summary>The key policy every write is checked against: the provider's native constraints under the options' policy, exactly as a load builds it (spec §2, decision B2).</summary>
    private readonly BundleKeyValidator _validator;
`````

In `src/OKF4net/BundleConceptWriter.cs`, find:

`````csharp
            ? ReparsePoints.NormalizeNamespaceForLockKey(ReparsePoints.CanonicalizeRoot(_fileSystem.Root))
            : ProviderLockPrefix + provider.Id;
    }

    /// <summary>The bundle root, as passed to the constructor (the filesystem provider's <see cref="FileSystemBundleProvider.Root"/>).</summary>
    /// <exception cref="NotSupportedException">The writer was built over another provider: a path has no meaning for it (spec §9).</exception>
    public string BundleRoot => _fileSystem?.Root
        ?? throw new NotSupportedException("this writer is not over a filesystem provider, so it has no bundle root");

    /// <summary>
`````

and replace it with:

`````csharp
            ? ReparsePoints.NormalizeNamespaceForLockKey(ReparsePoints.CanonicalizeRoot(_fileSystem.Root))
            : ProviderLockPrefix + provider.Id;
    }

    /// <summary>The bundle root, as passed to the constructor (the filesystem provider's <see cref="FileSystemBundleProvider.Root"/>). Obsolete (spec §9, §10): a writer over another provider has no root.</summary>
    /// <exception cref="NotSupportedException">The writer was built over another provider: a path has no meaning for it (spec §9).</exception>
    [Obsolete("BundleConceptWriter.BundleRoot has no meaning for a writer over a provider other than the filesystem: use the Root of the FileSystemBundleProvider the writer was built over, or the provider's Location. Removed with the asynchronous provider contract (#152).")]
    public string BundleRoot => _fileSystem?.Root
        ?? throw new NotSupportedException("this writer is not over a filesystem provider, so it has no bundle root");

    /// <summary>
`````

In `src/OKF4net/FrontmatterResource.cs`, find:

`````csharp
namespace OKF4net;

/// <summary>
/// How a §6.2 path-valued frontmatter field's raw string is shaped, which
/// determines how <see cref="Bundle.TryResolveResource"/> resolves it.
/// </summary>
public enum FrontmatterResourceKind
{
    /// <summary>An absolute URL (<c>scheme://...</c>), e.g. <c>https://example.com/x</c>. Never resolved to a local path.</summary>
`````

and replace it with:

`````csharp
namespace OKF4net;

/// <summary>
/// How a §6.2 path-valued frontmatter field's raw string is shaped, which
/// determines how <see cref="Bundle.TryResolveResourceKey"/> resolves it.
/// </summary>
public enum FrontmatterResourceKind
{
    /// <summary>An absolute URL (<c>scheme://...</c>), e.g. <c>https://example.com/x</c>. Never resolved to a local path.</summary>
`````

In `src/OKF4net/FrontmatterResource.cs`, find:

`````csharp
    /// </summary>
    ConceptRelative,
}

/// <summary>The outcome of resolving a §6.2 path-valued frontmatter field to a filesystem path via <see cref="Bundle.TryResolveResource"/>.</summary>
public enum ResourceResolutionStatus
{
    /// <summary>The raw value is a URL; it was never resolved to a local path (<c>absolutePath</c> is <c>null</c>).</summary>
    Url,
`````

and replace it with:

`````csharp
    /// </summary>
    ConceptRelative,
}

/// <summary>The outcome of resolving a §6.2 path-valued frontmatter field to a key via <see cref="Bundle.TryResolveResourceKey"/> (or, before the seam, to a filesystem path).</summary>
public enum ResourceResolutionStatus
{
    /// <summary>The raw value is a URL; it was never resolved to a local path (<c>absolutePath</c> is <c>null</c>).</summary>
    Url,
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
/// one (<see langword="null"/> for body-level or file-level findings).
/// </summary>
public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message, DiagnosticCode Code, string? Field = null)
{
    /// <summary>
    /// Renders as <c>[severity] path: message</c> or <c>[severity] concept:
    /// message</c> (falling back to a bare <c>[severity] message</c> if
    /// neither is set). Unaffected by <see cref="Code"/> or <see cref="Field"/>
`````

and replace it with:

`````csharp
/// one (<see langword="null"/> for body-level or file-level findings).
/// </summary>
public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message, DiagnosticCode Code, string? Field = null)
{
    /// <summary>
    /// The key of the object the finding is about (spec §9): the concept's, the
    /// <c>index.md</c>'s or <c>log.md</c>'s, or the unparseable document's when its name is a
    /// key. <see langword="null"/> for a hand-built finding, for a rejected name that is no
    /// key, and for a finding about nothing in particular. <see cref="Path"/> is that key's
    /// display path (<see cref="Providers.BundleDisplayPath.Compose"/> under the bundle's
    /// location), the text the <c>validate</c> snapshots pin.
    /// </summary>
    public Providers.BundleKey? Key { get; init; }

    /// <summary>
    /// Renders as <c>[severity] path: message</c> or <c>[severity] concept:
    /// message</c> (falling back to a bare <c>[severity] message</c> if
    /// neither is set). Unaffected by <see cref="Code"/> or <see cref="Field"/>
`````

In `src/OKF4net/Validate.cs`, find:

`````csharp
                $"link target does not resolve to a concept in the bundle: {raw}",
                DiagnosticCode.BrokenLink));
        }

        return new ValidationReport(diagnostics);
    }

    /// <summary>
    /// For a bare §6.2 path that resolved to <see cref="ResourceResolutionStatus.Missing"/>,
`````

and replace it with:

`````csharp
                $"link target does not resolve to a concept in the bundle: {raw}",
                DiagnosticCode.BrokenLink));
        }

        return new ValidationReport(AttachKeys(bundle, diagnostics));
    }

    /// <summary>
    /// Sets <see cref="Diagnostic.Key"/> on every finding whose <see cref="Diagnostic.Path"/>
    /// is the display path of a key of <paramref name="bundle"/> — a concept's, an index's, a
    /// log's, an unparseable document's — or, for a finding without a path, whose
    /// <see cref="Diagnostic.Concept"/> names a loaded concept (spec §9). Once, after the
    /// findings are collected, so the construction sites above keep their exact
    /// <see cref="Diagnostic.Path"/> text, which the <c>validate</c> snapshots pin.
    /// </summary>
    private static List<Diagnostic> AttachKeys(Bundle bundle, List<Diagnostic> diagnostics)
    {
        var keys = new Dictionary<string, BundleKey>(StringComparer.Ordinal);
        foreach (var concept in bundle.Concepts)
        {
            if (concept.Key is { } key)
            {
                keys.TryAdd(concept.Path, key);
            }
        }

        foreach (var key in bundle.IndexKeys.Concat(bundle.LogKeys))
        {
            keys.TryAdd(BundleDisplayPath.Compose(bundle.Location, key), key);
        }

        foreach (var problem in bundle.ParseProblems)
        {
            if (problem.Key is { } key)
            {
                keys.TryAdd(bundle.DisplayPath(problem), key);
            }
        }

        for (var i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            if (diagnostic.Path is { } path)
            {
                if (keys.TryGetValue(path, out var key))
                {
                    diagnostics[i] = diagnostic with { Key = key };
                }
            }
            else if (diagnostic.Concept is { } id && bundle.Get(id)?.Key is { } conceptKey)
            {
                diagnostics[i] = diagnostic with { Key = conceptKey };
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// For a bare §6.2 path that resolved to <see cref="ResourceResolutionStatus.Missing"/>,
`````

- [ ] **Step 4: Build with no warning, run the new test, then the whole suite and the producers**

Run: `dotnet build OKF4net.sln -nologo -v q 2>&1 | grep -cE "error|warn"` — expected `0` (every remaining reader of an obsolete member is in a suppressed test file; a reader left in `src/` is an error here, since warnings are errors).
Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ObsoleteMembersTests" -nologo -v q` — expected 2 passed.
Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration" -nologo -v q` — expected **`Passed: 3596, Skipped: 36, Total: 3632`** (the base's 3536 / 36 / 3572 + 60).
Run: `dotnet test producers/OkfProducer.sln -nologo -v q` — expected `Passed: 826, Skipped: 15, Total: 841` (the base's counts; without the `PruningTests` edit the producers build fails on `error CS0618: 'Bundle.ParseErrors' is obsolete`).

- [ ] **Step 5: Commit** (two commits, the producers edit on its own)

```bash
git add src/OKF4net/Bundle.cs src/OKF4net/BundleConceptWriter.cs src/OKF4net/Validate.cs src/OKF4net/Audit.cs src/OKF4net/FrontmatterResource.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/ObsoleteMembersTests.cs tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs tests/OKF4net.Tests/AuditTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs tests/OKF4net.Tests/BundleLoadPerformanceTests.cs tests/OKF4net.Tests/BundleTests.cs tests/OKF4net.Tests/CliDisplayPathTests.cs tests/OKF4net.Tests/FrontmatterResourceTests.cs tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs tests/OKF4net.Tests/Providers/BundleHandleTests.cs tests/OKF4net.Tests/Providers/BundlePortabilityTests.cs tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderScopeTests.cs tests/OKF4net.Tests/ValidateTests.cs tests/OKF4net.Tests/Yaml/YamlDepthSymmetryTests.cs
git commit -m "feat(core): the path-based members are obsolete; Diagnostic and AuditFinding carry the key their display path projects

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git add producers/tests/OkfProducer.Tests/Generation/PruningTests.cs
git commit -m "test(producers): read Bundle.ParseProblems, not the obsolete ParseErrors

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

CHANGELOG entries (Task 10): `### Added` "**`Diagnostic.Key` and `AuditFinding.Key`** …"; `### Changed (breaking)` "**The path-based members are obsolete** …" with the member-by-member migration.

### Task 10: Documentation — CHANGELOG, both READMEs, CLAUDE.md, the design's dated notes

Every entry the earlier tasks named, consolidated; the Catalog README's "Local filesystem bundles only" replaced by the providers section and the memory-key migration note (#143); the main README's catalog paragraph; CLAUDE.md's `Providers/`, `OKF4net.Catalog/` bullets (`okf fmt`/`parse` out of the seam by design, the obsolete inventory, the catalogue's handles); two dated notes in the design (§9 and §10) recording decisions L1–L14.

**Files:**
- Modify: `CHANGELOG.md` (after the last lot-C `### Added` bullet, `:116-118`: the lot-D Added bullets, the new `### Changed (breaking)` section, the lot-D Changed bullets at the top of `### Changed`)
- Modify: `README.md:409-411` (after "One image per source"), `:935-938` (the catalog's V1 limits)
- Modify: `src/OKF4net.Catalog/README.md:255-259`
- Modify: `CLAUDE.md:47` (two clauses of the `Providers/` bullet), `:52` (the `OKF4net.Catalog/` bullet)
- Modify: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (a note before "Catalog : nommer un provider", a note after "La migration asynchrone (#152) …")

**Interfaces:** none (docs only). Every claim below was checked against the code of Tasks 1–9; a reviewer re-reads each edited bullet against the tree.

- [ ] **Step 1: Write the documentation**

In `CHANGELOG.md`, find:

`````markdown
  non-displayable outcome with `ImageOutdated`
  saying whether the image may be behind. `RunCapturedAsync` runs §10.5 from
  step 3 on a capture; `RunAsync` captures and calls it, and reads nothing
  from the bundle afterwards.

### Changed

- **`okf-mcp`'s bundle discovery reads a candidate's root `index.md` under a
  16 MiB bound** (#139, lot C; spec §6, §9): `OkfBundleDiscovery.ReadRootIndexOrNull`
  answers `null` — "no declared version", as for any unreadable index — for
  a larger file without reading it (`OkfBundleDiscovery.MaxRootIndexBytes`),
`````

and replace it with:

`````markdown
  non-displayable outcome with `ImageOutdated`
  saying whether the image may be behind. `RunCapturedAsync` runs §10.5 from
  step 3 on a capture; `RunAsync` captures and calls it, and reads nothing
  from the bundle afterwards.
- **`Bundle.DisplayPath(BundleParseError)`** (#139, lot D; spec §8): the one
  display path of a load problem — the location, then the key's segments with
  the OS separator, or the raw name with `/` turned into the OS separator when
  the name is no key — the string the obsolete `ParseErrors` projected and the
  CLI, its JSON and the viewer print.
- **`Diagnostic.Key` and `AuditFinding.Key`** (#139, lot D; spec §9): the key
  of the object a finding is about — the concept's, the `index.md`'s or
  `log.md`'s, the unparseable document's when its name is a key — beside the
  display path `Path` still carries (`null` for a rejected name that is no
  key, or a hand-built finding). `Path` is unchanged, so the `validate` and
  `audit --json` outputs are too.
- **`OkfBundleKnowledgeSource(string id, BundleHandle handle)`** and
  `OkfBundleKnowledgeSource.Handle` (#139, lot D; spec §5.2, §9): a catalog
  source served from a handle it shares with every other consumer of that
  handle. The directory constructor builds a handle of its own (see Changed).
- **A `catalog.json` source names its provider** (#139, lot D; spec §9):
  `"provider": "<type>"` (`filesystem` when absent) and `"settings": { … }`
  (string values), on `KnowledgeCatalogSource.ProviderType` and `.Settings`
  (`ProviderSettings`, compared by value); a host registers one
  `IBundleProviderFactory` per type (`KnowledgeCatalogOptions.ProviderFactories`,
  `KnowledgeOptions.AddProviderFactory`), each declaring the settings it
  accepts, so no credential can enter through the manifest. New diagnostics:
  `MalformedProvider`, `MalformedSettings`, `IllegalSetting` (any setting for
  `filesystem`, or one the factory does not list), `IllegalPath` (a `path` on
  another provider type, whose storage is its settings), `IllegalMemoryProvider`
  (memory stays on the filesystem), `UnknownProvider` (no factory registered)
  and `ProviderUnavailable` (the factory, or the handle over its provider,
  threw: the diagnostic names the exception type, never its text).
- **The catalog snapshot owns the handles** (#139, lot D; spec §5.2, §9):
  `KnowledgeCatalogSnapshot.Handles` holds one `BundleHandle` per enabled
  knowledge source (empty for a snapshot the parser or a test built by hand),
  built by `FileKnowledgeCatalog` under `KnowledgeCatalogOptions.LoadOptions`
  and the source's refresh policy (`RefreshPolicy`, `OnAccess` by default;
  `RefreshPolicyFor` per source; `KnowledgeOptions.RefreshPolicy` for a host),
  the filesystem provider confined to the catalog root. A reload reuses a
  source's handle only when its effective configuration (provider type,
  resolved directory, settings, policy, load options) is identical; otherwise
  it builds a new one and disposes the old one with the catalog, which only
  ends its change subscription — a request still holding the old snapshot
  keeps reading its image.
- **`MemoryTierDeletion`** (#139, lot D; spec §1, §9): what deleting one tier's
  scope did, as a `BundleWriteReport` (see Changed (breaking)).

### Changed (breaking)

- **`MemoryDeleteResult` is `(IReadOnlyList<MemoryTierDeletion> Tiers,
  string? Error)`** (#139, lot D; spec §1, §9, §10): `TiersDeleted` counted
  directories removed by `Directory.Delete`; a scope is now a prefix whose
  objects are deleted one by one, so each tier reports the keys deleted and
  those that failed, with why. `IMemoryStore.DeleteScopeAsync` changes
  signature with it. Migration: `result.TiersDeleted == n` becomes
  `result.Tiers.Count == n && result.Tiers.All(t => t.Report.Succeeded)`;
  `result.Error` is unchanged (`null` when nothing failed).
- **The path-based members are obsolete** (#139, lot D; spec §9, §10), kept
  one version with a warning (never an error) whose message names the
  replacement: `Bundle.Root` → `Bundle.Location`; `Bundle.IndexFiles` /
  `LogFiles` → `IndexKeys` / `LogKeys` with
  `BundleDisplayPath.Compose(bundle.Location, key)` for a path to print;
  `Bundle.ParseErrors` → `ParseProblems` with `bundle.DisplayPath(problem)`;
  `Bundle.TryResolveResource` → `TryResolveResourceKey`;
  `Bundle.ReadResourceText(string)` → `ReadResourceText(BundleKey)`;
  `BundleConceptWriter.BundleRoot` and `OkfBundleTools.BundleRoot` → the
  `FileSystemBundleProvider`'s `Root`, or the provider's `Location`. They are
  removed with the asynchronous provider contract (#152). No code of this
  repository reads them any more (the tests that pin their strings suppress
  the warning); a host that does keeps their historical behaviour, warned.
  `Concept.Path`, `Diagnostic.Path` and `AuditFinding.Path` are not obsolete:
  they are the §8 display projection every output prints, beside `Key`.

### Changed

- **A catalog source no longer reloads its bundle at every request** (#139,
  lot D; spec §5.3, §9, §10): `GroupedKnowledgeResolver`,
  `MergedKnowledgeResolver` and `PriorityWeightedKnowledgeResolver` search
  through the handles the snapshot keeps, so two requests over an unchanged
  source load it once, and a change in storage is seen at the next request
  under the default `OnAccess` policy (the bundle's token — a listing with
  sizes and modification times for the filesystem — is compared before each
  use; a reload that fails keeps the previous image served, `Handle.IsStale`).
  The fused resolvers deduplicate by the provider's id, the canonical root for
  the filesystem: two sources naming one directory are still collapsed and
  reported under the resolved directory, and so now are two spellings that
  differ by a trailing separator. A source whose first load fails is reported
  `SourceUnavailable` as before. A snapshot that carries no handle for a source
  (built by hand, or by `CatalogManifestParser.TryParse`) is served as before:
  the path re-resolved and the bundle loaded at each request. The catalog
  root's confinement is re-checked by the provider before every access: a
  source directory replaced by a junction after the handle was created makes
  the next token read fail and the previous image is served stale, nothing
  under the junction ever read.
- **`new OkfBundleKnowledgeSource(id, directory)` builds its own `OnAccess`
  handle** (#139, lot D; spec §5.2): it used to load the bundle at every
  search; it now loads once and compares the token at each search, so an
  external change is still seen at the next one. Two sources built that way
  over one directory have two images.
- **`FileMemoryStore` reads, enumerates and deletes through the provider**
  (#139, lot D; spec §3, §8, §9): one `FileSystemBundleProvider` per tier
  root, and, for a scope, one rooted at the scope's directory and confined to
  the tier root, so the chain from the tier root to the scope is re-checked
  STRICTly before every access (the old `IsReparseEscaped` walk, now the
  provider's; the diagnostics are unchanged). A scope exists while it holds a
  key (spec §9, decision 14): an empty or absent scope directory is read and
  enumerated as nothing and deleted as a no-op. Deleting a scope deletes each
  object under its prefix on its own, unconditionally, and **leaves the
  directories in place** (spec §8; `Directory.Delete(recursive)` removed
  them); a `.md` the storage holds under a name that is no key cannot be
  deleted through the provider and is reported as a failure under the scope's
  prefix. An object that cannot be deleted (held open on Windows, say) is
  reported with the runtime's text — which can name a host path, as the
  old `could not be deleted: …` text did (#211) — and the deletion can be run
  again. Writes already went through the provider (lot B); they now share
  the tier's provider instance.
- **A memory scope segment whose base is a Windows reserved device name is
  prefixed with `_`** (#139, lot D; spec §9, D4): `con.foo` encodes as
  `_con.foo-<hash>` where it encoded as `con.foo-<hash>`, a name Windows maps
  to a device (the extension does not lift the reservation); the hash is that
  of the raw identifier and is unchanged, and no other identifier changes
  (`con` alone, `console.x`, `com10.x` are not reserved). Such scopes never
  worked on Windows. **Migration (Linux, macOS):** a tenant, user or session
  identifier whose part before its first `.` is `CON`, `PRN`, `AUX`, `NUL`,
  `COM1`–`COM9` or `LPT1`–`LPT9` (any case) has its existing directory under
  the old name, which is no longer read: rename it by prefixing `_`
  (`mv memory-tenant/con.foo-0123… memory-tenant/_con.foo-0123…`, and the same
  for each such segment under `memory-user/` and `memory-session/`).
- **`okf info`, `okf info --json`, `okf validate` and the viewer read the keys
  and the display path** (#139, lot D; spec §8, §9) instead of the obsolete
  path-based members (`Location`, `IndexKeys`/`LogKeys`, `ParseProblems` with
  `Bundle.DisplayPath`, `ConceptId.FromKey` for an index's location): the same
  strings by construction, so no byte of `okf` or `okf-render` output changes.

- **`okf-mcp`'s bundle discovery reads a candidate's root `index.md` under a
  16 MiB bound** (#139, lot C; spec §6, §9): `OkfBundleDiscovery.ReadRootIndexOrNull`
  answers `null` — "no declared version", as for any unreadable index — for
  a larger file without reading it (`OkfBundleDiscovery.MaxRootIndexBytes`),
`````

In `CLAUDE.md`, find:

`````markdown
  - `Internal/LfLines.cs` — the single shared line splitter (splits on `\n` only, stripping a preceding `\r`). Use it anywhere `\n`-based line splitting matters; do not reintroduce private copies.
  - `Internal/ReparsePoints.cs` — internal symlink/junction detection; `OKF4net.Catalog` is granted `InternalsVisibleTo` so it can reuse this seam rather than duplicating a platform-specific implementation. **Two predicates, and picking the wrong one is a security bug: a guard fails closed, a walk fails open.** Anything that then writes, deletes or reads outward uses the STRICT pair (`IsReparsePointOrUninspectable` / `HasReparsePointOrUninspectableAncestor`, where an entry whose link status cannot be read is refused like a link); only the lenient `IsReparsePoint` / `HasReparsePointAncestor` are for enumeration that must not abort on an unreadable entry — and that is now exactly two sites: one walk (`Providers/FileSystemBundleProvider.Walk`, through which every walk of the bundle goes — a load's, an index regeneration's, `GetVersion`'s; `IndexGenerator`'s own two walks went when it moved onto the provider) plus `Bundle.cs:912-913`, `TryResolveResource`'s §6.2 status classification, which is not a walk at all — it is lenient because the actual read gate downstream, `Bundle.ReadResourceText(string)` (guard at `:972-973`), uses the STRICT pair and throws. Do not add a third, and never add a lenient use in a guard.
  - `Internal/CliArgs.cs` — the single shared command-line argument scanner (flags, valued flags, positionals, `--` separator), used by both `okf` (`OKF4net.Cli`, one call per verb with that verb's own declared flag lists) and `okf-render` (`OKF4net.Render`, single command); do not reintroduce a second hand-rolled scanner in either binary.
  - `Internal/AuditText.cs` — the single renderer of the audit summary, one audit finding line, and one verification record line, behind both `okf audit`/`okf verify` and the `okf_audit`/`okf_verify` tools; this text is golden-locked (`tests/fixtures/golden/audit-v02.out`, `verify.out`) — do not fork a second renderer.
  - `Providers/` — the storage seam under `Bundle` (design: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lots A, B and C). `IBundleProvider` is a key space of byte objects that knows nothing of concepts; the core validates every raw name as a **tree** (`BundleKeyValidator`: base rules, the provider's `NativeConstraints`, the host's `BundleKeyPolicy` — `Native` by default, `Strict` opt-in; portability findings are warnings, `Bundle.PortabilityIssues` → `KeyNotPortable`), enforces the `BundleLimits` by counting bytes itself, resolves §6.2 paths lexically on keys (`ResourceKeyResolver`, never dereferencing a URL), and keeps the historical members (`Concept.Path`, `Bundle.Root`, `ParseErrors`) as projections of the new ones by the display rule (`BundleDisplayPath`: location + OS separator + segments, byte-identical to the old `Path.Combine`). `BundleHandle` (spec §5.2) holds the image of one source — a provider, the load options, a `BundleRefreshPolicy` (`Manual`, `OnAccess`, `Every`, `Notify`) — and is meant to be **shared** by every consumer of that source (`OkfBundleTools`, the `OkfContextProvider` built over it, whatever else the host hands it to; a consumer built from a root string makes its own handle, hence its own image): a reload that fails keeps the previous image served and sets `IsStale`/`LastLoadError` (only a failed first load throws from `Current`), and `Invalidate` is a generation counter, acknowledged only by a successful load that started after it — every committed write of a `BundleConceptWriter` built over a handle calls it. `Bundle.ExecutableResources` (§5.1) is the load's survey of the `computation` file and the `attester.resource` of every §10 concept, each resource key read once per load (never once per reference) and found `Pinned` with its version or in a state that forbids executing it (`Undeclared`, `Url`, `Unsafe`, `Missing`, `Unreadable`, `TooLarge`): a resource's own problem is its state and never fails the load, and sanctioned code is read for a run only pinned to that version (`ReadResourceText(BundleKey, expected, out version)`). `FileSystemBundleProvider` is the default and writable: `Write` puts the bytes in a temporary `.okf-write-<32 hex>.tmp` beside the target, then, under a striped in-process commit lock, re-checks the boundary, the path STRICTly, the exact stored name and the condition (for `IfMatch`, the SHA-256 of the stored bytes) and commits with `File.Replace` or `File.Move` without overwrite; a replacement Windows refuses without effect is made again, at most three times in all (`BundleWriteNotCommittedException`), any other failed commit is classified from the disk, and an unknown outcome keeps the temporary (`BundleWriteOutcomeUnknownException`). That is the only way `src/OKF4net` writes a bundle (outside it, `okf fmt` and `FileMemoryStore`'s scope deletion still write directly; the latter moves onto it in lot D): `BundleConceptWriter` (concepts, verifications, `log.md`) and `IndexGenerator` write through a provider — `AppendToConceptAtomic`, `RecordVerifications`, `AppendLog` and the index regeneration conditioned on the version they read (on absence for a creation) and retrying a conflict, at most three attempts in all, `WriteConcept` unconditional unless given a condition and never retried — and a creation first passes `BundleTreeCheck`; do not add a write around them. `Bundle.ListNames` is the one listing of the whole bundle that applies every bound (a load and a regeneration both list through it; `BundleTreeCheck`'s creation checks list too, bounded by `MaxEntries` only) and `Bundle.ReadText` the one bounded strict-UTF-8 reader of object content (the loader, `BundleConceptWriter` and `IndexGenerator` read through it); do not fork either. With a confinement boundary the filesystem provider re-checks the chain boundary→root STRICTly before **every** access, because that chain lies outside every per-key ancestor walk: their upper bound is the bundle root, which they inspect nothing above. Under `InvariantGlobalization` `string.Normalize` is a silent no-op, so NFC is decided by `NfcQuickCheck`'s tables (UAX #15's quick check: `NFC_QC=No` plus the derived composing-mark set) and canonical equivalence between sibling names by `CanonicalForm` (table-driven canonical decomposition with canonical ordering — the NFD — Hangul by arithmetic), never by `Normalize`; all four tables are derived from ICU by `UnicodeTableTests`, which fails with the literals to paste whenever a host normalizer disagrees with them and compares `Decompose` with the host's NFD on every scalar — that test is the tables' executable guard.
- **`src/OKF4net.Cli/`** — the `okf` binary (`validate`/`audit`/`verify`/`info`/`index`/`graph`/`parse`/`fmt`), published two ways from this one project: Native AOT (`PublishAot`, `InvariantGlobalization`) for the release archives, winget and `install.sh`, and `dotnet pack` as the portable `OKF4net.Cli` dotnet tool (`PackAsTool`, `RollForward` `Major`, `OKF4net.xml` kept out by `AllowedReferenceRelatedFileExtensions`, and `TargetLatestRuntimePatch` turned off under `_IsPacking` — `PublishAot` otherwise makes pack stamp the packing SDK's runtime patch, e.g. 10.0.8, as the tool's minimum instead of 10.0.0) so a .NET repository can pin `okf` in its tool manifest for CI. The in-process tests never see that package: its only guard is `ci.yml`'s `dotnet tool smoke test` job, which checks its contents and runtimeconfig (framework floor `10.0.0`, `rollForward`, invariant globalization), restores it from a manifest and asserts `validate`'s exit codes. All logic lives in `OkfCli.Run(args, stdin, out, err)` so tests invoke it in-process without spawning a process; `stdin` is read only by `verify -` (concept ids, one per line), no other verb touches it. Each verb's `VerbSpec.Run` handler takes the `CliArgs` parsed against that verb's own declared contract, plus `stdin` and `stdout`, so the shared dispatch in `Run()` does not need a wider delegate than the one verb (`verify`) that actually reads `stdin` requires; `okf` and `okf-render` share the same `OKF4net.Internal.CliArgs` scanner rather than each hand-rolling its own. Every verb is a `VerbSpec` in the `Verbs` table — usage, flag allowlists, per-verb help, positional arity (`Variadic`, set only by `verify`) and handler in one place, so a verb cannot be declared and left unreachable — and the two meta-commands `help`/`version` (and their flag forms) are dispatched by a `switch` in `Run()` before the table, deliberately, since they operate on no bundle. Carries no reference to `OKF4net.Viewer`: static-site generation (`render`) was split out into `src/OKF4net.Render/` so this CI-facing validator does not carry that vendored JavaScript.
- **`src/OKF4net.Attestation/`** — zero-dep §10 attested-computation orchestration, referencing only `OKF4net`. Defines the host-plugged contracts (`IParameterBinder`, `IComputationExecutor`, `IAttester`, resolved per concept's `runtime` field through `IAttestationRuntimeRegistry`) and the value types that flow between them (`BoundComputation`, `Receipt`, `AttestationVerdict`, `AttestationContext`, `AttestationOutcome`); `AttestationOrchestrator.RunAsync` drives one run end to end (capture → bind → execute → receipt-shape check → attest → gate on verdict + `stale_after`), errors-as-data, never writing a verdict back to the bundle (§10.6). `AttestationOrchestrator.Capture` (spec §5.1, §5.4) is the **single** place sanctioned code is read for a run, and it reads only through `Bundle.ReadResourceText(BundleKey, expected, out version)` on the keys and versions `Bundle.ExecutableResources` surveyed at the load, never through the historical path members: the `computation` file (an inline fence is not read) and the attester are each read only if their surveyed state is `Pinned`, and only at that version, so a file replaced after the load is refused rather than run at its new bytes; a caller-supplied version of the computation file (what `okf_run_computation` was shown) must equal the image's or the capture is refused. Nothing is read after it: `RunCapturedAsync` runs on the `AttestationCapture` alone, which is bound to its `ConceptId` and to the `Bundle` image it was taken on, and refused for another concept or another image. A concept without `attester.resource`, or whose `attester.resource` is a URL, is refused before any runtime stage (D6, `docs/spec-conformance` S10.5-1), so `AttesterSourceText` is never null at a runtime's entry — do not reopen that path. A refusal the image may be behind on says so (`AttestationCaptureResult.ImageOutdated`), and a caller holding a `BundleHandle` invalidates it. Referenced by `OKF4net.Agents` to back `okf_run_computation`.
- **`src/OKF4net.Attestation.Containers/`** — a container-based host implementation of §10's contracts: runs the *actual* sanctioned script/SQL and attester script a bundle references in a real container (Docker/Podman/nerdctl via a single `CliContainerEngine(binaryName)`), never a C# reimplementation. `AllowlistParameterBinder` is shared by both `ContainerRuntimeKind`s and never edits `BoundComputation.BoundText` — parameter values travel separately (a JSON env var for `Script`, native driver binding for `SqlClient`), because textual substitution both reopens injection and breaks provenance comparisons that expect a sanctioned placeholder (e.g. BigQuery's `@name`) to survive verbatim into a receipt's `executed_sql`. No container is ever given a bind-mounted volume — every payload (script text, SQL text, attester module source) travels over stdin; the in-container command is always a short, fixed string. `ContainerAttester` always runs on its own fixed Python image (`ContainerAttesterOptions`), independent of whatever image an executor's `ContainerRuntimeProfile` uses. Depended on by nothing yet in `src/` — see `samples/attestation-containers-demo/` for a worked end-to-end example. References only `OKF4net.Attestation`, zero third-party `PackageReference`; requires a container engine binary on `PATH` at run time (a new *runtime* prerequisite for this repo, not a NuGet dependency).
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`. `OkfBundleTools` serves its image from a `BundleHandle` (`Handle`, spec §5.2: shared with the `OkfContextProvider` built over it and with whoever else the host hands the handle to; the root-string constructors build their own, `Manual`) and does no direct file I/O: its reads go through the image's provider, index regeneration through `IndexGenerator.Regenerate(Handle.Provider, …)`, its writes through a `BundleConceptWriter` built over the handle, and the write tools are offered only when the provider can write. **Observations (spec §5.4, D1/D5) are recorded at the presentation boundary only**: the functions bound by `GetTools()` as `okf_read_concept`, `okf_get_computation`, `okf_write_concept` and `okf_run_computation` are the internal `ReadConceptTool`, `GetComputationTool`, `WriteConceptTool` and `RunComputationToolAsync`, which record into the per-instance `ObservationRegistry` (10 000 keys, LRU; per tool set, not shared through the handle) as one of four purposes — full concept, code, computation file, absent — and gate on it: a write needs a full or absent observation, a run needs a full or code observation of the concept at the version the image holds and, for a file computation, a file observation, the gate deciding from the image's `ExecutableResources` survey (no live I/O) and deferring a state `Capture` cannot take to `Capture`'s own refusal. The public `ReadConcept`/`GetComputation`/`WriteConcept`/`RunComputationAsync` a host calls record nothing and gate on nothing (the public `RunComputationAsync` still captures through `AttestationOrchestrator.Capture`), and `OkfContextProvider` calls the public ones — an injection is never an observation. Do not bind a public method to a model-facing name, and do not record outside a `*Tool` wrapper.
- **`src/OKF4net.Catalog/`** — knowledge-catalog model and logic, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Depended on by `OKF4net.Catalog.Hosting`. Each manifest source carries a `role` (`SourceRole`): `Knowledge` (read-only, searched by `IKnowledgeResolver`) or `Memory` (writable, scoped by a required `tier` — `session`/`user`/`tenant`, all three backed by `FileMemoryStore`, fed by `IMemoryStore`, never searched by the resolver); any other `role` string in `catalog.json` is rejected (`CatalogDiagnosticCode.IllegalRole`).
- **`src/OKF4net.Catalog.Hosting/`** — host-integration layer for the catalog, referencing only `OKF4net.Catalog`. This is the sole project allowed a `Microsoft.Extensions.*` package (`Microsoft.Extensions.DependencyInjection.Abstractions`) — an explicit, narrowly-scoped exception to the zero-dependency rule so catalog sources can register with a host's `IServiceCollection`; the core dependency graph (`OKF4net.Catalog` → `OKF4net`) stays zero-dependency and acyclic.
- **`src/OKF4net.Viewer/`** — static HTML site generation for a bundle, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Backs the `okf-render` binary (`src/OKF4net.Render/`), not `okf` itself. Units: `SiteModel` (pure `Bundle` → display-model projection, including the page head, `DisplayBody` without a leading H1 that repeats the title, the bundle name and the graph page's free name), `SiteIndex` + `IndexScript` (pure `Bundle` → site index v2 → `assets/okf-index.js`: concepts with their type rank and a description truncated at 200 code points, merged edges, ghosts, the id tree, the ranked `types` table), `HtmlWriter` (the only I/O; one `RenderDocumentStart`/`RenderHeader` for the page, index and graph views, the `PageScripts` table), `ViewerAssets` (every file under `Assets/` — CSS, JS, fonts, licence texts — embedded under its path and written by `HtmlWriter` with no per-file code). The interactive scripts (`okf-theme.js` in `<head>`; `okf-resize.js` first at the end of `<body>`, before `marked.min.js` and `viewer.js`; `okf-site.js`, `okf-shapes.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js`, `okf-page.js`, `okf-local.js` after `viewer.js`) are classic scripts that never touch `viewer.js`'s `{ body, links }` contract and insert bundle text through `textContent`, or `setAttribute` on a fixed attribute name — never `innerHTML` or a markup string; every chrome CSS selector and every `okf-page.js` query is anchored to a chrome container, because the sanitizer keeps `class` on `<code>` (the harness checks the `*chrome-classes.md` fixture of every slice that has one). **`okf-shapes.js` (`OkfShapes`) is the single source of every type, trust, staleness and ghost shape** (explorer, palette, chips, lists, legends, and the P2/P3 graphs): geometry in absolute coordinates from one `SIZES` table, a fixed SVG vocabulary, colours only through classes reading the CSS tokens — never draw such a shape elsewhere. The viewer also vendors three font families — Inter, Inter Tight and Space Mono, latin subset, woff2, unmodified, SIL OFL 1.1 (the exception recorded beside marked in "What this is") — under `Assets/fonts/`, beside their licence texts and a provenance table (`fonts/README.md`, the one `Assets/` file that is not embedded); `viewer.css` declares their `@font-face` with relative `fonts/…` URLs, which load from `file://` in Chrome, Edge and Firefox (spec §11.0). The scripts' guard is the same jsdom harness, which loads a site generated from `tools/viewer-security-check/fixtures/hostile-bundle/`, and runs every `cases/*.js` a later slice adds through a frozen helper object; what jsdom cannot see (layout, fonts, contrast) is checked by the tooled recette `tools/viewer-security-check/recette/`, run by hand, with Playwright resolved at run time and never a dependency (design: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`). `okf-local.js` (`OkfLocal`) draws the "Neighbourhood" section of a concept page's context panel (no neighbour, no section): `build` and `labels` are pure — no DOM, no clock, no randomness — and are called directly by `tools/viewer-security-check/cases/p2.js`; the concept and its neighbours at 1 or 2 hops, at most 40 drawn (the rest named in an equivalent list that holds every neighbour, behind a "+N omitted" button), absent targets as ghosts, and an "Open in graph" link that repeats the header's own `graph.html#<concept id>` link; its "Enlarge the neighbourhood" button opens the same neighbourhood in a modal dialog (`#okf-local-modal`, appended to `<body>`: the rest of the page `inert`, Tab kept inside, Escape closes) whose drawing is laid out for its measured area through the optional `viewport` of `build`/`labels` — without one, the panel's layout is unchanged, pinned by a digest in `cases/p2-modal.js`. `okf-resize.js` needs no other script and loads first on concept pages, the index and `graph.html`: from 1,100 px up it adds a keyboard- and pointer-operable splitter (`role="separator"`) between the middle column and the right-hand one (context panel, graph drawer), 240 to 720 px wide (at most 60% of the layout, leaving the middle column 360), kept in `localStorage` under `okf-context-w`, from which only a three-digit width within those bounds is ever read back. The global graph is a page of its own, `graph.html` at the site root (`graph-1.html`… when a concept takes the name, `SiteModel.FreeGraphPagePath`), written only by `HtmlWriter.RenderGraph` and the only page that loads `okf-sim.js` and `okf-graph.js`. **`okf-sim.js` is deterministic by construction and must stay so**: no DOM, no clock, no `Math.random`, only `+ - * / % >>>`, `Math.sqrt` and the exact `Math.floor`/`ceil`/`trunc`/`abs`/`min`/`max`/`imul` — never another `Math` function nor `**`, whose results vary between engines — with a fixed accumulation order and its work counted (never timed) per slice, suspendable inside an iteration. `tools/viewer-security-check/cases/p3.js` is its guard: determinism across loads and slicings, bounds, a sandbox whose `Math` holds only the allowed functions, and a source smoke check that says it is one. `okf-graph.js` is the page part: facets, the list equivalent (always shown above `OkfSim.NODE_LIMIT`, 1,500 nodes), pan, zoom and node drag, and the keyboard contract; a dragged position feeds nothing back into the layout, and a canvas that changes size (the splitter, the window) is fitted again unless the reader moved the view. **One deliberate exception to "the audit computation is not forked"**: the browser compares `Date.now()` with each concept's `staleAfterMs` (§5.5 deadline rounded up to a whole millisecond in C#) so staleness is shown as of reading, not of generation; everything else about trust and staleness comes from `ConceptAudit`. Markdown is rendered **client-side** by a vendored copy of marked (MIT, v15.0.12, credited in `NOTICE`) — the generated page carries its raw markdown plus a link-rewiring table as an HTML-safe JSON payload, escaped by `HtmlSafeJson` so untrusted bundle content cannot break out of the `<script>` container. Raw HTML is neutralized by sanitizing the **parsed DOM** in `viewer.js`, not by marked itself (it has no `sanitize` option any more) and not by patching marked's renderer hooks (tried and dropped — see below): an element allowlist (gated for a handful of tags by an attribute-value constraint table, e.g. `<input>` survives only as `type="checkbox"`, forced `disabled`, since a screen reader announces a real checkbox with its state where a decorative glyph would lose it), a per-tag attribute allowlist that drops every `on*` handler, URL-scheme validation on `href`/`src`, and an opaque-tags table (`<script>`, `<style>`, `<iframe>`, `<noembed>`, `<noframes>`, `<xmp>`, `<plaintext>`, `<template>`, `<base>`) dropped with no content kept at all, since it is source, not prose; a disallowed tag *not* on that table (e.g. `<div>`, `<details>`) is unwrapped instead — the element is dropped but its already-sanitized children move up in its place, by marking disallowed elements, then detaching every node bottom-up and re-appending each kept node top-down, so every unwrap mutation moves one childless node (opaque removals still drag their subtrees, innermost first). Unwrapping one element at a time (innermost- or outermost-first) is superlinear, because moving a node drags its whole subtree; `tools/viewer-security-check/` guards this by counting the nodes every DOM mutation drags while sanitizing (bound 4 × N) — never by timing it. This sanitizer is the whole defense, not one layer of it — renderer-hook patching (suppressing marked's `html` renderer output) was tried and measured against the vendored build plus the hostile-payload battery in `tools/viewer-security-check/`: it stopped nothing the sanitizer alone didn't already stop, while it silently deleted benign wrapped content (e.g. `<details><summary>...</summary>body</details>` rendered as `""` instead of keeping "body"), because marked's `Renderer.image()` interpolates the `alt` attribute with no escaping at all — `![foo" onerror="alert(1)](x.png)` breaks out of the attribute with no raw-HTML token involved, a class of bug no renderer-hook override can see, let alone stop; no amount of patching marked's hooks bounds that class in general. **Do not reintroduce renderer-hook patching as "extra defense in depth"** — it buys no security property the sanitizer lacks and reintroduces the content-loss bug. xunit runs on .NET and cannot execute JavaScript, so `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` only smoke-checks for source-text markers (allowlist names, rejected schemes) — those tests stay green even if the sanitizer is gutted, and are **not** proof it works. The real guard is `tools/viewer-security-check/`, a Node/jsdom harness that runs the actual vendored `marked.min.js` and `viewer.js` against hostile payloads; CI runs it as the `viewer sanitizer (JS)` job. **Whenever you re-vendor `marked.min.js` or edit `viewer.js`, that harness is what tells you whether the defense still holds** — add a case to it for any new payload class you discover. No full-text search by design: a static site has no process to run `ConceptSearch` in, and mirroring its weights in JS would fork the scorer. The "Jump to" palette is not search: it matches titles, ids and tags by fixed tiers (exact id, prefix, substring, exact tag) and never reads bodies. Search is planned to arrive via the VS Code extension (#163), whose extension host can call the .NET side — **there is no `okf serve` and none is planned** (#40's live-server half was dropped in its favour).
- **`src/OKF4net.Render/`** — the `okf-render` binary (`okf-render <bundle> --out <dir>`), published Native AOT (`PublishAot`, `InvariantGlobalization`) like `OKF4net.Cli`, referencing only `OKF4net.Viewer` (and `OKF4net`). Split out of `OKF4net.Cli` so `okf validate` — the small, dependency-free CI validator distributed via winget and as the `OKF4net.Cli` dotnet tool — does not carry `OKF4net.Viewer`'s vendored viewer JavaScript, which every `okf validate` run paid for (+164 KB / +4.0% on the AOT binary) but never executed; the same "leaf owns its own dependencies" pattern `OKF4net.Mcp` already established. All logic lives in `OkfRenderCli.Run(args, out, err)`, mirroring `OkfCli`: it scans its one command with the shared `OKF4net.Internal.CliArgs` (`--out` as the valued flag, `-h`/`--help` as help flags, `-V`/`--version` as valueless flags) — the same scanner `okf` uses, not a second hand-rolled copy — keeping only its own render-specific `missing --out <dir>` check; it has no verb table of its own since one command needs no dispatch machinery. `render` never shipped in a release, so `okf` carries no shim or hint pointing at this binary — `okf render` is an ordinary unknown subcommand.
- **`src/OKF4net.Mcp/`** — a local [Model Context Protocol](https://modelcontextprotocol.io) server exposing one OKF bundle over stdio, published as the `okf-mcp` `dotnet tool` (`PackAsTool`, no Native AOT). Thin entry point: `Program.cs` resolves the bundle root + read-only flag via `OkfMcpConfig` (testable static; on misconfig it prints a one-line usage/error to stderr and exits non-zero) and starts a stdio host serving `OKF4net.Agents`' `OkfBundleTools` (its `GetTools(mode)` functions, so the observation gate of `okf_write_concept` applies; it builds no `AttestationOrchestrator`, so `okf_run_computation` is never exposed there); the bundle discovery reads a candidate root `index.md` bounded to 16 MiB (`OkfBundleDiscovery.MaxRootIndexBytes`) and treats a longer or unreadable one as unmarked. **stdio invariant: stdout is reserved for the JSON-RPC stream, every log line goes to stderr.** Tests live in `tests/OKF4net.Tests/Mcp/`.
`````

and replace it with:

`````markdown
  - `Internal/LfLines.cs` — the single shared line splitter (splits on `\n` only, stripping a preceding `\r`). Use it anywhere `\n`-based line splitting matters; do not reintroduce private copies.
  - `Internal/ReparsePoints.cs` — internal symlink/junction detection; `OKF4net.Catalog` is granted `InternalsVisibleTo` so it can reuse this seam rather than duplicating a platform-specific implementation. **Two predicates, and picking the wrong one is a security bug: a guard fails closed, a walk fails open.** Anything that then writes, deletes or reads outward uses the STRICT pair (`IsReparsePointOrUninspectable` / `HasReparsePointOrUninspectableAncestor`, where an entry whose link status cannot be read is refused like a link); only the lenient `IsReparsePoint` / `HasReparsePointAncestor` are for enumeration that must not abort on an unreadable entry — and that is now exactly two sites: one walk (`Providers/FileSystemBundleProvider.Walk`, through which every walk of the bundle goes — a load's, an index regeneration's, `GetVersion`'s; `IndexGenerator`'s own two walks went when it moved onto the provider) plus `Bundle.cs:912-913`, `TryResolveResource`'s §6.2 status classification, which is not a walk at all — it is lenient because the actual read gate downstream, `Bundle.ReadResourceText(string)` (guard at `:972-973`), uses the STRICT pair and throws. Do not add a third, and never add a lenient use in a guard.
  - `Internal/CliArgs.cs` — the single shared command-line argument scanner (flags, valued flags, positionals, `--` separator), used by both `okf` (`OKF4net.Cli`, one call per verb with that verb's own declared flag lists) and `okf-render` (`OKF4net.Render`, single command); do not reintroduce a second hand-rolled scanner in either binary.
  - `Internal/AuditText.cs` — the single renderer of the audit summary, one audit finding line, and one verification record line, behind both `okf audit`/`okf verify` and the `okf_audit`/`okf_verify` tools; this text is golden-locked (`tests/fixtures/golden/audit-v02.out`, `verify.out`) — do not fork a second renderer.
  - `Providers/` — the storage seam under `Bundle` (design: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lots A, B and C). `IBundleProvider` is a key space of byte objects that knows nothing of concepts; the core validates every raw name as a **tree** (`BundleKeyValidator`: base rules, the provider's `NativeConstraints`, the host's `BundleKeyPolicy` — `Native` by default, `Strict` opt-in; portability findings are warnings, `Bundle.PortabilityIssues` → `KeyNotPortable`), enforces the `BundleLimits` by counting bytes itself, resolves §6.2 paths lexically on keys (`ResourceKeyResolver`, never dereferencing a URL), and keeps the historical members as projections of the new ones by the display rule (`BundleDisplayPath`: location + OS separator + segments, byte-identical to the old `Path.Combine`; `Bundle.DisplayPath(problem)` for a load problem) — `Concept.Path`, `Diagnostic.Path` and `AuditFinding.Path` are that projection beside their `Key`, while `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)` and the two `BundleRoot` properties are `[Obsolete]` (lot D; `ObsoleteMembersTests` is the inventory, nothing in `src/` reads them, and they go with #152). `BundleHandle` (spec §5.2) holds the image of one source — a provider, the load options, a `BundleRefreshPolicy` (`Manual`, `OnAccess`, `Every`, `Notify`) — and is meant to be **shared** by every consumer of that source (`OkfBundleTools`, the `OkfContextProvider` built over it, whatever else the host hands it to; a consumer built from a root string makes its own handle, hence its own image): a reload that fails keeps the previous image served and sets `IsStale`/`LastLoadError` (only a failed first load throws from `Current`), and `Invalidate` is a generation counter, acknowledged only by a successful load that started after it — every committed write of a `BundleConceptWriter` built over a handle calls it. `Bundle.ExecutableResources` (§5.1) is the load's survey of the `computation` file and the `attester.resource` of every §10 concept, each resource key read once per load (never once per reference) and found `Pinned` with its version or in a state that forbids executing it (`Undeclared`, `Url`, `Unsafe`, `Missing`, `Unreadable`, `TooLarge`): a resource's own problem is its state and never fails the load, and sanctioned code is read for a run only pinned to that version (`ReadResourceText(BundleKey, expected, out version)`). `FileSystemBundleProvider` is the default and writable: `Write` puts the bytes in a temporary `.okf-write-<32 hex>.tmp` beside the target, then, under a striped in-process commit lock, re-checks the boundary, the path STRICTly, the exact stored name and the condition (for `IfMatch`, the SHA-256 of the stored bytes) and commits with `File.Replace` or `File.Move` without overwrite; a replacement Windows refuses without effect is made again, at most three times in all (`BundleWriteNotCommittedException`), any other failed commit is classified from the disk, and an unknown outcome keeps the temporary (`BundleWriteOutcomeUnknownException`). That is the only way a bundle is written anywhere in `src/` (`okf fmt` and `okf parse` read and write one isolated file named on the command line, which belongs to no bundle, and stay on `System.IO` by design, spec §3; `FileMemoryStore` deletes a scope object by object through its tier's provider since lot D): `BundleConceptWriter` (concepts, verifications, `log.md`) and `IndexGenerator` write through a provider — `AppendToConceptAtomic`, `RecordVerifications`, `AppendLog` and the index regeneration conditioned on the version they read (on absence for a creation) and retrying a conflict, at most three attempts in all, `WriteConcept` unconditional unless given a condition and never retried — and a creation first passes `BundleTreeCheck`; do not add a write around them. `Bundle.ListNames` is the one listing of the whole bundle that applies every bound (a load and a regeneration both list through it; `BundleTreeCheck`'s creation checks list too, bounded by `MaxEntries` only) and `Bundle.ReadText` the one bounded strict-UTF-8 reader of object content (the loader, `BundleConceptWriter` and `IndexGenerator` read through it); do not fork either. With a confinement boundary the filesystem provider re-checks the chain boundary→root STRICTly before **every** access, because that chain lies outside every per-key ancestor walk: their upper bound is the bundle root, which they inspect nothing above. Under `InvariantGlobalization` `string.Normalize` is a silent no-op, so NFC is decided by `NfcQuickCheck`'s tables (UAX #15's quick check: `NFC_QC=No` plus the derived composing-mark set) and canonical equivalence between sibling names by `CanonicalForm` (table-driven canonical decomposition with canonical ordering — the NFD — Hangul by arithmetic), never by `Normalize`; all four tables are derived from ICU by `UnicodeTableTests`, which fails with the literals to paste whenever a host normalizer disagrees with them and compares `Decompose` with the host's NFD on every scalar — that test is the tables' executable guard.
- **`src/OKF4net.Cli/`** — the `okf` binary (`validate`/`audit`/`verify`/`info`/`index`/`graph`/`parse`/`fmt`), published two ways from this one project: Native AOT (`PublishAot`, `InvariantGlobalization`) for the release archives, winget and `install.sh`, and `dotnet pack` as the portable `OKF4net.Cli` dotnet tool (`PackAsTool`, `RollForward` `Major`, `OKF4net.xml` kept out by `AllowedReferenceRelatedFileExtensions`, and `TargetLatestRuntimePatch` turned off under `_IsPacking` — `PublishAot` otherwise makes pack stamp the packing SDK's runtime patch, e.g. 10.0.8, as the tool's minimum instead of 10.0.0) so a .NET repository can pin `okf` in its tool manifest for CI. The in-process tests never see that package: its only guard is `ci.yml`'s `dotnet tool smoke test` job, which checks its contents and runtimeconfig (framework floor `10.0.0`, `rollForward`, invariant globalization), restores it from a manifest and asserts `validate`'s exit codes. All logic lives in `OkfCli.Run(args, stdin, out, err)` so tests invoke it in-process without spawning a process; `stdin` is read only by `verify -` (concept ids, one per line), no other verb touches it. Each verb's `VerbSpec.Run` handler takes the `CliArgs` parsed against that verb's own declared contract, plus `stdin` and `stdout`, so the shared dispatch in `Run()` does not need a wider delegate than the one verb (`verify`) that actually reads `stdin` requires; `okf` and `okf-render` share the same `OKF4net.Internal.CliArgs` scanner rather than each hand-rolling its own. Every verb is a `VerbSpec` in the `Verbs` table — usage, flag allowlists, per-verb help, positional arity (`Variadic`, set only by `verify`) and handler in one place, so a verb cannot be declared and left unreachable — and the two meta-commands `help`/`version` (and their flag forms) are dispatched by a `switch` in `Run()` before the table, deliberately, since they operate on no bundle. Carries no reference to `OKF4net.Viewer`: static-site generation (`render`) was split out into `src/OKF4net.Render/` so this CI-facing validator does not carry that vendored JavaScript.
- **`src/OKF4net.Attestation/`** — zero-dep §10 attested-computation orchestration, referencing only `OKF4net`. Defines the host-plugged contracts (`IParameterBinder`, `IComputationExecutor`, `IAttester`, resolved per concept's `runtime` field through `IAttestationRuntimeRegistry`) and the value types that flow between them (`BoundComputation`, `Receipt`, `AttestationVerdict`, `AttestationContext`, `AttestationOutcome`); `AttestationOrchestrator.RunAsync` drives one run end to end (capture → bind → execute → receipt-shape check → attest → gate on verdict + `stale_after`), errors-as-data, never writing a verdict back to the bundle (§10.6). `AttestationOrchestrator.Capture` (spec §5.1, §5.4) is the **single** place sanctioned code is read for a run, and it reads only through `Bundle.ReadResourceText(BundleKey, expected, out version)` on the keys and versions `Bundle.ExecutableResources` surveyed at the load, never through the historical path members: the `computation` file (an inline fence is not read) and the attester are each read only if their surveyed state is `Pinned`, and only at that version, so a file replaced after the load is refused rather than run at its new bytes; a caller-supplied version of the computation file (what `okf_run_computation` was shown) must equal the image's or the capture is refused. Nothing is read after it: `RunCapturedAsync` runs on the `AttestationCapture` alone, which is bound to its `ConceptId` and to the `Bundle` image it was taken on, and refused for another concept or another image. A concept without `attester.resource`, or whose `attester.resource` is a URL, is refused before any runtime stage (D6, `docs/spec-conformance` S10.5-1), so `AttesterSourceText` is never null at a runtime's entry — do not reopen that path. A refusal the image may be behind on says so (`AttestationCaptureResult.ImageOutdated`), and a caller holding a `BundleHandle` invalidates it. Referenced by `OKF4net.Agents` to back `okf_run_computation`.
- **`src/OKF4net.Attestation.Containers/`** — a container-based host implementation of §10's contracts: runs the *actual* sanctioned script/SQL and attester script a bundle references in a real container (Docker/Podman/nerdctl via a single `CliContainerEngine(binaryName)`), never a C# reimplementation. `AllowlistParameterBinder` is shared by both `ContainerRuntimeKind`s and never edits `BoundComputation.BoundText` — parameter values travel separately (a JSON env var for `Script`, native driver binding for `SqlClient`), because textual substitution both reopens injection and breaks provenance comparisons that expect a sanctioned placeholder (e.g. BigQuery's `@name`) to survive verbatim into a receipt's `executed_sql`. No container is ever given a bind-mounted volume — every payload (script text, SQL text, attester module source) travels over stdin; the in-container command is always a short, fixed string. `ContainerAttester` always runs on its own fixed Python image (`ContainerAttesterOptions`), independent of whatever image an executor's `ContainerRuntimeProfile` uses. Depended on by nothing yet in `src/` — see `samples/attestation-containers-demo/` for a worked end-to-end example. References only `OKF4net.Attestation`, zero third-party `PackageReference`; requires a container engine binary on `PATH` at run time (a new *runtime* prerequisite for this repo, not a NuGet dependency).
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`. `OkfBundleTools` serves its image from a `BundleHandle` (`Handle`, spec §5.2: shared with the `OkfContextProvider` built over it and with whoever else the host hands the handle to; the root-string constructors build their own, `Manual`) and does no direct file I/O: its reads go through the image's provider, index regeneration through `IndexGenerator.Regenerate(Handle.Provider, …)`, its writes through a `BundleConceptWriter` built over the handle, and the write tools are offered only when the provider can write. **Observations (spec §5.4, D1/D5) are recorded at the presentation boundary only**: the functions bound by `GetTools()` as `okf_read_concept`, `okf_get_computation`, `okf_write_concept` and `okf_run_computation` are the internal `ReadConceptTool`, `GetComputationTool`, `WriteConceptTool` and `RunComputationToolAsync`, which record into the per-instance `ObservationRegistry` (10 000 keys, LRU; per tool set, not shared through the handle) as one of four purposes — full concept, code, computation file, absent — and gate on it: a write needs a full or absent observation, a run needs a full or code observation of the concept at the version the image holds and, for a file computation, a file observation, the gate deciding from the image's `ExecutableResources` survey (no live I/O) and deferring a state `Capture` cannot take to `Capture`'s own refusal. The public `ReadConcept`/`GetComputation`/`WriteConcept`/`RunComputationAsync` a host calls record nothing and gate on nothing (the public `RunComputationAsync` still captures through `AttestationOrchestrator.Capture`), and `OkfContextProvider` calls the public ones — an injection is never an observation. Do not bind a public method to a model-facing name, and do not record outside a `*Tool` wrapper.
- **`src/OKF4net.Catalog/`** — knowledge-catalog model and logic, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Depended on by `OKF4net.Catalog.Hosting`. Each manifest source carries a `role` (`SourceRole`): `Knowledge` (read-only, searched by `IKnowledgeResolver`) or `Memory` (writable, scoped by a required `tier` — `session`/`user`/`tenant`, all three backed by `FileMemoryStore`, fed by `IMemoryStore`, never searched by the resolver); any other `role` string in `catalog.json` is rejected (`CatalogDiagnosticCode.IllegalRole`). **The snapshot owns the handles** (lot D, spec §9): `FileKnowledgeCatalog` builds one `BundleHandle` per enabled knowledge source into `KnowledgeCatalogSnapshot.Handles` — the filesystem provider confined to the catalog root, or the provider of the `IBundleProviderFactory` registered for the source's `provider` type (`KnowledgeCatalogOptions.ProviderFactories`; each factory declares its `AcceptedSettings`, so no credential enters through `catalog.json`; `filesystem` cannot be registered) — under `LoadOptions` and the source's policy (`RefreshPolicy`, `OnAccess` by default, `RefreshPolicyFor` per source), reuses it across reloads only on an identical effective configuration, and disposes what leaves (a disposal only ends the change subscription). Every resolver opens a source through `CatalogSourceOpener`: the snapshot's handle, else (a hand-built snapshot) the path re-resolved and loaded per request as before; the fused engine deduplicates by `IBundleProvider.Id`. `FileMemoryStore` goes through one `FileSystemBundleProvider` per tier root and, per scope, one rooted at the scope and confined to the tier root (the STRICT re-check of the chain before every access): a scope exists while it holds a key, a deletion is per object and leaves the directories, and `DeleteScopeAsync` returns a `MemoryTierDeletion` per tier. `MemoryPath.Encode` prefixes with `_` a segment whose base is a Windows reserved device name (`BundleKeyValidator.HasWindowsReservedBase`, the one list).
- **`src/OKF4net.Catalog.Hosting/`** — host-integration layer for the catalog, referencing only `OKF4net.Catalog`. This is the sole project allowed a `Microsoft.Extensions.*` package (`Microsoft.Extensions.DependencyInjection.Abstractions`) — an explicit, narrowly-scoped exception to the zero-dependency rule so catalog sources can register with a host's `IServiceCollection`; the core dependency graph (`OKF4net.Catalog` → `OKF4net`) stays zero-dependency and acyclic.
- **`src/OKF4net.Viewer/`** — static HTML site generation for a bundle, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Backs the `okf-render` binary (`src/OKF4net.Render/`), not `okf` itself. Units: `SiteModel` (pure `Bundle` → display-model projection, including the page head, `DisplayBody` without a leading H1 that repeats the title, the bundle name and the graph page's free name), `SiteIndex` + `IndexScript` (pure `Bundle` → site index v2 → `assets/okf-index.js`: concepts with their type rank and a description truncated at 200 code points, merged edges, ghosts, the id tree, the ranked `types` table), `HtmlWriter` (the only I/O; one `RenderDocumentStart`/`RenderHeader` for the page, index and graph views, the `PageScripts` table), `ViewerAssets` (every file under `Assets/` — CSS, JS, fonts, licence texts — embedded under its path and written by `HtmlWriter` with no per-file code). The interactive scripts (`okf-theme.js` in `<head>`; `okf-resize.js` first at the end of `<body>`, before `marked.min.js` and `viewer.js`; `okf-site.js`, `okf-shapes.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js`, `okf-page.js`, `okf-local.js` after `viewer.js`) are classic scripts that never touch `viewer.js`'s `{ body, links }` contract and insert bundle text through `textContent`, or `setAttribute` on a fixed attribute name — never `innerHTML` or a markup string; every chrome CSS selector and every `okf-page.js` query is anchored to a chrome container, because the sanitizer keeps `class` on `<code>` (the harness checks the `*chrome-classes.md` fixture of every slice that has one). **`okf-shapes.js` (`OkfShapes`) is the single source of every type, trust, staleness and ghost shape** (explorer, palette, chips, lists, legends, and the P2/P3 graphs): geometry in absolute coordinates from one `SIZES` table, a fixed SVG vocabulary, colours only through classes reading the CSS tokens — never draw such a shape elsewhere. The viewer also vendors three font families — Inter, Inter Tight and Space Mono, latin subset, woff2, unmodified, SIL OFL 1.1 (the exception recorded beside marked in "What this is") — under `Assets/fonts/`, beside their licence texts and a provenance table (`fonts/README.md`, the one `Assets/` file that is not embedded); `viewer.css` declares their `@font-face` with relative `fonts/…` URLs, which load from `file://` in Chrome, Edge and Firefox (spec §11.0). The scripts' guard is the same jsdom harness, which loads a site generated from `tools/viewer-security-check/fixtures/hostile-bundle/`, and runs every `cases/*.js` a later slice adds through a frozen helper object; what jsdom cannot see (layout, fonts, contrast) is checked by the tooled recette `tools/viewer-security-check/recette/`, run by hand, with Playwright resolved at run time and never a dependency (design: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`). `okf-local.js` (`OkfLocal`) draws the "Neighbourhood" section of a concept page's context panel (no neighbour, no section): `build` and `labels` are pure — no DOM, no clock, no randomness — and are called directly by `tools/viewer-security-check/cases/p2.js`; the concept and its neighbours at 1 or 2 hops, at most 40 drawn (the rest named in an equivalent list that holds every neighbour, behind a "+N omitted" button), absent targets as ghosts, and an "Open in graph" link that repeats the header's own `graph.html#<concept id>` link; its "Enlarge the neighbourhood" button opens the same neighbourhood in a modal dialog (`#okf-local-modal`, appended to `<body>`: the rest of the page `inert`, Tab kept inside, Escape closes) whose drawing is laid out for its measured area through the optional `viewport` of `build`/`labels` — without one, the panel's layout is unchanged, pinned by a digest in `cases/p2-modal.js`. `okf-resize.js` needs no other script and loads first on concept pages, the index and `graph.html`: from 1,100 px up it adds a keyboard- and pointer-operable splitter (`role="separator"`) between the middle column and the right-hand one (context panel, graph drawer), 240 to 720 px wide (at most 60% of the layout, leaving the middle column 360), kept in `localStorage` under `okf-context-w`, from which only a three-digit width within those bounds is ever read back. The global graph is a page of its own, `graph.html` at the site root (`graph-1.html`… when a concept takes the name, `SiteModel.FreeGraphPagePath`), written only by `HtmlWriter.RenderGraph` and the only page that loads `okf-sim.js` and `okf-graph.js`. **`okf-sim.js` is deterministic by construction and must stay so**: no DOM, no clock, no `Math.random`, only `+ - * / % >>>`, `Math.sqrt` and the exact `Math.floor`/`ceil`/`trunc`/`abs`/`min`/`max`/`imul` — never another `Math` function nor `**`, whose results vary between engines — with a fixed accumulation order and its work counted (never timed) per slice, suspendable inside an iteration. `tools/viewer-security-check/cases/p3.js` is its guard: determinism across loads and slicings, bounds, a sandbox whose `Math` holds only the allowed functions, and a source smoke check that says it is one. `okf-graph.js` is the page part: facets, the list equivalent (always shown above `OkfSim.NODE_LIMIT`, 1,500 nodes), pan, zoom and node drag, and the keyboard contract; a dragged position feeds nothing back into the layout, and a canvas that changes size (the splitter, the window) is fitted again unless the reader moved the view. **One deliberate exception to "the audit computation is not forked"**: the browser compares `Date.now()` with each concept's `staleAfterMs` (§5.5 deadline rounded up to a whole millisecond in C#) so staleness is shown as of reading, not of generation; everything else about trust and staleness comes from `ConceptAudit`. Markdown is rendered **client-side** by a vendored copy of marked (MIT, v15.0.12, credited in `NOTICE`) — the generated page carries its raw markdown plus a link-rewiring table as an HTML-safe JSON payload, escaped by `HtmlSafeJson` so untrusted bundle content cannot break out of the `<script>` container. Raw HTML is neutralized by sanitizing the **parsed DOM** in `viewer.js`, not by marked itself (it has no `sanitize` option any more) and not by patching marked's renderer hooks (tried and dropped — see below): an element allowlist (gated for a handful of tags by an attribute-value constraint table, e.g. `<input>` survives only as `type="checkbox"`, forced `disabled`, since a screen reader announces a real checkbox with its state where a decorative glyph would lose it), a per-tag attribute allowlist that drops every `on*` handler, URL-scheme validation on `href`/`src`, and an opaque-tags table (`<script>`, `<style>`, `<iframe>`, `<noembed>`, `<noframes>`, `<xmp>`, `<plaintext>`, `<template>`, `<base>`) dropped with no content kept at all, since it is source, not prose; a disallowed tag *not* on that table (e.g. `<div>`, `<details>`) is unwrapped instead — the element is dropped but its already-sanitized children move up in its place, by marking disallowed elements, then detaching every node bottom-up and re-appending each kept node top-down, so every unwrap mutation moves one childless node (opaque removals still drag their subtrees, innermost first). Unwrapping one element at a time (innermost- or outermost-first) is superlinear, because moving a node drags its whole subtree; `tools/viewer-security-check/` guards this by counting the nodes every DOM mutation drags while sanitizing (bound 4 × N) — never by timing it. This sanitizer is the whole defense, not one layer of it — renderer-hook patching (suppressing marked's `html` renderer output) was tried and measured against the vendored build plus the hostile-payload battery in `tools/viewer-security-check/`: it stopped nothing the sanitizer alone didn't already stop, while it silently deleted benign wrapped content (e.g. `<details><summary>...</summary>body</details>` rendered as `""` instead of keeping "body"), because marked's `Renderer.image()` interpolates the `alt` attribute with no escaping at all — `![foo" onerror="alert(1)](x.png)` breaks out of the attribute with no raw-HTML token involved, a class of bug no renderer-hook override can see, let alone stop; no amount of patching marked's hooks bounds that class in general. **Do not reintroduce renderer-hook patching as "extra defense in depth"** — it buys no security property the sanitizer lacks and reintroduces the content-loss bug. xunit runs on .NET and cannot execute JavaScript, so `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` only smoke-checks for source-text markers (allowlist names, rejected schemes) — those tests stay green even if the sanitizer is gutted, and are **not** proof it works. The real guard is `tools/viewer-security-check/`, a Node/jsdom harness that runs the actual vendored `marked.min.js` and `viewer.js` against hostile payloads; CI runs it as the `viewer sanitizer (JS)` job. **Whenever you re-vendor `marked.min.js` or edit `viewer.js`, that harness is what tells you whether the defense still holds** — add a case to it for any new payload class you discover. No full-text search by design: a static site has no process to run `ConceptSearch` in, and mirroring its weights in JS would fork the scorer. The "Jump to" palette is not search: it matches titles, ids and tags by fixed tiers (exact id, prefix, substring, exact tag) and never reads bodies. Search is planned to arrive via the VS Code extension (#163), whose extension host can call the .NET side — **there is no `okf serve` and none is planned** (#40's live-server half was dropped in its favour).
- **`src/OKF4net.Render/`** — the `okf-render` binary (`okf-render <bundle> --out <dir>`), published Native AOT (`PublishAot`, `InvariantGlobalization`) like `OKF4net.Cli`, referencing only `OKF4net.Viewer` (and `OKF4net`). Split out of `OKF4net.Cli` so `okf validate` — the small, dependency-free CI validator distributed via winget and as the `OKF4net.Cli` dotnet tool — does not carry `OKF4net.Viewer`'s vendored viewer JavaScript, which every `okf validate` run paid for (+164 KB / +4.0% on the AOT binary) but never executed; the same "leaf owns its own dependencies" pattern `OKF4net.Mcp` already established. All logic lives in `OkfRenderCli.Run(args, out, err)`, mirroring `OkfCli`: it scans its one command with the shared `OKF4net.Internal.CliArgs` (`--out` as the valued flag, `-h`/`--help` as help flags, `-V`/`--version` as valueless flags) — the same scanner `okf` uses, not a second hand-rolled copy — keeping only its own render-specific `missing --out <dir>` check; it has no verb table of its own since one command needs no dispatch machinery. `render` never shipped in a release, so `okf` carries no shim or hint pointing at this binary — `okf render` is an ordinary unknown subcommand.
- **`src/OKF4net.Mcp/`** — a local [Model Context Protocol](https://modelcontextprotocol.io) server exposing one OKF bundle over stdio, published as the `okf-mcp` `dotnet tool` (`PackAsTool`, no Native AOT). Thin entry point: `Program.cs` resolves the bundle root + read-only flag via `OkfMcpConfig` (testable static; on misconfig it prints a one-line usage/error to stderr and exits non-zero) and starts a stdio host serving `OKF4net.Agents`' `OkfBundleTools` (its `GetTools(mode)` functions, so the observation gate of `okf_write_concept` applies; it builds no `AttestationOrchestrator`, so `okf_run_computation` is never exposed there); the bundle discovery reads a candidate root `index.md` bounded to 16 MiB (`OkfBundleDiscovery.MaxRootIndexBytes`) and treats a longer or unreadable one as unmarked. **stdio invariant: stdout is reserved for the JSON-RPC stream, every log line goes to stderr.** Tests live in `tests/OKF4net.Tests/Mcp/`.
`````

In `README.md`, find:

`````markdown
displayable location. The image is replaced as a whole but its documents are
not frozen (the `Frontmatter` of today is mutable): modifying a document of a
shared image is not supported.

### As a CLI

On Windows, install via [winget](https://github.com/microsoft/winget-pkgs):
`````

and replace it with:

`````markdown
displayable location. The image is replaced as a whole but its documents are
not frozen (the `Frontmatter` of today is mutable): modifying a document of a
shared image is not supported.

A catalog keeps the handles itself (spec §9): each `FileKnowledgeCatalog`
snapshot carries one per enabled knowledge source
(`snapshot.Handles[sourceId]`), built under the catalog's
`KnowledgeCatalogOptions.LoadOptions` and the source's refresh policy
(`RefreshPolicy`, `OnAccess` by default, so a source stays as fresh as when it
was reloaded at every request at the cost of a token read; `RefreshPolicyFor`
chooses per source), and the resolvers search through them. A reload keeps a
source's handle, image included, while its effective configuration is
unchanged, and rebuilds it otherwise (a changed `path`, say); a handle that
leaves the catalog is disposed, which only ends its change subscription — a
request still holding the old snapshot keeps reading its image. A
`catalog.json` source may name another storage than a directory under the
catalog root: `"provider": "<type>"` plus `"settings"`, served by the
`IBundleProviderFactory` the host registered for that type
(`KnowledgeOptions.AddProviderFactory`, or `KnowledgeCatalogOptions.ProviderFactories`);
each factory declares the settings it accepts, and a source carrying any other
setting, an unknown type, or a `path` on a non-filesystem type is rejected
with the whole reload, so no credential ever travels through the manifest
(spec §7). Memory sources stay on the filesystem. The path-based members of
`Bundle` (`Root`, `IndexFiles`, `LogFiles`, `ParseErrors`,
`TryResolveResource`, `ReadResourceText(string)`) and the two `BundleRoot`
properties are obsolete (a warning naming the replacement) and removed with
the asynchronous contract (#152).

### As a CLI

On Windows, install via [winget](https://github.com/microsoft/winget-pkgs):
`````

In `README.md`, find:

`````markdown
```

**V1 limits, stated exactly:**

- Local filesystem bundles only.
- One shared catalog.
- No semantic/fuzzy deduplication across sources (two manifest entries
  resolving to the *same directory* are collapsed only under the two merged
  strategies; similar content in genuinely different bundles is not).
`````

and replace it with:

`````markdown
```

**V1 limits, stated exactly:**

- Bundles under the catalog root by default; another storage only through a
  provider factory the host registers (`provider` in `catalog.json`, see "One
  image per source" above). No provider but the filesystem ships with this
  repository (#141 embedded resources, #142 S3 are the tracked ones), and
  memory sources are always on the filesystem.
- One shared catalog.
- No semantic/fuzzy deduplication across sources (two manifest entries
  resolving to the *same directory* are collapsed only under the two merged
  strategies; similar content in genuinely different bundles is not).
`````

In `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, find:

`````markdown
- **Les sources mémoire** restent de type `filesystem` et sont résolues comme
  aujourd'hui. Une source mémoire d'un autre type est rejetée avec un
  diagnostic, puisque la mémoire distante est hors périmètre.

**Catalog : nommer un provider dans `catalog.json`.** Une source peut porter
`"provider": "<type>"` et un objet `"settings"`. Le type `filesystem` reste le
défaut, avec `path` comme aujourd'hui. L'hôte enregistre des fabriques de
providers par type (options de `FileKnowledgeCatalog`, et une extension
`````

and replace it with:

`````markdown
- **Les sources mémoire** restent de type `filesystem` et sont résolues comme
  aujourd'hui. Une source mémoire d'un autre type est rejetée avec un
  diagnostic, puisque la mémoire distante est hors périmètre.

> **Note du 2026-11-02 (lot D, plan d'implémentation, décisions L1 à L14).**
> Les handles vivent sur l'instantané (`KnowledgeCatalogSnapshot.Handles`,
> vide pour un instantané construit à la main ou par le parseur : les
> resolvers retombent alors sur la résolution du chemin à chaque requête,
> comme avant). La configuration effective comparée pour réutiliser un handle
> est (type de provider, répertoire résolu, `settings`, politique de
> rafraîchissement, `BundleLoadOptions`) ; la frontière de confinement est
> toujours la racine du catalogue, donc hors de la clé. Les fabriques sont
> `IBundleProviderFactory` (`ProviderType`, `AcceptedSettings`, `Create`),
> enregistrées dans `KnowledgeCatalogOptions.ProviderFactories` ; `filesystem`
> ne peut pas l'être. Un `KnowledgeCatalogSource` gagne `ProviderType` et
> `Settings` en propriétés `init` (pas de rupture) ; son `Path` est vide, et
> refusé s'il est présent (`IllegalPath`), hors `filesystem`. Le constructeur
> `OkfBundleKnowledgeSource(string, string)` crée son propre handle en
> `OnAccess` (une source de catalogue par nature : le rechargement à chaque
> appel devient une comparaison de jeton). La déduplication par
> `IBundleProvider.Id` fusionne aussi deux graphies qui ne diffèrent que par
> un séparateur final. Le §11 « refusée au prochain accès » pour une racine
> remplacée par une jonction est appliqué selon le §5.2 : la lecture du jeton
> est refusée, le rechargement échoue, l'image précédente est servie périmée
> (`IsStale`, `LastLoadError` enveloppant `BundleAccessRefusedException`) et
> rien sous la jonction n'est lu.

**Catalog : nommer un provider dans `catalog.json`.** Une source peut porter
`"provider": "<type>"` et un objet `"settings"`. Le type `filesystem` reste le
défaut, avec `path` comme aujourd'hui. L'hôte enregistre des fabriques de
providers par type (options de `FileKnowledgeCatalog`, et une extension
`````

In `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, find:

`````markdown
    donc de signature sur `DeleteScopeAsync`. `VerificationOutcome`, lui, est
    seulement enrichi.

  La migration asynchrone (#152) sera un second changement cassant, accepté.
- **Comportement, filesystem compris** :
  - bornes finies par défaut (§6). La borne d'entrées compte **toutes** les
    entrées, `.git` et `node_modules` compris (§2) : un très gros dossier peut
    désormais faire échouer un chargement qui réussissait. Un concept de plus de
`````

and replace it with:

`````markdown
    donc de signature sur `DeleteScopeAsync`. `VerificationOutcome`, lui, est
    seulement enrichi.

  La migration asynchrone (#152) sera un second changement cassant, accepté.

  > **Note du 2026-11-02 (lot D).** Les membres fondés sur les chemins ne sont
  > pas retirés dans ce lot mais marqués `[Obsolete]` (avertissement, jamais
  > erreur, message nommant le remplaçant), comme `BundleRoot` : `Bundle.Root`,
  > `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`,
  > `ReadResourceText(string)` et les deux `BundleRoot` ; leur retrait va avec
  > #152. `Concept.Path`, `Diagnostic.Path` et `AuditFinding.Path` restent, à
  > côté d'une `Key` ajoutée (`Diagnostic.Key`, `AuditFinding.Key`) : ce sont
  > la projection d'affichage du §8 que chaque sortie imprime. Les seules
  > ruptures effectives de ce lot sont `MemoryDeleteResult` et la signature de
  > `IMemoryStore.DeleteScopeAsync` ; `KnowledgeCatalogSource` gagne ses
  > réglages en propriétés `init`, sans rupture.
- **Comportement, filesystem compris** :
  - bornes finies par défaut (§6). La borne d'entrées compte **toutes** les
    entrées, `.git` et `node_modules` compris (§2) : un très gros dossier peut
    désormais faire échouer un chargement qui réussissait. Un concept de plus de
`````

In `src/OKF4net.Catalog/README.md`, find:

`````markdown
only) -- a `SourceVisibilityPolicy` function should compare `TenantId`/
`UserId`/`SessionId` individually, not compare two `KnowledgeAccessScope`
instances with `==`/`Equals`.

## V1 limits

- Local filesystem bundles only — no remote/HTTP sources, no external
  connectors.
- One shared catalog per `FileKnowledgeCatalog` instance.
- No semantic/fuzzy deduplication — two concepts with similar content in
  genuinely different bundles are both returned. Only under the two merged
  strategies are two manifest entries resolving to the *same directory*
`````

and replace it with:

`````markdown
only) -- a `SourceVisibilityPolicy` function should compare `TenantId`/
`UserId`/`SessionId` individually, not compare two `KnowledgeAccessScope`
instances with `==`/`Equals`.

## Bundle providers and handles

Each `FileKnowledgeCatalog` snapshot owns one `BundleHandle` per enabled
knowledge source (`snapshot.Handles[sourceId]`), and every resolver searches
through them: an unchanged source is loaded once across requests, and a
change in its storage is seen at the next request under the default
`OnAccess` policy (`KnowledgeCatalogOptions.RefreshPolicy`, overridable per
source with `RefreshPolicyFor`; `LoadOptions` sets the bounds and key policy
of every load). A reload keeps a source's handle while its effective
configuration is unchanged and rebuilds it otherwise; a handle that leaves the
catalog is disposed, which only ends its change subscription. A snapshot built
by hand, or by `CatalogManifestParser.TryParse`, carries no handle: a resolver
then resolves the source's path and loads at each request, as before.

A source may name its storage:

```json
{ "id": "remote", "provider": "s3", "settings": { "bucket": "docs", "prefix": "okf/" } }
```

`provider` defaults to `filesystem` (the only type shipped here, named by
`path` under the catalog root and confined to it on every access). Any other
type needs an `IBundleProviderFactory` the host registers
(`KnowledgeOptions.AddProviderFactory(factory)` or
`KnowledgeCatalogOptions.ProviderFactories`); the factory declares the
settings it accepts, and the whole reload is rejected for a source that
carries any other setting (`IllegalSetting`), an unregistered type
(`UnknownProvider`), a `path` on a non-filesystem type (`IllegalPath`) or a
factory that throws (`ProviderUnavailable`, naming the exception type only).
Credentials never travel through the manifest: the host gives them to the
factory. A `role:"memory"` source must stay on the filesystem
(`IllegalMemoryProvider`).

### Migration: memory scope names with a reserved base

A tenant, user or session identifier whose part before its first `.` is a
Windows reserved device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`,
`LPT1`–`LPT9`, any case — `con.foo`, `aux.a.b`) now encodes with a leading
`_` (`_con.foo-<hash>`), the hash unchanged. Such scopes never worked on
Windows; on Linux and macOS their existing directories keep the old name and
are no longer read until renamed:

```sh
mv memory-tenant/con.foo-0123456789abcdef memory-tenant/_con.foo-0123456789abcdef
```

and the same for each such segment under `memory-user/<tenant>/` and
`memory-session/<tenant>/<user>/`. No other identifier changes.

## V1 limits

- Bundles under the catalog root unless the host registers a provider
  factory (see above); no provider but the filesystem ships with this
  package, and memory sources are always on the filesystem.
- One shared catalog per `FileKnowledgeCatalog` instance.
- No semantic/fuzzy deduplication — two concepts with similar content in
  genuinely different bundles are both returned. Only under the two merged
  strategies are two manifest entries resolving to the *same directory*
`````

- [ ] **Step 2: Check the claims and the build**

Run: `git diff --stat` — expected exactly the five files above. Run `dotnet build OKF4net.sln -nologo -v q` — 0 warnings (no code changed). Read each CHANGELOG bullet against the task it names: the counts, the exception names, the texts (`IllegalPath`, `ProviderUnavailable`, `_con.foo-<hash>`, "use …", "(#152)") must be the code's.

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md README.md src/OKF4net.Catalog/README.md CLAUDE.md docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md
git commit -m "docs: lot D changelog, catalog providers and handles in the READMEs, CLAUDE.md and the design's dated notes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 11: Verification — the whole suite on Windows and Linux, format, AOT, producers, samples, the viewer harness, the differential

Nothing is written in this task; it is the evidence the lot is declared done on. Every command runs from the worktree root; nothing else must build in the worktree while a Release publish runs.

**Files:** none (a scratch differential script and its output, outside the repository).

- [ ] **Step 1: Windows, the whole suite, format, build**

Run: `dotnet build OKF4net.sln -nologo -v q 2>&1 | grep -cE "error|warn"` — expected `0`.
Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration" -nologo -v q` — expected `Passed: 3596, Skipped: 36, Total: 3632`.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expected exit 0, no file listed.

- [ ] **Step 2: Linux, as root and as an unprivileged user**

Run the Docker command of the Global Constraints. Expected: root `Passed: 3559, Skipped: 73, Total: 3632`; tester (Providers, Agents, Attestation, Catalog) `Passed: 1424, Skipped: 46, Total: 1470`; 0 failed in both.

- [ ] **Step 3: Producers and samples**

Run: `dotnet test producers/OkfProducer.sln -nologo -v q` — expected `Passed: 826, Skipped: 15, Total: 841`.
Run, for each of `samples/agents-quickstart`, `samples/catalog-explorer`, `samples/attestation-containers-demo`: `dotnet build <sample>/<solution or project> -nologo -v q` — expected 0 errors (`samples/acme-retail-agent` fails `NU1605` on the base as on this branch, PR #209's pin; not a lot-D matter).

- [ ] **Step 4: Native AOT and the viewer harness**

From PowerShell, with `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH`:
`dotnet publish src/OKF4net.Cli -c Release -nologo -v q` and `dotnet publish src/OKF4net.Render -c Release -nologo -v q` — expected no error or warning; `src/OKF4net.Cli/bin/Release/net10.0/win-x64/publish/okf.exe` about 4 845 056 bytes and `okf-render.exe` about 4 603 904 bytes (within a few KB of the lot-C sizes 4 839 936 / 4 602 880); `okf.exe validate bundles/acme_retail` ends with `✓ conformant with OKF v0.2`.
From `tools/viewer-security-check`: `npm ci` then `npm test` — expected `453 passed, 0 failed` (the harness renders the hostile bundle with the Release `okf-render`, so run it after the publish, never during).

- [ ] **Step 5: The differential against the lot-C tip**

Create a detached base worktree: `git worktree add --detach .claude/worktrees/lotd-base 3ad76b8`. Save the script below as `<scratch>/lotd-diff.sh` and run `bash <scratch>/lotd-diff.sh <base worktree Windows path> <this worktree Windows path> <scratch Windows path>/lotd-diff-out` from Git Bash (it builds the Debug `okf` and `okf-render` of both trees, runs the nine read-only verb forms and `okf-render` on six repository bundles, masks the two clock fields, and diffs transcripts and sites).

`````bash
#!/usr/bin/env bash
# Differential of the read-only `okf` verbs and of `okf-render` between two worktrees
# (BASE = the lot-C tip, HEAD = the lot-D branch), on every repository bundle.
# Usage: lotd-diff.sh <base-worktree> <head-worktree> <out-dir>
set -u
BASE="$1"; HEAD="$2"; OUT="$3"
mkdir -p "$OUT"
BUNDLES="bundles/acme_retail bundles/ga4 bundles/meridian_transit bundles/attestation_containers_demo tests/fixtures/appendix_a tests/fixtures/okf_v02"
mask() { sed -E 's/"evaluatedAt":"[^"]*"/"evaluatedAt":"<masked>"/; s/"asOf":"[^"]*"/"asOf":"<masked>"/; s/^(as of|evaluated at|Evaluated at|evaluatedAt).*/\1 <masked>/' ; }
for wt in base head; do
  if [ "$wt" = base ]; then ROOT="$BASE"; else ROOT="$HEAD"; fi
  OKF="$ROOT/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll"
  RENDER="$ROOT/src/OKF4net.Render/bin/Debug/net10.0/okf-render.dll"
  ( cd "$ROOT" && dotnet build src/OKF4net.Cli -nologo -v q >/dev/null 2>&1 && dotnet build src/OKF4net.Render -nologo -v q >/dev/null 2>&1 ) || { echo "build failed in $ROOT"; exit 1; }
  : > "$OUT/$wt.txt"
  for b in $BUNDLES; do
    for verb in "validate" "validate --json" "info" "info --json" "audit" "audit --json" "audit --json --type Table" "graph" "graph --dot"; do
      echo "== $b :: okf $verb" >> "$OUT/$wt.txt"
      ( cd "$ROOT" && dotnet "$OKF" $verb "$b" 2>&1; echo "exit=$?" ) | mask >> "$OUT/$wt.txt"
    done
    rm -rf "$OUT/site-$wt-$(basename "$b")"
    ( cd "$ROOT" && dotnet "$RENDER" "$b" --out "$OUT/site-$wt-$(basename "$b")" >/dev/null 2>&1; echo "render exit=$?" ) >> "$OUT/$wt.txt"
  done
done
diff "$OUT/base.txt" "$OUT/head.txt" > "$OUT/cli.diff" && echo "CLI transcripts identical ($(wc -l < "$OUT/base.txt") lines)" || echo "CLI transcripts DIFFER (see $OUT/cli.diff)"
for b in $BUNDLES; do
  n=$(basename "$b")
  diff -r "$OUT/site-base-$n" "$OUT/site-head-$n" > "$OUT/site-$n.diff" && echo "site $n identical ($(find "$OUT/site-head-$n" -type f | wc -l) files)" || echo "site $n DIFFERS (see $OUT/site-$n.diff)"
done
`````

Expected, exactly:

```
CLI transcripts identical (477 lines)
site acme_retail identical (34 files)
site ga4 identical (34 files)
site meridian_transit identical (33 files)
site attestation_containers_demo identical (28 files)
site appendix_a identical (29 files)
site okf_v02 identical (27 files)
```

Then `git worktree remove .claude/worktrees/lotd-base`. A differing line is a real failure to investigate on the C# side (Global Constraints), never a snapshot to refresh.

- [ ] **Step 6: Housekeeping**

Run: `git status --short` — expected empty (nothing untracked: no script, no output in the worktree); `git diff --stat 3ad76b8..HEAD` — 61 files; `git log --oneline 3ad76b8..HEAD` — 11 commits. `graphify update .` if the hook has not run. Report the counts of Steps 1–5 in the lot's final message.

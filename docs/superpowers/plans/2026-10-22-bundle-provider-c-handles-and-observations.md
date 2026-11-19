# Bundle provider, lot C: handles, observations and pinned attestation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the provider seam its image holder and its session contract: one `BundleHandle` per source (one image, four lazy refresh policies, a reload obligation), the executable resources of every §10 concept surveyed and pinned at the load, an attestation that captures sanctioned code once through the provider and refuses an undeclared or URL attester (D6), agent tools that serve their image from a handle with no direct I/O, an observation registry so that `okf_write_concept` and `okf_run_computation` act only on what the session was shown (D1, D5), and an `okf-mcp` discovery bounded at 16 MiB — with no byte of the CLI's output moving and every existing public signature kept.

**Architecture:** The handle lives in `src/OKF4net/Providers/` (`BundleHandle`, `BundleRefreshPolicy`, `IBundleChangeNotifier`): a volatile image plus a generation counter for the obligation, one load lock, policy checks under that lock, a `TimeProvider` for `Every`. The core survey lives in `src/OKF4net/ExecutableResources.cs` and `Bundle.Load` (`Bundle.ExecutableResources`, `Bundle.ReadResourceText(key, expected, out version)`, the internal pinned reader `ReadTextAt`). `AttestationOrchestrator` splits §10.5 into `Capture` (the single read of sanctioned code, pinned) and `RunCapturedAsync` (steps 3–10 on a capture); `RunAsync` keeps its signature and delegates. `OkfBundleTools` is built over a handle, reads everything through `Handle.Provider`, and binds its model-facing names to internal `*Tool` wrappers that record into and gate on a per-instance `ObservationRegistry` (`src/OKF4net.Agents/Internal/`); the public methods are untouched. `OkfBundleDiscovery.ReadRootIndexOrNull` reads under a bound.

**Tech Stack:** C# 14 / .NET 10, BCL only in `src/OKF4net`, `src/OKF4net.Attestation` and `src/OKF4net.Mcp`'s library code (zero new `PackageReference`, no P/Invoke); `Microsoft.Agents.AI` in `src/OKF4net.Agents` only; xunit + `Xunit.SkippableFact` in `tests/OKF4net.Tests`; Docker image `mcr.microsoft.com/dotnet/sdk:10.0` for the Linux runs; node 22 for the one fixture-rewriting script of Task 3 and the differential scripts of Task 8.

**Spec:** `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (revision 6 with its dated maintainer notes), sections §5.1–§5.4 (image, handle, policies, observations, D1/D3/D5/D6), the attestation half of §5.4, §9's rows `BundleConceptWriter` (handle constructor), `OkfBundleTools`, `OkfContextProvider`, `AttestationOrchestrator`, `okf-mcp`, and the §10/§11 items they carry; the maintainer's governing rule for every lot: everything works as before for existing callers beyond what §10 announces. Lot A (`docs/superpowers/plans/2026-10-12-bundle-provider-a-contract-and-filesystem.md`, merged as `fa983e4`) and lot B (branch `feat/bundle-provider-b`, tip `c48f23f`, decisions B1–B48) are its base.

## Replay

Every code block of this plan was taken from a spike built one commit per task on branch `spike/lotc-1` (worktree `E:\Sources\okf\.claude\worktrees\bundle-provider-c-spike`, from `c48f23f`, upstream unset), rebuilt and tested as the steps say; the counts below are that spike's, on Windows 11 (.NET SDK 10.0.204) unless stated. Red steps were replayed on a detached worktree of each task's parent commit with the task's test file alone. Base of the plan, Windows full suite: `Passed: 3431, Skipped: 35, Total: 3466`. End of the plan: **Windows `Passed: 3490, Skipped: 35, Total: 3525`**, `dotnet format OKF4net.sln --verify-no-changes` clean, `dotnet build OKF4net.sln` with 0 warnings; Linux (container, root) full suite `3453 / 72 / 3525`, Providers as root `479 / 22 / 501`, Providers + Agents + Attestation as an unprivileged user `1090 / 40 / 1130`; `dotnet test producers/OkfProducer.sln` `826 / 15 / 841` (the base's own counts); Native AOT publish of `okf` (4 830 208 bytes) and `okf-render` (4 587 008 bytes) succeeds and `okf validate bundles/acme_retail` prints `✓ conformant with OKF v0.2`; the read-only `okf` verbs (`validate`, `validate --json`, `info`, `info --json`, `audit`, `audit --json`, `graph`, `graph --dot`) give byte-identical transcripts on the eight repository bundles against `c48f23f` (after the `evaluatedAt` clock is masked), and the public `OkfBundleTools` methods give byte-identical outputs and trees on four bundles (Task 8).

## Scope and cut

The design says four lots: A read side (merged), B write side (done, `c48f23f`), C handles/observations/attestation, D catalogue/viewer/breaking changes. This plan implements **lot C**, cut as follows (the ambiguities are listed under "Open decisions"):

**In lot C** (all of §5, the attestation rows of §9, the handle rows of §9):

- §5.2–§5.3: `BundleHandle`, `BundleRefreshPolicy` (`Manual`, `OnAccess`, `Every`, `Notify`), `IBundleChangeNotifier`, the reload obligation and its acknowledgement (§5.1, revision 6), a failed reload keeping the previous image served, one load at a time; `BundleConceptWriter(BundleHandle, …)` (§9).
- §5.1: the executable-resource survey at the load (`Pinned`, `Undeclared`, `Url`, `Unsafe`, `Missing`, `Unreadable`, `TooLarge`), local to the resource, never a load failure; the pinned read `Bundle.ReadResourceText(key, expected, out version)`.
- §5.4 (attestation) and D6: `AttestationOrchestrator` reads sanctioned code through `TryResolveResourceKey`/`ReadResourceText(BundleKey, …)`, the attester pinned to the image, the computation file pinned to an observed version when one is given; an undeclared or URL attester refused before any runtime stage; the `docs/spec-conformance/` entry (S10.5-1) and the CHANGELOG entry; the `InlineComputation` helper and every test fixture that runs a computation now declare a file attester.
- §9 `OkfBundleTools`: constructor on `BundleHandle`, `Handle`, `Location`; no direct I/O left (`okf_browse`'s `index.md` read and prefix check, `okf_changes_since`'s `log.md` reads, `okf_regenerate_indexes`, `okf_get_computation`'s file, all through the provider); write tools filtered by the effective permission; the observation registry; `okf_write_concept` and `okf_run_computation` conditioned on it (§4, §5.4); `OkfContextProvider` shares the tools' handle and records nothing (D5).
- §9 `okf-mcp`: the root `index.md` of the discovery read under a 16 MiB bound.

**Left to lot D** (the design assigns them to the catalogue, the viewer or the breaking list, and nothing in lot C needs them): the catalogue snapshot owning handles and `ReloadAsync`, `OkfBundleKnowledgeSource` on a handle with the source's policy (`OnAccess` default for `catalog.json`), provider factories in `catalog.json`, `FileMemoryStore` through the provider and `MemoryDeleteResult`, the viewer's `SiteModel`, `Concept.Path` → `Key` and every breaking removal of §10, `BundleRoot` marked `[Obsolete]` (it already throws `NotSupportedException` for a non-filesystem provider, which this lot keeps), `okf fmt`/`parse` (out of the seam by design), the consolidated CHANGELOG/README/CLAUDE.md pass of #143.

**Why this cut.** Everything a handle needs to be useful to the agent tools is in C; everything that needs a *catalogue* of handles is in D. D6 and the survey are in C because the pinned attestation (§5.4 point 3) cannot be built without the survey, and the survey's states are what D6 refuses on. The `okf-mcp` bound is in C because the brief puts it there and it is one function with one test.

## Open decisions (the controller rules; the plan proceeds on the recommendation)

1. **Where the observation gate sits (C19).** The design records observations "at the presentation boundary, never in the public `ReadConcept`/`GetComputation`" and conditions "the tools" on them. If the public `WriteConcept`/`RunComputationAsync` were gated too, no direct caller (hosts, 58 existing test call sites, the samples) could ever write or run, since nothing but the model-facing functions records. The plan gates the **model-facing functions only**: `GetTools()` binds `okf_read_concept`, `okf_get_computation`, `okf_write_concept` and `okf_run_computation` to internal `*Tool` wrappers that record and gate; the public methods keep their behaviour. Recommendation: keep.
2. **After a committed `okf_write_concept`, the observation is dropped (C20).** The written file is not what the model sent (the writer may auto-stamp `generated`), so a second rewrite reads first. The alternative — recording the written version — would need the writer to return it. Recommendation: drop.
3. **A failed reload keeps the previous image served (C18; spec §5.2).** Four existing tests pinned the opposite (`bundle unavailable` from the context provider, `Error` from `okf_validate_bundle`, after the bundle root vanished behind a loaded tool set); §10 does not list this change, §5.2 states it. The plan follows §5.2, re-pins the tests and writes the CHANGELOG entry. Recommendation: follow §5.2.
4. **The public `RunComputationAsync` also captures and reloads after a refused pin (C22/C23).** Its results are unchanged (the orchestrator's `RunAsync` is `Capture` + `RunCapturedAsync`), but a refusal the image may be behind on now invalidates the handle for a direct caller too, so that the next attempt recovers without a host `Refresh()`. Recommendation: yes — otherwise a `Manual` host is refused forever after an attester change.
5. **The D6 conformance entry and the `okf-mcp` bound are in C**, where lot B's decomposition table had put them in D; the brief puts both in C, and the task that makes a behaviour change documents it. Recommendation: C.
6. **`okf_regenerate_indexes` keeps the `string` overload's contract through the internal `IndexGenerator.Regenerate(…, legacy: true)` (C17)** rather than reporting `BundleWriteReport.Failed` lines, so its output is byte-identical. Recommendation: keep for C; lot D may switch to the report.
7. **The resource bound of the survey is `BundleLimits.MaxObjectBytes`** (256 MiB since the maintainer's note of 2026-10-19), not the design's 16 MiB literal (C8). Recommendation: the bound in force.
8. **A refusal at the resolution where it was at the read.** Over a directory that denies listing (a junction with deny ACEs), the provider's exact-name lookup answers "no such entry", so a computation file or an attester behind it is now `could not be resolved (Missing)` instead of `could not be read: UnauthorizedAccessException`; three Windows-only tests relax their assertion to either refusal (C12). Recommendation: accept (both refuse, nothing escapes).

Found in the design, resolved as above: the §5.4 presentation-boundary rule and the §4 "tools conditioned on observations" rule cannot both hold for the public methods without breaking every direct caller (decision 1); §5.2's served-stale image contradicts four pinned tests, not the spec (decision 3); §6's table keeps 16 MiB while the maintainer raised the bound (decision 7); under `Manual`, §5.4 point 1 cannot see a concept changed externally without a refresh — the image holds the version shown — which §5.1's last paragraph already states ("sous `Manual` une modification externe … demande un `Refresh()`"), so the plan's test of that case refreshes the handle, as §11 revision 4 writes it.

## Decisions taken by this plan (for the maintainer to confirm)

**C1. Scope.** As cut above; nothing of lot D is started, and no public signature of lot A or B changes.

**C2. `BundleHandle` is in `OKF4net.Providers`.** `new BundleHandle(provider, options?, refreshPolicy?, time?)`; `Provider`, `Options`, `RefreshPolicy`, `Location`, `Current`, `Refresh()`, `Invalidate()`, `IsStale`, `LastLoadError`, `Dispose()` (unsubscribes a notifier). One image (`Bundle`), replaced as a whole. The first load failing throws `BundleLoadException` from `Current` and `Refresh()`; every later failure keeps the previous image served from `Current`, sets `IsStale` and `LastLoadError` (the `BundleLoadException`, whose inner exception is the raw cause), and `Refresh()` throws it.

**C3. Policies.** `BundleRefreshPolicy` is a sealed record with `Mode` (`BundleRefreshMode`: `Manual`, `OnAccess`, `Interval`, `Notify`) and `Interval`; statics `Manual`, `OnAccess`, `Notify`, factory `Every(TimeSpan)` (positive). `IBundleChangeNotifier.Subscribe(Action) : IDisposable`; `Notify` over a provider that is not a notifier is an `ArgumentException` at construction (an explicit configuration error, §5.3). A signal only sets a flag; the next access compares the token.

**C4. The obligation is a generation counter.** `Invalidate()` increments `_required`; a load records the generation it started at; an image whose generation is behind `_required` is reloaded, whatever the policy; a load that started before the call, or that failed, leaves it pending (§5.1 revision 6). `IsStale` is true while a reload failed or an obligation is pending.

**C5. Policy checks run under the load lock**, except the `Manual` fast path (two volatile reads, no lock). A version token that cannot be read (`BundleProviderException`, `IOException`, `UnauthorizedAccessException`) counts as changed: the reload then fails the same way and marks the image stale. Concurrent callers wait for the load in progress and re-check under the lock, so one storage change costs one load.

**C6. `Every` reads `TimeProvider`** (`TimeProvider.System` by default); the interval restarts at every successful load.

**C7. A writer over a handle invalidates it** before the caller's `onWriteCommitted`, through the existing notification seam (`Invalidate` never throws, so B46/B48 are untouched); a refused or failed write does not invalidate.

**C8. The survey.** `Bundle.Load` surveys, inside its provider scope and after the graph is built, the `computation` file and the `attester.resource` of every concept whose `Frontmatter.IsAttestedComputation`, into `Bundle.ExecutableResources : IReadOnlyDictionary<ConceptId, ExecutableResourceSurvey>` (`ExecutableResource(Field, RawPath, State, Key, Version)`). The computation's `Undeclared` means no `computation:` path (inline or absent code); the attester's `Undeclared` refuses the run (D6). Each resolved resource is read through the one bounded reader (strict UTF-8, `BundleLimits.MaxObjectBytes`) so its version is known; `BundleObjectTooLargeException` → `TooLarge`, `BundleObjectNotFoundException` → `Missing`, `BundleAccessRefusedException` → `Unsafe`, any other `BundleProviderException` → the load fails (`Translate`), `IOException`/`UnauthorizedAccessException`/`DecoderFallbackException` → `Unreadable`.

**C9. The pinned reader.** `Bundle.ReadResourceText(BundleKey key, BundleObjectVersion? expected, out BundleObjectVersion version)` (the one-argument overload delegates to it); `BundleVersionMismatchException` propagates untranslated, every other provider failure is translated as before. Internally `ReadTextAt(provider, key, limits, chunk, expected, out version)`, which `ReadText` calls with `null` — a second name, not an overload, so every existing `cref="ReadText"` stays unambiguous.

**C10. The orchestrator.** `public static AttestationCaptureResult Capture(Bundle, Concept, BundleObjectVersion? expectedComputationFileVersion = null)` returns `AttestationCapture(SanctionedComputation Computation, string AttesterSourceText)` or a non-displayable `AttestationOutcome` plus `ImageOutdated`; `public ValueTask<AttestationOutcome> RunCapturedAsync(Bundle, ConceptId, values, AttestationCapture, StalePolicy?, CancellationToken)` runs §10.5 from step 3; `RunAsync` keeps its signature (step 1, `Capture`, `RunCapturedAsync`). `AttestationContext.AttesterSourceText` stays `string?` (not breaking) and is documented never null at a runtime's entry. Refusal texts: `no attester.resource is declared, so the computation cannot be attested and is not run`; `attester resource 'x' is a URL, which is never read, so the computation cannot be attested and is not run`; `attester resource 'x' could not be resolved (Missing|Unsafe)`; `attester resource 'x' could not be read (Unreadable|TooLarge)`; `attester resource 'x' changed since the bundle was loaded`; `computation file 'p' changed since it was shown; read it again with okf_get_computation before running it`; the pre-existing `computation file 'p' could not be resolved ({status})` / `could not be read: {TypeName}` and `attester resource 'x' could not be read: {TypeName}`. `ImageOutdated` is true for every resource-state refusal and false for `Undeclared`, `Url` and "no computation".

**C11. D6's reach in the tests is a script, not hand edits.** `declare-attesters.js` (Task 3) rewrites every `tmp.Write("c/rev.md", …)` fixture that declares `type: Attested Computation` and no `attester:` — 15 in `AttestationOrchestratorTests.cs`, 24 in `OkfComputationToolsTests.cs`, 3 in `AIFunctionExposureTests.cs` — inserting `attester: { resource: att.py }` as the last frontmatter line (braces doubled in an interpolated `$"…"` fixture) and `tmp.Write("att.py", …)` after the statement, CRLF preserved. Fixtures that only render the contract are touched too; their assertions do not count lines, so they stay green (replayed).

**C12. Refusals that moved from the read to the resolution** (open decision 8): `Computation_file_reached_through_an_uninspectable_junction_is_refused_before_binding`, `Attester_source_reached_through_an_uninspectable_junction_is_refused_without_calling_the_attester`, `GetComputation_refuses_a_computation_file_reached_through_a_junction_whose_link_status_cannot_be_inspected` assert `could not be re` (resolved or read).

**C13. `OkfBundleTools` constructors.** `public OkfBundleTools(BundleHandle handle, AttestationOrchestrator? orchestrator = null)`; the root-string constructors build `new BundleHandle(new FileSystemBundleProvider(bundleRoot))` (`Manual`) after their `Directory.Exists` check and keep the given string as `BundleRoot`; `Handle` and `Location` are new; `BundleRoot` returns the filesystem provider's root or throws `NotSupportedException` (its `[Obsolete]` is lot D). The internal test seam `(string, orchestrator, IBundleProvider? writeProvider)` keeps its shape: the writer is `new BundleConceptWriter(handle)` or, with a write provider, `new BundleConceptWriter(writeProvider, handle.Invalidate, handle.Options)`.

**C14. Effective write permission.** `GetTools(mode)` forces `OkfToolMode.ReadOnly` when `Handle.Provider.Capabilities.CanWrite` is false. `RegenerateIndexes` throws `BundleReadOnlyException` before `RunTool`, like the writer's methods; `Verify` runs inside `RunTool` and so returns `Error: the provider is read-only; the write was refused before being attempted` (its existing structure).

**C15. `okf_browse` through the provider.** A path is a prefix (`BundleKey`), allowed by the image's policy (`IsKeyAllowed`) and existing when an object lies strictly under it at the provider now (`Enumerate(prefix)`; a provider exception or I/O failure is "not found", as the old strict ancestor walk was); the root needs no check. The directory's `index.md` is read live through `Bundle.ReadResourceText` (strict UTF-8, bounded, the provider's guards); `FileNotFoundException` falls back to the generated listing. Changes: an empty directory is not found (D2), a non-UTF-8 index is an `Error:`, an index through a link is refused.

**C16. `okf_changes_since`** iterates `bundle.LogKeys` and reads each through `ReadResourceText(key)`; `rel` is the key text; the skip notes keep `SkipReason`'s words.

**C17. `okf_regenerate_indexes`** calls `IndexGenerator.Regenerate(Handle.Provider, IndexGenerator.DefaultSynthesize, Handle.Options, legacy: true)` (internal, visible to `OKF4net.Agents`) and prints `report.Written` keys — the same lines as before, the same silent skips and the same thrown failures — and invalidates the handle in `finally`.

**C18. A reload that fails keeps the previous image** for the tools too (spec §5.2): `A_failed_load_does_not_put_the_bundle_path_in_the_model_message` becomes a first-load failure, `A_failed_load_reports_the_real_exception_to_the_host` becomes `A_failed_reload_keeps_the_previous_image_and_reports_the_real_exception_on_the_handle` (the host channel is `Handle.LastLoadError`, path in its inner exception), `Bundle_becoming_unavailable_after_a_prior_successful_load_is_caught_not_thrown` and `ValidateBundle_never_throws_when_bundle_root_disappears` assert the stale image.

**C19. The observation gate sits at the model boundary** (open decision 1): `ReadConceptTool`, `GetComputationTool`, `WriteConceptTool`, `RunComputationToolAsync` are `internal`, carry the same `[Description]` strings as the public methods through shared `private const` fields (so `AIFunctionExposureTests`' description and schema tests hold unchanged), and are what `GetTools()` binds. The public methods delegate to `ReadConceptCore`/`GetComputationCore`/`RunComputationCoreAsync(gated: false)` and record nothing.

**C20. The registry** (`OKF4net.Agents.Internal.ObservationRegistry`, `internal`, exposed as `OkfBundleTools.Observations` for tests): per key, `FullConcept` version, `Code` version, `Absent`; `RecordFullConcept`/`RecordCode` clear `Absent`, `RecordAbsent` clears both; `Forget(key)`; `TryGetFullConcept`, `TryGetCode`, `IsAbsent` touch the key; LRU over `DefaultCapacity = 10 000` (constructor parameter for tests); a lock; per tool-set instance. `ReadConceptTool` records the whole concept at `bundle.ObjectVersions[key]`, or absent when the id parses and no concept exists; `GetComputationTool` records the concept (contract and code) and, for a file, the file at the version read, only when the rendering showed the code. A committed `WriteConceptTool` forgets the key.

**C21. Write refusal texts.** `Error: '{id}' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.`; a condition that does not hold is B29's text (`changed since it was read, or no longer exists` / `already exists`), and `DropBundleOnError` already refreshes the image; an id that does not parse goes to the public `WriteConcept`, whose refusal is unchanged.

**C22. Run gating.** `GuardObserved(bundle, concept, out fileVersion)`: an observation (full or code) of the concept is required and must equal `bundle.ObjectVersions[key]` — else `Error: '{id}' has not been shown to this session; read it first with okf_get_computation, then run it.` or `Error: '{id}' changed since it was shown to this session; read it again with okf_get_computation before running it.` (the image invalidated); a file computation whose file resolves needs a code observation of the file (`Error: the computation file 'p' of '{id}' has not been shown to this session; …`), which pins the read. Then `Capture(bundle, concept, fileVersion)`; a refusal with `ImageOutdated` invalidates the handle; `RunCapturedAsync` on the capture. A concept not found or not a computation goes to `_orchestrator.RunAsync` as before.

**C23. The public `RunComputationAsync`** runs the same core ungated (`fileVersion = null`): its results are the orchestrator's, and it invalidates the handle on an outdated capture too (open decision 4).

**C24. Existing tests that acted through the exposed functions now read or show first**: `Agent_writes_concept_regenerates_indexes_and_validates_end_to_end` (a scripted `okf_read_concept` turn, 5 turns, 4 results), `Write_then_read_round_trips_through_mcp` (an `okf_read_concept` call answering not found), and five computation tests (`okf_run_computation_binds_parameterValues_dictionary_from_a_json_object`, `okf_run_computation_still_binds_distinct_top_level_parameter_names`, `okf_run_computation_passes_a_dictionary_argument_through_unchanged`, `An_exact_integer_parameter_value_still_reaches_the_binder_as_a_long`, `Okf_run_computation_binds_a_JSON_integer_through_the_real_AllowlistParameterBinder_and_executes`) call `GetComputationTool("c/rev")` first. `Browse_path_cannot_forge_a_line_in_the_generated_listing` puts a concept in its directory (D2).

**C25. `okf-mcp`.** `OkfBundleDiscovery.MaxRootIndexBytes = 16 MiB`; `ReadRootIndexOrNull` answers `null` for a longer file without reading it and counts the bytes it reads (a growing file is bounded too); the pure `TryDiscover` is unchanged.

**C26. Documentation per task.** Each task's CHANGELOG entry (`### Added` for API, `### Changed` for behaviour); the README gains "One image per source: `BundleHandle`" (Task 4); the conformance entry S10.5-1 (Task 3); CLAUDE.md's `Providers/`, `OKF4net.Attestation` and `OKF4net.Agents` bullets (Task 8).

## Global Constraints

- **Branch and worktree.** Execute in a dedicated worktree on branch `feat/bundle-provider-c`, created from `feat/bundle-provider-b` at `c48f23f` (or from `origin/dev` once lot B is merged there) — never in `E:\Sources\okf` (the shared checkout): `git worktree add .claude/worktrees/bundle-provider-c -b feat/bundle-provider-c c48f23f`, then `git branch --unset-upstream` (a branch created from a tracking branch inherits its upstream). Never `git checkout dev`. Do not push before the lot is reviewed.
- **Zero third-party runtime dependencies** in `src/OKF4net`, `src/OKF4net.Attestation`, `src/OKF4net.Mcp`'s library code: BCL only, no `PackageReference`, **no P/Invoke**. `src/OKF4net.Agents` references `Microsoft.Agents.AI` only. `InvariantGlobalization` stays on in `OKF4net.Cli` and `OKF4net.Render`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on every public member (missing docs fail the build), nullable on, `TreatWarningsAsErrors`, LangVersion 14. **An XML `cref` may only name a type or member that exists at that task**, and never an ambiguous overload (CS0419 is an error: that is why the pinned reader is `ReadTextAt`, not an overload of `ReadText`).
- **Source files are CRLF in the working tree** (`core.autocrlf`; the index holds LF). After writing a **new** `.cs` file, convert it with `unix2dos -q <file>`; `Edit` on an existing CRLF file keeps CRLF. **Never run MSYS `sed -i` on a `.cs` file** (it strips every CR). The node script of Task 3 preserves CRLF by construction. Before each commit, for every touched `.cs` file, `tr -cd '\r' < f | wc -c` must equal `wc -l < f`. No Python is assumed on the machine.
- **Tool-layer pitfalls.** The file-writing tool decodes `\uXXXX` escapes and collapses `\\`: the code in this plan writes `\n` inside C# string literals only (which survive), uses `(char)0x2028` style constants where the existing tests do, and builds no `\\` sequences; the node script writes its `\n` sequences as `\\n` in JavaScript source on purpose. Check any doubtful byte with `cat -A`.
- **`tests/fixtures/` is never edited and no snapshot is regenerated.** Lot C promises byte-identical CLI output; if a golden comparison turns red, STOP and report.
- **Path-safety guards use the STRICT predicates.** This lot adds no `ReparsePoints` call at all: every access goes through the provider, whose guards are lot A's and B's. `OkfBundleTools` ends the lot with no `File.*`/`Directory.*` call but the root-string constructor's `Directory.Exists` check (a constructor argument check, as before).
- **Provider errors are translated at the core boundary** (§7): every `Error:` or reason text a tool or the orchestrator renders names keys, exception types or library sentences, never a host path, except the raw I/O messages lot B already lets through (#211).
- **Model-facing names are bound to the `*Tool` wrappers only** (C19); never bind a public method to a model-facing name, never record an observation outside a `*Tool` wrapper.
- Each task adds its own `CHANGELOG.md` entries under `## [Unreleased]`, describing behaviour, not commits; the Added entries go after the lot B entries at the end of `### Added`, the Changed entries at the top of `### Changed`, each new one above the previous task's (so the final order is Task 7, 6, 5, 4, 3, then lot B's).
- Commit messages say only what the code does. End each with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Stage files by name — **never `git add -A`** (the graphify hook rebuilds in the background after each commit; it writes nothing into the worktree).
- **Tests.** `dotnet build OKF4net.sln` with 0 warnings, `dotnet format OKF4net.sln --verify-no-changes`, and `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` green at the end of every task (the counts in each task are what to expect). On Windows without Developer Mode the file-symlink tests skip; the junction tests run. **The Linux container run is mandatory at Task 8** (and advisable after Tasks 1, 2 and 4), as root for the full suite and as an unprivileged user for the Providers, Agents and Attestation classes, from the worktree root in Git Bash:

  ```bash
  SRC="$(pwd -W)"   # Windows path of the worktree, for Docker Desktop
  MSYS_NO_PATHCONV=1 docker run --rm -v "$SRC:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -e; mkdir -p /work; cd /src
    tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" --exclude=./producers -cf - . | (cd /work && tar xf -)
    cd /work && dotnet test tests/OKF4net.Tests --filter "Category!=ContainerIntegration" 2>&1 | grep -E "Passed!|Failed!|^\s+Failed |Error Message" -A3
    useradd -m tester && chown -R tester /work
    su tester -c "cd /work && dotnet test tests/OKF4net.Tests --filter \"FullyQualifiedName~OKF4net.Tests.Providers|FullyQualifiedName~OKF4net.Tests.Agents|FullyQualifiedName~OKF4net.Tests.Attestation\" 2>&1 | grep -E \"Passed!|Failed!|^\s+Failed \" -A3"'
  ```

  Expected at Task 8: root `Passed: 3453, Skipped: 72, Total: 3525`; tester `Passed: 1090, Skipped: 40, Total: 1130`. (Do not pass `--no-build` or override `HOME` for the tester: the SDK's workload check then fails before any test runs.)
- **Timing-dependent tests outside lot C.** If exactly `AttestationOrchestratorTests.A_token_ignoring_stage_is_abandoned_when_the_token_fires("blocking")`, `OkfComputationToolsTests.A_token_ignoring_attester_cannot_outlast_the_timeout_or_display_its_result("blocking")` or `CliContainerEngineRunTests.A_hung_engine_kill_does_not_hang_the_timed_out_run` fails alone in a full run, rerun its class before concluding anything (lot B's note; none failed in this plan's replays).
- **Native AOT publish on this machine** needs `vswhere.exe` on `PATH` when run from Git Bash; run it from PowerShell with `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH` first (Task 8).
- `dotnet test producers/OkfProducer.sln` green locally before the lot is declared done (`producers/` is outside CI by decision; it uses `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` and `IndexGenerator.RegenerateIndexes(string)`, both untouched).

## Review Focus

1. **A stale image served after an external change.** Under `Manual` that is by design (the host refreshes); under `OnAccess`, `Every` and `Notify` the token decides; and whatever the policy, the attester's pin must catch a script replaced with the same length in the same second, which the filesystem token cannot see. Pinned by `OnAccess_compares_the_token_at_every_access_and_reloads_only_when_it_changed`, `Interval_compares_the_token_at_most_once_per_interval`, `Notify_needs_a_notifier_and_treats_a_signal_as_a_hint` (Task 1), `The_attester_is_pinned_to_the_version_the_image_was_loaded_with` (Task 3, mtime restored) and `A_refused_pin_obliges_a_reload_and_the_next_attempt_runs_on_the_new_state` under the four policies (Task 6).
2. **An observation recorded by a context injection, or by a host's own presentation.** Pinned by `A_context_injection_is_never_an_observation` and `Neither_the_public_method_nor_get_computation_nor_a_context_injection_authorises_a_write` (Task 5) and `A_context_injection_does_not_authorise_a_run`, `The_public_method_needs_no_observation_but_authorises_none` (Task 6).
3. **A handle shared across threads and tool sets.** Sixteen threads on a fresh handle must cost one load and see one instance; two tool sets over one handle see one image and each other's writes; one tool set's read must not replace another's observation. Pinned by `Concurrent_first_accesses_share_one_load`, `An_obligation_made_during_a_load_is_not_acknowledged_by_that_load` (Task 1), `Two_tool_sets_over_one_handle_share_one_image_and_a_write_refreshes_it_for_both` (Task 4), `Another_tool_sets_read_does_not_replace_this_sessions_observation` (Task 5), `Another_tool_sets_observation_does_not_replace_this_sessions` (Task 6).
4. **The attestation reading unpinned or undeclared code.** Pinned by `A_pinned_read_serves_the_observed_version_only` (Task 2), `An_undeclared_or_URL_attester_is_refused_before_the_runtime_is_called`, `A_computation_file_pinned_to_an_observed_version_is_refused_when_it_changed` (Task 3), `A_computation_file_changed_since_it_was_shown_is_refused_at_its_pinned_read`, `A_concept_whose_contract_changed_since_it_was_shown_is_refused_and_the_image_refreshed` (Task 6).
5. **Memory growth in a long-lived host**: an image replaced must be collectable, and the registry must stay bounded under ten thousand reads. Pinned by `A_replaced_image_is_not_retained` (Task 1, a `WeakReference` after `Refresh()`) and `An_observation_evicted_by_ten_thousand_later_reads_authorises_nothing` plus the registry's LRU unit test (Task 5).

---

### Task 1: `BundleHandle`, the refresh policies and the writer over a handle

**Files:**
- Create: `src/OKF4net/Providers/BundleRefreshPolicy.cs` (`BundleRefreshMode`, `BundleRefreshPolicy`, `IBundleChangeNotifier`)
- Create: `src/OKF4net/Providers/BundleHandle.cs`
- Modify: `src/OKF4net/BundleConceptWriter.cs` (one constructor, after the `string` one at `:281-284`)
- Create: `tests/OKF4net.Tests/Providers/NotifyingBundleProvider.cs` (a test provider that raises change signals; reused by Task 6)
- Test: `tests/OKF4net.Tests/Providers/BundleHandleTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: lot A's `IBundleProvider`, `BundleLoadOptions`, `BundleLimits`, `BundleVersion`, `Bundle.Load(IBundleProvider, BundleLoadOptions?)`, `BundleLoadException`, `BundleProviderException`; lot B's `BundleConceptWriter(IBundleProvider, Action?, BundleLoadOptions?)`; the test project's `InMemoryBundleProvider` (`Add`, `Calls`, `FailEnumerateWith`).
- Produces:
  - `public enum BundleRefreshMode { Manual, OnAccess, Interval, Notify }`; `public sealed record BundleRefreshPolicy` with `static Manual`, `static OnAccess`, `static Notify`, `static Every(TimeSpan every)`, `Mode`, `Interval`; `public interface IBundleChangeNotifier { IDisposable Subscribe(Action onChange); }`.
  - `public sealed class BundleHandle : IDisposable` — `BundleHandle(IBundleProvider provider, BundleLoadOptions? options = null, BundleRefreshPolicy? refreshPolicy = null, TimeProvider? time = null)`; `IBundleProvider Provider`, `BundleLoadOptions Options`, `BundleRefreshPolicy RefreshPolicy`, `string Location`, `bool IsStale`, `Exception? LastLoadError`, `Bundle Current { get; }`, `Bundle Refresh()`, `void Invalidate()`, `void Dispose()`.
  - `public BundleConceptWriter(BundleHandle handle, Action? onWriteCommitted = null)`.
  - `public sealed class NotifyingBundleProvider(IBundleProvider inner) : IBundleProvider, IBundleChangeNotifier` (tests) — `Signal()`, `Subscribers`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Providers/NotifyingBundleProvider.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A provider that can raise change signals (spec §5.3, <see cref="IBundleChangeNotifier"/>),
/// over any inner provider: <see cref="Signal"/> invokes every subscriber, as a watcher would.
/// Shared by the handle tests and the agent tool tests that drive the <c>Notify</c> policy.
/// </summary>
public sealed class NotifyingBundleProvider(IBundleProvider inner) : IBundleProvider, IBundleChangeNotifier
{
    private readonly List<Action> _subscribers = [];

    /// <summary>The number of live subscriptions.</summary>
    public int Subscribers => _subscribers.Count;

    /// <summary>Raises a change signal to every subscriber.</summary>
    public void Signal() => _subscribers.ToList().ForEach(s => s());

    /// <inheritdoc/>
    public IDisposable Subscribe(Action onChange)
    {
        _subscribers.Add(onChange);
        return new Subscription(() => _subscribers.Remove(onChange));
    }

    public string Id => inner.Id;
    public string Location => inner.Location;
    public BundleProviderCapabilities Capabilities => inner.Capabilities;
    public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
    public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);
    public bool Exists(BundleKey key) => inner.Exists(key);
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
    public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
```

Create `tests/OKF4net.Tests/Providers/BundleHandleTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// <see cref="BundleHandle"/> (spec §5.1 to §5.3): one image per handle, replaced as a whole;
/// the four refresh policies, all lazy; the reload obligation and its acknowledgement; a failed
/// reload that keeps the previous image served; one load at a time under concurrent access.
/// Every test drives an <see cref="InMemoryBundleProvider"/>, so the storage changes "behind
/// the bundle's back" through <see cref="InMemoryBundleProvider.Add"/>.
/// </summary>
public class BundleHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n";

    private static InMemoryBundleProvider Storage() => new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc);

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    private static int VersionChecks(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "version");

    [Fact]
    public void Manual_loads_once_and_serves_the_same_image_until_refreshed()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);

        var first = handle.Current;
        storage.Add("b.md", Doc);
        var second = handle.Current;

        Assert.Same(first, second);
        Assert.Equal(1, first.Count);
        Assert.Equal(1, Loads(storage));
        Assert.False(handle.IsStale);

        var refreshed = handle.Refresh();
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, refreshed.Count);
        Assert.Same(refreshed, handle.Current);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void Invalidate_obliges_the_next_access_to_reload_under_Manual()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var first = handle.Current;
        storage.Add("b.md", Doc);

        handle.Invalidate();

        Assert.True(handle.IsStale);
        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.False(handle.IsStale);
        Assert.Same(second, handle.Current);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void OnAccess_compares_the_token_at_every_access_and_reloads_only_when_it_changed()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        Assert.Same(first, handle.Current);
        Assert.Same(first, handle.Current);
        Assert.Equal(1, Loads(storage));
        Assert.Equal(3, VersionChecks(storage)); // one at the load, one per later access

        storage.Add("b.md", Doc);

        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void Interval_compares_the_token_at_most_once_per_interval()
    {
        var storage = Storage();
        var time = new ManualTime();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromSeconds(10)), time: time);
        var first = handle.Current;
        storage.Add("b.md", Doc);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Same(first, handle.Current); // inside the interval: no comparison, no reload
        Assert.Equal(1, VersionChecks(storage));

        time.Advance(TimeSpan.FromSeconds(2));
        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal(3, VersionChecks(storage)); // the comparison, then the reload's own

        Assert.Same(second, handle.Current); // the interval restarts at the reload
        Assert.Equal(3, VersionChecks(storage));
    }

    [Fact]
    public void Notify_needs_a_notifier_and_treats_a_signal_as_a_hint()
    {
        var storage = Storage();
        Assert.Throws<ArgumentException>(() => new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.Notify));

        var notifying = new NotifyingBundleProvider(storage);
        using var handle = new BundleHandle(notifying, refreshPolicy: BundleRefreshPolicy.Notify);
        var first = handle.Current;
        Assert.Same(first, handle.Current);
        Assert.Equal(1, VersionChecks(storage)); // no signal: no comparison

        notifying.Signal(); // spurious: nothing changed
        Assert.Same(first, handle.Current);
        Assert.Equal(2, VersionChecks(storage));
        Assert.Equal(1, Loads(storage));

        storage.Add("b.md", Doc);
        Assert.Same(first, handle.Current); // a change without a signal is not seen
        notifying.Signal();
        var second = handle.Current;
        Assert.Equal(2, second.Count);
        Assert.Equal(2, Loads(storage));

        handle.Dispose();
        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_failed_reload_keeps_the_previous_image_served_and_marks_it_stale()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        storage.Add("b.md", Doc);
        storage.FailEnumerateWith = new IOException("network down");

        Assert.Same(first, handle.Current);
        Assert.True(handle.IsStale);
        Assert.IsType<BundleLoadException>(handle.LastLoadError);

        var thrown = Assert.Throws<BundleLoadException>(() => handle.Refresh());
        Assert.Same(handle.LastLoadError, thrown);
        Assert.Same(first, handle.Current);

        storage.FailEnumerateWith = null;
        var second = handle.Current;
        Assert.Equal(2, second.Count);
        Assert.False(handle.IsStale);
        Assert.Null(handle.LastLoadError);
    }

    [Fact]
    public void The_first_load_failing_throws()
    {
        var storage = Storage();
        storage.FailEnumerateWith = new IOException("network down");
        using var handle = new BundleHandle(storage);

        Assert.Throws<BundleLoadException>(() => handle.Current);
        Assert.Throws<BundleLoadException>(() => handle.Refresh());
        Assert.NotNull(handle.LastLoadError);
    }

    /// <summary>
    /// Spec §5.1 (revision 6): an obligation is acknowledged only by a successful load started
    /// after it. A load already running when <see cref="BundleHandle.Invalidate"/> is called may
    /// have surveyed the old state, so it does not acknowledge, and the next access reloads
    /// again; a load that fails leaves it pending too.
    /// </summary>
    [Fact]
    public void An_obligation_made_during_a_load_is_not_acknowledged_by_that_load()
    {
        var storage = Storage();
        var hooked = new HookedProvider(storage);
        using var handle = new BundleHandle(hooked);
        hooked.OnEnumerate = () => handle.Invalidate(); // fires inside the first load
        var first = handle.Current;
        hooked.OnEnumerate = null;

        Assert.True(handle.IsStale);
        Assert.Equal(1, Loads(storage));
        var second = handle.Current; // reloads: the first load started before the obligation
        Assert.NotSame(first, second);
        Assert.Equal(2, Loads(storage));
        Assert.False(handle.IsStale);

        handle.Invalidate();
        storage.FailEnumerateWith = new IOException("network down");
        Assert.Same(second, handle.Current); // the failed reload leaves the obligation pending
        Assert.True(handle.IsStale);
        storage.FailEnumerateWith = null;
        Assert.NotSame(second, handle.Current); // acknowledged by the successful one
        Assert.False(handle.IsStale);
    }

    [Fact]
    public void Concurrent_first_accesses_share_one_load()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var results = new Bundle[16];
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
        {
            start.Wait();
            results[i] = handle.Current;
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(1, Loads(storage));
        Assert.All(results, b => Assert.Same(results[0], b));
    }

    /// <summary>A handle holds exactly one image: the one a refresh replaces is not retained (the memory of a long-lived host grows with the bundle, not with its history).</summary>
    [Fact]
    public void A_replaced_image_is_not_retained()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var previous = LoadAndForget(handle);
        storage.Add("b.md", Doc);
        handle.Refresh();

        for (var i = 0; i < 5 && previous.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(previous.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndForget(BundleHandle handle) => new(handle.Current);

    [Fact]
    public void A_writer_over_a_handle_invalidates_it_after_each_write_and_still_notifies()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var notified = 0;
        var writer = new BundleConceptWriter(handle, onWriteCommitted: () => notified++);
        Assert.Equal(1, handle.Current.Count);

        var result = writer.WriteConcept("b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n");

        Assert.StartsWith("Written", result, StringComparison.Ordinal);
        Assert.Equal(1, notified);
        Assert.True(handle.IsStale);
        Assert.Equal(2, handle.Current.Count);
        Assert.Throws<NotSupportedException>(() => writer.BundleRoot);
    }

    /// <summary>A clock the test moves by hand, for <see cref="BundleRefreshMode.Interval"/>.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond; // never 0: the handle reads 0 as "never checked"

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A provider that runs a callback at the start of every listing: the one moment a load is provably in progress.</summary>
    private sealed class HookedProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        public Action? OnEnumerate { get; set; }

        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);

        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
        {
            OnEnumerate?.Invoke();
            return inner.Enumerate(prefix, limits);
        }

        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);
        public bool Exists(BundleKey key) => inner.Exists(key);
        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
        public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }
}
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `2 error CS0246: The type or namespace name 'BundleHandle' could not be found` and `2 error CS0246: The type or namespace name 'IBundleChangeNotifier' could not be found`.

- [ ] **Step 3: Write the policies, the handle and the writer constructor**

Create `src/OKF4net/Providers/BundleRefreshPolicy.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>How a <see cref="BundleHandle"/> decides to reload its image (spec §5.3).</summary>
public enum BundleRefreshMode
{
    /// <summary>The host calls <see cref="BundleHandle.Refresh"/>; nothing else reloads (the general default).</summary>
    Manual,

    /// <summary>The provider's version token is compared with the image's before every use; a different token reloads.</summary>
    OnAccess,

    /// <summary>As <see cref="OnAccess"/>, but the token is compared at most once per <see cref="BundleRefreshPolicy.Interval"/>, at an access.</summary>
    Interval,

    /// <summary>A signal from the provider's <see cref="IBundleChangeNotifier"/> marks the image "to check"; the token is compared at the next access. A signal is a hint, never a reload by itself.</summary>
    Notify,
}

/// <summary>
/// The refresh policy of a <see cref="BundleHandle"/> (spec §5.3). Every policy is lazy: the
/// core runs no timer and no background thread, and a reload only ever happens inside an
/// access to <see cref="BundleHandle.Current"/> or a call to <see cref="BundleHandle.Refresh"/>.
/// Three events reload whatever the policy (spec §5.1, §5.2): the handle's own writes (a
/// <see cref="BundleConceptWriter"/> built over it), a write conflict, and a refused pin of
/// sanctioned code — each through <see cref="BundleHandle.Invalidate"/>.
/// </summary>
public sealed record BundleRefreshPolicy
{
    private BundleRefreshPolicy(BundleRefreshMode mode, TimeSpan interval)
    {
        Mode = mode;
        Interval = interval;
    }

    /// <summary>The host reloads by calling <see cref="BundleHandle.Refresh"/>; the general default.</summary>
    public static BundleRefreshPolicy Manual { get; } = new(BundleRefreshMode.Manual, TimeSpan.Zero);

    /// <summary>The version token is compared before every use of the image.</summary>
    public static BundleRefreshPolicy OnAccess { get; } = new(BundleRefreshMode.OnAccess, TimeSpan.Zero);

    /// <summary>The version token is compared at most every <paramref name="every"/>, at an access.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="every"/> is not positive.</exception>
    public static BundleRefreshPolicy Every(TimeSpan every)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero);
        return new(BundleRefreshMode.Interval, every);
    }

    /// <summary>A change signal from the provider (<see cref="IBundleChangeNotifier"/>) makes the next access compare the token. Only for a provider that implements <see cref="IBundleChangeNotifier"/>.</summary>
    public static BundleRefreshPolicy Notify { get; } = new(BundleRefreshMode.Notify, TimeSpan.Zero);

    /// <summary>The mode.</summary>
    public BundleRefreshMode Mode { get; }

    /// <summary>The minimum time between two token comparisons under <see cref="BundleRefreshMode.Interval"/>; <see cref="TimeSpan.Zero"/> otherwise.</summary>
    public TimeSpan Interval { get; }
}

/// <summary>
/// A provider that can signal that its storage may have changed (spec §5.3). A signal is a
/// hint: the handle compares the version token at its next access and reloads only when the
/// token differs — never blindly, since a watcher can lose events as well as raise spurious ones.
/// </summary>
public interface IBundleChangeNotifier
{
    /// <summary>Registers <paramref name="onChange"/>, invoked (on any thread) whenever the storage may have changed, until the returned subscription is disposed.</summary>
    IDisposable Subscribe(Action onChange);
}
```

Create `src/OKF4net/Providers/BundleHandle.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// The holder of one bundle's image (spec §5.2): a provider, the options every load uses, a
/// refresh policy, and the current <see cref="Bundle"/>. One object per source, meant to be
/// shared by every consumer of that source (the agent tools, a context provider, a catalog
/// source) so that they serve one image; the sharing is explicit — a consumer built from a
/// root string creates its own handle, and two such consumers have two images.
/// <para><b>One load at a time.</b> Concurrent callers wait for the load in progress instead of
/// each starting one; the image is replaced as a whole, so a call never sees two versions.</para>
/// <para><b>Failure.</b> When a reload fails (the storage is unreachable, a bound is exceeded) the
/// previous image stays served and <see cref="IsStale"/> says so; only the very first load throws.</para>
/// <para><b>The reload obligation</b> (spec §5.1): <see cref="Invalidate"/> obliges the next access
/// to reload, whatever the policy, and the obligation is acknowledged only by a successful load
/// started after it — a load already running when it is made, or a load that fails, leaves it
/// pending. The obligation is a generation counter: every load records the generation it
/// started at, and an image whose generation is behind the required one is reloaded.</para>
/// <para><b>Not frozen</b> (spec §5.1, D3): the image is replaced as a whole, but its documents
/// are the mutable <see cref="Frontmatter"/> of today; modifying a document of a shared image is
/// not supported.</para>
/// </summary>
public sealed class BundleHandle : IDisposable
{
    private sealed record Image(Bundle Bundle, long Generation);

    private readonly Lock _loadLock = new();
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private Image? _image;
    private long _required;
    private bool _checkRequested;
    private long _lastCheck;
    private bool _stale;
    private Exception? _lastLoadError;

    /// <summary>Creates a handle over <paramref name="provider"/>. Nothing is loaded until <see cref="Current"/> or <see cref="Refresh"/> is used.</summary>
    /// <param name="provider">Where the bundle is read.</param>
    /// <param name="options">The options of every load (bounds, key policy); <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <param name="refreshPolicy">When the image is reloaded; <see langword="null"/>: <see cref="BundleRefreshPolicy.Manual"/>.</param>
    /// <param name="time">The clock of <see cref="BundleRefreshMode.Interval"/>; <see langword="null"/>: <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="refreshPolicy"/> is <see cref="BundleRefreshPolicy.Notify"/> and <paramref name="provider"/> is not an <see cref="IBundleChangeNotifier"/>: an explicit configuration error (spec §5.3).</exception>
    public BundleHandle(IBundleProvider provider, BundleLoadOptions? options = null, BundleRefreshPolicy? refreshPolicy = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        Options = options ?? BundleLoadOptions.Default;
        RefreshPolicy = refreshPolicy ?? BundleRefreshPolicy.Manual;
        _time = time ?? TimeProvider.System;
        if (RefreshPolicy.Mode == BundleRefreshMode.Notify)
        {
            if (provider is not IBundleChangeNotifier notifier)
            {
                throw new ArgumentException("the Notify refresh policy needs a provider that implements IBundleChangeNotifier", nameof(refreshPolicy));
            }

            _subscription = notifier.Subscribe(() => Volatile.Write(ref _checkRequested, true));
        }
    }

    /// <summary>Where the bundle is read.</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The options of every load.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>When the image is reloaded.</summary>
    public BundleRefreshPolicy RefreshPolicy { get; }

    /// <summary>The provider's displayable location.</summary>
    public string Location => Provider.Location;

    /// <summary>
    /// <see langword="true"/> when the image served is known to be behind: the last reload
    /// failed (<see cref="LastLoadError"/>), or an obligation to reload is pending. Reset by a
    /// successful load.
    /// </summary>
    public bool IsStale => Volatile.Read(ref _stale) || (_image is { } image && image.Generation < Volatile.Read(ref _required));

    /// <summary>The failure of the last reload that kept the previous image served, or <see langword="null"/>.</summary>
    public Exception? LastLoadError => Volatile.Read(ref _lastLoadError);

    /// <summary>
    /// The image, loaded on first use and reloaded under <see cref="RefreshPolicy"/> and the
    /// reload obligation. When a reload fails the previous image is returned and
    /// <see cref="IsStale"/> is set.
    /// </summary>
    /// <exception cref="BundleLoadException">No image could ever be loaded (the first load failed).</exception>
    public Bundle Current
    {
        get
        {
            var image = Volatile.Read(ref _image);
            if (image is not null && RefreshPolicy.Mode == BundleRefreshMode.Manual && image.Generation >= Volatile.Read(ref _required))
            {
                return image.Bundle; // the fast path: nothing to compare under Manual
            }

            lock (_loadLock)
            {
                image = _image;
                if (image is not null && !NeedsReload(image))
                {
                    return image.Bundle;
                }

                return LoadLocked().Bundle;
            }
        }
    }

    /// <summary>Reloads now, whatever the policy, and returns the new image. A failure keeps the previous image served (<see cref="IsStale"/>) and is thrown.</summary>
    /// <exception cref="BundleLoadException">The load failed.</exception>
    public Bundle Refresh()
    {
        lock (_loadLock)
        {
            var image = LoadLocked();
            if (_lastLoadError is { } error)
            {
                throw error;
            }

            return image.Bundle;
        }
    }

    /// <summary>
    /// Obliges the next use to reload (spec §5.1, §5.2): after this handle's own writes, a
    /// write conflict, or a refused pin of sanctioned code. Never blocks and never throws; the
    /// obligation is acknowledged only by a successful load started after this call.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _required);

    /// <inheritdoc/>
    public void Dispose() => _subscription?.Dispose();

    private bool NeedsReload(Image image)
    {
        if (image.Generation < Volatile.Read(ref _required))
        {
            return true;
        }

        switch (RefreshPolicy.Mode)
        {
            case BundleRefreshMode.OnAccess:
                return TokenChanged(image);
            case BundleRefreshMode.Interval:
                {
                    var now = _time.GetTimestamp();
                    if (_lastCheck != 0 && _time.GetElapsedTime(_lastCheck, now) < RefreshPolicy.Interval)
                    {
                        return false;
                    }

                    _lastCheck = now;
                    return TokenChanged(image);
                }

            case BundleRefreshMode.Notify:
                if (!Volatile.Read(ref _checkRequested))
                {
                    return false;
                }

                Volatile.Write(ref _checkRequested, false);
                return TokenChanged(image);
            default:
                return false;
        }
    }

    /// <summary>A token that cannot be read counts as changed: the reload then fails the same way, and the image is marked stale.</summary>
    private bool TokenChanged(Image image)
    {
        try
        {
            return Provider.GetVersion(Options.Limits) != image.Bundle.Version;
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private Image LoadLocked()
    {
        var startedAt = Volatile.Read(ref _required);
        try
        {
            var bundle = Bundle.Load(Provider, Options);
            var image = new Image(bundle, startedAt);
            Volatile.Write(ref _image, image);
            Volatile.Write(ref _lastLoadError, null);
            Volatile.Write(ref _stale, false);
            _lastCheck = _time.GetTimestamp();
            return image;
        }
        catch (BundleLoadException e)
        {
            Volatile.Write(ref _lastLoadError, e);
            if (_image is not { } previous)
            {
                throw;
            }

            Volatile.Write(ref _stale, true);
            return previous;
        }
    }
}
```

In `src/OKF4net/BundleConceptWriter.cs`, replace

```text
        : this(new FileSystemBundleProvider(bundleRoot ?? throw new ArgumentNullException(nameof(bundleRoot))), onWriteCommitted)
    {
    }

    /// <summary>Creates a writer over <paramref name="provider"/> (spec §9).</summary>
```

with

```csharp
        : this(new FileSystemBundleProvider(bundleRoot ?? throw new ArgumentNullException(nameof(bundleRoot))), onWriteCommitted)
    {
    }

    /// <summary>
    /// Creates a writer over the provider and options of <paramref name="handle"/> (spec §5.2,
    /// §9): every committed write first invalidates the handle (its next use reloads the image;
    /// a refused or failed write does not), then runs <paramref name="onWriteCommitted"/>.
    /// </summary>
    /// <param name="handle">The handle whose provider is written and whose image is invalidated after each committed write.</param>
    /// <param name="onWriteCommitted">As for <see cref="BundleConceptWriter(string, Action?)"/>, run after the handle's invalidation.</param>
    public BundleConceptWriter(BundleHandle handle, Action? onWriteCommitted = null)
        : this(
            (handle ?? throw new ArgumentNullException(nameof(handle))).Provider,
            onWriteCommitted is null ? handle.Invalidate : () =>
            {
                handle.Invalidate();
                onWriteCommitted();
            },
            handle.Options)
    {
    }

    /// <summary>Creates a writer over <paramref name="provider"/> (spec §9).</summary>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~BundleHandleTests"`
Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11`.
Then: `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~BundleConceptWriter|FullyQualifiedName~BundleHandleTests"` — expected `Passed: 159, Skipped: 1, Total: 160`.

- [ ] **Step 5: CHANGELOG**

In `CHANGELOG.md`, under `## [Unreleased]` / `### Added`, replace

```text
  without any effect, on every attempt; the object is unchanged (see Changed,
  "A failed commit says what happened").

### Changed
```

with

```markdown
  without any effect, on every attempt; the object is unchanged (see Changed,
  "A failed commit says what happened").
- **`BundleHandle` (#139, lot C; spec §5.2, §5.3).** The holder of one
  bundle's image: `new BundleHandle(provider, loadOptions, refreshPolicy)`,
  `Current` (the image, loaded on first use), `Refresh()`, `Invalidate()`,
  `IsStale`, `LastLoadError`. One load at a time (concurrent callers wait for
  the load in progress), the image replaced as a whole, a failed reload keeps
  the previous image served and marks it stale. `BundleRefreshPolicy`:
  `Manual` (the default: the host calls `Refresh()`), `OnAccess` (the
  provider's version token is compared before every use), `Every(interval)`
  (compared at most once per interval, at an access) and `Notify` (a signal
  from a provider implementing the new `IBundleChangeNotifier` makes the next
  access compare the token; a signal is a hint, never a reload by itself). No
  policy runs a timer or a thread. `Invalidate()` obliges the next use to
  reload whatever the policy, and the obligation is acknowledged only by a
  successful load started after it (spec §5.1). `new BundleConceptWriter(handle,
  onWriteCommitted)` writes through the handle's provider under its options
  and invalidates the handle after each committed write.

### Changed
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/Providers/BundleRefreshPolicy.cs src/OKF4net/Providers/BundleHandle.cs src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/Providers/BundleHandleTests.cs tests/OKF4net.Tests/Providers/NotifyingBundleProvider.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net/Providers/BundleRefreshPolicy.cs src/OKF4net/Providers/BundleHandle.cs src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/Providers/BundleHandleTests.cs tests/OKF4net.Tests/Providers/NotifyingBundleProvider.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(providers): BundleHandle with the four refresh policies and the reload obligation; a writer over a handle invalidates it

BundleHandle holds one image per source: one load at a time, the image
replaced as a whole, a failed reload keeping the previous image served and
marked stale. BundleRefreshPolicy gives Manual, OnAccess, Every(interval) and
Notify (through IBundleChangeNotifier), all lazy. Invalidate() obliges the
next use to reload, acknowledged only by a successful load started after it.
BundleConceptWriter(BundleHandle) invalidates the handle after each
committed write, before the caller's notification.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 2: The executable-resource survey and the pinned resource read

**Files:**
- Create: `src/OKF4net/ExecutableResources.cs` (`ExecutableResourceState`, `ExecutableResource`, `ExecutableResourceSurvey`)
- Modify: `src/OKF4net/Bundle.cs` (`Load` at `:371`, `ReadText` at `:482-484`, after `ObjectVersions` at `:546`, `ReadResourceText(BundleKey)` at `:959-970`)
- Test: `tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: lot A's `Bundle.TryResolveResourceKey`, `Bundle.IsKeyAllowed`, `Bundle.ReadText(provider, key, limits, chunk, out version)`, `Bundle.Translate`, `BundleObjectTooLargeException`, `BundleObjectNotFoundException`, `BundleAccessRefusedException`, `BundleVersionMismatchException`; `Frontmatter.IsAttestedComputation`, `Frontmatter.ComputationContract` (`ComputationPath`, `Attester?.Resource`).
- Produces:
  - `public enum ExecutableResourceState { Pinned, Undeclared, Url, Unsafe, Missing, Unreadable, TooLarge }`; `public sealed record ExecutableResource(string Field, string? RawPath, ExecutableResourceState State, BundleKey? Key, BundleObjectVersion? Version)` with `bool IsPinned`; `public sealed record ExecutableResourceSurvey(ExecutableResource Computation, ExecutableResource Attester)`.
  - `public IReadOnlyDictionary<ConceptId, ExecutableResourceSurvey> Bundle.ExecutableResources` (one entry per Attested Computation concept).
  - `public string Bundle.ReadResourceText(BundleKey key, BundleObjectVersion? expected, out BundleObjectVersion version)`; `internal static string Bundle.ReadTextAt(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, BundleObjectVersion? expected, out BundleObjectVersion version)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The survey of executable resources at the load (spec §5.1): every Attested Computation
/// concept gets the state of its <c>computation</c> file and of its <c>attester.resource</c>,
/// pinned to a version when they were read; a resource's own problem is a state, never a load
/// failure; and <see cref="Bundle.ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)"/>
/// reads only the object at the pinned version.
/// </summary>
public class BundleExecutableResourceTests
{
    private const string Attester = "def attest(**_):\n    return {}\n";

    private static string Concept(string? computation, string? attester) =>
        "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: [job_id] }\n"
        + (computation is null ? string.Empty : $"computation: {computation}\n")
        + (attester is null ? string.Empty : $"attester: {{ resource: {attester} }}\n")
        + "---\n# Computation\n\n```sql\nSELECT 1\n```\n";

    private static BundleObjectVersion VersionOf(IBundleProvider provider, string key)
    {
        using var obj = provider.Read(BundleKey.Parse(key));
        return obj.Version;
    }

    [Fact]
    public void An_inline_computation_with_a_file_attester_pins_the_attester_only()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);

        var survey = bundle.ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Undeclared, survey.Computation.State);
        Assert.Null(survey.Computation.RawPath);
        Assert.Equal(ExecutableResourceState.Pinned, survey.Attester.State);
        Assert.True(survey.Attester.IsPinned);
        Assert.Equal("attester.resource", survey.Attester.Field);
        Assert.Equal("att.py", survey.Attester.RawPath);
        Assert.Equal(BundleKey.Parse("att.py"), survey.Attester.Key);
        Assert.Equal(VersionOf(storage, "att.py"), survey.Attester.Version);
    }

    [Fact]
    public void A_computation_file_is_pinned_too_and_a_plain_concept_has_no_entry()
    {
        var storage = new InMemoryBundleProvider()
            .Add("c/rev.md", Concept("q.sql", "att.py")).Add("q.sql", "SELECT 2\n").Add("att.py", Attester)
            .Add("n.md", "---\ntype: Note\ntitle: N\ndescription: n.\n---\n\nN.\n");
        var bundle = Bundle.Load(storage);

        var survey = bundle.ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Pinned, survey.Computation.State);
        Assert.Equal("computation", survey.Computation.Field);
        Assert.Equal(VersionOf(storage, "q.sql"), survey.Computation.Version);
        Assert.Single(bundle.ExecutableResources);
        Assert.Equal(2, bundle.Count);
    }

    [Theory]
    [InlineData(null, ExecutableResourceState.Undeclared)]
    [InlineData("https://example.com/att.py", ExecutableResourceState.Url)]
    [InlineData("../../att.py", ExecutableResourceState.Unsafe)]
    [InlineData("nope.py", ExecutableResourceState.Missing)]
    public void Every_negative_attester_state_is_surveyed_and_the_concept_is_still_loaded(string? attester, ExecutableResourceState expected)
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, attester)).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);

        var resource = bundle.ExecutableResources[ConceptId.Parse("c/rev")].Attester;
        Assert.Equal(expected, resource.State);
        Assert.False(resource.IsPinned);
        Assert.Null(resource.Version);
        Assert.Equal(attester, resource.RawPath);
        Assert.NotNull(bundle.Get(ConceptId.Parse("c/rev")));
        Assert.Empty(bundle.ParseErrors);
        Assert.NotNull(BundleValidator.Validate(bundle));
    }

    [Fact]
    public void An_unreadable_or_oversized_resource_is_a_state_not_a_load_failure()
    {
        var unreadable = new InMemoryBundleProvider().Add("c/rev.md", Concept("q.sql", "att.py")).Add("q.sql", "x").Add("att.py", Attester)
            .FailReadOf("att.py", new IOException("disk"));
        var survey = Bundle.Load(unreadable).ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Unreadable, survey.Attester.State);
        Assert.Equal(BundleKey.Parse("att.py"), survey.Attester.Key);
        Assert.Equal(ExecutableResourceState.Pinned, survey.Computation.State);

        // A bound the concept (about 140 bytes) fits and a 400-byte attester does not.
        var small = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", new string('#', 400) + "\n");
        var bounded = Bundle.Load(small, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 256 } });
        var tooLarge = bounded.ExecutableResources[ConceptId.Parse("c/rev")].Attester;
        Assert.Equal(ExecutableResourceState.TooLarge, tooLarge.State);
        Assert.Equal(1, bounded.Count);
    }

    [Fact]
    public void A_resource_that_is_not_UTF8_is_unreadable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", Concept(null, "att.py"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "att.py"), [0x64, 0x65, 0x66, 0xFF, 0xFE]);

        var survey = Bundle.Load(tmp.Path).ExecutableResources[ConceptId.Parse("c/rev")];

        Assert.Equal(ExecutableResourceState.Unreadable, survey.Attester.State);
    }

    [Fact]
    public void A_global_failure_during_the_survey_fails_the_load()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester)
            .FailReadOf("att.py", new BundleLimitExceededException(BundleBound.Entries, 1));

        var e = Assert.Throws<BundleLoadException>(() => Bundle.Load(storage));

        Assert.Contains("entries", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_read_serves_the_observed_version_only()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);
        var pinned = bundle.ExecutableResources[ConceptId.Parse("c/rev")].Attester;

        var text = bundle.ReadResourceText(pinned.Key!, pinned.Version, out var version);
        Assert.Equal(Attester, text);
        Assert.Equal(pinned.Version, version);

        storage.Add("att.py", "def attest(**_):\n    return {'tampered': True}\n");

        var e = Assert.Throws<BundleVersionMismatchException>(() => bundle.ReadResourceText(pinned.Key!, pinned.Version, out _));
        Assert.Equal(BundleKey.Parse("att.py"), e.Key);
        Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("def attest(**_):\n    return {'tampered': True}\n")), bundle.ReadResourceText(pinned.Key!, null, out var current));
        Assert.NotEqual(pinned.Version, current);
    }
}
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `8 error CS0103: The name 'ExecutableResourceState' does not exist in the current context` and `2 error CS0246: The type or namespace name 'ExecutableResourceState' could not be found`.

- [ ] **Step 3: The survey and the pinned reader**

Create `src/OKF4net/ExecutableResources.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net;

/// <summary>
/// The state of one executable resource a §10 concept references — its <c>computation</c>
/// file or its <c>attester.resource</c> — as surveyed when the bundle was loaded (spec §5.1).
/// A state other than <see cref="Pinned"/> never fails the load and never removes the concept
/// from the image; it refuses the execution of that computation, naming the resource and the
/// state.
/// </summary>
public enum ExecutableResourceState
{
    /// <summary>Read and hashed at the load: executable, pinned to <see cref="ExecutableResource.Version"/>.</summary>
    Pinned,

    /// <summary>The concept declares no such field (for the computation: no <c>computation:</c> path, the code is inline or absent; for the attester: no <c>attester.resource</c>, which refuses the execution, D6).</summary>
    Undeclared,

    /// <summary>The value is a <c>scheme://…</c> URL: never read, and the execution is refused (D6).</summary>
    Url,

    /// <summary>The §6.2 resolution was refused (spec §2): the value climbs above the root, names a rejected key, or its path is refused by the provider.</summary>
    Unsafe,

    /// <summary>No object under the resolved key.</summary>
    Missing,

    /// <summary>The object could not be read, or is not valid UTF-8.</summary>
    Unreadable,

    /// <summary>The object is larger than <see cref="BundleLimits.MaxObjectBytes"/> (spec §6); nothing past the bound was read.</summary>
    TooLarge,
}

/// <summary>One executable resource of a §10 concept and its surveyed state (spec §5.1).</summary>
/// <param name="Field">The frontmatter field: <c>computation</c> or <c>attester.resource</c>.</param>
/// <param name="RawPath">The field's raw value, or <see langword="null"/> when <see cref="State"/> is <see cref="ExecutableResourceState.Undeclared"/>.</param>
/// <param name="State">The surveyed state.</param>
/// <param name="Key">The resolved key, when the value resolved to one (<see cref="ExecutableResourceState.Pinned"/>, <see cref="ExecutableResourceState.Missing"/>, <see cref="ExecutableResourceState.Unreadable"/>, <see cref="ExecutableResourceState.TooLarge"/>).</param>
/// <param name="Version">The object's version when <see cref="State"/> is <see cref="ExecutableResourceState.Pinned"/>: what a later read is pinned to.</param>
public sealed record ExecutableResource(string Field, string? RawPath, ExecutableResourceState State, BundleKey? Key, BundleObjectVersion? Version)
{
    /// <summary><see langword="true"/> when the resource was read and hashed at the load.</summary>
    public bool IsPinned => State == ExecutableResourceState.Pinned;
}

/// <summary>The two executable resources of one §10 concept (spec §5.1), surveyed at the load.</summary>
/// <param name="Computation">The <c>computation</c> file; <see cref="ExecutableResourceState.Undeclared"/> when the code is inline (or absent).</param>
/// <param name="Attester">The <c>attester.resource</c>.</param>
public sealed record ExecutableResourceSurvey(ExecutableResource Computation, ExecutableResource Attester);
```

In `src/OKF4net/Bundle.cs`, four edits.

(a) In `Load`, replace

```text
        return new Bundle(provider, options, validator, version, okfVersion, concepts, index, indexKeys, logKeys, parseProblems, rejectedKeys, tree.RejectedPrefixes.ToList(), portabilityIssues, objectVersions, outbound, backlinks);
    }
```

with

```csharp
        var bundle = new Bundle(provider, options, validator, version, okfVersion, concepts, index, indexKeys, logKeys, parseProblems, rejectedKeys, tree.RejectedPrefixes.ToList(), portabilityIssues, objectVersions, outbound, backlinks);

        // The executable resources of every §10 concept are read and hashed now (spec §5.1),
        // inside the load's scope, so that an execution can later be pinned to the version
        // this image was loaded with. A resource's own problem is a state, never a load failure.
        bundle.SurveyExecutableResources(chunk);
        return bundle;
    }

    /// <summary>
    /// Surveys the <c>computation</c> file and the <c>attester.resource</c> of every Attested
    /// Computation concept (spec §5.1): each is resolved like any §6.2 value
    /// (<see cref="TryResolveResourceKey"/>) and, when it resolves, read through the one bounded
    /// reader so that its version is known. The rule is local: a missing, unreadable, oversized
    /// or refused resource is a state of that resource; a global failure (a bound of spec §6, a
    /// provider refusal that is not the resource's own path) still fails the load, as it would
    /// without the survey (spec §5.1, revision 6).
    /// </summary>
    private void SurveyExecutableResources(byte[] chunk)
    {
        foreach (var concept in Concepts)
        {
            var frontmatter = concept.Document.Frontmatter;
            if (!frontmatter.IsAttestedComputation)
            {
                continue;
            }

            var contract = frontmatter.ComputationContract;
            _executableResources[concept.Id] = new ExecutableResourceSurvey(
                SurveyResource(concept, "computation", contract.ComputationPath, chunk),
                SurveyResource(concept, "attester.resource", contract.Attester?.Resource, chunk));
        }
    }

    private ExecutableResource SurveyResource(Concept concept, string field, string? rawPath, byte[] chunk)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return new ExecutableResource(field, null, ExecutableResourceState.Undeclared, null, null);
        }

        TryResolveResourceKey(concept, rawPath, out var key, out var status);
        switch (status)
        {
            case ResourceResolutionStatus.Url:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Url, null, null);
            case ResourceResolutionStatus.Unsafe:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Unsafe, null, null);
            case ResourceResolutionStatus.Missing:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Missing, null, null);
        }

        try
        {
            ReadTextAt(Provider, key!, Options.Limits, chunk, null, out var version);
            return new ExecutableResource(field, rawPath, ExecutableResourceState.Pinned, key, version);
        }
        catch (BundleObjectTooLargeException)
        {
            return new ExecutableResource(field, rawPath, ExecutableResourceState.TooLarge, key, null);
        }
        catch (BundleObjectNotFoundException)
        {
            return new ExecutableResource(field, rawPath, ExecutableResourceState.Missing, key, null);
        }
        catch (BundleAccessRefusedException)
        {
            return new ExecutableResource(field, rawPath, ExecutableResourceState.Unsafe, key, null);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, key); // a bound or a global refusal: the load fails, as without the survey
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return new ExecutableResource(field, rawPath, ExecutableResourceState.Unreadable, key, null);
        }
    }
```

(b) Replace

```text
    internal static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, out BundleObjectVersion version)
    {
        using var obj = provider.Read(key);
```

with

```csharp
    internal static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, out BundleObjectVersion version) =>
        ReadTextAt(provider, key, limits, chunk, null, out version);

    /// <summary>As <see cref="ReadText"/>, pinned: with <paramref name="expected"/>, the provider reads only the object at that version (spec §5.4) and throws <see cref="BundleVersionMismatchException"/> otherwise.</summary>
    internal static string ReadTextAt(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, BundleObjectVersion? expected, out BundleObjectVersion version)
    {
        using var obj = provider.Read(key, expected);
```

(c) Replace

```text
    public IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions { get; }
```

with

```csharp
    public IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions { get; }

    private readonly Dictionary<ConceptId, ExecutableResourceSurvey> _executableResources = [];

    /// <summary>
    /// The executable resources of every Attested Computation concept of this image — its
    /// <c>computation</c> file and its <c>attester.resource</c> — with the state each was found
    /// in at the load (spec §5.1): <see cref="ExecutableResourceState.Pinned"/> with its version,
    /// or why it cannot be executed. One entry per concept whose frontmatter
    /// <see cref="Frontmatter.IsAttestedComputation"/>; a concept with a problem is still in
    /// <see cref="Concepts"/>, readable, validated and audited.
    /// </summary>
    public IReadOnlyDictionary<ConceptId, ExecutableResourceSurvey> ExecutableResources => _executableResources;
```

(d) Replace

```text
    public string ReadResourceText(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsKeyAllowed(key, out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }

        try
        {
            return ReadText(Provider, key, Options.Limits, new byte[ReadChunkBytes], out _);
        }
```

with

```csharp
    public string ReadResourceText(BundleKey key) => ReadResourceText(key, null, out _);

    /// <summary>
    /// As <see cref="ReadResourceText(BundleKey)"/>, reporting the object's
    /// <paramref name="version"/> and, with <paramref name="expected"/>, pinned to it (spec §5.4):
    /// the provider reads only the object at that version, so sanctioned code that changed since
    /// it was observed or surveyed is never handed to a runtime.
    /// </summary>
    /// <param name="key">The resolved key.</param>
    /// <param name="expected">The version the read is pinned to, or <see langword="null"/> for the current object.</param>
    /// <param name="version">The version of the object read.</param>
    /// <exception cref="BundleVersionMismatchException">The object's version is not <paramref name="expected"/>; nothing was read.</exception>
    /// <exception cref="UnauthorizedAccessException">The key is not allowed, or the provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public string ReadResourceText(BundleKey key, BundleObjectVersion? expected, out BundleObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsKeyAllowed(key, out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }

        try
        {
            return ReadTextAt(Provider, key, Options.Limits, new byte[ReadChunkBytes], expected, out version);
        }
        catch (BundleVersionMismatchException)
        {
            throw;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~BundleExecutableResourceTests"`
Expected: 0 warnings, 0 errors; `Passed: 10, Skipped: 0, Total: 10`.
Then: `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~BundleExecutableResourceTests|FullyQualifiedName~BundleProviderLoadTests|FullyQualifiedName~BundleTests|FullyQualifiedName~AttestationOrchestratorTests|FullyQualifiedName~GoldenParityTests"` — expected `Passed: 119, Skipped: 2, Total: 121` (the survey changes no golden byte).

- [ ] **Step 5: CHANGELOG**

In `CHANGELOG.md`, replace

```text
  and invalidates the handle after each committed write.

### Changed
```

with

```markdown
  and invalidates the handle after each committed write.
- **Executable resources surveyed at the load (#139, lot C; spec §5.1).**
  `Bundle.ExecutableResources` gives, for every Attested Computation concept,
  the state of its `computation` file and of its `attester.resource`
  (`ExecutableResourceSurvey`, `ExecutableResource`,
  `ExecutableResourceState`): `Pinned` with the object's version when it was
  read and hashed, or `Undeclared`, `Url`, `Unsafe`, `Missing`, `Unreadable`
  or `TooLarge`. A resource's own problem is a state: the concept is still
  loaded, validated and audited; a global failure (a bound, a refusal that is
  not the resource's own path) still fails the load. `Bundle.ReadResourceText(key,
  expected, out version)` reads a resource pinned to a version: an object that
  changed since throws `BundleVersionMismatchException` and nothing is read.

### Changed
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/ExecutableResources.cs src/OKF4net/Bundle.cs tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net/ExecutableResources.cs src/OKF4net/Bundle.cs tests/OKF4net.Tests/Providers/BundleExecutableResourceTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(core): survey the executable resources of every attested computation at the load; pinned resource reads

Bundle.ExecutableResources holds, per Attested Computation concept, the
state of its computation file and of its attester.resource: Pinned with the
object's version when it was read and hashed, else Undeclared, Url, Unsafe,
Missing, Unreadable or TooLarge. A resource's own problem is a state; a
global failure still fails the load. Bundle.ReadResourceText(key, expected,
out version) reads a resource pinned to a version through the provider.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 3: The attestation captures sanctioned code once, pinned, and refuses an undeclared or URL attester (D6)

**Files:**
- Modify: `src/OKF4net.Attestation/AttestationOrchestrator.cs` (`RunAsync` at `:86-122`, its doc at `:57-82`, the two helpers at `:425-547`, the `using` at `:2`)
- Modify: `src/OKF4net.Attestation/Values.cs` (`AttesterSourceText` doc at `:35-41`; two records appended)
- Modify: `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`, `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`, `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs` (the fixture script, then three edits in the first)
- Modify: `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md` (§10 section, after S10.3-1), `CHANGELOG.md`
- Scratch, outside the repository (below `$S`): `declare-attesters.js` — never committed

**Interfaces:**
- Consumes: Task 2 (`Bundle.ExecutableResources`, `ExecutableResourceState`, `Bundle.ReadResourceText(key, expected, out version)`, `BundleVersionMismatchException`); lot A's `Bundle.TryResolveResourceKey`, `ResourceResolutionStatus`.
- Produces:
  - `public sealed record AttestationCapture(SanctionedComputation Computation, string AttesterSourceText)`; `public sealed record AttestationCaptureResult(AttestationCapture? Capture, AttestationOutcome? Failure, bool ImageOutdated)`.
  - `public static AttestationCaptureResult AttestationOrchestrator.Capture(Bundle bundle, Concept concept, BundleObjectVersion? expectedComputationFileVersion = null)`.
  - `public ValueTask<AttestationOutcome> AttestationOrchestrator.RunCapturedAsync(Bundle bundle, ConceptId conceptId, IReadOnlyDictionary<string, object?> parameterValues, AttestationCapture capture, StalePolicy? policy = null, CancellationToken cancellationToken = default)`.
  - `RunAsync(Bundle, ConceptId, values, StalePolicy?, CancellationToken)` keeps its signature (now non-`async`, returning a `ValueTask` built from the capture).
  - Every test fixture that runs a computation declares `attester: { resource: att.py }` and writes `att.py`; `InlineComputation` in `AttestationOrchestratorTests` included.

- [ ] **Step 1: Rewrite the fixtures and write the failing tests**

Save as `$S/declare-attesters.js` (outside the repository):

```javascript
// D6 (spec §5.1, §10): every test fixture that runs an attested computation must declare a
// file attester, or the run is now refused before the runtime is called. For each
// `tmp.Write("c/rev.md", …)` statement whose content declares `type: Attested Computation`
// and no `attester:`, this inserts `attester: { resource: att.py }` as the last frontmatter
// line and writes `att.py` right after the statement. Bytes outside those statements are
// untouched (CRLF included). usage: node declare-attesters.js <file>...
const fs = require('fs');
const ATTESTER = 'attester: { resource: att.py }';
const ATT_WRITE = 'tmp.Write("att.py", "def attest(**_):\\n    return {}\\n");';
for (const file of process.argv.slice(2)) {
  const src = fs.readFileSync(file, 'utf8');
  let out = '';
  let pos = 0;
  let touched = 0;
  const re = /tmp\.Write\(\s*"c\/rev\.md",/g;
  let m;
  while ((m = re.exec(src)) !== null) {
    const end = src.indexOf(');', m.index);
    const stmt = src.slice(m.index, end + 2);
    if (!stmt.includes('type: Attested Computation') || stmt.includes('attester:')) continue;
    const typeAt = stmt.indexOf('type: Attested Computation');
    const close = stmt.indexOf('\\n---\\n', typeAt);
    if (close < 0) throw new Error(`${file}: no frontmatter close in ${stmt}`);
    // An interpolated fixture ($"…") needs its braces doubled.
    const attester = stmt.includes('$"') ? ATTESTER.replace('{', '{{').replace('}', '}}') : ATTESTER;
    const patched = stmt.slice(0, close + 2) + attester + stmt.slice(close);
    const lineStart = src.lastIndexOf('\n', m.index) + 1;
    const indent = src.slice(lineStart, m.index);
    const eol = src[end + 2] === '\r' ? '\r\n' : '\n';
    out += src.slice(pos, m.index) + patched + eol + indent + ATT_WRITE;
    pos = end + 2;
    touched++;
    re.lastIndex = end + 2;
  }
  out += src.slice(pos);
  fs.writeFileSync(file, out);
  console.log(`${file}: ${touched} fixture(s) now declare att.py`);
}
```

Run, from the worktree root: `node $S/declare-attesters.js tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs`
Expected:

```text
tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs: 15 fixture(s) now declare att.py
tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs: 24 fixture(s) now declare att.py
tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs: 3 fixture(s) now declare att.py
```

and `tr -cd '\r' < f | wc -c` still equals `wc -l < f` for each (1150, 1129 and 566 lines).

Then, in `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs` (as the script left it), replace the whole test `Absent_attester_declaration_yields_null_source_text_not_a_failure` — exactly this text, which the script has just given its `att.py` line:

```text
    [Fact]
    public async Task Absent_attester_declaration_yields_null_source_text_not_a_failure()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\nattester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        tmp.Write("att.py", "def attest(**_):\n    return {}\n");
        var bundle = Bundle.Load(tmp.Path);
        var id = ConceptId.Parse("c/rev");

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.True(outcome.Displayable);
        Assert.Null(captured!.AttesterSourceText);
    }
```

with these three tests:

```csharp
    /// <summary>
    /// D6 (spec §5.1, §10; docs/spec-conformance S10.5-1): a computation that declares no
    /// <c>attester.resource</c>, or declares a URL, is refused before any stage runs. Until lot C
    /// this test was <c>Absent_attester_declaration_yields_null_source_text_not_a_failure</c>:
    /// the host's attester ran with a null source. There is nothing to pin in either case, so
    /// nothing the host or the model could hold for the attestation code that ran.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("attester: { resource: https://example.com/att.py }\n")]
    public async Task An_undeclared_or_URL_attester_is_refused_before_the_runtime_is_called(string attesterLine)
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" + attesterLine + "---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle = Bundle.Load(tmp.Path);
        var id = ConceptId.Parse("c/rev");

        var stages = 0;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.BindFunc = (contract, computation, values, _) =>
        {
            stages++;
            return ValueTask.FromResult(new BoundComputation("bigquery", computation.InlineCode, null, values));
        };
        runtime.AttestFunc = (_, _) =>
        {
            stages++;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.False(outcome.Displayable);
        Assert.Equal(0, stages);
        Assert.Null(outcome.Receipt);
        var reason = Assert.Single(outcome.Reasons);
        Assert.Contains(attesterLine.Length == 0 ? "no attester.resource is declared" : "is a URL, which is never read", reason, StringComparison.Ordinal);
        Assert.Contains("is not run", reason, StringComparison.Ordinal);

        var captured = AttestationOrchestrator.Capture(bundle, bundle.Get(id)!);
        Assert.Null(captured.Capture);
        Assert.False(captured.ImageOutdated);
        Assert.Equal(outcome.Reasons, captured.Failure!.Reasons);
    }

    /// <summary>
    /// The pin (spec §5.4, point 17): the attester's source is read at the version the image
    /// was loaded with. A script replaced after the load — same length, same second, which the
    /// filesystem's weak bundle token cannot see — is refused, the attester never called, and
    /// the capture says the image is outdated so that a handle reloads it.
    /// </summary>
    [Fact]
    public async Task The_attester_is_pinned_to_the_version_the_image_was_loaded_with()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputationWithAttesterFile(tmp, "def attest(**_):\n    return {}  # v1\n");
        var attested = false;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (_, _) =>
        {
            attested = true;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        var path = Path.Combine(tmp.Path, "att.py");
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "def attest(**_):\n    return {}  # v2\n");
        File.SetLastWriteTimeUtc(path, stamp);

        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.False(outcome.Displayable);
        Assert.False(attested);
        Assert.Contains(outcome.Reasons, r => r == "attester resource 'att.py' changed since the bundle was loaded");
        var captured = AttestationOrchestrator.Capture(bundle, bundle.Get(id)!);
        Assert.Null(captured.Capture);
        Assert.True(captured.ImageOutdated);

        var reloaded = Bundle.Load(tmp.Path);
        var fresh = await orch.RunAsync(reloaded, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(fresh.Displayable);
        Assert.True(attested);
    }

    /// <summary>A computation file pinned to the version a caller observed (spec §5.4, point 2) is refused when it changed; unpinned, it is read as it is.</summary>
    [Fact]
    public void A_computation_file_pinned_to_an_observed_version_is_refused_when_it_changed()
    {
        using var tmp = new TempDir();
        tmp.Write("references/revenue.sql", "SELECT 1\n");
        tmp.Write("att.py", "def attest(**_):\n    return {}\n");
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\nattester: { resource: att.py }\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        var observed = bundle.ExecutableResources[concept.Id].Computation.Version;

        var pinned = AttestationOrchestrator.Capture(bundle, concept, observed);
        Assert.NotNull(pinned.Capture);
        Assert.Equal("SELECT 1\n", pinned.Capture!.Computation.InlineCode);
        Assert.Equal("def attest(**_):\n    return {}\n", pinned.Capture.AttesterSourceText);

        File.WriteAllText(Path.Combine(tmp.Path, "references", "revenue.sql"), "SELECT 2\n");

        var refused = AttestationOrchestrator.Capture(bundle, concept, observed);
        Assert.Null(refused.Capture);
        Assert.True(refused.ImageOutdated);
        Assert.Contains(refused.Failure!.Reasons, r => r.StartsWith("computation file 'references/revenue.sql' changed since it was shown", StringComparison.Ordinal));

        var unpinned = AttestationOrchestrator.Capture(bundle, concept);
        Assert.Equal("SELECT 2\n", unpinned.Capture!.Computation.InlineCode);
    }
```

Then, in the same file, replace

```text
        Assert.Contains(outcome.Reasons, r => r.Contains("computation file 'x/y/secret.sql' could not be read: UnauthorizedAccessException", StringComparison.Ordinal));
    }
```

with

```csharp
        // The refusal moved from the read to the resolution (lot C): the provider's exact-name
        // lookup cannot list "x" once the junction denies it, so the key resolves Missing. Either
        // refusal is what this test pins: nothing outside the bundle reaches the binder.
        Assert.Contains(outcome.Reasons, r => r.StartsWith("computation file 'x/y/secret.sql' could not be re", StringComparison.Ordinal));
    }
```

and replace

```text
        Assert.Contains(outcome.Reasons, r => r.Contains("attester resource 'x/y/att.py' could not be read: UnauthorizedAccessException", StringComparison.Ordinal));
    }
```

with

```csharp
        // As for the computation file above: refused at the resolution (the survey finds it
        // Missing once "x" denies listing), or at the pinned read — never attested.
        Assert.Contains(outcome.Reasons, r => r.StartsWith("attester resource 'x/y/att.py' could not be re", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `10 error CS0117: 'AttestationOrchestrator' does not contain a definition for 'Capture'`.

- [ ] **Step 3: The capture types and the orchestrator**

In `src/OKF4net.Attestation/Values.cs`, replace

```text
/// <param name="AttesterSourceText">
/// The attester's script source, already resolved and read via §6.2
/// (mirrors how <see cref="Computation"/> is resolved) — <see langword="null"/>
/// when the concept declares no attester, an empty resource, or a URL
/// resource. No <see cref="IAttester"/> implementation needs a
/// <see cref="Bundle"/> or <see cref="Concept"/> as a result.
/// </param>
```

with

```csharp
/// <param name="AttesterSourceText">
/// The attester's script source, resolved and read through the bundle's provider (§6.2),
/// pinned to the version the image was loaded with (spec §5.4). Never <see langword="null"/>
/// at a runtime's entry since D6: a concept that declares no attester, or a URL, is refused
/// before any stage runs. The type stays nullable so that hosts building a context of their
/// own are not broken. No <see cref="IAttester"/> implementation needs a <see cref="Bundle"/>
/// or <see cref="Concept"/> as a result.
/// </param>
```

and append to the end of the file (after `AttestationOutcome`'s closing `);`):

```csharp

/// <summary>
/// What one run executes, captured once before any runtime is called (spec §5.4): the
/// sanctioned computation (inline, or the file's text) and the attester's source, read pinned
/// to the version the image was loaded with. Built by <see cref="AttestationOrchestrator.Capture"/>.
/// </summary>
/// <param name="Computation">The sanctioned computation: inline code, or the file's already-read text.</param>
/// <param name="AttesterSourceText">The attester's source, read at its pinned version.</param>
public sealed record AttestationCapture(SanctionedComputation Computation, string AttesterSourceText);

/// <summary>The result of <see cref="AttestationOrchestrator.Capture"/>: a capture, or a non-displayable outcome saying why nothing is run.</summary>
/// <param name="Capture">What the run executes, or <see langword="null"/> when the capture was refused.</param>
/// <param name="Failure">The non-displayable outcome to report, when <see cref="Capture"/> is <see langword="null"/>.</param>
/// <param name="ImageOutdated"><see langword="true"/> when the refusal is one the image may be behind on — a resource missing, unreadable, refused, over the bound, or whose version changed — so that a caller holding a <c>BundleHandle</c> invalidates it (spec §5.1); <see langword="false"/> for an undeclared or URL attester and for a concept without a computation.</param>
public sealed record AttestationCaptureResult(AttestationCapture? Capture, AttestationOutcome? Failure, bool ImageOutdated);
```

(the file's line endings: after appending with a heredoc, run `unix2dos -q src/OKF4net.Attestation/Values.cs`; the whole file must be CRLF).

In `src/OKF4net.Attestation/AttestationOrchestrator.cs`, four edits.

(a) Replace the line `using System.Diagnostics.CodeAnalysis;` with `using OKF4net.Providers;` (the `[NotNullWhen]` attributes leave with the helpers; `BundleObjectVersion` and `BundleVersionMismatchException` arrive).

(b) Replace, in `RunAsync`'s doc comment,

```text
    /// <para>
    /// The body reads as the numbered §10.5 steps, in order. Two of them —
    /// resolving the computation and attesting — live in
    /// <see cref="TryResolveComputation"/> and <see cref="AttestAsync"/>
    /// because they branch several ways with I/O of their own, and inlining
    /// them buried the pipeline they are steps of. Everything else is here on
    /// purpose: the sequence IS the specification, and hiding it behind
    /// helpers would cost more than it saved.
    /// </para>
    /// <para><b>Fail-closed on an unresolvable <c>attester.resource</c>.</b> When a
    /// concept declares one, it is resolved (§6.2) and read <i>before</i> binding or
    /// execution, and a value that does not resolve — missing file, or a path that
    /// would escape the bundle — ends the run with a non-displayable outcome. Nothing
    /// executes. This is deliberate: an attester the bundle names but the host cannot
    /// read is an attestation that was specified and then not performed, and §10.6 is
    /// about not displaying a figure whose check did not happen.
    ///
    /// It is also a behaviour change for hosts that predate
    /// <see cref="AttestationContext.AttesterSourceText"/>, which is to say all of
    /// them: such a host's <see cref="IAttester"/> supplies its own implementation and
    /// never wanted the bundle's source, yet a declared-but-broken
    /// <c>attester.resource</c> now stops its run. The cheapest workaround is deleting
    /// the <c>attester:</c> block from the bundle, which silently removes attestation
    /// altogether — so fix the path instead, or point it at a resource the host can
    /// read.</para>
    /// </summary>
```

with

```csharp
    /// <para>
    /// The body reads as the numbered §10.5 steps, in order: step 1 here, step 2 in
    /// <see cref="Capture"/> (the one step that reads sanctioned code, pinned to the
    /// image), steps 3 to 10 in <see cref="RunCapturedAsync"/>, with attestation in
    /// <see cref="AttestAsync"/>. Everything else is inline on purpose: the sequence IS
    /// the specification, and hiding it behind helpers would cost more than it saved.
    /// </para>
    /// <para><b>Fail-closed on the <c>attester.resource</c>.</b> The attester's source is
    /// read <i>before</i> binding or execution, pinned to the version the image was loaded
    /// with, and a concept whose attester does not resolve — missing file, a path that
    /// would escape the bundle, a resource that changed since the load — ends the run with
    /// a non-displayable outcome. Nothing executes. So does a concept that declares no
    /// <c>attester.resource</c>, or declares a URL (D6, spec §5.1): there is nothing to pin,
    /// so the run is refused before the runtime is called, and the host's
    /// <see cref="IAttester"/> never receives a <see langword="null"/>
    /// <see cref="AttestationContext.AttesterSourceText"/>. This is deliberate: an attester
    /// the bundle names but the host cannot read is an attestation that was specified and
    /// then not performed, and §10.6 is about not displaying a figure whose check did not
    /// happen. A host whose <see cref="IAttester"/> attested without a source in the bundle
    /// sees its computations refused until the bundle declares a file attester
    /// (docs/spec-conformance, S10.5-1).</para>
    /// </summary>
```

(c) Replace the head of `RunAsync` up to and including the `// Step 3` comment:

```text
    public async ValueTask<AttestationOutcome> RunAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        // Errors-as-data (see class remarks) extends to the parameters argument
        // itself: a caller passing null must get the normal "missing required
        // parameter" outcome below, never a NullReferenceException.
        parameterValues ??= new Dictionary<string, object?>();

        // Step 1: load the concept; it must exist and be an Attested Computation.
        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return Fail($"concept '{conceptId}' was not found or is not an attested computation");
        }

        var frontmatter = concept.Document.Frontmatter;
        var contract = frontmatter.ComputationContract;

        // Step 2: resolve the sanctioned computation (inline fence, or file via §6.2 path-safe resolution).
        if (!TryResolveComputation(bundle, concept, out var resolved, out var resolutionFailure))
        {
            return resolutionFailure;
        }

        // Step 2b: resolve the attester's own source, the same way (§6.2).
        if (!TryResolveAttesterSource(bundle, concept, contract, out var attesterSourceText, out var attesterResolutionFailure))
        {
            return attesterResolutionFailure;
        }

        // Step 3: resolve the runtime.
```

with

```csharp
    public ValueTask<AttestationOutcome> RunAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // Step 1: load the concept; it must exist and be an Attested Computation.
        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return ValueTask.FromResult(Fail($"concept '{conceptId}' was not found or is not an attested computation"));
        }

        // Step 2: capture the sanctioned code — the computation (inline fence, or the file read
        // through the provider) and the attester's source, pinned to the image (spec §5.4).
        var captured = Capture(bundle, concept);
        return captured.Capture is null
            ? ValueTask.FromResult(captured.Failure!)
            : RunCapturedAsync(bundle, conceptId, parameterValues, captured.Capture, policy, cancellationToken);
    }

    /// <summary>
    /// Captures, once and before any runtime is called, everything a run executes (spec §5.4):
    /// the sanctioned computation — the inline <c># Computation</c> fence, or the
    /// <c>computation:</c> file read through the provider (<see cref="Bundle.TryResolveResourceKey"/>,
    /// <see cref="Bundle.ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)"/>),
    /// pinned to <paramref name="expectedComputationFileVersion"/> when the caller observed one —
    /// and the attester's source, pinned to the version the image was loaded with
    /// (<see cref="Bundle.ExecutableResources"/>): its state must be
    /// <see cref="ExecutableResourceState.Pinned"/>, and it is read at that version only.
    /// <para><b>D6 (spec §5.1, §10).</b> A concept that declares no <c>attester.resource</c>, or
    /// declares a URL, is refused here: there is nothing to pin, so nothing the host or the model
    /// could hold for the attestation code that ran. The host's <see cref="IAttester"/> is never
    /// called with a <see langword="null"/> source. This narrows §10.5 for this implementation
    /// (docs/spec-conformance, S10.5-1).</para>
    /// <para>A failure is an <see cref="AttestationOutcome"/>, never an exception.
    /// <see cref="AttestationCaptureResult.ImageOutdated"/> says whether the failure is one the
    /// image may be behind on — a resource missing, unreadable, refused, over the bound, or whose
    /// version changed — in which case a caller holding a <see cref="BundleHandle"/> invalidates
    /// it so that the next attempt starts from a fresh survey (spec §5.1); an undeclared or URL
    /// attester is not such a failure, since only a change of the concept can alter it.</para>
    /// </summary>
    /// <param name="bundle">The image the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept, from that image.</param>
    /// <param name="expectedComputationFileVersion">The version of the <c>computation</c> file the caller observed, which the read is pinned to; <see langword="null"/> reads the file as it is.</param>
    public static AttestationCaptureResult Capture(Bundle bundle, Concept concept, BundleObjectVersion? expectedComputationFileVersion = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(concept);

        SanctionedComputation resolved;
        var computation = concept.Document.Computation();
        switch (computation.Source)
        {
            case ComputationSource.Inline when !string.IsNullOrEmpty(computation.InlineCode):
                resolved = computation;
                break;

            case ComputationSource.File when !string.IsNullOrEmpty(computation.Path):
                if (!bundle.TryResolveResourceKey(concept, computation.Path, out var key, out var status)
                    || status != ResourceResolutionStatus.Resolved)
                {
                    return Refused($"computation file '{computation.Path}' could not be resolved ({status})", imageOutdated: true);
                }

                string text;
                try
                {
                    text = bundle.ReadResourceText(key!, expectedComputationFileVersion, out _);
                }
                catch (BundleVersionMismatchException)
                {
                    return Refused($"computation file '{computation.Path}' changed since it was shown; read it again with okf_get_computation before running it", imageOutdated: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    // The exception TYPE, as for the stage failures: this reason string is
                    // rendered into a model's context, and a raw I/O message can carry a host
                    // path. computation.Path is bundle-relative and safe to name.
                    return Refused($"computation file '{computation.Path}' could not be read: {e.GetType().Name}", imageOutdated: true);
                }

                resolved = new SanctionedComputation(ComputationSource.File, text, computation.Path);
                break;

            default:
                return Refused("attested computation has no computation (neither an inline `# Computation` fence nor a `computation:` path)", imageOutdated: false);
        }

        if (!bundle.ExecutableResources.TryGetValue(concept.Id, out var survey))
        {
            return Refused($"concept '{concept.Id}' is not part of the bundle image it was given with", imageOutdated: false);
        }

        var attester = survey.Attester;
        switch (attester.State)
        {
            case ExecutableResourceState.Undeclared:
                return Refused("no attester.resource is declared, so the computation cannot be attested and is not run", imageOutdated: false);
            case ExecutableResourceState.Url:
                return Refused($"attester resource '{attester.RawPath}' is a URL, which is never read, so the computation cannot be attested and is not run", imageOutdated: false);
            case ExecutableResourceState.Unsafe:
            case ExecutableResourceState.Missing:
                return Refused($"attester resource '{attester.RawPath}' could not be resolved ({attester.State})", imageOutdated: true);
            case ExecutableResourceState.Unreadable:
            case ExecutableResourceState.TooLarge:
                return Refused($"attester resource '{attester.RawPath}' could not be read ({attester.State})", imageOutdated: true);
        }

        string attesterSourceText;
        try
        {
            attesterSourceText = bundle.ReadResourceText(attester.Key!, attester.Version, out _);
        }
        catch (BundleVersionMismatchException)
        {
            return Refused($"attester resource '{attester.RawPath}' changed since the bundle was loaded", imageOutdated: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return Refused($"attester resource '{attester.RawPath}' could not be read: {e.GetType().Name}", imageOutdated: true);
        }

        return new AttestationCaptureResult(new AttestationCapture(resolved, attesterSourceText), null, false);
    }

    private static AttestationCaptureResult Refused(string reason, bool imageOutdated) => new(null, Fail(reason), imageOutdated);

    /// <summary>
    /// Runs the §10.5 workflow from step 3 on what <paramref name="capture"/> holds — the
    /// computation and the attester's source, captured once by <see cref="Capture"/> — so that
    /// nothing is read from the bundle after the capture (spec §5.4). <see cref="RunAsync"/>
    /// captures and calls this; <c>okf_run_computation</c> captures itself, pinned to what its
    /// session observed, then calls this.
    /// </summary>
    /// <param name="bundle">The image the concept was loaded from.</param>
    /// <param name="conceptId">The attested-computation concept to run.</param>
    /// <param name="parameterValues">The parameter values supplied for this run (§10.3: values only, never computation code).</param>
    /// <param name="capture">What the run executes.</param>
    /// <param name="policy">The staleness gating policy for this run; defaults to the constructor's <c>defaultPolicy</c>.</param>
    /// <param name="cancellationToken">A token to cancel binding/execution/attestation.</param>
    public async ValueTask<AttestationOutcome> RunCapturedAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        AttestationCapture capture,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(capture);

        // Errors-as-data (see class remarks) extends to the parameters argument
        // itself: a caller passing null must get the normal "missing required
        // parameter" outcome below, never a NullReferenceException.
        parameterValues ??= new Dictionary<string, object?>();

        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return Fail($"concept '{conceptId}' was not found or is not an attested computation");
        }

        var frontmatter = concept.Document.Frontmatter;
        var contract = frontmatter.ComputationContract;
        var resolved = capture.Computation;
        var attesterSourceText = capture.AttesterSourceText;

        // Step 3: resolve the runtime.
```

(d) Delete the two helpers `TryResolveComputation` and `TryResolveAttesterSource` — exactly this text, from the `§10.5 step 2` summary to the end of `TryResolveAttesterSource` (the next member, `IsCallerCancellation`, and its doc comment stay):

```text
    /// <summary>
    /// §10.5 step 2: resolves the concept's sanctioned computation — the inline
    /// <c># Computation</c> fence, or the <c>computation:</c> file read through
    /// §6.2 path-safe resolution.
    ///
    /// Extracted from <see cref="RunAsync"/> purely to keep that method
    /// readable: it is one linear pipeline, and this was the one step that
    /// branched three ways with I/O of its own inside. Behaviour is unchanged,
    /// including which failures are reported and their exact wording.
    /// </summary>
    /// <param name="bundle">The bundle the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept.</param>
    /// <param name="resolved">The resolved computation, when this returns <see langword="true"/>.</param>
    /// <param name="failure">The non-displayable outcome to return, when this returns <see langword="false"/>.</param>
    private static bool TryResolveComputation(
        Bundle bundle,
        Concept concept,
        out SanctionedComputation resolved,
        [NotNullWhen(false)] out AttestationOutcome? failure)
    {
        var computation = concept.Document.Computation();
        failure = null;

        switch (computation.Source)
        {
            case ComputationSource.Inline when !string.IsNullOrEmpty(computation.InlineCode):
                resolved = computation;
                return true;

            case ComputationSource.File when !string.IsNullOrEmpty(computation.Path):
                resolved = default;
                if (!bundle.TryResolveResource(concept, computation.Path, out var absolutePath, out var status)
                    || status != ResourceResolutionStatus.Resolved)
                {
                    failure = Fail($"computation file '{computation.Path}' could not be resolved ({status})");
                    return false;
                }

                string text;
                try
                {
                    text = bundle.ReadResourceText(absolutePath!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    // The exception TYPE, for the same reason as the stage
                    // failures above: this reason string is rendered into a
                    // model's context, and an IOException's own message carries
                    // the absolute host path. computation.Path is bundle-
                    // relative and safe to name; e.Message is not.
                    failure = Fail($"computation file '{computation.Path}' could not be read: {e.GetType().Name}");
                    return false;
                }

                resolved = new SanctionedComputation(ComputationSource.File, text, computation.Path);
                return true;

            default:
                resolved = default;
                failure = Fail("attested computation has no computation (neither an inline `# Computation` fence nor a `computation:` path)");
                return false;
        }
    }

    /// <summary>
    /// Resolves the concept's <c>attester.resource</c> the same way
    /// <see cref="TryResolveComputation"/> resolves <c>computation</c>, so no
    /// <see cref="IAttester"/> implementation ever needs a <see cref="Bundle"/>
    /// itself. Absent, empty, or URL-valued <c>attester.resource</c> is not a
    /// failure — §11 leaves the attester optional (a validator warning, not
    /// an error, flags an empty resource) — it simply yields a
    /// <see langword="null"/> source. A declared resource that cannot
    /// actually be resolved or read on disk IS an early failure, same as a
    /// broken computation file.
    /// </summary>
    /// <param name="bundle">The bundle the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept.</param>
    /// <param name="contract">The concept's §10.2 contract.</param>
    /// <param name="attesterSourceText">The resolved source text, or <see langword="null"/> when there is nothing to resolve.</param>
    /// <param name="failure">The non-displayable outcome to return, when this returns <see langword="false"/>.</param>
    private static bool TryResolveAttesterSource(
        Bundle bundle,
        Concept concept,
        AttestedComputationContract contract,
        out string? attesterSourceText,
        [NotNullWhen(false)] out AttestationOutcome? failure)
    {
        attesterSourceText = null;
        failure = null;

        var resource = contract.Attester?.Resource;
        if (string.IsNullOrEmpty(resource))
        {
            return true;
        }

        if (!bundle.TryResolveResource(concept, resource, out var absolutePath, out var status) || status == ResourceResolutionStatus.Url)
        {
            // URLs are never resolved on disk (§6.2); nothing to read. (The
            // `!TryResolveResource(...)` half never actually triggers --
            // resolution always returns true -- kept only for the same
            // defensive symmetry TryResolveComputation above already uses.)
            return true;
        }

        if (status != ResourceResolutionStatus.Resolved)
        {
            failure = Fail($"attester resource '{resource}' could not be resolved ({status})");
            return false;
        }

        try
        {
            attesterSourceText = bundle.ReadResourceText(absolutePath!);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            failure = Fail($"attester resource '{resource}' could not be read: {e.GetType().Name}");
            return false;
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~OKF4net.Tests.Attestation|FullyQualifiedName~OKF4net.Tests.Agents|FullyQualifiedName~OKF4net.Tests.Mcp"`
Expected: 0 warnings, 0 errors; `Passed: 629, Skipped: 8, Total: 637` (on Windows with junction privilege; the two uninspectable-junction tests of the orchestrator skip elsewhere).

- [ ] **Step 5: The conformance entry and the CHANGELOG**

In `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`, replace

```text
  `IAttester.AttestAsync` and isn't implemented in this repo, so Partial.

### §11 Conformance
```

with

```markdown
  `IAttester.AttestAsync` and isn't implemented in this repo, so Partial.
- **S10.5-1** (§10.5, the attestation step: "attest … via the attester") —
  **Diverges / Intentional** (Major, added 2026-10-22, decision D6 of the
  bundle-provider design, `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`
  §5.1). §10.5 names the attester as the process that verifies a run and
  §11 leaves `attester` optional (an empty resource is a validator warning).
  Until lot C, `AttestationOrchestrator` let the host's registered
  `IAttester` attest a concept that declared no `attester.resource`, or
  declared a URL, with `AttestationContext.AttesterSourceText = null`. Now
  (`src/OKF4net.Attestation/AttestationOrchestrator.cs`, `Capture`) both
  cases are **refused before any runtime stage is called**, as a
  non-displayable outcome naming the reason (`no attester.resource is
  declared, so the computation cannot be attested and is not run` / `… is a
  URL, which is never read, so …`). Reason: the orchestrator pins every piece
  of sanctioned code to the version the image was loaded with (`Bundle.ExecutableResources`),
  so that what ran is what the bundle declared; an undeclared or remote
  attester has nothing to pin, hence nothing the host or the model could hold
  for the attestation code that actually ran. `AttesterSourceText` is never
  `null` at a runtime's entry. Scope: every bundle of this repository
  declares a file attester, and `ContainerAttester` already refused a null
  source; an external host that relied on an `IAttester` attesting without a
  source sees its computations refused until its bundle declares one. Tests:
  `An_undeclared_or_URL_attester_is_refused_before_the_runtime_is_called`,
  `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`.

### §11 Conformance
```

In `CHANGELOG.md`, replace

```text
  changed since throws `BundleVersionMismatchException` and nothing is read.

### Changed
```

with

```markdown
  changed since throws `BundleVersionMismatchException` and nothing is read.
- **`AttestationOrchestrator.Capture` and `RunCapturedAsync`** (#139, lot C;
  spec §5.4). `Capture(bundle, concept, expectedComputationFileVersion?)`
  reads, once and before any runtime is called, everything a run executes —
  the computation (inline, or the file through the provider, pinned to the
  version the caller observed when it gives one) and the attester's source,
  pinned to the version the image was loaded with — as an
  `AttestationCapture`, or a non-displayable outcome with `ImageOutdated`
  saying whether the image may be behind. `RunCapturedAsync` runs §10.5 from
  step 3 on a capture; `RunAsync` captures and calls it, and reads nothing
  from the bundle afterwards.

### Changed
```

and replace

```text
### Changed

- **Finite default bounds on loading** (spec §6), out of reach of real
```

with

```markdown
### Changed

- **A computation without a file attester is no longer run (D6; spec §5.1,
  §10; docs/spec-conformance S10.5-1).** `AttestationOrchestrator.RunAsync`
  refuses, before any runtime stage is called, a concept that declares no
  `attester.resource` or declares a URL: there is nothing to pin, so nothing
  the host or the model could hold for the attestation code that ran. The
  host's `IAttester` used to be called with `AttesterSourceText = null` in
  both cases; it never is now. Every bundle of this repository declares a
  file attester; an external host that attested without one sees those
  computations refused (`displayable: no`, reason `no attester.resource is
  declared, so the computation cannot be attested and is not run`) until its
  bundle declares a file attester.
- **Sanctioned code is read through the provider and pinned** (spec §5.4,
  §9). The orchestrator resolves the `computation` file and the
  `attester.resource` on keys (`Bundle.TryResolveResourceKey`,
  `Bundle.ReadResourceText(BundleKey, …)`) instead of the historical
  path-based members: the key policy, the rejected-key guard, the exact-case
  and backslash rules now apply to what a run reads, which closes the gap the
  previous release listed as temporary (`okf_run_computation` could read a
  resource `okf validate` reported `Unsafe` or `Missing`). The attester is
  read at the version the image was loaded with: a script replaced after the
  load — same length, same second, which the filesystem's bundle token cannot
  see — is refused (`attester resource 'x' changed since the bundle was
  loaded`), as is one whose surveyed state is not `Pinned` (`could not be
  resolved (Missing)`, `could not be read (TooLarge)`). A refusal's wording
  for a resource that could not be opened after the load names the exception
  type as before; a resource whose directory can no longer be listed (a
  junction that denies listing) is now refused at the resolution (`could not
  be resolved (Missing)`) rather than at the read.
- **Finite default bounds on loading** (spec §6), out of reach of real
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net.Attestation/AttestationOrchestrator.cs src/OKF4net.Attestation/Values.cs tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net.Attestation/AttestationOrchestrator.cs src/OKF4net.Attestation/Values.cs tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs docs/spec-conformance/2026-07-31-okf-spec-gap-report.md CHANGELOG.md
git commit -F - <<'MSG'
feat(attestation): capture sanctioned code once through the provider, pinned; refuse an undeclared or URL attester (D6)

AttestationOrchestrator.Capture reads the computation file and the attester
source on keys, the attester pinned to the version the image was loaded
with and the computation file to the version a caller observed, and reports
whether a refusal leaves the image outdated. RunCapturedAsync runs steps 3
to 10 on a capture; RunAsync captures and delegates. A concept without an
attester.resource, or with a URL, is refused before any stage (D6,
docs/spec-conformance S10.5-1). Every test fixture that runs a computation
now declares a file attester.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 4: `OkfBundleTools` serves its image from a `BundleHandle` and makes no direct I/O

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (the fields at `:110-112`, the constructors at `:162-180`, `GetBundle`/`InvalidateBundle` at `:217-235`, `GetTools(mode)` at `:343-352`, `Browse` at `:446-470`, `RegenerateIndexes` at `:937-975`, `ChangesSince` at `:1077-1080`, `AppendLogFileChanges` at `:1394-1402`, `GetComputation` at `:1152-1175`, a `PrefixExists` helper before `BuildLevelListing` at `:1683`; the `_writer` doc at `:57-66` and `:101-103`)
- Modify: `tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs` (`:169-212`, `:426-437`), `tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs` (`:101-108`), `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs` (`:387`, `:501-504`)
- Test: `tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs`
- Modify: `CHANGELOG.md`, `README.md` (after "Writing through a provider")

**Interfaces:**
- Consumes: Task 1 (`BundleHandle`, `BundleConceptWriter(BundleHandle, …)`), lot A (`Bundle.LogKeys`, `Bundle.IsKeyAllowed`, `Bundle.ReadResourceText(BundleKey)`, `Bundle.TryResolveResourceKey`, `BundleKey.Parse/TryParse/Append`, `IBundleProvider.Enumerate`), lot B's internal `IndexGenerator.Regenerate(provider, synthesize, options, legacy)` and `IndexGenerator.DefaultSynthesize`.
- Produces:
  - `public OkfBundleTools(BundleHandle handle, AttestationOrchestrator? orchestrator = null)`; `public BundleHandle Handle { get; }`; `public string Location { get; }`; `public string BundleRoot` now throws `NotSupportedException` over a non-filesystem provider.
  - `GetTools(mode)` offers no write tool when `Handle.Provider.Capabilities.CanWrite` is false; `RegenerateIndexes()` throws `BundleReadOnlyException` on a read-only provider.
  - `Browse`, `ChangesSince`, `RegenerateIndexes`, `GetComputation` read and write through `Handle.Provider` only; the `FileSystemBundleProvider`-only members are gone from the class but the constructor's `Directory.Exists`.
  - The tool set's one cache is the handle: `GetBundle()` returns `Handle.Current`, `InvalidateBundle()` calls `Handle.Invalidate()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <see cref="OkfBundleTools"/> over a <see cref="BundleHandle"/> (spec §5.2, §9): the image
/// is the handle's, shared with whoever else holds the handle; the write tools are offered
/// only when the provider can write; and no tool reads outside the provider — browsing,
/// the change log and a computation's file all go through it, so they work over a provider
/// that is not a directory at all.
/// </summary>
public class OkfBundleToolsHandleTests
{
    private const string Note = "---\ntype: Note\ntitle: {0}\ndescription: {1}\n---\n\nBody of {0}.\n";

    private static InMemoryBundleProvider Storage(bool canWrite = true) => new InMemoryBundleProvider(canWrite: canWrite)
        .Add("tables/orders.md", string.Format(null, Note, "Orders", "One row per order."))
        .Add("tables/index.md", "# tables\n\n* [Orders](orders.md) - One row per order.\n")
        .Add("notes/a.md", string.Format(null, Note, "A", "a."))
        .Add("log.md", "# Log\n\n## 2026-07-22\n\n* **Creation**: Bootstrapped.\n")
        .Add("assets/logo.png", "png");

    [Fact]
    public void Two_tool_sets_over_one_handle_share_one_image_and_a_write_refreshes_it_for_both()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var a = new OkfBundleTools(handle);
        var b = new OkfBundleTools(handle);

        Assert.Same(a.GetBundle(), b.GetBundle());
        Assert.Same(handle, a.Handle);
        Assert.Equal("memory://memory", a.Location);
        Assert.Throws<NotSupportedException>(() => a.BundleRoot);

        var written = a.WriteConcept("notes/b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n");

        Assert.StartsWith("Written", written, StringComparison.Ordinal);
        Assert.True(handle.IsStale);
        Assert.Equal(3, b.GetBundle().Count);
        Assert.Same(a.GetBundle(), b.GetBundle());
    }

    [Fact]
    public void A_root_string_builds_a_handle_of_its_own_and_keeps_the_root()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", string.Format(null, Note, "A", "a."));
        var first = new OkfBundleTools(tmp.Path);
        var second = new OkfBundleTools(tmp.Path);

        Assert.NotSame(first.Handle, second.Handle);
        Assert.Equal(tmp.Path, first.BundleRoot);
        Assert.Equal(tmp.Path, first.Location);
        Assert.Equal(BundleRefreshMode.Manual, first.Handle.RefreshPolicy.Mode);
        Assert.IsType<FileSystemBundleProvider>(first.Handle.Provider);
    }

    [Theory]
    [InlineData(OkfToolMode.ReadWrite)]
    [InlineData(OkfToolMode.RequireApprovalForWrites)]
    [InlineData(OkfToolMode.ReadOnly)]
    public void Over_a_provider_that_cannot_write_no_write_tool_is_offered_whatever_the_mode(OkfToolMode mode)
    {
        using var handle = new BundleHandle(Storage(canWrite: false));
        var names = new OkfBundleTools(handle).GetTools(mode).Cast<AIFunction>().Select(f => f.Name).ToList();

        Assert.Equal(8, names.Count);
        Assert.DoesNotContain(names, OkfBundleTools.WriteToolNames.Contains);
        Assert.Contains("okf_read_concept", names);

        using var writable = new BundleHandle(Storage(canWrite: true));
        Assert.Equal(mode == OkfToolMode.ReadOnly ? 8 : 12, new OkfBundleTools(writable).GetTools(mode).Count);
    }

    [Fact]
    public void Writes_over_a_read_only_provider_throw_before_touching_anything()
    {
        var storage = Storage(canWrite: false);
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        storage.ClearCalls();

        Assert.Throws<BundleReadOnlyException>(() => tools.WriteConcept("notes/b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n"));
        Assert.Throws<BundleReadOnlyException>(() => tools.AppendLog("Update", "x"));
        Assert.Throws<BundleReadOnlyException>(() => tools.RegenerateIndexes());
        // Verify runs inside RunTool, which renders the library's refusal as its Error: text.
        Assert.Equal("Error: the provider is read-only; the write was refused before being attempted", tools.Verify("tables/orders", "human:ada", "2026-07-22T00:00:00Z"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal) || c.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public void Browse_reads_the_index_and_lists_prefixes_through_the_provider()
    {
        using var handle = new BundleHandle(Storage());
        var tools = new OkfBundleTools(handle);

        Assert.Equal("# tables\n\n* [Orders](orders.md) - One row per order.\n", tools.Browse("tables"));
        Assert.StartsWith("# notes/\n", tools.Browse("notes/"), StringComparison.Ordinal); // the heading echoes the path as given, as before
        Assert.Contains("notes/a", tools.Browse("notes"), StringComparison.Ordinal);
        Assert.StartsWith("# (bundle root)", tools.Browse(), StringComparison.Ordinal);
        Assert.StartsWith("# assets", tools.Browse("assets"), StringComparison.Ordinal); // a prefix with a non-.md object exists
        Assert.Equal("Error: path 'nope' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("nope"));
        Assert.Equal("Error: path 'tables/orders.md' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("tables/orders.md"));
    }

    [Fact]
    public void Browse_reads_an_index_as_strict_UTF8_and_the_live_index_not_the_cached_one()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        tools.GetBundle();

        storage.Add("tables/index.md", "# tables (rewritten behind the image)\n");
        Assert.Equal("# tables (rewritten behind the image)\n", tools.Browse("tables"));

        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        File.WriteAllBytes(Path.Combine(tmp.Path, "tables", "index.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        Assert.StartsWith("Error: ", new OkfBundleTools(tmp.Path).Browse("tables"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_directory_is_not_a_prefix_and_an_index_through_a_link_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var tools = new OkfBundleTools(tmp.Path);

        Assert.Equal("Error: path 'empty' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("empty"));
    }

    [Fact]
    public void ChangesSince_reads_every_log_through_the_provider()
    {
        var storage = Storage().Add("notes/log.md", "# Log\n\n## 2026-07-23\n\n* **Update**: Nested.\n");
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        var report = tools.ChangesSince("2026-07-01");

        Assert.Contains("## log.md\n", report, StringComparison.Ordinal);
        Assert.Contains("## notes/log.md\n", report, StringComparison.Ordinal);
        Assert.Contains("Bootstrapped.", report, StringComparison.Ordinal);
        Assert.Contains("Nested.", report, StringComparison.Ordinal);

        storage.FailReadOf("notes/log.md", new IOException("disk"));
        var skipped = tools.ChangesSince("2026-07-01");
        Assert.Contains("> Skipped notes/log.md (could not be read: I/O error).", skipped, StringComparison.Ordinal);
        Assert.Contains("Bootstrapped.", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void RegenerateIndexes_writes_through_the_handles_provider_and_refreshes_the_image()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        tools.GetBundle();

        var result = tools.RegenerateIndexes();

        // `assets` holds no .md, so it is referenced by the root index but gets none of its own.
        Assert.Equal("Regenerated 3 index file(s):\n- notes/index.md\n- tables/index.md\n- index.md\n", result);
        Assert.Contains(storage.Calls, c => c == "write index.md MustNotExist");
        Assert.Contains(storage.Calls, c => c == "write tables/index.md IfMatch");
        Assert.Equal(3, tools.GetBundle().IndexKeys.Count);
        Assert.Contains("* [assets](assets/index.md)", tools.Browse(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetComputation_reads_the_file_through_the_provider()
    {
        var storage = Storage()
            .Add("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/q.sql\nattester: { resource: att.py }\n---\n")
            .Add("references/q.sql", "SELECT 1\n")
            .Add("att.py", "def attest(**_):\n    return {}\n");
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        var rendered = tools.GetComputation("c/rev");

        Assert.Contains("File: references/q.sql\n\n```\nSELECT 1\n```\n", rendered, StringComparison.Ordinal);

        storage.FailReadOf("references/q.sql", new IOException("disk"));
        Assert.Contains("Error: computation file 'references/q.sql' could not be read: I/O error reading 'references/q.sql' (IOException)", tools.GetComputation("c/rev"), StringComparison.Ordinal);
    }
}
```

Then re-pin the four tests whose premise §5.2 changes (a reload that fails keeps the previous image; decision C18) and the two that D2 and the key-based read move (C12, C24):

In `tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs`, replace

```text
    [Fact]
    public async Task A_failed_load_does_not_put_the_bundle_path_in_the_model_message()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var provider = new OkfContextProvider(tools);

        // Load once so the failure happens on a RELOAD, then take the root away.
        tools.GetBundle();
        tools.InvalidateBundle();
        Directory.Delete(tmp.Path, recursive: true);

        var result = await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var text = Assert.Single(result.Messages!).Text;
        Assert.Contains("bundle unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tmp.Path, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sanitizing the model-facing message must not leave the HOST blind: the
    /// injected context was the only place that detail ever surfaced, so
    /// removing it there without a replacement channel would trade a leak for
    /// an undiagnosable failure.
    /// </summary>
    [Fact]
    public async Task A_failed_load_reports_the_real_exception_to_the_host()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var seen = new List<Exception>();
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { OnInternalError = seen.Add });

        tools.GetBundle();
        tools.InvalidateBundle();
        Directory.Delete(tmp.Path, recursive: true);

        await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var reported = Assert.Single(seen);
        Assert.Contains(tmp.Path, reported.Message, StringComparison.Ordinal);
    }
```

with

```csharp
    [Fact]
    public async Task A_failed_load_does_not_put_the_bundle_path_in_the_model_message()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var provider = new OkfContextProvider(tools);

        // Never loaded: the first load fails, and the failure is reported by category.
        Directory.Delete(tmp.Path, recursive: true);

        var result = await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var text = Assert.Single(result.Messages!).Text;
        Assert.Contains("bundle unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tmp.Path, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sanitizing the model-facing message must not leave the HOST blind: the
    /// injected context was the only place that detail ever surfaced, so
    /// removing it there without a replacement channel would trade a leak for
    /// an undiagnosable failure. For a reload that fails after a successful load
    /// the channel is the handle (spec §5.2, lot C): the previous image stays
    /// served, <see cref="BundleHandle.IsStale"/> says so and
    /// <see cref="BundleHandle.LastLoadError"/> carries the raw failure, path
    /// included; nothing reaches the model but the image it already had.
    /// </summary>
    [Fact]
    public async Task A_failed_reload_keeps_the_previous_image_and_reports_the_real_exception_on_the_handle()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var seen = new List<Exception>();
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { OnInternalError = seen.Add });

        tools.GetBundle();
        tools.InvalidateBundle();
        Directory.Delete(tmp.Path, recursive: true);

        var result = await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var text = Assert.Single(result.Messages!).Text;
        Assert.StartsWith("<okf-context", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tmp.Path, text, StringComparison.Ordinal);
        Assert.Empty(seen);
        Assert.True(tools.Handle.IsStale);
        // The library-authored message names no host path (spec §7); the raw cause does.
        Assert.StartsWith("I/O error while listing the bundle", tools.Handle.LastLoadError!.Message, StringComparison.Ordinal);
        Assert.Contains(tmp.Path, tools.Handle.LastLoadError.InnerException!.Message, StringComparison.Ordinal);
    }
```

and, in the same file (`Bundle_becoming_unavailable_after_a_prior_successful_load_is_caught_not_thrown`), replace

```text
        tools.GetBundle();
        Directory.Delete(tmp.Path, recursive: true);
        tools.InvalidateBundle();

        var result = await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var text = Assert.Single(result.Messages!).Text;
        Assert.StartsWith("bundle unavailable: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<okf-context", text, StringComparison.Ordinal);
    }
```

with

```csharp
        // Since lot C (spec §5.2) a reload that fails keeps the previous image served,
        // marked stale, so the injection below is built from it; this test keeps its
        // one purpose: nothing escapes ProvideAIContextAsync.
        tools.GetBundle();
        Directory.Delete(tmp.Path, recursive: true);
        tools.InvalidateBundle();

        var result = await provider.ProvideForTest(BuildInvokingContext("orders"), CancellationToken.None);

        var text = Assert.Single(result.Messages!).Text;
        Assert.StartsWith("<okf-context", text, StringComparison.Ordinal);
        Assert.True(tools.Handle.IsStale);
    }
```

In `tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs`, replace

```text
        tools.GetBundle(); // force initial load/cache
        Directory.Delete(tmp.Path, recursive: true);
        tools.InvalidateBundle();

        var result = tools.ValidateBundle();

        Assert.Contains("Error", result);
    }
```

with

```csharp
        tools.GetBundle(); // force initial load/cache
        Directory.Delete(tmp.Path, recursive: true);
        tools.InvalidateBundle();

        // Since lot C (spec §5.2) the reload that fails keeps the previous image served,
        // marked stale: the report is that image's, with the resources it can no longer
        // find, and nothing is thrown.
        var result = tools.ValidateBundle();

        Assert.Contains("concept(s)", result, StringComparison.Ordinal);
        Assert.True(tools.Handle.IsStale);
    }
```

In `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs`, replace

```text
        var dir = "sub" + LineSeparator + "FORGED";
        Directory.CreateDirectory(Path.Combine(tmp.Path, dir));

        var rendered = new OkfBundleTools(tmp.Path).Browse(dir);
```

with

```csharp
        var dir = "sub" + LineSeparator + "FORGED";
        // A concept inside: a directory is a prefix of the key space, which exists while an
        // object lies under it (spec §2, D2) -- an empty one is "not found" since lot C.
        tmp.Write(dir + "/a.md", "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n");

        var rendered = new OkfBundleTools(tmp.Path).Browse(dir);
```

and replace

```text
        Assert.DoesNotContain("OUTSIDE-THE-BUNDLE", result, StringComparison.Ordinal);
        Assert.Contains("Error: computation file 'x/y/secret.sql' could not be read: ", result, StringComparison.Ordinal);
```

with

```csharp
        Assert.DoesNotContain("OUTSIDE-THE-BUNDLE", result, StringComparison.Ordinal);
        // Refused at the resolution since lot C (the provider cannot list "x" once the
        // junction denies it, so the key is Missing) or at the read: either way nothing
        // outside the bundle is shown.
        Assert.Contains("Error: computation file 'x/y/secret.sql' could not be re", result, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `20 error CS1503: Argument 1: cannot convert from 'OKF4net.Providers.BundleHandle' to 'string'`, `10 error CS1061: 'OkfBundleTools' does not contain a definition for 'Handle'`, `4 error CS1061: 'OkfBundleTools' does not contain a definition for 'Location'`.

- [ ] **Step 3: The tool set over a handle**

In `src/OKF4net.Agents/OkfBundleTools.cs`, twelve edits.

(a) Replace

```text
    private readonly BundleConceptWriter _writer;

    private Bundle? _bundle;
```

with

```csharp
    private readonly BundleConceptWriter _writer;

    /// <summary>
    /// The image holder (spec §5.2): the one cache of this tool set, shared with any
    /// <c>OkfContextProvider</c> built over it and with whatever else the host hands the same
    /// handle to. Every read tool takes its <see cref="Bundle"/> from it through
    /// <see cref="GetBundle"/>; every write invalidates it (<see cref="BundleConceptWriter(BundleHandle, Action?)"/>).
    /// </summary>
    private readonly BundleHandle _handle;

    /// <summary>The root directory when the handle is over a <see cref="FileSystemBundleProvider"/> (the string the constructor was given, or that provider's <see cref="FileSystemBundleProvider.Root"/>); <see langword="null"/> otherwise.</summary>
    private readonly string? _root;
```

(b) Replace

```text
    internal OkfBundleTools(string bundleRoot, AttestationOrchestrator? orchestrator, IBundleProvider? writeProvider)
    {
        if (!Directory.Exists(bundleRoot))
        {
            throw new ArgumentException($"bundle root does not exist: {bundleRoot}", nameof(bundleRoot));
        }

        BundleRoot = bundleRoot;
        _orchestrator = orchestrator;

        _writer = writeProvider is null
            ? new BundleConceptWriter(bundleRoot, onWriteCommitted: () => _bundle = null)
            : new BundleConceptWriter(writeProvider, onWriteCommitted: () => _bundle = null);
        _writer.AutoStampGenerated = true;
        _writer.UtcNow = () => UtcNow();
    }

    /// <summary>The bundle's root directory, as passed to the constructor.</summary>
    public string BundleRoot { get; }
```

with

```csharp
    internal OkfBundleTools(string bundleRoot, AttestationOrchestrator? orchestrator, IBundleProvider? writeProvider)
        : this(
            Directory.Exists(bundleRoot)
                ? new BundleHandle(new FileSystemBundleProvider(bundleRoot))
                : throw new ArgumentException($"bundle root does not exist: {bundleRoot}", nameof(bundleRoot)),
            orchestrator,
            writeProvider)
    {
        _root = bundleRoot;
    }

    /// <summary>
    /// Creates the tool set over <paramref name="handle"/> (spec §5.2, §9): its image is the
    /// handle's, reloaded under the handle's refresh policy and after this tool set's own
    /// writes, and shared with every other consumer the host gives the same handle to — an
    /// <c>OkfContextProvider</c> built over this tool set uses it too. The write tools are
    /// offered only when the handle's provider can write (<see cref="GetTools(OkfToolMode)"/>).
    /// </summary>
    /// <param name="handle">The handle over the bundle's provider.</param>
    /// <param name="orchestrator">The attestation orchestrator to run §10.5 computations through, or <see langword="null"/> to leave attested-computation execution unwired.</param>
    public OkfBundleTools(BundleHandle handle, AttestationOrchestrator? orchestrator = null)
        : this(handle, orchestrator, writeProvider: null)
    {
    }

    private OkfBundleTools(BundleHandle handle, AttestationOrchestrator? orchestrator, IBundleProvider? writeProvider)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _root = (handle.Provider as FileSystemBundleProvider)?.Root;
        _orchestrator = orchestrator;

        _writer = writeProvider is null
            ? new BundleConceptWriter(handle)
            : new BundleConceptWriter(writeProvider, onWriteCommitted: handle.Invalidate, handle.Options);
        _writer.AutoStampGenerated = true;
        _writer.UtcNow = () => UtcNow();
    }

    /// <summary>The handle this tool set serves its image from (spec §5.2); give it to an <c>OkfContextProvider</c> or another tool set to share one image.</summary>
    public BundleHandle Handle => _handle;

    /// <summary>The bundle's displayable location (the root directory for the filesystem provider).</summary>
    public string Location => _handle.Location;

    /// <summary>The bundle's root directory: the string a root-based constructor was given, or the filesystem provider's root.</summary>
    /// <exception cref="NotSupportedException">The tool set is over another provider: a path has no meaning for it (spec §9).</exception>
    public string BundleRoot => _root
        ?? throw new NotSupportedException("this tool set is not over a filesystem provider, so it has no bundle root");
```

(c) Replace

```text
    /// <summary>
    /// Returns the loaded bundle, loading it from <see cref="BundleRoot"/> on
    /// first access and caching it thereafter until <see cref="InvalidateBundle"/>
    /// is called.
    /// </summary>
    internal Bundle GetBundle()
    {
        using (_writer.EnterWriteLock())
        {
            return _bundle ??= Bundle.Load(BundleRoot);
        }
    }

    /// <summary>
    /// Drops the cached bundle so the next <see cref="GetBundle"/> call
    /// reloads it from disk. Call after any write to <see cref="BundleRoot"/>.
    /// </summary>
    internal void InvalidateBundle()
    {
        using (_writer.EnterWriteLock())
        {
            _bundle = null;
        }
    }
```

with

```csharp
    /// <summary>
    /// Returns the handle's image (<see cref="BundleHandle.Current"/>): loaded on first
    /// access, reloaded under the handle's refresh policy and after <see cref="InvalidateBundle"/>.
    /// Taken under the write lock, as before the handle, so that a read never interleaves
    /// with this tool set's own writes.
    /// </summary>
    internal Bundle GetBundle()
    {
        using (_writer.EnterWriteLock())
        {
            return _handle.Current;
        }
    }

    /// <summary>
    /// Obliges the handle to reload at the next <see cref="GetBundle"/>
    /// (<see cref="BundleHandle.Invalidate"/>). Call after any write to the bundle.
    /// </summary>
    internal void InvalidateBundle()
    {
        using (_writer.EnterWriteLock())
        {
            _handle.Invalidate();
        }
    }
```

(d) In `GetTools(OkfToolMode mode)`, replace

```text
        return mode switch
        {
            OkfToolMode.ReadOnly =>
                tools.Where(t => !WriteToolNames.Contains(((AIFunction)t).Name)).ToList(),
```

with

```csharp
        // The effective permission to write is `provider.CanWrite && mode != ReadOnly`
        // (spec §3): over a provider that cannot write, the write tools are not offered,
        // by the same filtering as ReadOnly; the library's BundleReadOnlyException stays
        // the last rampart for a direct caller.
        if (!_handle.Provider.Capabilities.CanWrite)
        {
            mode = OkfToolMode.ReadOnly;
        }

        return mode switch
        {
            OkfToolMode.ReadOnly =>
                tools.Where(t => !WriteToolNames.Contains(((AIFunction)t).Name)).ToList(),
```

(e) In `Browse`, replace

```text
            var bundle = GetBundle();
            var fullDir = segments.Length == 0 ? bundle.Root : Path.Combine([bundle.Root, .. segments]);

            // Strict ancestor walk: Browse reads through fullDir, so a directory
            // whose link status cannot be read is refused like a link (a guard
            // fails closed -- see ReparsePoints.IsReparsePointOrUninspectable).
            if (!ReparsePoints.IsWithinBundleRoot(bundle.Root, fullDir)
                || !Directory.Exists(fullDir)
                || ReparsePoints.HasReparsePointOrUninspectableAncestor(bundle.Root, fullDir))
            {
                // Folded, same reason as the invalid-path line above: this is
                // the one Browse message an arbitrary string reaches, since
                // any path that does not resolve lands here. See OneLine.
                return $"Error: path '{OneLine(path ?? string.Empty)}' not found in the bundle. Use okf_browse to list available directories.";
            }

            var indexPath = Path.Combine(fullDir, IndexFilename);
            if (File.Exists(indexPath))
            {
                return File.ReadAllText(indexPath);
            }

            return BuildLevelListing(bundle, segments, relPath);
```

with

```csharp
            var bundle = GetBundle();

            // A directory is a prefix of the key space (spec §2): it exists while an object
            // lies under it, and its key must pass the load's policy. The provider answers
            // through its own guards -- a prefix through a junction, or one whose entry cannot
            // be inspected, is refused, never listed -- so every path that does not resolve
            // lands on the one not-found line below.
            BundleKey? prefix = null;
            if (segments.Length > 0
                && (!BundleKey.TryParse(string.Join('/', segments), out prefix, out _)
                    || !bundle.IsKeyAllowed(prefix, out _)
                    || !PrefixExists(bundle, prefix)))
            {
                // Folded, same reason as the invalid-path line above: this is
                // the one Browse message an arbitrary string reaches, since
                // any path that does not resolve lands here. See OneLine.
                return $"Error: path '{OneLine(path ?? string.Empty)}' not found in the bundle. Use okf_browse to list available directories.";
            }

            // The directory's index.md as it is now, read through the provider as strict
            // UTF-8 (spec §9; a non-UTF-8 index is an Error: like any unreadable object);
            // without one, a listing generated from the image.
            var indexKey = prefix is null ? BundleKey.Parse(IndexFilename) : prefix.Append(IndexFilename);
            try
            {
                return bundle.ReadResourceText(indexKey);
            }
            catch (FileNotFoundException)
            {
                return BuildLevelListing(bundle, segments, relPath);
            }
```

(f) Replace the whole body of `RegenerateIndexes`:

```text
    public string RegenerateIndexes()
    {
        return RunTool(() =>
        {
            IReadOnlyList<string> written;
            using (_writer.EnterWriteLock())
            {
                try
                {
                    written = IndexGenerator.RegenerateIndexes(BundleRoot);
                }
                finally
                {
                    // Also when the pass fails part-way (an unknown outcome among others): the
                    // indexes it did write are on disk and no notification reports them.
                    _bundle = null;
                }
            }

            if (written.Count == 0)
            {
                return "No index.md files were regenerated (empty bundle?).";
            }

            var relative = written
                .Select(p => Path.GetRelativePath(BundleRoot, p).Replace('\\', '/'))
                .ToList();

            var sb = new StringBuilder();
            sb.Append("Regenerated ").Append(relative.Count).Append(" index file(s):").Append('\n');
            foreach (var rel in relative)
            {
                // Same sink shape as AppendLogFileChanges's "## {rel}" and
                // "> Skipped {rel}": `rel` is a Path.GetRelativePath over a
                // bundle path (just above), and a directory name may carry a
                // line terminator. One index file, one "- " line.
                sb.Append("- ").Append(OneLine(rel)).Append('\n');
            }

            return sb.ToString();
        });
    }
```

with

```csharp
    public string RegenerateIndexes()
    {
        // The capability guard of every write entry point (spec §3), outside RunTool like
        // the writer's own: a read-only provider is a host mistake, not a content error.
        if (!_handle.Provider.Capabilities.CanWrite)
        {
            throw new BundleReadOnlyException(_handle.Provider.Id);
        }

        return RunTool(() =>
        {
            IReadOnlyList<BundleKey> written;
            using (_writer.EnterWriteLock())
            {
                try
                {
                    // The string overload's contract (what it skips silently, what it
                    // throws), through the handle's provider and options rather than a
                    // second provider built from the root: the output of this tool does
                    // not change, and a confinement boundary the host gave the provider
                    // still applies.
                    written = IndexGenerator.Regenerate(_handle.Provider, IndexGenerator.DefaultSynthesize, _handle.Options, legacy: true).Written;
                }
                finally
                {
                    // Also when the pass fails part-way (an unknown outcome among others): the
                    // indexes it did write are on disk and no notification reports them.
                    _handle.Invalidate();
                }
            }

            if (written.Count == 0)
            {
                return "No index.md files were regenerated (empty bundle?).";
            }

            var sb = new StringBuilder();
            sb.Append("Regenerated ").Append(written.Count).Append(" index file(s):").Append('\n');
            foreach (var key in written)
            {
                // Same sink shape as AppendLogFileChanges's "## {rel}" and
                // "> Skipped {rel}": a key is the '/'-joined path under the
                // root, and a directory name may carry a line terminator. One
                // index file, one "- " line.
                sb.Append("- ").Append(OneLine(key.ToString())).Append('\n');
            }

            return sb.ToString();
        });
    }
```

(g) In `ChangesSince`, replace

```text
            foreach (var logPath in bundle.LogFiles)
            {
                any |= AppendLogFileChanges(bundle.Root, logPath, date, notes, changes);
            }
```

with

```csharp
            foreach (var logKey in bundle.LogKeys)
            {
                any |= AppendLogFileChanges(bundle, logKey, date, notes, changes);
            }
```

(h) Replace

```text
    private static bool AppendLogFileChanges(string bundleRoot, string logPath, string date, StringBuilder notes, StringBuilder changes)
    {
        var rel = Path.GetRelativePath(bundleRoot, logPath).Replace('\\', '/');

        string text;
        try
        {
            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath));
        }
```

with

```csharp
    private static bool AppendLogFileChanges(Bundle bundle, BundleKey logKey, string date, StringBuilder notes, StringBuilder changes)
    {
        var rel = logKey.ToString();

        string text;
        try
        {
            // Through the provider, strict UTF-8, bounded (spec §6, §9): a missing, refused,
            // oversized or non-UTF-8 log is the skip note below, as the direct read's was.
            text = bundle.ReadResourceText(logKey);
        }
```

(i) In `GetComputation`, replace

```text
                case { Source: ComputationSource.File, Path.Length: > 0 } file:
                    if (!bundle.TryResolveResource(concept, file.Path!, out var absolutePath, out var status)
                        || status != ResourceResolutionStatus.Resolved)
                    {
```

with

```csharp
                case { Source: ComputationSource.File, Path.Length: > 0 } file:
                    if (!bundle.TryResolveResourceKey(concept, file.Path!, out var fileKey, out var status)
                        || status != ResourceResolutionStatus.Resolved)
                    {
```

(j) and replace

```text
                        // Guarded: TryResolveResource only establishes path
                        // safety, not readability -- the file may still fail
                        // to read (I/O error, or non-UTF-8 content), same
                        // lesson as AttestationOrchestrator.RunAsync's own
                        // file-computation step.
                        text = bundle.ReadResourceText(absolutePath!);
```

with

```csharp
                        // Guarded: TryResolveResourceKey only establishes that
                        // the key is allowed and exists, not readability -- the
                        // object may still fail to read (I/O error, refused path,
                        // non-UTF-8 content), same lesson as the orchestrator's
                        // own file-computation capture.
                        text = bundle.ReadResourceText(fileKey!);
```

(k) Before `BuildLevelListing`'s doc comment, replace

```text
    /// <summary>
    /// Builds the fallback listing for a bundle level with no <c>index.md</c>:
    /// the subdirectories and concepts found directly under
    /// <paramref name="segments"/>, derived from <see cref="Bundle.Concepts"/>.
    /// </summary>
    private static string BuildLevelListing(Bundle bundle, IReadOnlyList<string> segments, string relPath)
```

with

```csharp
    /// <summary>
    /// Whether an object lies under <paramref name="prefix"/> at the provider now (spec §2: a
    /// prefix exists while an object lives under it; an empty directory does not, D2). A
    /// listing the provider refuses — a junction or an entry that cannot be inspected on the
    /// prefix's path, the confinement boundary — or that fails is "not found": nothing is
    /// read through it, which is what the old strict ancestor walk refused too.
    /// </summary>
    private static bool PrefixExists(Bundle bundle, BundleKey prefix)
    {
        try
        {
            // Strictly under the prefix: an object stored under the prefix's own key (a
            // provider may list it) is not a directory, and the old Directory.Exists said so.
            var under = prefix + "/";
            return bundle.Provider.Enumerate(prefix, bundle.Options.Limits).Any(l => l.RawName.StartsWith(under, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the fallback listing for a bundle level with no <c>index.md</c>:
    /// the subdirectories and concepts found directly under
    /// <paramref name="segments"/>, derived from <see cref="Bundle.Concepts"/>.
    /// </summary>
    private static string BuildLevelListing(Bundle bundle, IReadOnlyList<string> segments, string relPath)
```

(l) The `_writer` field's documentation names the old cache twice; replace

```text
    /// than caching a lock object. Constructed with <c>onWriteCommitted:
    /// () =&gt; _bundle = null</c> so a successful write invalidates this
    /// instance's cache atomically with the write, from inside the shared
    /// lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write lock guards <see cref="_bundle"/> and every write this class
```

with

```csharp
    /// than caching a lock object. Built over <see cref="_handle"/>, so a
    /// successful write invalidates the handle's image atomically with the
    /// write, from inside the shared lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write lock guards the image (<see cref="_handle"/>) and every write this class
```

and replace

```text
    /// directly on disk. The per-instance <see cref="_bundle"/> CACHE
    /// deliberately stays instance-level: <see cref="AppendToConceptAtomic"/>
```

with

```csharp
    /// directly on disk. The image (<see cref="_handle"/>) is per handle, shared
    /// only with the consumers the host gives the same handle to: <see cref="AppendToConceptAtomic"/>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~OkfBundleToolsHandleTests"`
Expected: 0 warnings, 0 errors; `Passed: 12, Skipped: 0, Total: 12`.
Then the full suite: `dotnet test tests/OKF4net.Tests --no-build --filter "Category!=ContainerIntegration"` — expected `Passed: 3467, Skipped: 35, Total: 3502`. `grep -n "File\.\|Directory\.\|ReparsePoints" src/OKF4net.Agents/OkfBundleTools.cs | grep -v "///\|//"` prints the constructor's `Directory.Exists(bundleRoot)` line only.

- [ ] **Step 5: CHANGELOG and README**

In `CHANGELOG.md`, replace the first

```text
- **Finite default bounds on loading** (spec §6), out of reach of real
```

with

```markdown
- **`OkfBundleTools` serves its image from a `BundleHandle` and makes no
  direct I/O** (#139, lot C; spec §5.2, §9). `new OkfBundleTools(handle,
  orchestrator)` shares the handle's image with every consumer the host gives
  the same handle to (an `OkfContextProvider` built over the tool set uses it);
  `Handle` and `Location` are new; the root-based constructors build a handle
  of their own (`Manual` refresh, as the cache was) and `BundleRoot` throws
  `NotSupportedException` for a tool set over any other provider. What moved
  onto the provider, and what changed with it: `okf_browse` answers through
  the provider's listing and reads a directory's `index.md` through it as
  strict UTF-8 — an empty directory is now `not found` (a prefix exists while
  an object lies under it, spec §2, D2), an `index.md` that is not valid UTF-8
  is an `Error:` where it was read with replacement characters, and one
  reached through a symlink or junction is refused where it was read;
  `okf_changes_since` reads every `log.md` through the provider (the skip
  notes are unchanged); `okf_regenerate_indexes` regenerates through the
  handle's provider and options under the `string` overload's contract (same
  output, same skips and failures), so a confinement boundary the host gave
  the provider applies to it; `okf_get_computation` resolves and reads the
  computation file on keys (its `could not be read:` text now carries the
  library's message, `I/O error reading 'references/q.sql' (IOException)`,
  never a host path, and a file whose directory can no longer be listed is
  `could not be resolved (Missing)`). The write tools are offered only when
  the handle's provider can write (`provider.CanWrite && mode != ReadOnly`,
  spec §3): over a read-only provider `GetTools` filters them out whatever the
  mode, and a direct call throws `BundleReadOnlyException` (`okf_verify`,
  which runs inside the tool's error channel, returns it as `Error: the
  provider is read-only; …`). **A reload that fails after a successful load
  keeps the previous image served** (spec §5.2): `okf_validate_bundle` and
  the context provider used to answer `bundle unavailable` once the bundle
  root vanished after a load; they now serve the last image, `Handle.IsStale`
  is `true` and `Handle.LastLoadError` carries the failure (the raw cause,
  path included, as its inner exception); the first load failing still
  reports `bundle unavailable`.
- **Finite default bounds on loading** (spec §6), out of reach of real
```

In `README.md`, replace

```text
"Failures" remarks of `RegenerateIndexesWith` give the rest of what they throw.
```

with

```markdown
"Failures" remarks of `RegenerateIndexesWith` give the rest of what they throw.

### One image per source: `BundleHandle`

A `BundleHandle` (spec §5.2) holds one bundle's image: its provider, the
`BundleLoadOptions` every load uses, a refresh policy and the current `Bundle`
(`handle.Current`, loaded on first use). One load runs at a time — concurrent
callers wait for it instead of starting their own — and the image is replaced
as a whole, so a caller never sees two versions. A reload that fails (the
storage unreachable, a bound exceeded) keeps the previous image served:
`handle.IsStale` is `true` and `handle.LastLoadError` holds the failure (its
inner exception is the raw cause, for the host's log only); only the very
first load throws. `handle.Invalidate()` obliges the next use to reload
whatever the policy; the obligation is acknowledged only by a successful load
started after it. The refresh policies (`BundleRefreshPolicy`) are all lazy —
no timer, no thread: `Manual` (the default: the host calls `handle.Refresh()`),
`OnAccess` (the provider's version token is compared before every use),
`Every(interval)` (compared at most once per interval, at an access) and
`Notify` (a provider implementing `IBundleChangeNotifier` signals a possible
change; the next access compares the token — a signal is a hint, never a
reload by itself, and asking for `Notify` over a provider that cannot signal is
an `ArgumentException`).

Sharing is explicit: `new OkfBundleTools(handle)` and `new
BundleConceptWriter(handle)` work on that image (the writer invalidates it
after each committed write), an `OkfContextProvider` built over the tool set
uses the tool set's handle, and two consumers built from a root string
(`new OkfBundleTools(root)`) have two handles and two images. `tools.Handle`
is the handle a tool set serves from; `tools.Location` the provider's
displayable location. The image is replaced as a whole but its documents are
not frozen (the `Frontmatter` of today is mutable): modifying a document of a
shared image is not supported.
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/OkfBundleToolsHandleTests.cs tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs CHANGELOG.md README.md
git commit -F - <<'MSG'
feat(agents): OkfBundleTools serves its image from a BundleHandle and makes no direct I/O

A tool set is built over a handle (the root-based constructors build their
own, Manual) and shares its image with whoever holds the handle. Browse
lists prefixes and reads index.md through the provider, ChangesSince reads
every log.md through it, RegenerateIndexes regenerates through the handle's
provider under the string overload's contract, GetComputation resolves and
reads the computation file on keys. The write tools are offered only when
the provider can write. A reload that fails keeps the previous image served,
marked stale (spec §5.2).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 5: The observation registry; `okf_write_concept` writes only what the session read

**Files:**
- Create: `src/OKF4net.Agents/Internal/ObservationRegistry.cs`
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (after `_orchestrator`; `GetTools` bindings; `ReadConcept`; `WriteConcept`; `GetComputation`)
- Modify: `tests/OKF4net.Tests/Agents/AgentIntegrationTests.cs`, `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs` (a read before the write)
- Test: `tests/OKF4net.Tests/Agents/ObservationTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 4 (`Handle`, `GetBundle`, `DropBundleOnError`), lot B (`BundleConceptWriter.WriteConcept(string, string, string, BundleWriteCondition)`, its B29 refusal texts), lot A (`ConceptId.ToKey`, `Bundle.ObjectVersions`, `Concept.Key`).
- Produces:
  - `internal sealed class ObservationRegistry` (`OKF4net.Agents.Internal`): `ObservationRegistry(int capacity = DefaultCapacity)`, `const int DefaultCapacity = 10_000`, `Capacity`, `Count`, `RecordFullConcept(BundleKey, BundleObjectVersion)`, `RecordCode(BundleKey, BundleObjectVersion)`, `RecordAbsent(BundleKey)`, `Forget(BundleKey)`, `TryGetFullConcept(BundleKey, out BundleObjectVersion)`, `TryGetCode(BundleKey, out BundleObjectVersion)`, `IsAbsent(BundleKey)`.
  - `internal ObservationRegistry OkfBundleTools.Observations`.
  - `internal string OkfBundleTools.ReadConceptTool(string conceptId)`, `internal string GetComputationTool(string conceptId)`, `internal string WriteConceptTool(string conceptId, string frontmatterYaml, string body)` — the functions `GetTools()` binds as `okf_read_concept`, `okf_get_computation`, `okf_write_concept`; the public methods keep their behaviour and record nothing.
  - `private const string` description fields shared by the public methods and the wrappers (`ReadConceptDescription`, `ConceptIdDescription`, `WriteConceptDescription`, `WriteConceptIdDescription`, `FrontmatterYamlDescription`, `BodyDescription`, `GetComputationDescription`, `ComputationIdDescription`, `RunComputationDescription`, `ParameterValuesDescription`; the last two are used by Task 6).

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Agents/ObservationTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Agents.Internal;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// The observation registry and the write it conditions (spec §4, §5.4; D1, D5): a session
/// rewrites only what <c>okf_read_concept</c> showed it in full, at the version it was shown,
/// and creates only what that tool showed absent; a context injection, a host's direct call
/// of the public method, <c>okf_get_computation</c> and an evicted entry authorise nothing.
/// The exposed functions are reached through their internal wrappers
/// (<see cref="OkfBundleTools.ReadConceptTool"/>, <see cref="OkfBundleTools.WriteConceptTool"/>,
/// <see cref="OkfBundleTools.GetComputationTool"/>), which <see cref="OkfBundleTools.GetTools()"/>
/// binds, so the tests say exactly which boundary records what.
/// </summary>
public class ObservationTests
{
    private const string Fm = "type: Note\ntitle: A\ndescription: a.\n";
    private const string Doc = "---\n" + Fm + "---\n\nA.\n";
    private const string Refusal = "Error: 'notes/a' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static (InMemoryBundleProvider Storage, OkfBundleTools Tools) ToolsOver(params (string Key, string Text)[] objects)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        foreach (var (key, text) in objects)
        {
            storage.Add(key, text);
        }

        return (storage, new OkfBundleTools(new BundleHandle(storage)));
    }

    [Fact]
    public void The_registry_keeps_three_purposes_and_evicts_the_least_recently_used()
    {
        var registry = new ObservationRegistry(capacity: 2);
        var v1 = new BundleObjectVersion("1");
        var v2 = new BundleObjectVersion("2");

        registry.RecordFullConcept(K("a.md"), v1);
        registry.RecordCode(K("a.md"), v2);
        Assert.True(registry.TryGetFullConcept(K("a.md"), out var full) && full == v1);
        Assert.True(registry.TryGetCode(K("a.md"), out var code) && code == v2);
        Assert.False(registry.IsAbsent(K("a.md")));

        registry.RecordAbsent(K("a.md")); // clears both
        Assert.True(registry.IsAbsent(K("a.md")));
        Assert.False(registry.TryGetFullConcept(K("a.md"), out _));
        Assert.False(registry.TryGetCode(K("a.md"), out _));
        registry.RecordFullConcept(K("a.md"), v2); // clears absent
        Assert.False(registry.IsAbsent(K("a.md")));

        registry.RecordFullConcept(K("b.md"), v1);
        Assert.True(registry.TryGetFullConcept(K("a.md"), out _)); // a is now the most recent
        registry.RecordFullConcept(K("c.md"), v1); // evicts b, the least recently used
        Assert.Equal(2, registry.Count);
        Assert.False(registry.TryGetFullConcept(K("b.md"), out _));
        Assert.True(registry.TryGetFullConcept(K("a.md"), out _));

        registry.Forget(K("a.md"));
        Assert.False(registry.TryGetFullConcept(K("a.md"), out _));
        Assert.Equal(1, registry.Count);
        Assert.Equal(ObservationRegistry.DefaultCapacity, new ObservationRegistry().Capacity);
        Assert.Equal(10_000, ObservationRegistry.DefaultCapacity);
    }

    [Fact]
    public void A_rewrite_needs_a_full_read_and_a_committed_write_consumes_it()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));

        Assert.Contains("A.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        var written = tools.WriteConceptTool("notes/a", Fm, "Changed.\n");
        Assert.StartsWith("Written", written, StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/a.md IfMatch");

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed twice.\n"));
        Assert.Contains("Changed.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Changed twice.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_concept_changed_since_it_was_read_is_a_conflict_and_the_image_is_refreshed()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));
        tools.ReadConceptTool("notes/a");
        storage.Add("notes/a.md", "---\n" + Fm + "---\n\nChanged elsewhere.\n");

        var refused = tools.WriteConceptTool("notes/a", Fm, "Mine.\n");

        Assert.Equal("Error: 'notes/a' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.", refused);
        Assert.Contains("Changed elsewhere.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal); // the image was refreshed
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Mine.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_concept_shown_absent_can_be_created_and_a_deletion_seen_is_a_conflict_then_a_creation()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));

        Assert.Equal(Refusal.Replace("notes/a", "notes/b", StringComparison.Ordinal), tools.WriteConceptTool("notes/b", Fm, "B.\n"));
        Assert.Contains("not found", tools.ReadConceptTool("notes/b"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/b", Fm, "B.\n"), StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/b.md MustNotExist");

        tools.ReadConceptTool("notes/a");
        storage.Delete(K("notes/a.md"), BundleWriteCondition.None);
        Assert.StartsWith("Error: 'notes/a' changed since it was read, or no longer exists", tools.WriteConceptTool("notes/a", Fm, "Again.\n"), StringComparison.Ordinal);
        Assert.Contains("not found", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Again.\n"), StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/a.md MustNotExist");
    }

    [Fact]
    public void Neither_the_public_method_nor_get_computation_nor_a_context_injection_authorises_a_write()
    {
        var (_, tools) = ToolsOver(
            ("notes/a.md", Doc),
            ("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nattester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT 1\n```\n"),
            ("att.py", "def attest(**_):\n    return {}\n"));

        Assert.Contains("A.", tools.ReadConcept("notes/a"), StringComparison.Ordinal); // the public method, as a host calls it
        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
        Assert.Equal(0, tools.Observations.Count);

        Assert.Contains("SELECT 1", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Equal(Refusal.Replace("notes/a", "c/rev", StringComparison.Ordinal), tools.WriteConceptTool("c/rev", "type: Attested Computation\ntitle: R\ndescription: r.\n", "Rewritten.\n"));

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
    }

    /// <summary>D5: a concept the context provider injects, whole or truncated, is not an observation.</summary>
    [Fact]
    public async Task A_context_injection_is_never_an_observation()
    {
        var (_, tools) = ToolsOver(("notes/a.md", Doc));
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { TokenBudget = 100_000 });

        var context = await provider.ProvideForTest(InvokingContext("a"), CancellationToken.None);

        var injected = Assert.Single(context.Messages!).Text;
        Assert.Contains("A.", injected, StringComparison.Ordinal); // the whole concept was injected
        Assert.Equal(0, tools.Observations.Count);
        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
    }

    [Fact]
    public void Another_tool_sets_read_does_not_replace_this_sessions_observation()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("notes/a.md", Doc);
        using var handle = new BundleHandle(storage);
        var first = new OkfBundleTools(handle);
        var second = new OkfBundleTools(handle);

        first.ReadConceptTool("notes/a");
        storage.Add("notes/a.md", "---\n" + Fm + "---\n\nv2.\n");
        handle.Invalidate();
        Assert.Contains("v2.", second.ReadConceptTool("notes/a"), StringComparison.Ordinal);

        Assert.StartsWith("Error: 'notes/a' changed since it was read", first.WriteConceptTool("notes/a", Fm, "From first.\n"), StringComparison.Ordinal);
        Assert.StartsWith("Written", second.WriteConceptTool("notes/a", Fm, "From second.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_observation_evicted_by_ten_thousand_later_reads_authorises_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("n/c0.md", Doc);
        for (var i = 1; i <= ObservationRegistry.DefaultCapacity; i++)
        {
            storage.Add($"n/c{i}.md", Doc);
        }

        var tools = new OkfBundleTools(new BundleHandle(storage));
        tools.ReadConceptTool("n/c0");
        for (var i = 1; i <= ObservationRegistry.DefaultCapacity; i++)
        {
            tools.ReadConceptTool($"n/c{i}");
        }

        Assert.Equal(ObservationRegistry.DefaultCapacity, tools.Observations.Count);
        Assert.StartsWith("Error: 'n/c0' has not been read by this session", tools.WriteConceptTool("n/c0", Fm, "Changed.\n"), StringComparison.Ordinal);
        tools.ReadConceptTool("n/c0");
        Assert.StartsWith("Written", tools.WriteConceptTool("n/c0", Fm, "Changed.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_exposed_functions_are_the_recording_ones_and_keep_their_descriptions()
    {
        var (_, tools) = ToolsOver(("notes/a.md", Doc));
        var functions = tools.GetTools().Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);

        Assert.Equal(nameof(OkfBundleTools.ReadConceptTool), functions["okf_read_concept"].UnderlyingMethod!.Name);
        Assert.Equal(nameof(OkfBundleTools.WriteConceptTool), functions["okf_write_concept"].UnderlyingMethod!.Name);
        Assert.Equal(nameof(OkfBundleTools.GetComputationTool), functions["okf_get_computation"].UnderlyingMethod!.Name);
        Assert.Equal(AIFunctionFactory.Create(tools.ReadConcept, "okf_read_concept").JsonSchema.GetRawText(), functions["okf_read_concept"].JsonSchema.GetRawText());
        Assert.Equal(AIFunctionFactory.Create(tools.WriteConcept, "okf_write_concept").JsonSchema.GetRawText(), functions["okf_write_concept"].JsonSchema.GetRawText());
        Assert.Equal(AIFunctionFactory.Create(tools.GetComputation, "okf_get_computation").Description, functions["okf_get_computation"].Description);
    }

    private static AIContextProvider.InvokingContext InvokingContext(string userMessageText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var aiContext = new AIContext { Messages = [new ChatMessage(ChatRole.User, userMessageText)] };
#pragma warning disable MAAI001 // InvokingContext's public ctor is marked [Experimental].
        return new AIContextProvider.InvokingContext(agent, session: null, aiContext);
#pragma warning restore MAAI001
    }
}
```

Two existing tests write through the exposed `okf_write_concept` without a read (C24); give each the read first. In `tests/OKF4net.Tests/Agents/AgentIntegrationTests.cs`, replace

```text
        var scriptedClient = new ScriptedChatClient(
        [
            ScriptStep.Call("okf_write_concept", new Dictionary<string, object?>
            {
                ["conceptId"] = "tables/refunds",
```

with

```csharp
        var scriptedClient = new ScriptedChatClient(
        [
            // Read first (spec §5.4, D1): okf_write_concept creates only a concept this
            // session has seen absent, and rewrites only one it has read in full.
            ScriptStep.Call("okf_read_concept", new Dictionary<string, object?> { ["conceptId"] = "tables/refunds" }),
            ScriptStep.Call("okf_write_concept", new Dictionary<string, object?>
            {
                ["conceptId"] = "tables/refunds",
```

then replace

```text
        // Four round-trips to the scripted "model": the three tool-call
        // turns above, plus the final plain-text-answer turn.
        Assert.Equal(4, scriptedClient.TurnsTaken);
```

with

```csharp
        // Five round-trips to the scripted "model": the four tool-call
        // turns above (the read that precedes the write included), plus the
        // final plain-text-answer turn.
        Assert.Equal(5, scriptedClient.TurnsTaken);
```

then replace

```text
        // The scripted client observed the real FunctionResultContent for
        // each of the three tool calls, in order -- proof the framework's
        // pipeline round-tripped genuine tool output back to the "model".
        Assert.Equal(3, scriptedClient.ObservedFunctionResults.Count);
        Assert.Contains("Written", scriptedClient.ObservedFunctionResults[0]);
```

with

```csharp
        // The scripted client observed the real FunctionResultContent for
        // each of the four tool calls, in order -- proof the framework's
        // pipeline round-tripped genuine tool output back to the "model".
        Assert.Equal(4, scriptedClient.ObservedFunctionResults.Count);
        Assert.Contains("not found", scriptedClient.ObservedFunctionResults[0]);
        Assert.Contains("Written", scriptedClient.ObservedFunctionResults[1]);
```

then replace `        // ObservedFunctionResults[1] (okf_regenerate_indexes's result) is` with `        // ObservedFunctionResults[2] (okf_regenerate_indexes's result) is`, replace `        Assert.Contains("✓ conformant", scriptedClient.ObservedFunctionResults[2]);` with `        Assert.Contains("✓ conformant", scriptedClient.ObservedFunctionResults[3]);`, and replace `        Assert.DoesNotContain("✗", scriptedClient.ObservedFunctionResults[2]);` with `        Assert.DoesNotContain("✗", scriptedClient.ObservedFunctionResults[3]);`.

In `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs`, replace

```text
            var write = await client.CallToolAsync(
                "okf_write_concept",
                new Dictionary<string, object?>
                {
                    ["conceptId"] = "notes/test",
```

with

```csharp
            // Read first (spec §5.4, D1): the session must have seen the concept absent
            // before okf_write_concept creates it.
            var absent = await client.CallToolAsync(
                "okf_read_concept",
                new Dictionary<string, object?> { ["conceptId"] = "notes/test" });
            Assert.Contains("not found", ResultText(absent));

            var write = await client.CallToolAsync(
                "okf_write_concept",
                new Dictionary<string, object?>
                {
                    ["conceptId"] = "notes/test",
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `36 error CS1061: 'OkfBundleTools' does not contain a definition for 'WriteConceptTool'`, `24 … 'ReadConceptTool'`, `10 error CS0103: The name 'ObservationRegistry' does not exist in the current context`, `6 … 'Observations'`, `4 error CS0246: The type or namespace name 'ObservationRegistry' could not be found`, `2 … 'GetComputationTool'`.

- [ ] **Step 3: The registry and the wrappers**

Create `src/OKF4net.Agents/Internal/ObservationRegistry.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Agents.Internal;

/// <summary>
/// What one session was shown, and to what end (spec §5.4, D1): per key, the version shown
/// as a <b>full concept</b> (<c>okf_read_concept</c>), the version shown as <b>code</b>
/// (<c>okf_get_computation</c>, for the concept and for its computation file), or that the
/// key was shown <b>absent</b> (<c>okf_read_concept</c> answered not found). A full-concept
/// observation authorises a rewrite and the run of an inline computation; a code observation
/// the run only; an absent one a creation. A new observation of the same purpose replaces the
/// previous one; an absent observation clears both others and either of them clears absent.
/// <para><b>Bounded</b> to <see cref="Capacity"/> keys, the least recently used evicted first
/// (a record or a lookup makes a key the most recent); an evicted entry is gone, and the
/// action it would have authorised is refused until a new read. The registry is per tool
/// set: it is not shared through the handle, nor with another tool set, nor with a context
/// provider, and it survives every refresh of the image. Thread-safe.</para>
/// </summary>
internal sealed class ObservationRegistry
{
    /// <summary>The bound of spec §5.4: 10 000 keys.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<BundleKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new(); // most recently used first

    /// <summary>Creates a registry bounded to <paramref name="capacity"/> keys.</summary>
    public ObservationRegistry(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The bound on the number of keys.</summary>
    public int Capacity { get; }

    /// <summary>The number of keys observed and not evicted.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Records that the whole concept under <paramref name="key"/> was shown at <paramref name="version"/>.</summary>
    public void RecordFullConcept(BundleKey key, BundleObjectVersion version)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.FullConcept = version;
            entry.Absent = false;
        }
    }

    /// <summary>Records that the code under <paramref name="key"/> (a concept's contract and code, or a computation file) was shown at <paramref name="version"/>.</summary>
    public void RecordCode(BundleKey key, BundleObjectVersion version)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.Code = version;
            entry.Absent = false;
        }
    }

    /// <summary>Records that <paramref name="key"/> was shown absent.</summary>
    public void RecordAbsent(BundleKey key)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.Absent = true;
            entry.FullConcept = null;
            entry.Code = null;
        }
    }

    /// <summary>Drops every observation of <paramref name="key"/>: what the session saw no longer exists under that version.</summary>
    public void Forget(BundleKey key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var node))
            {
                _order.Remove(node);
            }
        }
    }

    /// <summary>The version the whole concept was shown at, if it was.</summary>
    public bool TryGetFullConcept(BundleKey key, out BundleObjectVersion version) => TryGet(key, static e => e.FullConcept, out version);

    /// <summary>The version the code was shown at, if it was.</summary>
    public bool TryGetCode(BundleKey key, out BundleObjectVersion version) => TryGet(key, static e => e.Code, out version);

    /// <summary>Whether the key was shown absent (and not shown since).</summary>
    public bool IsAbsent(BundleKey key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return false;
            }

            MoveToFront(node);
            return node.Value.Absent;
        }
    }

    private bool TryGet(BundleKey key, Func<Entry, BundleObjectVersion?> read, out BundleObjectVersion version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node) && read(node.Value) is { } observed)
            {
                MoveToFront(node);
                version = observed;
                return true;
            }

            version = default;
            return false;
        }
    }

    /// <summary>The entry of <paramref name="key"/>, made the most recent — created if needed, evicting the least recent past the capacity. Under the lock.</summary>
    private Entry Touch(BundleKey key)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            MoveToFront(node);
            return node.Value;
        }

        if (_entries.Count >= Capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }

        var entry = new Entry(key);
        node = _order.AddFirst(entry);
        _entries[key] = node;
        return entry;
    }

    private void MoveToFront(LinkedListNode<Entry> node)
    {
        if (!ReferenceEquals(_order.First, node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private sealed class Entry(BundleKey key)
    {
        public BundleKey Key { get; } = key;

        public BundleObjectVersion? FullConcept { get; set; }

        public BundleObjectVersion? Code { get; set; }

        public bool Absent { get; set; }
    }
}
```

In `src/OKF4net.Agents/OkfBundleTools.cs`, nine edits.

(a) Replace

```text
    private readonly AttestationOrchestrator? _orchestrator;
```

with

```csharp
    private readonly AttestationOrchestrator? _orchestrator;

    /// <summary>
    /// What this tool set's session was shown, and to what end (spec §5.4, D1): recorded only
    /// by the functions exposed to the model (<c>okf_read_concept</c>, <c>okf_get_computation</c>),
    /// once they have produced their complete result — never by the public
    /// <see cref="ReadConcept"/> and <see cref="GetComputation"/> a host or an
    /// <c>OkfContextProvider</c> calls (D5: an injection is not an observation). Consulted by
    /// <c>okf_write_concept</c> and <c>okf_run_computation</c>. Per instance: not shared
    /// through the handle, and it survives every refresh of the image.
    /// </summary>
    private readonly ObservationRegistry _observations = new();

    /// <summary>The session's observation registry; a test seam.</summary>
    internal ObservationRegistry Observations => _observations;

    private const string ReadConceptDescription = "Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.";
    private const string ConceptIdDescription = "The concept id, e.g. 'tables/orders'.";
    private const string WriteConceptDescription = "Create or update a concept document. The frontmatter must contain non-empty type, title and description (producer-grade validation is enforced before writing).";
    private const string WriteConceptIdDescription = "The concept id (path without .md), e.g. 'tables/refunds'.";
    private const string FrontmatterYamlDescription = "Frontmatter as 'key: value' lines (YAML subset).";
    private const string BodyDescription = "The markdown body.";
    private const string GetComputationDescription = "Read an Attested Computation's §10 contract (runtime, parameters, executor, attester) and its sanctioned computation source (inline code, or the text of a referenced file).";
    private const string ComputationIdDescription = "The concept id, e.g. 'computations/monthly-revenue'.";
    private const string RunComputationDescription = "Run an Attested Computation (§10.5: bind, execute, attest, gate on staleness) via the configured attestation runtime, and return the resulting outcome (displayable, verdict, receipt, reasons).";
    private const string ParameterValuesDescription = "Parameter values for this run, by name (§10.3: values only, never computation code).";
```

(b) In `GetTools(OkfToolMode mode)`, replace `            AIFunctionFactory.Create(ReadConcept, "okf_read_concept"),` with `            AIFunctionFactory.Create(ReadConceptTool, "okf_read_concept"),`; replace `            AIFunctionFactory.Create(WriteConcept, "okf_write_concept"),` with `            AIFunctionFactory.Create(WriteConceptTool, "okf_write_concept"),`; replace `            AIFunctionFactory.Create(GetComputation, "okf_get_computation"),` with `            AIFunctionFactory.Create(GetComputationTool, "okf_get_computation"),`.

(c) Replace the head of `ReadConcept`:

```text
    [Description("Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.")]
    public string ReadConcept([Description("The concept id, e.g. 'tables/orders'.")] string conceptId)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

            var sb = new StringBuilder();
```

with

```csharp
    [Description(ReadConceptDescription)]
    public string ReadConcept([Description(ConceptIdDescription)] string conceptId) => ReadConceptCore(conceptId, out _, out _);

    /// <summary>
    /// <c>okf_read_concept</c>: <see cref="ReadConcept"/>, then the observation (spec §5.4):
    /// the whole concept shown at its version, or the key shown absent. Recorded here, at the
    /// presentation boundary, never in the public method a host or an <c>OkfContextProvider</c>
    /// calls (D5). Internal so that tests reach the exposed function without the AI plumbing.
    /// </summary>
    [Description(ReadConceptDescription)]
    internal string ReadConceptTool([Description(ConceptIdDescription)] string conceptId)
    {
        var result = ReadConceptCore(conceptId, out var key, out var version);
        if (key is not null)
        {
            if (version is { } shown)
            {
                _observations.RecordFullConcept(key, shown);
            }
            else
            {
                _observations.RecordAbsent(key);
            }
        }

        return result;
    }

    /// <summary>
    /// <see cref="ReadConcept"/>, reporting what the rendering showed: <paramref name="key"/>
    /// with its <paramref name="version"/> when the whole concept was rendered, the
    /// <paramref name="key"/> alone when the id parsed but no concept exists under it (shown
    /// absent), neither otherwise (an invalid id, a bundle that failed to load).
    /// </summary>
    private string ReadConceptCore(string conceptId, out BundleKey? key, out BundleObjectVersion? version)
    {
        key = null;
        version = null;
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        BundleKey? shownKey = null;
        BundleObjectVersion? shownVersion = null;
        var result = RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id))
            {
                return ConceptNotFoundMessage(conceptId);
            }

            if (bundle.Get(id) is not { } concept)
            {
                shownKey = id.ToKey();
                return ConceptNotFoundMessage(conceptId);
            }

            var conceptKey = concept.Key ?? id.ToKey();
            if (bundle.ObjectVersions.TryGetValue(conceptKey, out var objectVersion))
            {
                shownKey = conceptKey;
                shownVersion = objectVersion;
            }

            var sb = new StringBuilder();
```

(d) Replace the tail of `ReadConcept`:

```text
                sb.Append(_orchestrator is not null
                    ? "(Use okf_get_computation for the full computation source; okf_run_computation to run it.)"
                    : "(Use okf_get_computation for the full computation source.)").Append('\n');
            }

            return sb.ToString();
        });
    }
```

with

```csharp
                sb.Append(_orchestrator is not null
                    ? "(Use okf_get_computation for the full computation source; okf_run_computation to run it.)"
                    : "(Use okf_get_computation for the full computation source.)").Append('\n');
            }

            return sb.ToString();
        });
        key = shownKey;
        version = shownVersion;
        return result;
    }
```

(e) Replace `WriteConcept`:

```text
    [Description("Create or update a concept document. The frontmatter must contain non-empty type, title and description (producer-grade validation is enforced before writing).")]
    public string WriteConcept(
        [Description("The concept id (path without .md), e.g. 'tables/refunds'.")] string conceptId,
        [Description("Frontmatter as 'key: value' lines (YAML subset).")] string frontmatterYaml,
        [Description("The markdown body.")] string body) =>
        DropBundleOnError(_writer.WriteConcept(conceptId, frontmatterYaml, body));
```

with

```csharp
    [Description(WriteConceptDescription)]
    public string WriteConcept(
        [Description(WriteConceptIdDescription)] string conceptId,
        [Description(FrontmatterYamlDescription)] string frontmatterYaml,
        [Description(BodyDescription)] string body) =>
        DropBundleOnError(_writer.WriteConcept(conceptId, frontmatterYaml, body));

    /// <summary>
    /// <c>okf_write_concept</c>: <see cref="WriteConcept"/> conditioned on what this session
    /// observed (spec §4, §5.4, D1). A whole-concept observation at version v writes with
    /// <see cref="BundleWriteCondition.IfMatch"/>; an absent observation creates with
    /// <see cref="BundleWriteCondition.MustNotExist"/>; anything else — never read in full,
    /// only shown by <c>okf_get_computation</c>, injected by a context provider (D5), or
    /// evicted from the registry — is refused with no write, whether the concept exists or
    /// not. A condition that does not hold is the writer's refusal (the concept changed, or no
    /// longer exists, since it was read; read it again), and the image is refreshed. A
    /// committed write drops the observation: what the session saw is no longer what the
    /// bundle holds, so the next rewrite reads first. An id that does not parse goes to the
    /// writer, whose refusal is unchanged.
    /// </summary>
    [Description(WriteConceptDescription)]
    internal string WriteConceptTool(
        [Description(WriteConceptIdDescription)] string conceptId,
        [Description(FrontmatterYamlDescription)] string frontmatterYaml,
        [Description(BodyDescription)] string body)
    {
        if (!ConceptId.TryParse(conceptId, out var id))
        {
            return WriteConcept(conceptId, frontmatterYaml, body);
        }

        var key = id.ToKey();
        BundleWriteCondition condition;
        if (_observations.TryGetFullConcept(key, out var observed))
        {
            condition = BundleWriteCondition.IfMatch(observed);
        }
        else if (_observations.IsAbsent(key))
        {
            condition = BundleWriteCondition.MustNotExist;
        }
        else
        {
            return $"Error: '{id}' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.";
        }

        var result = DropBundleOnError(_writer.WriteConcept(conceptId, frontmatterYaml, body, condition));
        if (!result.StartsWith("Error:", StringComparison.Ordinal))
        {
            _observations.Forget(key);
        }

        return result;
    }
```

(f) Replace the head of `GetComputation`:

```text
    [Description("Read an Attested Computation's §10 contract (runtime, parameters, executor, attester) and its sanctioned computation source (inline code, or the text of a referenced file).")]
    public string GetComputation([Description("The concept id, e.g. 'computations/monthly-revenue'.")] string conceptId)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

```

with

```csharp
    [Description(GetComputationDescription)]
    public string GetComputation([Description(ComputationIdDescription)] string conceptId) => GetComputationCore(conceptId, out _, out _, out _, out _);

    /// <summary>
    /// <c>okf_get_computation</c>: <see cref="GetComputation"/>, then the code observation
    /// (spec §5.4) — the concept (contract and code) at its version and, for a file
    /// computation, the file at its version — recorded only when the rendering showed the
    /// code: a file that could not be resolved or read records nothing. Never by the public
    /// method (D5). Internal so that tests reach the exposed function without the AI plumbing.
    /// </summary>
    [Description(GetComputationDescription)]
    internal string GetComputationTool([Description(ComputationIdDescription)] string conceptId)
    {
        var result = GetComputationCore(conceptId, out var conceptKey, out var conceptVersion, out var fileKey, out var fileVersion);
        if (conceptKey is not null && conceptVersion is { } shown)
        {
            _observations.RecordCode(conceptKey, shown);
            if (fileKey is not null && fileVersion is { } shownFile)
            {
                _observations.RecordCode(fileKey, shownFile);
            }
        }

        return result;
    }

    /// <summary>
    /// <see cref="GetComputation"/>, reporting what the rendering showed when it showed the
    /// code: the concept's key and version, and the computation file's when the code came
    /// from one; all <see langword="null"/> otherwise.
    /// </summary>
    private string GetComputationCore(string conceptId, out BundleKey? conceptKey, out BundleObjectVersion? conceptVersion, out BundleKey? fileKey, out BundleObjectVersion? fileVersion)
    {
        conceptKey = null;
        conceptVersion = null;
        fileKey = null;
        fileVersion = null;
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        BundleKey? shownConcept = null;
        BundleObjectVersion? shownConceptVersion = null;
        BundleKey? shownFile = null;
        BundleObjectVersion? shownFileVersion = null;
        var result = RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

```

(g) In its `switch`, replace

```text
                case { Source: ComputationSource.File, Path.Length: > 0 } file:
                    if (!bundle.TryResolveResourceKey(concept, file.Path!, out var fileKey, out var status)
                        || status != ResourceResolutionStatus.Resolved)
                    {
```

with

```csharp
                case { Source: ComputationSource.File, Path.Length: > 0 } file:
                    if (!bundle.TryResolveResourceKey(concept, file.Path!, out var resolvedKey, out var status)
                        || status != ResourceResolutionStatus.Resolved)
                    {
```

(h) and replace the rest of the switch and the method's end:

```text
                    string text;
                    try
                    {
                        // Guarded: TryResolveResourceKey only establishes that
                        // the key is allowed and exists, not readability -- the
                        // object may still fail to read (I/O error, refused path,
                        // non-UTF-8 content), same lesson as the orchestrator's
                        // own file-computation capture.
                        text = bundle.ReadResourceText(fileKey!);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
                    {
                        sb.Append("Error: computation file '").Append(OneLine(file.Path!)).Append("' could not be read: ").Append(OneLine(e.Message)).Append('\n');
                        break;
                    }

                    sb.Append("File: ").Append(OneLine(file.Path!)).Append('\n').Append('\n');
                    sb.Append("```\n").Append(text.TrimEnd('\n')).Append('\n').Append("```\n");
                    break;

                case { Source: ComputationSource.Inline, InlineCode.Length: > 0 } inline:
                    sb.Append("```\n").Append(inline.InlineCode!.TrimEnd('\n')).Append('\n').Append("```\n");
                    break;

                default:
                    sb.Append(NoneLine).Append('\n');
                    break;
            }

            return sb.ToString();
        });
    }
```

with

```csharp
                    string text;
                    BundleObjectVersion readVersion;
                    try
                    {
                        // Guarded: TryResolveResourceKey only establishes that
                        // the key is allowed and exists, not readability -- the
                        // object may still fail to read (I/O error, refused path,
                        // non-UTF-8 content), same lesson as the orchestrator's
                        // own file-computation capture.
                        text = bundle.ReadResourceText(resolvedKey!, null, out readVersion);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
                    {
                        sb.Append("Error: computation file '").Append(OneLine(file.Path!)).Append("' could not be read: ").Append(OneLine(e.Message)).Append('\n');
                        break;
                    }

                    sb.Append("File: ").Append(OneLine(file.Path!)).Append('\n').Append('\n');
                    sb.Append("```\n").Append(text.TrimEnd('\n')).Append('\n').Append("```\n");
                    shownFile = resolvedKey;
                    shownFileVersion = readVersion;
                    Shown();
                    break;

                case { Source: ComputationSource.Inline, InlineCode.Length: > 0 } inline:
                    sb.Append("```\n").Append(inline.InlineCode!.TrimEnd('\n')).Append('\n').Append("```\n");
                    Shown();
                    break;

                default:
                    sb.Append(NoneLine).Append('\n');
                    break;
            }

            return sb.ToString();

            // The code was shown: the concept (contract and code) at the version of this image.
            void Shown()
            {
                var conceptKeyShown = concept.Key ?? id.ToKey();
                if (bundle.ObjectVersions.TryGetValue(conceptKeyShown, out var objectVersion))
                {
                    shownConcept = conceptKeyShown;
                    shownConceptVersion = objectVersion;
                }
            }
        });
        conceptKey = shownConcept;
        conceptVersion = shownConceptVersion;
        fileKey = shownFile;
        fileVersion = shownFileVersion;
        return result;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~ObservationTests|FullyQualifiedName~AgentIntegrationTests|FullyQualifiedName~BundleHandleTests"`
Expected: 0 warnings, 0 errors; `Passed: 22, Skipped: 0, Total: 22` (10 observation tests, the integration test, the 11 handle tests).
Then the full suite: `dotnet test tests/OKF4net.Tests --no-build --filter "Category!=ContainerIntegration"` — expected `Passed: 3476, Skipped: 35, Total: 3511`.

- [ ] **Step 5: CHANGELOG**

In `CHANGELOG.md`, replace the first

```text
- **Finite default bounds on loading** (spec §6), out of reach of real
```

with

```markdown
- **`okf_write_concept` writes only what the session has read** (#139,
  lot C; spec §4, §5.4; decisions D1, D5). Each `OkfBundleTools` keeps an
  observation registry of what its session was shown and to what end:
  `okf_read_concept` records the whole concept at its version (or the id
  shown absent), `okf_get_computation` records the concept's contract and
  code, and the computation file, at their versions. `okf_write_concept`
  rewrites a concept read in full with `IfMatch(version)` and creates one
  shown absent with `MustNotExist`; otherwise it refuses with no write —
  `Error: 'x' has not been read by this session; read it first with
  okf_read_concept (a context block injected for you, or okf_get_computation,
  does not count), then write it.` — whether the concept exists or not. A
  concept that changed, or vanished, since it was read is the writer's
  conflict (`Error: 'x' changed since it was read, or no longer exists; the
  write was refused. Read it again before writing it.`) and the image is
  refreshed; reading it again (`not found` included) re-authorises the write.
  A committed write drops the observation, so a second rewrite reads first.
  The registry is per tool set (never shared through the handle, nor with
  another tool set or a context provider), survives every refresh, and holds
  10 000 keys, the least recently used evicted first; an evicted entry
  authorises nothing until a new read. Only the functions exposed to the
  model record: the public `ReadConcept` and `GetComputation` a host calls
  record nothing, and a concept `OkfContextProvider` injects, whole or
  truncated, is not an observation (D5). An agent that wrote without reading
  first now makes one more call; the refusal says which. The tool names,
  descriptions and schemas are unchanged.
- **Finite default bounds on loading** (spec §6), out of reach of real
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net.Agents/Internal/ObservationRegistry.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/ObservationTests.cs tests/OKF4net.Tests/Agents/AgentIntegrationTests.cs tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net.Agents/Internal/ObservationRegistry.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/ObservationTests.cs tests/OKF4net.Tests/Agents/AgentIntegrationTests.cs tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(agents): observation registry; okf_write_concept conditioned on what the session read

ObservationRegistry keeps, per tool set, what the model was shown and to
what end (full concept, code, absent), bounded to 10 000 keys LRU. The
functions exposed as okf_read_concept and okf_get_computation record at the
presentation boundary; the public methods record nothing (D5). The exposed
okf_write_concept rewrites with IfMatch on a full-concept observation,
creates with MustNotExist on an absent one, and refuses otherwise.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 6: `okf_run_computation` runs only what the session was shown, captured once and pinned

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`RunComputationAsync` head, its orchestrator call, the `GetTools` binding, a `GuardObserved` helper before `AppendLogFileChanges`)
- Modify: `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs` (three tests), `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs` (two tests)
- Test: `tests/OKF4net.Tests/Agents/RunComputationObservationTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 3 (`AttestationOrchestrator.Capture`, `RunCapturedAsync`, `AttestationCaptureResult`), Task 5 (`_observations`, `GetComputationTool`, `ReadConceptTool`, the description constants), Task 1's `NotifyingBundleProvider`.
- Produces: `internal Task<string> OkfBundleTools.RunComputationToolAsync(string conceptId, IReadOnlyDictionary<string, object?> parameterValues, CancellationToken cancellationToken = default)` — what `GetTools()` binds as `okf_run_computation`; `public Task<string> RunComputationAsync(…)` keeps its signature and behaviour (ungated) and also recovers after a refused pin; `private string? GuardObserved(Bundle, Concept, out BundleObjectVersion? fileVersion)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Agents/RunComputationObservationTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Providers;
using OKF4net.Tests.Attestation;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <c>okf_run_computation</c> runs only what the session was shown (spec §5.4, D1): the
/// concept at the version the image holds (contract and runtime included), the computation
/// file at the version <c>okf_get_computation</c> showed, and the attester pinned to the image.
/// A refusal for a changed version or a negative resource state invalidates the image, so the
/// next attempt starts from a fresh survey under every refresh policy (spec §5.1), and the
/// session's observations survive that refresh.
/// </summary>
public class RunComputationObservationTests
{
    private const string Attester = "def attest(**_):\n    return {}  # v1\n";
    private const string Inline = "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: [job_id] }\nattester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT 1\n```\n";
    private const string FromFile = "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: q.sql\nexecutor: { receipt: [job_id] }\nattester: { resource: att.py }\n---\n";

    private sealed class Runs
    {
        public int Stages;
        public string? AttesterSeen;
        public string? CodeSeen;
    }

    private static (InMemoryBundleProvider Storage, OkfBundleTools Tools, Runs Runs) ToolsOver(string concept, BundleRefreshPolicy? policy = null, IBundleProvider? wrapped = null)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", concept).Add("att.py", Attester).Add("q.sql", "SELECT 2\n");
        var runs = new Runs();
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.BindFunc = (contract, computation, values, _) =>
        {
            runs.Stages++;
            runs.CodeSeen = computation.InlineCode;
            return ValueTask.FromResult(new BoundComputation("bigquery", computation.InlineCode, null, values));
        };
        runtime.AttestFunc = (context, _) =>
        {
            runs.Stages++;
            runs.AttesterSeen = context.AttesterSourceText;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var handle = new BundleHandle(wrapped ?? storage, refreshPolicy: policy);
        return (storage, new OkfBundleTools(handle, new AttestationOrchestrator(reg)), runs);
    }

    private static Task<string> Run(OkfBundleTools tools) => tools.RunComputationToolAsync("c/rev", new Dictionary<string, object?>());

    [Fact]
    public async Task A_computation_not_shown_to_the_session_is_refused_and_a_shown_one_runs()
    {
        var (_, tools, runs) = ToolsOver(Inline);

        Assert.Equal("Error: 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        Assert.Contains("SELECT 1", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.Equal(Attester, runs.AttesterSeen);

        // A whole-concept observation authorises an inline run too (spec §5.4).
        var (_, other, otherRuns) = ToolsOver(Inline);
        other.ReadConceptTool("c/rev");
        Assert.Contains("displayable: yes", await Run(other), StringComparison.Ordinal);
        Assert.Equal(2, otherRuns.Stages);
    }

    [Fact]
    public async Task The_public_method_needs_no_observation_but_authorises_none()
    {
        var (_, tools, runs) = ToolsOver(Inline);

        Assert.Contains("displayable: yes", await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>()), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);

        tools.GetComputation("c/rev"); // the public method: a host presenting the result itself
        Assert.StartsWith("Error: 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concept_whose_contract_changed_since_it_was_shown_is_refused_and_the_image_refreshed()
    {
        var (storage, tools, runs) = ToolsOver(FromFile);
        tools.GetComputationTool("c/rev");

        // The contract changes and the handle is refreshed (spec §11, revision 4): the file is
        // unchanged, yet the concept the session was shown is not the one the image holds.
        storage.Add("c/rev.md", FromFile.Replace("runtime: bigquery", "runtime: bigquery\nparameters:\n  - { name: year, required: true }", StringComparison.Ordinal));
        tools.Handle.Invalidate();

        Assert.Equal("Error: 'c/rev' changed since it was shown to this session; read it again with okf_get_computation before running it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        Assert.Contains("year", tools.GetComputationTool("c/rev"), StringComparison.Ordinal); // the refreshed image
        var refused = await Run(tools); // the required parameter is missing: the run reached the orchestrator
        Assert.Contains("missing required parameter 'year'", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_computation_file_changed_since_it_was_shown_is_refused_at_its_pinned_read()
    {
        var (storage, tools, runs) = ToolsOver(FromFile);
        tools.ReadConceptTool("c/rev"); // a whole-concept observation does not show the file
        Assert.Equal("Error: the computation file 'q.sql' of 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Contains("SELECT 2", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 2\n", runs.CodeSeen);

        storage.Add("q.sql", "SELECT 3\n");

        var refused = await Run(tools);
        Assert.Contains("computation file 'q.sql' changed since it was shown; read it again with okf_get_computation before running it", refused, StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.True(tools.Handle.IsStale);

        Assert.Contains("SELECT 3", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 3\n", runs.CodeSeen);
    }

    /// <summary>D5: a computation injected by the context provider is not an observation, inline code included.</summary>
    [Fact]
    public async Task A_context_injection_does_not_authorise_a_run()
    {
        var (_, tools, runs) = ToolsOver(Inline);
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { TokenBudget = 100_000 });
        var agent = new ScriptedChatClient([]).AsAIAgent();
#pragma warning disable MAAI001 // InvokingContext's public ctor is marked [Experimental].
        var context = await provider.ProvideForTest(new AIContextProvider.InvokingContext(agent, session: null, new AIContext { Messages = [new ChatMessage(ChatRole.User, "SELECT")] }), CancellationToken.None);
#pragma warning restore MAAI001

        Assert.Contains("SELECT 1", Assert.Single(context.Messages!).Text, StringComparison.Ordinal);
        Assert.StartsWith("Error: 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);
    }

    [Fact]
    public async Task An_observation_survives_a_refresh_of_the_image()
    {
        var (storage, tools, runs) = ToolsOver(Inline);
        tools.GetComputationTool("c/rev");
        storage.Add("notes/other.md", "---\ntype: Note\ntitle: O\ndescription: o.\n---\n\nO.\n");
        tools.Handle.Invalidate();

        Assert.Equal(2, tools.GetBundle().Count); // reloaded
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
    }

    [Fact]
    public async Task Another_tool_sets_observation_does_not_replace_this_sessions()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", Inline).Add("att.py", Attester);
        using var handle = new BundleHandle(storage);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })) });
        var first = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        var second = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        first.GetComputationTool("c/rev");

        storage.Add("c/rev.md", Inline.Replace("SELECT 1", "SELECT 9", StringComparison.Ordinal));
        handle.Invalidate();
        second.GetComputationTool("c/rev");

        Assert.StartsWith("Error: 'c/rev' changed since it was shown", await Run(first), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(second), StringComparison.Ordinal);
    }

    /// <summary>
    /// Recovery after a refused pin (spec §5.1, §11), under each policy: the attester changes
    /// after the load; the first attempt is refused (unless the policy itself reloads, as
    /// OnAccess does on the in-memory token, which hashes content); the next attempt, with no
    /// host <c>Refresh()</c>, runs on the new version. Same path for an attester missing at the
    /// load and created after it. The session's observation is untouched by the reload.
    /// </summary>
    [Theory]
    [InlineData("manual", false)]
    [InlineData("onaccess", false)]
    [InlineData("interval", false)]
    [InlineData("notify", false)]
    [InlineData("manual", true)]
    [InlineData("notify", true)]
    public async Task A_refused_pin_obliges_a_reload_and_the_next_attempt_runs_on_the_new_state(string policy, bool missingFirst)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", Inline);
        if (!missingFirst)
        {
            storage.Add("att.py", Attester);
        }

        var notifying = new NotifyingBundleProvider(storage);
        var refresh = policy switch
        {
            "onaccess" => BundleRefreshPolicy.OnAccess,
            "interval" => BundleRefreshPolicy.Every(TimeSpan.FromHours(1)),
            "notify" => BundleRefreshPolicy.Notify,
            _ => BundleRefreshPolicy.Manual,
        };
        var runs = new Runs();
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.AttestFunc = (context, _) =>
        {
            runs.Stages++;
            runs.AttesterSeen = context.AttesterSourceText;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        using var handle = new BundleHandle(notifying, refreshPolicy: refresh);
        var tools = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        tools.GetComputationTool("c/rev");
        var loaded = handle.Current;
        Assert.Equal(missingFirst ? ExecutableResourceState.Missing : ExecutableResourceState.Pinned, loaded.ExecutableResources[ConceptId.Parse("c/rev")].Attester.State);

        const string v2 = "def attest(**_):\n    return {}  # v2\n";
        storage.Add("att.py", v2);

        var first = await Run(tools);
        if (policy != "onaccess")
        {
            Assert.Contains(missingFirst ? "attester resource 'att.py' could not be resolved (Missing)" : "attester resource 'att.py' changed since the bundle was loaded", first, StringComparison.Ordinal);
            Assert.Equal(0, runs.Stages);
            Assert.True(handle.IsStale);
        }

        var second = await Run(tools);
        Assert.Contains("displayable: yes", second, StringComparison.Ordinal);
        Assert.Equal(v2, runs.AttesterSeen);
        Assert.NotSame(loaded, handle.Current);
        Assert.False(handle.IsStale);
        Assert.Equal(1, tools.Observations.Count);
    }
}
```

Five existing tests invoke the exposed `okf_run_computation` without showing the computation (C24); each shows it first. In `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs`, replace

```text
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var function = GetFunction(tools, "okf_run_computation");

        var argsJson = """{"conceptId": "c/rev", "parameterValues": {"threshold": 42, "label": "q3"}}""";
```

with

```csharp
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        tools.GetComputationTool("c/rev"); // shown first (spec §5.4): okf_run_computation runs only what the session was shown
        var function = GetFunction(tools, "okf_run_computation");

        var argsJson = """{"conceptId": "c/rev", "parameterValues": {"threshold": 42, "label": "q3"}}""";
```

then replace

```text
    public async Task okf_run_computation_still_binds_distinct_top_level_parameter_names()
    {
        using var tmp = new TempDir();
        var capture = new RunCapture();
        var function = GetFunction(WiredTools(tmp, capture), "okf_run_computation");
```

with

```csharp
    public async Task okf_run_computation_still_binds_distinct_top_level_parameter_names()
    {
        using var tmp = new TempDir();
        var capture = new RunCapture();
        var tools = WiredTools(tmp, capture);
        tools.GetComputationTool("c/rev"); // shown first (spec §5.4)
        var function = GetFunction(tools, "okf_run_computation");
```

then replace

```text
        var tools = WiredTools(tmp, capture);

        var viaFunction = await GetFunction(tools, "okf_run_computation").InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
```

with

```csharp
        var tools = WiredTools(tmp, capture);
        tools.GetComputationTool("c/rev"); // shown first (spec §5.4); the direct call below needs no observation

        var viaFunction = await GetFunction(tools, "okf_run_computation").InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
```

In `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`, replace (in `InvokeRunComputationWith`)

```text
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var function = tools.GetTools().Cast<AIFunction>().Single(f => f.Name == "okf_run_computation");
```

with

```csharp
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        tools.GetComputationTool("c/rev"); // shown first (spec §5.4): okf_run_computation runs only what the session was shown
        var function = tools.GetTools().Cast<AIFunction>().Single(f => f.Name == "okf_run_computation");
```

and replace

```text
        // this test must cover that wrapper too, exactly as a real host calls it.
        var function = tools.GetTools().Cast<AIFunction>().Single(f => f.Name == "okf_run_computation");
```

with

```csharp
        // this test must cover that wrapper too, exactly as a real host calls it.
        tools.GetComputationTool("c/rev"); // shown first (spec §5.4)
        var function = tools.GetTools().Cast<AIFunction>().Single(f => f.Name == "okf_run_computation");
```

- [ ] **Step 2: Run the build to verify the tests fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (replayed): build errors only — `2 error CS1061: 'OkfBundleTools' does not contain a definition for 'RunComputationToolAsync'`.

- [ ] **Step 3: The gated run**

In `src/OKF4net.Agents/OkfBundleTools.cs`, four edits.

(a) Replace the head of `RunComputationAsync`:

```text
    [Description("Run an Attested Computation (§10.5: bind, execute, attest, gate on staleness) via the configured attestation runtime, and return the resulting outcome (displayable, verdict, receipt, reasons).")]
    public async Task<string> RunComputationAsync(
        [Description("The concept id, e.g. 'computations/monthly-revenue'.")] string conceptId,
        [Description("Parameter values for this run, by name (§10.3: values only, never computation code).")] IReadOnlyDictionary<string, object?> parameterValues,
        CancellationToken cancellationToken = default)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }
```

with

```csharp
    [Description(RunComputationDescription)]
    public Task<string> RunComputationAsync(
        [Description(ComputationIdDescription)] string conceptId,
        [Description(ParameterValuesDescription)] IReadOnlyDictionary<string, object?> parameterValues,
        CancellationToken cancellationToken = default) =>
        RunComputationCoreAsync(conceptId, parameterValues, gated: false, cancellationToken);

    /// <summary>
    /// <c>okf_run_computation</c>: <see cref="RunComputationAsync"/>, gated on what this session
    /// observed (spec §5.4, D1). The concept must have been shown — in full by
    /// <c>okf_read_concept</c>, or as code by <c>okf_get_computation</c> — at the version the
    /// current image holds, so that the contract and the runtime run are the ones shown; a
    /// file computation must also have been shown by <c>okf_get_computation</c>, and the file is
    /// read pinned to that version. Otherwise the run is refused with no runtime called:
    /// <c>Error: … has not been shown to this session; read it first with okf_get_computation</c>,
    /// or <c>… changed since it was shown …</c>, in which case the image is refreshed. The
    /// attester is pinned to the image whatever the path (<see cref="AttestationOrchestrator.Capture"/>).
    /// Internal so that tests reach the exposed function without the AI plumbing.
    /// </summary>
    [Description(RunComputationDescription)]
    internal Task<string> RunComputationToolAsync(
        [Description(ComputationIdDescription)] string conceptId,
        [Description(ParameterValuesDescription)] IReadOnlyDictionary<string, object?> parameterValues,
        CancellationToken cancellationToken = default) =>
        RunComputationCoreAsync(conceptId, parameterValues, gated: true, cancellationToken);

    private async Task<string> RunComputationCoreAsync(string conceptId, IReadOnlyDictionary<string, object?> parameterValues, bool gated, CancellationToken cancellationToken)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }
```

(b) Replace the orchestrator call:

```text
        try
        {
            var outcome = await _orchestrator
                .RunAsync(GetBundle(), ConceptId.Parse(conceptId), parameterValues, cancellationToken: linked.Token)
                .ConfigureAwait(false);
            return FormatOutcome(outcome);
        }
```

with

```csharp
        try
        {
            var bundle = GetBundle();
            var id = ConceptId.Parse(conceptId);
            AttestationOutcome outcome;
            if (bundle.Get(id) is not { } concept || !concept.Document.Frontmatter.IsAttestedComputation)
            {
                // Not found, or not a computation: the orchestrator's own refusal, as before.
                outcome = await _orchestrator.RunAsync(bundle, id, parameterValues, cancellationToken: linked.Token).ConfigureAwait(false);
            }
            else
            {
                BundleObjectVersion? fileVersion = null;
                if (gated && GuardObserved(bundle, concept, out fileVersion) is { } refused)
                {
                    return refused;
                }

                // Captured once, before any runtime is called (spec §5.4): the computation
                // (pinned to the file version the session observed, when gated) and the
                // attester at the version the image was loaded with. A refusal the image may
                // be behind on invalidates it, so that the next attempt starts from a fresh
                // survey (spec §5.1) -- whatever the handle's policy, and for a direct caller
                // of RunComputationAsync too.
                var captured = AttestationOrchestrator.Capture(bundle, concept, fileVersion);
                if (captured.Capture is null)
                {
                    if (captured.ImageOutdated)
                    {
                        InvalidateBundle();
                    }

                    return FormatOutcome(captured.Failure!);
                }

                outcome = await _orchestrator
                    .RunCapturedAsync(bundle, id, parameterValues, captured.Capture, cancellationToken: linked.Token)
                    .ConfigureAwait(false);
            }

            return FormatOutcome(outcome);
        }
```

(c) In `GetTools(OkfToolMode mode)`, replace `            tools.Add(new TopLevelDuplicateParameterGuard(AIFunctionFactory.Create(RunComputationAsync, "okf_run_computation")));` with `            tools.Add(new TopLevelDuplicateParameterGuard(AIFunctionFactory.Create(RunComputationToolAsync, "okf_run_computation")));`.

(d) Before `AppendLogFileChanges`'s doc comment, replace

```text
    /// <summary>
    /// Processes one <c>log.md</c> for <see cref="ChangesSince"/>: on a
```

with

```csharp
    /// <summary>
    /// The gate of <c>okf_run_computation</c> (spec §5.4, points 1 and 2): the refusal text, or
    /// <see langword="null"/> when the session observed <paramref name="concept"/> at the version
    /// this image holds and, for a file computation whose file resolves, that file too
    /// (<paramref name="fileVersion"/> is then the version the read is pinned to). A concept
    /// shown at another version refreshes the image: a changed contract or runtime is run only
    /// after it is shown again. A file that does not resolve is left to the capture, which
    /// refuses it as before.
    /// </summary>
    private string? GuardObserved(Bundle bundle, Concept concept, out BundleObjectVersion? fileVersion)
    {
        fileVersion = null;
        var key = concept.Key ?? concept.Id.ToKey();
        BundleObjectVersion? observed = _observations.TryGetFullConcept(key, out var full) ? full
            : _observations.TryGetCode(key, out var code) ? code
            : null;
        if (observed is null)
        {
            return $"Error: '{concept.Id}' has not been shown to this session; read it first with okf_get_computation, then run it.";
        }

        if (!bundle.ObjectVersions.TryGetValue(key, out var current) || current != observed.Value)
        {
            InvalidateBundle();
            return $"Error: '{concept.Id}' changed since it was shown to this session; read it again with okf_get_computation before running it.";
        }

        var computation = concept.Document.Computation();
        if (computation is { Source: ComputationSource.File, Path.Length: > 0 } file
            && bundle.TryResolveResourceKey(concept, file.Path!, out var fileKey, out var status)
            && status == ResourceResolutionStatus.Resolved)
        {
            if (!_observations.TryGetCode(fileKey!, out var shownFile))
            {
                return $"Error: the computation file '{OneLine(file.Path!)}' of '{concept.Id}' has not been shown to this session; read it first with okf_get_computation, then run it.";
            }

            fileVersion = shownFile;
        }

        return null;
    }

    /// <summary>
    /// Processes one <c>log.md</c> for <see cref="ChangesSince"/>: on a
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~RunComputationObservationTests"`
Expected: 0 warnings, 0 errors; `Passed: 13, Skipped: 0, Total: 13`.
Then the full suite: `dotnet test tests/OKF4net.Tests --no-build --filter "Category!=ContainerIntegration"` — expected `Passed: 3489, Skipped: 35, Total: 3524`.

- [ ] **Step 5: CHANGELOG**

In `CHANGELOG.md`, replace the first

```text
- **Finite default bounds on loading** (spec §6), out of reach of real
```

with

```markdown
- **`okf_run_computation` runs only what the session was shown, pinned**
  (#139, lot C; spec §5.1, §5.4; D1). The exposed tool captures, once and
  before any runtime is called, everything the run executes: the concept
  must have been shown — in full by `okf_read_concept`, or as code by
  `okf_get_computation` — at the version the current image holds (a changed
  contract or runtime is refused: `Error: 'x' changed since it was shown to
  this session; read it again with okf_get_computation before running it.`,
  and the image is refreshed); a file computation must also have been shown
  by `okf_get_computation`, and the file is read pinned to that version (a
  file changed since is refused at the read: `computation file 'q.sql'
  changed since it was shown; read it again …`); the attester is read pinned
  to the version the image was loaded with. A computation never shown is
  refused with `Error: 'x' has not been shown to this session; read it first
  with okf_get_computation, then run it.` and no stage runs; a context
  injection does not count (D5). **Recovery after a refusal** (spec §5.1): a
  refusal for a changed version or a negative resource state (`Missing`,
  `Unreadable`, `TooLarge`, `Unsafe`) obliges the handle to reload, so the
  next attempt — with no host `Refresh()`, under every policy — runs on the
  new state; an undeclared or URL attester does not (only a change of the
  concept can alter it). The public `RunComputationAsync` a host calls
  needs no observation and is otherwise unchanged (it captures the same way,
  with the attester pinned, and reloads after such a refusal too); the
  session's observations survive every refresh. The tool's name, description
  and schema are unchanged.
- **Finite default bounds on loading** (spec §6), out of reach of real
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/RunComputationObservationTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents/RunComputationObservationTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(agents): okf_run_computation runs only what the session was shown, captured once and pinned

The exposed tool requires a full-concept or code observation of the concept
at the version the image holds, and a code observation of the computation
file, which is read pinned to it; the attester is pinned to the image. A
refusal the image may be behind on invalidates the handle, for the exposed
tool and the public method alike, so the next attempt starts from a fresh
survey. Every test that ran a computation through the exposed function now
shows it first.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 7: `okf-mcp`'s discovery reads the root `index.md` under a 16 MiB bound

**Files:**
- Modify: `src/OKF4net.Mcp/OkfBundleDiscovery.cs` (`ReadRootIndexOrNull` at `:88-113`)
- Test: `tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs` (one test before `Adapter_returns_null_on_invalid_utf8`)
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing new (`OKF4net.Mcp` keeps its dependency set; no `InternalsVisibleTo` is added).
- Produces: `public const long OkfBundleDiscovery.MaxRootIndexBytes = 16L * 1024 * 1024`; `ReadRootIndexOrNull` answers `null` for a longer `index.md` without reading it and counts the bytes it reads; `TryDiscover` is unchanged.

- [ ] **Step 1: Write the failing test**

In `tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs`, replace

```text
    [Fact]
    public void Adapter_returns_null_on_invalid_utf8()
```

with

```csharp
    /// <summary>
    /// Spec §6, §9 (lot C): the root <c>index.md</c> is read under the 16 MiB bound; past it
    /// the index is unreadable, hence "no declared version", without reading it. The file is
    /// a sparse one of 16 MiB + 1 bytes, created by setting its length.
    /// </summary>
    [Fact]
    public void Adapter_returns_null_when_the_index_exceeds_the_bound_without_reading_it()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            var path = Path.Combine(dir, "index.md");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var marked = Encoding.UTF8.GetBytes(Marked);
                stream.Write(marked, 0, marked.Length);
                stream.SetLength(OkfBundleDiscovery.MaxRootIndexBytes + 1);
            }

            Assert.Equal(16L * 1024 * 1024, OkfBundleDiscovery.MaxRootIndexBytes);
            Assert.Null(OkfBundleDiscovery.ReadRootIndexOrNull(dir));

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(OkfBundleDiscovery.MaxRootIndexBytes);
            }

            Assert.NotNull(OkfBundleDiscovery.ReadRootIndexOrNull(dir)); // exactly at the bound: read
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Adapter_returns_null_on_invalid_utf8()
```

- [ ] **Step 2: Run the build to verify the test fails**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected (executed): `6 error CS0117: 'OkfBundleDiscovery' does not contain a definition for 'MaxRootIndexBytes'`.

- [ ] **Step 3: The bounded reader**

In `src/OKF4net.Mcp/OkfBundleDiscovery.cs`, replace

```text
    /// <summary>
    /// Production <c>readRootIndex</c> accessor: reads
    /// <c>&lt;directory&gt;/index.md</c> as strict UTF-8, returning
    /// <see langword="null"/> on any read failure (missing file, I/O error,
    /// permission denied, invalid UTF-8) — the same "unreadable means no
    /// declared version" stance as <see cref="Bundle.OkfVersion"/>.
    /// </summary>
    /// <param name="directory">Candidate bundle root.</param>
    /// <returns>The root index text, or <see langword="null"/>.</returns>
    public static string? ReadRootIndexOrNull(string directory)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(Path.Combine(directory, IndexFilename)));
        }
```

with

```csharp
    /// <summary>The most a candidate's root <c>index.md</c> is read (16 MiB, spec §6, §9): a larger one is unreadable, hence unmarked, without reading it.</summary>
    public const long MaxRootIndexBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Production <c>readRootIndex</c> accessor: reads
    /// <c>&lt;directory&gt;/index.md</c> as strict UTF-8, within
    /// <see cref="MaxRootIndexBytes"/>, returning <see langword="null"/> on any
    /// read failure (missing file, I/O error, permission denied, invalid UTF-8,
    /// over the bound) — the same "unreadable means no declared version" stance
    /// as <see cref="Bundle.OkfVersion"/>. The bytes are counted as they are
    /// read, so a file that grows while it is read is bounded too.
    /// </summary>
    /// <param name="directory">Candidate bundle root.</param>
    /// <returns>The root index text, or <see langword="null"/>.</returns>
    public static string? ReadRootIndexOrNull(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, IndexFilename), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRootIndexBytes)
            {
                return null;
            }

            using var buffer = new MemoryStream((int)stream.Length);
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxRootIndexBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` then `dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~OkfBundleDiscoveryTests"`
Expected: 0 warnings, 0 errors; `Passed: 16, Skipped: 0, Total: 16`.

- [ ] **Step 5: CHANGELOG**

In `CHANGELOG.md`, replace the first

```text
- **Finite default bounds on loading** (spec §6), out of reach of real
```

with

```markdown
- **`okf-mcp`'s bundle discovery reads a candidate's root `index.md` under a
  16 MiB bound** (#139, lot C; spec §6, §9): `OkfBundleDiscovery.ReadRootIndexOrNull`
  answers `null` — "no declared version", as for any unreadable index — for
  a larger file without reading it (`OkfBundleDiscovery.MaxRootIndexBytes`),
  where it read the whole file before. A directory whose `index.md` is over
  the bound is therefore not discovered as a marked bundle; name it on the
  command line or in `OKF_BUNDLE_ROOT` instead.
- **Finite default bounds on loading** (spec §6), out of reach of real
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net.Mcp/OkfBundleDiscovery.cs tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet format OKF4net.sln --verify-no-changes   # exit code 0
git add src/OKF4net.Mcp/OkfBundleDiscovery.cs tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(mcp): bound the root index.md read of the bundle discovery to 16 MiB

ReadRootIndexOrNull answers null, as for any unreadable index, for a file
over MaxRootIndexBytes without reading it, and counts the bytes it reads.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 8: Final verification — CLAUDE.md, the full suite on Windows and Linux, AOT, the producer, the differentials

**Files:**
- Modify: `CLAUDE.md` (the `Providers/` bullet at `:47`, the `src/OKF4net.Attestation/` bullet at `:49`, the `src/OKF4net.Agents/` bullet at `:51`)
- Unchanged, verified here: `src/OKF4net.Cli/`, `src/OKF4net.Render/`, `src/OKF4net.Catalog/` (`FileMemoryStore` keeps the writer's string constructor; `OkfBundleKnowledgeSource` keeps `Bundle.Load(string)` — both lot D), `src/OKF4net.Viewer/`, `producers/`, `samples/`
- Scratch, outside the repository (below `$S`): `differential-cli.sh`, `tools-differential.sh`, `toolsdiff/` — never committed

**Interfaces:**
- Consumes: Tasks 1–7.
- Produces: no API. The evidence that the CLI's bytes and the public tool methods' outputs are unchanged, that the lot builds and tests on both platforms, that the AOT binaries still publish, and that the producer still builds and tests.

- [ ] **Step 1: CLAUDE.md**

In `CLAUDE.md`, replace `lots A and B). \`IBundleProvider\`` (in the `Providers/` bullet) with `lots A, B and C). \`IBundleProvider\``.

In the `src/OKF4net.Attestation/` bullet, replace

```text
AttestationOrchestrator.RunAsync` drives one run end to end (resolve → bind → execute → receipt-shape check → attest → gate on verdict + `stale_after`), errors-as-data, never writing a verdict back to the bundle (§10.6)
```

with

```markdown
AttestationOrchestrator.RunAsync` drives one run end to end (capture → bind → execute → receipt-shape check → attest → gate on verdict + `stale_after`), errors-as-data, never writing a verdict back to the bundle (§10.6). The capture (`AttestationOrchestrator.Capture`, spec §5.4) is the **single** place sanctioned code is read for a run, through `Bundle.TryResolveResourceKey`/`ReadResourceText(BundleKey, expected, …)` and never through the historical path members: the attester pinned to the version `Bundle.ExecutableResources` surveyed at the load, the computation file to the version the caller observed when it gives one, and nothing is read after it (`RunCapturedAsync` runs on the capture alone). A concept without `attester.resource`, or with a URL, is refused before any runtime stage (D6, `docs/spec-conformance` S10.5-1): `AttesterSourceText` is never null at a runtime's entry — do not reopen that path
```

Replace the whole `src/OKF4net.Agents/` bullet

```text
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`.
```

with

```markdown
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`. `OkfBundleTools` serves its image from a `BundleHandle` (spec §5.2: `Handle`, shared with the `OkfContextProvider` built over it and with whoever else the host hands the handle; the root-string constructors build their own, `Manual`) and makes **no direct I/O**: browsing, the change log, index regeneration and a computation's file all go through `Handle.Provider`, and the write tools are offered only when it can write. **Observations (spec §5.4, D1/D5) are recorded at the presentation boundary only**: the functions bound as `okf_read_concept`, `okf_get_computation`, `okf_write_concept` and `okf_run_computation` are the internal `*Tool` wrappers (`ReadConceptTool`, `GetComputationTool`, `WriteConceptTool`, `RunComputationToolAsync`), which record into the per-instance `ObservationRegistry` (10 000 keys, LRU) and gate on it; the public `ReadConcept`/`GetComputation`/`WriteConcept`/`RunComputationAsync` a host calls record nothing and gate on nothing, and `OkfContextProvider` calls the public ones — an injection is never an observation. Do not bind a public method to a model-facing name, and do not record outside a `*Tool` wrapper.
```

- [ ] **Step 2: The full suite, the build and the format, on Windows**

Run: `dotnet build OKF4net.sln -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"`; `dotnet test OKF4net.sln --no-build --filter "Category!=ContainerIntegration"`; `dotnet format OKF4net.sln --verify-no-changes`.
Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed: 0, Passed: 3490, Skipped: 35, Total: 3525`; format exit code 0. Also `grep -rn "File\.\|Directory\." src/OKF4net.Agents/OkfBundleTools.cs | grep -v "///\|//"` prints only the root-string constructor's `Directory.Exists(bundleRoot)`.

- [ ] **Step 3: Linux, as root and as an unprivileged user**

Run the container command of the Global Constraints.
Expected (executed): root `Passed: 3453, Skipped: 72, Total: 3525`; tester (Providers, Agents, Attestation) `Passed: 1090, Skipped: 40, Total: 1130`. (The Providers classes alone, as root, give `479 / 22 / 501`.)

- [ ] **Step 4: Native AOT**

From PowerShell, in the worktree:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish src/OKF4net.Cli -c Release -nologo -v q 2>&1 | Select-String -Pattern "error|warning"
Get-Item src/OKF4net.Cli/bin/Release/net10.0/win-x64/publish/okf.exe | Select-Object Length
dotnet publish src/OKF4net.Render -c Release -nologo -v q 2>&1 | Select-String -Pattern "error|warning"
Get-Item src/OKF4net.Render/bin/Release/net10.0/win-x64/publish/okf-render.exe | Select-Object Length
& src/OKF4net.Cli/bin/Release/net10.0/win-x64/publish/okf.exe validate bundles/acme_retail | Select-Object -Last 2
```

Expected (executed): no error or warning line; `okf.exe` 4 830 208 bytes and `okf-render.exe` 4 587 008 bytes (sizes may differ by a few KB with the SDK patch); `9 concept(s); 0 error(s), 22 warning(s), 0 info.` then `✓ conformant with OKF v0.2`.

- [ ] **Step 5: The producer and the samples**

Run: `dotnet test producers/OkfProducer.sln`
Expected (executed): `Passed!  - Failed: 0, Passed: 826, Skipped: 15, Total: 841` — the base's own counts (the producer uses `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` and `IndexGenerator.RegenerateIndexes(string)`, both untouched).
Run: `for s in samples/*/*.sln; do echo "== $s"; dotnet build "$s" -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"; done`
Expected: `agents-quickstart`, `attestation-containers-demo` and `catalog-explorer` build with `0 Warning(s)`, `0 Error(s)`; `acme-retail-agent` fails with `NU1605 … Detected package downgrade: Microsoft.Agents.AI from 1.24.0 to 1.20.0`, exactly as on the base (lot B's Deviation 4; the sample pins 1.20.0).

- [ ] **Step 6: The CLI's bytes against the base**

Build a detached worktree of the base and its CLI: `git worktree add --detach ../lotc-base c48f23f && (cd ../lotc-base && dotnet build src/OKF4net.Cli -v q -nologo)`; this worktree's CLI is built by Step 2. Save as `$S/differential-cli.sh` (it reads each worktree's own bundles by the same relative path, so the printed paths agree, and masks the `evaluatedAt` clock of the JSON outputs):

```bash
#!/bin/sh
# usage: differential-cli.sh <base worktree> <branch worktree> <scratch dir>
# Runs the read-only okf verbs of both builds on the same input bundles (each worktree's own
# copy, by the same relative path, so the printed paths agree) and compares the transcripts
# (stdout, stderr, exit codes) byte for byte. Prints one line per bundle.
BASE="$1"; BRANCH="$2"; OUT="$3"
rm -rf "${OUT:?}"; mkdir -p "$OUT"
for rel in bundles/acme_retail bundles/ga4 bundles/meridian_transit bundles/attestation_containers_demo \
           tests/fixtures/appendix_a tests/fixtures/okf_v02 tests/fixtures/okf_v02_computation tests/fixtures/okf_v02_reserved; do
  name=$(basename "$rel")
  for side in base branch; do
    if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
    (
      cd "$WT"
      OKF="dotnet src/OKF4net.Cli/bin/Debug/net10.0/okf.dll"
      $OKF validate "$rel"; echo "exit=$?"
      $OKF validate --json "$rel"; echo "exit=$?"
      $OKF info "$rel"; echo "exit=$?"
      $OKF info --json "$rel"; echo "exit=$?"
      $OKF audit "$rel"; echo "exit=$?"
      $OKF audit --json "$rel"; echo "exit=$?"
      $OKF graph "$rel"; echo "exit=$?"
      $OKF graph --dot "$rel"; echo "exit=$?"
    ) 2>&1 | sed -E "s/\"evaluatedAt\":\"[^\"]*\"/\"evaluatedAt\":\"T\"/" > "$OUT/$name.$side.out"
  done
  if cmp -s "$OUT/$name.base.out" "$OUT/$name.branch.out"; then echo "$name: transcripts identical ($(wc -l < "$OUT/$name.base.out") lines)"; else echo "$name: TRANSCRIPTS DIFFER"; fi
done
```

Run, in Git Bash: `sh $S/differential-cli.sh <base worktree> <this worktree> $S/diff`. Expected (executed):

```text
acme_retail: transcripts identical (105 lines)
ga4: transcripts identical (65 lines)
meridian_transit: transcripts identical (78 lines)
attestation_containers_demo: transcripts identical (45 lines)
appendix_a: transcripts identical (65 lines)
okf_v02: transcripts identical (47 lines)
okf_v02_computation: transcripts identical (54 lines)
okf_v02_reserved: transcripts identical (48 lines)
```

- [ ] **Step 7: The public tool methods against the base**

The host API of `OkfBundleTools` (the public methods, which lot C leaves ungated) must give the same outputs and the same trees. Save `$S/toolsdiff/toolsdiff.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(OkfRoot)/src/OKF4net.Agents/OKF4net.Agents.csproj" />
  </ItemGroup>
</Project>
```

`$S/toolsdiff/Program.cs`:

```csharp
// Runs the public methods of OkfBundleTools (the host API, unchanged by lot C) on one bundle copy
// and prints every result, the root replaced by BUNDLE. Read tools first, then the write tools.
using OKF4net.Agents;

var root = args[0];
var tools = new OkfBundleTools(root);
void Print(string label, string result) => Console.Write($"== {label}\n{result.Replace(root, "BUNDLE")}\n");
var first = OKF4net.Bundle.Load(root).Concepts.Select(c => c.Id.ToString()).OrderBy(s => s, StringComparer.Ordinal).First();
Print("browse root", tools.Browse());
Print("browse first dir", tools.Browse(first.Split('/')[0]));
Print("browse missing", tools.Browse("nope"));
Print("read first", tools.ReadConcept(first));
Print("read missing", tools.ReadConcept("nope/nothing"));
Print("graph", tools.Graph());
Print("graph first", tools.Graph(first));
Print("search", tools.Search("orders"));
Print("audit", tools.Audit());
Print("validate", tools.ValidateBundle());
Print("changes", tools.ChangesSince("2020-01-01"));
Print("get computation", tools.GetComputation(first));
const string Fm = "type: Note\ntitle: Diff\ndescription: Differential run.\ngenerated:\n  by: human:ada\n  at: 2026-10-19T00:00:00Z\n";
Print("write new", tools.WriteConcept("notes/diff-new", Fm, "Body.\n"));
Print("write update", tools.WriteConcept("notes/diff-new", Fm, "Body two.\n"));
Print("write missing keys", tools.WriteConcept("notes/bad", "type: Note\n", "Body.\n"));
Print("write reserved", tools.WriteConcept("log", Fm, "Body.\n"));
Print("write invalid id", tools.WriteConcept("../escape", Fm, "Body.\n"));
Print("append log", tools.AppendLog("Update", "Differential run."));
Print("append log refused", tools.AppendLog("Up\ndate", "x"));
Print("regenerate", tools.RegenerateIndexes());
Print("regenerate again", tools.RegenerateIndexes());
Print("verify", tools.Verify("notes/diff-new", "human:ada", "2026-10-19T00:00:00Z"));
Print("verify missing", tools.Verify("notes/diff-new, notes/nope", "human:ada", "2026-10-19T01:00:00Z"));
Print("read written", tools.ReadConcept("notes/diff-new"));
```

and `$S/tools-differential.sh` (one build per side, each against that side's `OKF4net.Agents`; dates and timestamps masked):

```bash
#!/bin/sh
# usage: tools-differential.sh <base worktree> <branch worktree> <scratch dir>
BASE="$1"; BRANCH="$2"; OUT="$3"; HERE="$(cd "$(dirname "$0")" && pwd)"
rm -rf "${OUT:?}"; mkdir -p "$OUT"
for side in base branch; do
  if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
  cp -r "$HERE/toolsdiff" "$OUT/toolsdiff-$side"
  dotnet build "$OUT/toolsdiff-$side/toolsdiff.csproj" -nologo -v q -p:OkfRoot="$(cd "$WT" && pwd -W)" > "$OUT/build-$side.txt" 2>&1 || { echo "build $side failed"; cat "$OUT/build-$side.txt"; exit 1; }
done
for b in bundles/acme_retail bundles/ga4 tests/fixtures/okf_v02 tests/fixtures/okf_v02_computation; do
  name=$(basename $b)
  for side in base branch; do
    mkdir -p "$OUT/$side"; cp -r "$BRANCH/$b" "$OUT/$side/$name"
    dotnet "$OUT/toolsdiff-$side/bin/Debug/net10.0/toolsdiff.dll" "$(cd "$OUT/$side/$name" && pwd -W)" 2>&1 | sed -E "s/[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z/TIME/g; s/[0-9]{4}-[0-9]{2}-[0-9]{2}/DATE/g" > "$OUT/$name.$side.out"; echo "exit=$?" >> "$OUT/$name.$side.out"
  done
  if cmp -s "$OUT/$name.base.out" "$OUT/$name.branch.out"; then o="outputs identical ($(wc -l < "$OUT/$name.base.out") lines)"; else o="OUTPUTS DIFFER"; fi
  if diff -r "$OUT/base/$name" "$OUT/branch/$name" > /dev/null; then t="trees identical ($(find "$OUT/base/$name" -type f | wc -l) files)"; else t="TREES DIFFER"; fi
  echo "$name: $o; $t"
done
```

Run: `sh $S/tools-differential.sh <base worktree> <this worktree> $S/tools`. Expected (executed):

```text
acme_retail: outputs identical (326 lines); trees identical (20 files)
ga4: outputs identical (182 lines); trees identical (17 files)
okf_v02: outputs identical (147 lines); trees identical (7 files)
okf_v02_computation: outputs identical (194 lines); trees identical (19 files)
```

- [ ] **Step 8: Commit**

```bash
git add CLAUDE.md
git commit -F - <<'MSG'
docs: CLAUDE.md describes the handle, the observation boundary and the attestation capture (lot C)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
git worktree remove ../lotc-base
```

Then hand the branch to review (`superpowers:finishing-a-development-branch`): the lot is done when the controller has ruled on the eight open decisions and the review of the whole branch is clean. Do not push before.

## Appendix: what the spike measured that the plan relies on

- **`InMemoryBundleProvider.Enumerate(prefix)` lists an object stored under the prefix's own key** (`tables/orders.md` under prefix `tables/orders.md`), which `FileSystemBundleProvider` never does; `PrefixExists` therefore counts only names strictly under the prefix (C15), so `okf_browse tables/orders.md` is "not found" on both.
- **`InMemoryBundleProvider.GetVersion` hashes content**, so under `OnAccess` the attester change of Task 6's policy theory is seen by the token itself and the first attempt runs on v2; the filesystem token (name, length, mtime) does not see a same-length same-second replacement, which `The_attester_is_pinned_to_the_version_the_image_was_loaded_with` (Task 3) pins by restoring the mtime after the overwrite.
- **A bound of `Attester.Length - 1` also catches the concept** (both are under 256 bytes); the oversized test uses a 400-byte attester under a 256-byte bound (Task 2).
- **`../att.py` from `c/rev.md` resolves to the root** (one level up from `c/`), not above it; the `Unsafe` row of Task 2's theory uses `../../att.py`.
- **`Bundle.Load`'s translated message names no host path** (`I/O error while listing the bundle (DirectoryNotFoundException)`); the path is in the inner exception, which is where the host channel test of Task 4 looks.
- **A directory holding only non-`.md` objects is referenced by its parent's index but gets no `index.md` of its own** (`assets` in Task 4's regeneration test: 3 index files, not 4), and `Browse` of the root, without an `index.md`, lists concepts only, so `assets` appears there only after a regeneration.
- **The `GetTools_descriptions_come_from_the_underlying_method_DescriptionAttribute` test** reads the attribute on `function.UnderlyingMethod`; the `*Tool` wrappers carry the same `[Description]` strings through shared constants, and `okf_run_computation_shows_the_model_the_factory_generated_schema` compares the exposed schema with one generated from the public `RunComputationAsync`: identical parameters and descriptions give an identical schema (replayed).
- **The scripted-agent integration test counts turns and results**: a read before the write adds one turn and one result (`not found` first).

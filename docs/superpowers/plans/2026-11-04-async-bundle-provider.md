# Async bundle provider contract (#152) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the unreleased synchronous `IBundleProvider` with its asynchronous form before 0.8.0 is tagged, async at the I/O edges only, with a non-blocking synchronous bridge that keeps `okf`, `okf-render`, the producer and every golden on a synchronous path.

**Architecture:** The provider contract and the code that does provider I/O (loader, writer, index generator, handle, validator, resource reads, `Capture`) become `ValueTask`/`Task`/`IAsyncEnumerable`; the image (`Bundle`) keeps synchronous queries. The filesystem provider stays synchronous inside and returns completed `ValueTask`s, so a synchronous overload calls the async core and throws if the result is not already complete, never waiting. The writer's thread-bound monitor becomes an explicit `WriteLease` over `SemaphoreSlim`s, done first and on its own so lock regressions are not mixed with async regressions.

**Tech Stack:** C# 14 / .NET 10, BCL only in every `src/` project except where `CLAUDE.md` already allows a package (no new `PackageReference`), xunit, `CA2007` from the SDK analyzers.

**Spec:** `docs/superpowers/specs/2026-11-03-async-bundle-provider-design.md` (§3 recommendation, §3.11 tests, §4 maintainer decisions of 2026-11-04: all seven questions follow their recommendation). Prerequisite reading: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`.

## Global Constraints

- Branch `feat/async-provider` from `origin/dev`, own worktree under `.claude/worktrees/`; nothing is pushed or merged without the maintainer's explicit order.
- `IBundleProvider` is **replaced**, not duplicated: no `IAsyncBundleProvider`, no public sync adapter, no `[Obsolete]` shim for the provider contract (it was never published, design §1.5).
- `CancellationToken` is **required** (no default) on every `IBundleProvider` method; it is `= default` on public core API.
- The sync bridge **never waits** on an incomplete task. Side-effecting sync overloads refuse **before any I/O** when `Capabilities.CompletesSynchronously` is false (`InvalidOperationException`, message `"provider completed asynchronously; use the async overload"`); read-only sync overloads check completion afterwards, cancel and discard.
- Goldens (`tests/fixtures/`) and `bundles/` are never edited or regenerated; any golden diff is a C# regression (design R5). `docs/spec/SPEC.md` untouched.
- Every concurrency test is driven by `TaskCompletionSource`/counters/events, never by a delay or a wall-clock bound.
- `ConfigureAwait(false)` on every `await` in `src/` libraries (enforced by `CA2007` from Task 2), including `await using` and `await foreach`.
- `Dispose` of `BundleHandle` stays subscription-only (`BundleHandle.cs:149`); no load runs under a caller's token.
- `GetTools` stays bound to the internal `*Tool` wrappers; public `…Async` methods record and require no observation.
- New files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`; warnings are errors; `dotnet format OKF4net.sln --verify-no-changes` before each commit; commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Verify per task: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`. If restore fails with NU1903, update the .NET SDK (10.0.403+); do not suppress the audit.

## Review Focus

Failure modes the spec implies that no single task exercises by itself; each is pinned in the task named.

1. A synchronous `okf verify`/`okf validate` under a real write conflict must retry, not throw (the retry waits are `Thread.Sleep` in sync mode) — Task 4.
2. `OnAccess` callers racing `Invalidate()` must never be served an image older than their required generation — Task 5.
3. A catalog reload that disposes a handle mid-search must still let that search finish its first load — Task 5.
4. A host `onWriteCommitted` callback calling `ReadConcept` on the same root must not deadlock — Task 1.
5. Cancelling a multi-object write after the first commit must report what landed, not throw — Task 4.

---

### Task 1: Write lease over `SemaphoreSlim` (design §3.9 step 1, §3.7, Q7)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`EnterWriteLock` `:415-512`, `t_heldLocks` `:215-216`, `RecordVerifications` `:1157`/`CheckVerificationTargets` `:1192`,`:2000`, `onWriteCommitted` `:296-299`)
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`GetBundle` `:289-295`, `InvalidateBundle` `:303`, `RegenerateIndexes` `:1207`)
- Create: `src/OKF4net/Internal/WriteLease.cs`
- Test: `tests/OKF4net.Tests/WriteLeaseTests.cs`, existing `RecordVerificationTests.cs:549` (#131), #86 alias tests, `BundleConceptWriterHookLockTests.cs`

**Interfaces:**
- Produces: `internal readonly struct WriteLease : IDisposable`; `internal static WriteLease BundleConceptWriter.AcquireWriteLock(...)` (blocking `Wait()` here, `WaitAsync(ct)` flavour added in Task 4); internal `Task<T> OkfBundleTools.RunUnderWriteLockAsync<T>(Func<WriteLease, Task<T>> body, CancellationToken ct)` and a sync twin used until Task 6.
- Behaviour kept: lexical gate then resolved key, always in that order; two aliases of one directory share the lock (#86).

- [ ] **Step 1: Write the failing tests** in `WriteLeaseTests.cs`: `A_second_acquisition_waits_until_the_lease_is_released` (second `Task` not completed while held, completes after dispose, no delay), `Two_aliases_of_one_directory_share_one_lease` (junction, reuse the #86 fixture helper), `A_write_callback_that_reads_the_same_root_does_not_deadlock` (callback calls `OkfBundleTools.ReadConcept` on the same root; assert it completes and ran **after** the lease was released: probe the semaphore from the callback with `Wait(0)` and expect true).
- [ ] **Step 2: Run them** (`--filter FullyQualifiedName~WriteLeaseTests`); expect FAIL (type missing).
- [ ] **Step 3: Implement `WriteLease` and replace `EnterWriteLock`/`t_heldLocks`** with one acquisition per public entry point; internal bodies become `…Locked(WriteLease lease, …)` (covers the two nestings of design §1.4: `RecordVerifications`→`CheckVerificationTargets`, and the three `OkfBundleTools` entries). Run `onWriteCommitted` and the handle invalidation after the lease is disposed, before the public call returns. Sync callers use `SemaphoreSlim.Wait()`.
- [ ] **Step 4: Run** the new tests, `RecordVerificationTests` (#131) **20 times in a loop**, the #86 alias tests, then the full suite. Expected: all pass. The writer-hook test of PR #230 must be adapted to probe the semaphore instead of `Monitor.TryEnter`, with the same four cases red under mutation.
- [ ] **Step 5: Add the CHANGELOG line** (Unreleased/Changed): the `onWriteCommitted` callback now runs after the bundle's write lock is released. Commit `refactor(writer): explicit write lease over SemaphoreSlim, callback after release (#152)`.

### Task 2: Async provider contract, filesystem and read-only providers, test base class (step 2, part 1)

**Files:**
- Modify: `src/OKF4net/Providers/IBundleProvider.cs`, `BundleObject.cs` (`BundleProviderCapabilities`, `BundleObject`), `FileSystemBundleProvider.cs`, `ReadOnlyBundleProvider.cs`, `src/OKF4net.Catalog/PrefixedBundleProvider.cs`
- Create: `tests/OKF4net.Tests/Providers/SyncTestProvider.cs`, `tests/OKF4net.Tests/Providers/CompletionTrackingProvider.cs`
- Modify: `.editorconfig` (CA2007 as error for `src/**` except `OKF4net.Cli`, `OKF4net.Render`, `OKF4net.Mcp`)
- Modify tests: `InMemoryBundleProvider.cs`, `NotifyingBundleProvider.cs`, `TestProviderFactory.cs` and the inline fakes in `BundleConceptWriterProviderTests`, `BundleConceptWriterRetryTests`, `CatalogHandleTests`, `CatalogResolverHandleTests`, `OkfBundleToolsUnknownOutcomeTests`, `RunComputationObservationTests`, `FileMemoryStoreProviderTests`

**Interfaces:**
- Produces, exactly as design §2(C): `ValueTask<BundleVersion> GetVersionAsync(BundleLimits, CancellationToken)`, `IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits, [EnumeratorCancellation] CancellationToken)`, `ValueTask<BundleObject> ReadAsync(BundleKey, BundleObjectVersion?, CancellationToken)`, `ValueTask<bool> ExistsAsync(BundleKey, CancellationToken)`, `ValueTask<BundleObjectVersion> WriteAsync(BundleKey, ReadOnlyMemory<byte>, BundleWriteCondition, CancellationToken)`, `ValueTask DeleteAsync(BundleKey, BundleWriteCondition, CancellationToken)`. `Id`, `Location`, `Capabilities`, `NativeConstraints` unchanged.
- `public sealed record BundleProviderCapabilities(bool CanWrite, bool CompletesSynchronously = false)`; the filesystem provider and `ReadOnlyBundleProvider` over it report `true`.
- `BundleObject` gains `IAsyncDisposable`; the core reads it with `ReadAsync(Memory<byte>, ct)`.
- Test base: `abstract class SyncTestProvider : IBundleProvider` with protected synchronous virtuals (`GetVersion`, `Enumerate`, `Read`, `Exists`, `Write`, `Delete`) wrapped in completed `ValueTask`s and a hand-written `IAsyncEnumerable`; `CompletesSynchronously` true. Every existing fake derives from it.

- [ ] **Step 1: Write the failing tests**: `Filesystem_provider_returns_completed_tasks` (`CompletionTrackingProvider` decorator records `IsCompletedSuccessfully` of every `ValueTask` over a load, a write, a regeneration and a provoked conflict; all true), `Enumerate_stops_at_the_bound_and_disposes_the_enumerator` (infinite listing stops at `MaxEntries + 1`, `DisposeAsync` observed), `The_read_only_decorator_forwards_cancellation`, `A_load_lists_each_directory_once_across_awaits` (design §3.4: `DirectoryListings` counted over an async load equals the sync count).
- [ ] **Step 2: Run them**; expect compile FAIL.
- [ ] **Step 3: Flip the contract.** `FileSystemBundleProvider` keeps every STRICT/lenient predicate and every `Thread.Sleep` retry (`:273`, `:966`, `:1083`) synchronous inside; its async methods return `ValueTask.FromResult`/completed tasks; `EnumerateAsync` is a hand-written `IAsyncEnumerable` over the list it already materialises (`:200-222`), because an `async` iterator without `await` is CS1998. `ListingMemo`/`IProviderScope` flow through `AsyncLocal` unchanged. `PrefixedBundleProvider` forwards the new members. The core is **not** adapted yet: until Task 3 the build is held by a temporary internal `Sync*` extension used only by `Bundle`, `BundleConceptWriter`, `IndexGenerator`, `BundleHandle`; Task 3 and 4 remove it.
- [ ] **Step 4: Migrate the fakes** to `SyncTestProvider`; enable `CA2007` and fix every hit.
- [ ] **Step 5: Run** the full suite; expected: green, goldens untouched. Commit `feat(providers)!: async IBundleProvider, synchronous-completing filesystem provider (#152)`.

### Task 3: Async load path and the non-blocking bridge (step 2, part 2)

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`Load` `:210-396`, `ListNames` `:552-580`, `ReadText` `:597-618`, `TryResolveResourceKey` `:1030-1082`, `ReadResourceText(BundleKey,…)` `:1095-1150`, `ExecutableResources` survey `:394`,`:456-483`)
- Create: `src/OKF4net/Internal/SyncBridge.cs`
- Test: `tests/OKF4net.Tests/Providers/SyncBridgeTests.cs`, `BundleProviderLoadTests.cs`

**Interfaces:**
- Produces: `static ValueTask<Bundle> Bundle.LoadAsync(IBundleProvider, BundleLoadOptions?, CancellationToken ct = default)` (design table); `Bundle.Load(string root)` kept, through `internal static class SyncBridge { T Run<T>(ValueTask<T>, string overload); void Run(ValueTask) ; bool RequireSynchronousProvider(IBundleProvider) }`. `ListNamesAsync`, `ReadTextAtAsync` keep every bound count. `Bundle.Load(IBundleProvider, …)` sync surface is **removed**.
- `ResolveResourceKeyAsync(Concept, string raw, CancellationToken) → ValueTask<(BundleKey?, ResourceResolutionStatus)>` and `ReadResourceTextAsync(BundleKey, BundleObjectVersion? expected, CancellationToken) → ValueTask<(string, BundleObjectVersion)>`; the old sync members are kept as bridged wrappers for `string`-rooted bundles only.

- [ ] **Step 1: Write the failing tests** in `SyncBridgeTests`: `The_bridge_throws_on_an_incomplete_task_and_does_not_wait` (provider whose `ValueTask`s hang on a `TaskCompletionSource`; sync `Load` over it throws `InvalidOperationException` immediately; assert the source is still **unresolved**), `A_sync_write_on_an_async_provider_is_refused_before_any_io` (provider counts calls; zero calls; then complete its pending work and assert **no commit**), `Load_over_the_filesystem_matches_the_sync_golden_bytes` (run `GoldenParityTests` after).
- [ ] **Step 2: Run them**; expect FAIL.
- [ ] **Step 3: Implement `LoadAsync`** with one listing-bound loop and one bounded reader (no fork): sequential in this task (degree 1). `Bundle.Load(string)` = `SyncBridge.Run(LoadAsync(new FileSystemBundleProvider(root), …))`. Remove the temporary `Sync*` extension from the loader.
- [ ] **Step 4: Run** the full suite, the AOT smoke locally if possible (`dotnet publish src/OKF4net.Cli -c Release`), and **record the `okf` AOT size before/after** in the commit message body (design R7). Commit `feat(core): Bundle.LoadAsync and the non-blocking sync bridge (#152)`.

### Task 4: Async writer and index generator (step 3)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs`, `src/OKF4net/IndexGenerator.cs` (`:204-490`), `src/OKF4net/Internal/WriteLease.cs`
- Test: `tests/OKF4net.Tests/BundleConceptWriterRetryTests.cs`, `RecordVerificationTests.cs`, new `tests/OKF4net.Tests/BundleConceptWriterCancellationTests.cs`

**Interfaces:**
- Produces: `…Async(…, CancellationToken ct = default)` twin for every public write method of `BundleConceptWriter` (`WriteConceptAsync`, `AppendToConceptAtomicAsync`, `AppendLogAsync`, `RecordVerificationsAsync`) and `IndexGenerator.RegenerateIndexesAsync`/`…WithAsync`; the `string`-rooted sync overloads stay and go through `SyncBridge` after `RequireSynchronousProvider`.
- `internal enum ExecutionMode { Synchronous, Asynchronous }` passed by the bridges; the `Sleep` seam (`:2382`) becomes `Func<TimeSpan, CancellationToken, ValueTask>` (sync mode `Thread.Sleep`, async mode `Task.Delay`). It governs only retry waits.
- `AcquireWriteLockAsync(ct)` using `WaitAsync(ct)`.

- [ ] **Step 1: Write the failing tests**: `Sync_verify_retries_under_a_real_conflict_instead_of_throwing` (review focus 1: conflict provoked through the existing retry hook, sync entry point, no sleeping), `Cancel_before_commit_leaves_the_object_unchanged`, `Cancel_between_two_objects_returns_the_report_with_the_rest_not_attempted` (Q5: `RecordVerifications` of 3 targets cancelled after the first commit returns `VerificationOutcome` whose `Report` lists 1 landed and 2 `not attempted (cancelled)`, and the handle was invalidated), `Cancel_after_the_first_commit_never_throws`.
- [ ] **Step 2: Run them**; expect FAIL.
- [ ] **Step 3: Convert** the writer and generator bodies to async over the lease from Task 1; cancellation is honoured before a commit and between objects only; after a first commit the operation returns its report. Remove the last temporary `Sync*` extension.
- [ ] **Step 4: Run** `RecordVerificationTests` #131 in a 20× loop, the #86 tests, the full suite, goldens. Commit `feat(writer): async writer and index generator, cancellation reports what landed (#152)`.

### Task 5: Single-flight `BundleHandle`, `ValidateAsync`, `CaptureAsync` (step 4)

**Files:**
- Modify: `src/OKF4net/Providers/BundleHandle.cs` (`_loadLock` `:32`, `Current` `:94-131`, `Refresh`, load `:207-258`, generation counter `:19-23`,`:221`), `src/OKF4net/Validate.cs` (`:353`, `:684`, `:786`, `:954`, `:1033`), `src/OKF4net.Attestation/AttestationOrchestrator.cs` (`Capture` `:140`,`:188`,`:240`)
- Test: `tests/OKF4net.Tests/Providers/BundleHandleTests.cs`, `Catalog/CatalogResolverHandleTests.cs`, `Attestation/…`

**Interfaces:**
- Produces: `Task<Bundle> BundleHandle.GetCurrentAsync(CancellationToken ct = default)`, `Task RefreshAsync(CancellationToken ct = default)`; the blocking `Current`/`Refresh()` are **removed** (a sync host uses the bridge on its own `string` constructors). `Task<ValidationResult> BundleValidator.ValidateAsync(Bundle, TimeProvider? clock, CancellationToken ct = default)`; `Validate` sync kept, bridged. `Task<AttestationCaptureResult> AttestationOrchestrator.CaptureAsync(Bundle, ConceptId, BundleObjectVersion? expected, CancellationToken ct = default)`.
- Handle state: `Task<Bundle>? _inflight` + `long _inflightGeneration` under a short `Lock` (no await inside). A caller joins only if `_inflightGeneration >= requiredGeneration`; otherwise it awaits the old task, then starts or joins one covering its generation. The shared load runs under no caller token.

- [ ] **Step 1: Write the failing tests**: `N_concurrent_GetCurrentAsync_enumerate_once` (provider blocked on a `TaskCompletionSource`; one `EnumerateAsync` counted), `A_caller_that_cancels_does_not_cancel_the_shared_load`, `An_access_after_Invalidate_waits_for_the_old_load_then_covers_its_generation` (review focus 2, ordering forced by events), `A_disposed_handle_still_serves_the_search_on_its_snapshot` (review focus 3, mirrors `A_catalog_reload_during_a_search_leaves_the_search_on_its_snapshots_handles`), `Cancel_mid_load_serves_the_previous_image_and_disposes_the_enumerator`, `ValidateAsync_over_the_filesystem_gives_the_same_verdicts_as_Validate` (including a link-escaped resource `Unsafe`, never `Missing`), `Capture_reads_only_pinned_versions_asynchronously`.
- [ ] **Step 2: Run them**; expect FAIL.
- [ ] **Step 3: Implement** single-flight with the generation-aware join; `IsStale`, `LastLoadError`, `Notify` re-arm and previous-image-on-failure unchanged; raw provider exceptions still wrapped as `BundleLoadException($"provider failure ({type})")`, but `OperationCanceledException` from the caller's token is never wrapped. Port `BundleValidator` to `ValidateAsync` with the existing sync body deleted (one implementation). Port `Capture`.
- [ ] **Step 4: Run** the full suite, then `dotnet test tests/OKF4net.Tests --filter Category=ContainerIntegration` with Docker running and `OKF_DEMO_PG_CONN` set: **0 skipped required**. Commit `feat(handle): single-flight async BundleHandle, ValidateAsync, CaptureAsync (#152)`.

### Task 6: Consumers: tools, context provider, catalog, memory store, MCP (step 5, Q3, Q6)

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`:401-412` bindings, `ReadConceptTool` `:465`, `WriteConceptTool` `:913`, `RunComputationToolAsync` `:1659`, `RunComputation` `:1600`), `src/OKF4net.Agents/OkfContextProvider.cs` (`:221-262`), `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs` (`:120-133`), `FileMemoryStore.cs` (`:130`,`:310`), `PrefixedBundleProvider.cs`, `FileKnowledgeCatalog.cs`, `src/OKF4net.Mcp/OkfMcpToolset.cs`
- Test: `tests/OKF4net.Tests/Agents/`, `Catalog/`, `Mcp/`

**Interfaces:**
- Produces: `Task<string>` internal wrappers `ReadConceptTool`, `GetComputationTool`, `WriteConceptTool`, `RunComputationToolAsync` (the other tools likewise) bound by `GetTools` with unchanged tool names; public `ReadConceptAsync`/`WriteConceptAsync`/`GetComputationAsync`/… (`Task<string>`, record and require nothing); the `string` public methods become `[Obsolete("… removed in the release after 0.8.0")]`, bridged, throwing `InvalidOperationException` on a provider that does not complete synchronously. The public sync `RunComputation` is **removed** (obsolete since 0.7.0). `GetBundleAsync` still takes the write lease (Q6).
- `IBundleProviderFactory.Create` stays sync; its XML doc states "no I/O at construction".

- [ ] **Step 1: Write the failing tests**: `GetTools_stays_bound_to_the_observation_checking_wrappers` (a write without a prior observation is refused through the tool; a public `WriteConceptAsync` is not), `Context_provider_injection_is_not_an_observation`, `Tool_reads_do_not_block_a_hung_provider_thread` (provider hangs on a `TaskCompletionSource`, tool call returns an incomplete `Task`, completes when released), `Memory_store_round_trip_over_an_async_provider`, `Mcp_serves_Task_string_tools_unchanged` (tool names list byte-identical).
- [ ] **Step 2: Run them**; expect FAIL.
- [ ] **Step 3: Port** the wrappers, `OkfContextProvider.ProvideAIContextAsync` (real awaits), catalog sources and `FileMemoryStore`/`PrefixedBundleProvider`; keep the fused-resolver dedup on `Provider.Id`; keep `FileKnowledgeCatalog`'s manifest read synchronous (a host-local file, design §1.2).
- [ ] **Step 4: Run** the full suite, then `dotnet test producers/OkfProducer.sln` locally (must stay green: it only uses `string` entry points). Commit `feat(agents,catalog): async tools, context provider and memory store (#152)`.

### Task 7: Bounded parallel reads and deterministic errors (step 6)

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`LoadAsync` read loop, `:309` shared chunk buffer, `:387` problem sort)
- Test: `tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs`

**Interfaces:**
- Consumes: `BundleLimits.MaxConcurrentRequests` (default 16, `BundleObject.cs:101`) and `ExecutionMode` from Task 4 (degree 1 when `Synchronous`).
- Produces: results written into the slot of their key; concepts keep `mdKeys` order; a global failure reports the **smallest rank** that failed (lower ranks awaited, higher cancelled); an `ArrayPool` buffer per reader.

- [ ] **Step 1: Write the failing tests**: `Parallel_load_gives_the_same_bundle_as_sequential` (all fixtures, compare concepts, order, `ParseProblems`), `Errors_name_the_lowest_failing_rank` (ranks 3 and 7 fail, 7 first; message names rank 3), `Concurrency_never_exceeds_MaxConcurrentRequests` (counter in the provider).
- [ ] **Step 2: Run them**; expect FAIL on determinism or bound.
- [ ] **Step 3: Implement** the bounded parallel reader for `Asynchronous` mode only; the synchronous path keeps degree 1.
- [ ] **Step 4: Run** the full suite, goldens untouched. Commit `feat(core): bounded parallel reads with deterministic errors (#152)`.

### Task 8: Documentation, size record, acceptance (step 6)

**Files:**
- Modify: `CHANGELOG.md`, `README.md`, `CLAUDE.md` (the `Providers/`, `Bundle`/`BundleHandle`, Attestation and Agents bullets: contract async, lease, bridge, `CompletesSynchronously`), `src/OKF4net*/README.md` touched by Tasks 3–6, `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (dated note), `web/src/pages/docs/{Library,Catalog,Agents}.tsx`, `ROADMAP.md`

- [ ] **Step 1: CHANGELOG** (Unreleased): the contract replaced before publication; the two observable changes of R2 (concurrent `OnAccess` comparisons merged, a caller's cancellation no longer interrupts others' load); the `onWriteCommitted` timing (Task 1); the removal of the sync `RunComputation`; the measured AOT size delta of Task 3; the new `…Async` tool methods and the `[Obsolete]` removal date. Update the web pages and run `cd web && npx tsc -b`.
- [ ] **Step 2: Acceptance run** (design §3.11 last bullet): full suite, `dotnet format --verify-no-changes`, no file under `tests/fixtures/` or `bundles/` changed (`git diff origin/dev --stat -- tests/fixtures bundles` empty), AOT publish smoke, `Category=ContainerIntegration` with **0 skipped**, `producers/OkfProducer.sln` green.
- [ ] **Step 3: Commit** `docs: async provider contract (#152)`; report to the maintainer; push and PR only on explicit order, with a base of `dev`.

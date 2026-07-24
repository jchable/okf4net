# OKF4net Lot 1 -- Explicit Memory Policy + Concurrency Fix

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development
> (or executing-plans). Small, self-contained, `OKF4net.Agents`-only. No
> dependency on the local-catalog work (Lot 2). Do this first.

**Goal:** Replace the boolean `EnableMemoryCapture` outright with an explicit
`MemoryCaptureMode { Disabled, SharedBundle }` enum (secure default
`Disabled`), and serialize same-day memory capture under the shared write lock
so the previously-documented E2 concurrent-capture lost-update cannot occur.

**Why now, standalone:** it finishes the Phase-3 memory story, has zero catalog
dependency, and the API break is free because nothing is published yet.

**Baseline:** current tests green (385/385 on the branch), goldens 5/5, format
clean. CLAUDE.md governs (SPDX header, file-scoped namespaces, public XML docs,
`TreatWarningsAsErrors`, `dotnet format --verify-no-changes`). Never touch
`tests/fixtures/`. `OKF4net.Agents`-only + its tests + its docs; no core/CLI
changes; no csproj changes.

## Task 1 -- Replace `EnableMemoryCapture` with `MemoryCaptureMode`

**Files:** `src/OKF4net.Agents/OkfContextProviderOptions.cs`,
`src/OKF4net.Agents/OkfContextProvider.cs`, provider tests, README + Agents
README.

- [ ] Add `public enum MemoryCaptureMode { Disabled, SharedBundle }` with XML
  docs: `Disabled` writes no conversational data; `SharedBundle` writes the
  current deterministic daily memory into the shared bundle, where any session
  that can read the bundle may later retrieve the captured exchange.
- [ ] Replace the `bool EnableMemoryCapture` property with
  `MemoryCaptureMode MemoryCapture { get; init; } = MemoryCaptureMode.Disabled;`.
  **Remove the boolean outright** -- the library is unpublished, so no
  `[Obsolete]` shim. (Grep the solution for `EnableMemoryCapture`; the only
  references are within `OKF4net.Agents` and its tests.)
- [ ] Update `OkfContextProvider.StoreAIContextAsync`'s gate from
  `if (!EnableMemoryCapture)` to `if (MemoryCapture != MemoryCaptureMode.SharedBundle)`.
  Retain the exact current `SharedBundle` write behavior and its security notes.
- [ ] Update every test that enabled capture (`OkfContextProviderMemoryTests`,
  `ContextProviderIntegrationTests`) to set
  `MemoryCapture = MemoryCaptureMode.SharedBundle`; the default-off tests assert
  `MemoryCaptureMode.Disabled` behavior. Add a regression test that the default
  options capture nothing (mirror the existing default-off test, now asserting
  the enum default).
- [ ] Update README + Agents README: the options table row and the memory
  caveat now describe `MemoryCapture` / `MemoryCaptureMode.SharedBundle`
  (opt-in), replacing the `EnableMemoryCapture` wording.

**Exit:** No `EnableMemoryCapture` symbol remains; the default is `Disabled`;
docs match; full suite + goldens green; format clean.

## Task 2 -- Serialize same-day memory capture (E2 fix)

**Files:** `src/OKF4net.Agents/OkfContextProvider.cs` (capture path),
possibly a narrowly scoped internal seam on `OkfBundleTools`; provider tests.

**Context (the race):** `CaptureMemory` reads the existing day-concept body via
`GetBundle()` and builds the new body *outside* the write lock, then calls the
public `WriteConcept`. Two concurrent `StoreAIContextAsync` calls sharing one
provider on the same UTC day can each read the same "before" and the second
`WriteConcept` overwrites the first's appended section, while `AppendLog`
(already locked) records both entries -- a `log.md`/memory count divergence and
a lost section.

- [ ] Make the read-modify-write of the day concept atomic under the same
  `_bundleLock` that already guards `OkfBundleTools`' writes. Prefer a narrow
  internal seam on `OkfBundleTools` (e.g. `internal string AppendToConceptAtomic(
  conceptId, transform)` that takes the lock, re-reads the current body, applies
  the caller's transform, validates, and writes) over duplicating locking in
  the provider -- reuse the existing validated + reparse-guarded + cache-invalidating
  write path, do not fork it. Document the seam.
- [ ] The provider's `CaptureMemory` uses that atomic append so concurrent
  same-day captures each append their own section (last-writer-wins is
  eliminated).
- [ ] Keep `AppendLog` behavior; ensure the memory-section count and the
  `log.md` Memory-entry count cannot diverge for concurrent same-day captures.

- [ ] **Test (must fail without the fix):** `Parallel.For` (e.g. 8x)
  `StoreAIContextAsync` for the same session/day with `SharedBundle`, then
  assert the day concept contains all 8 sections and `log.md` has all 8 Memory
  entries. Sabotage-verify: it fails (fewer sections than log entries) without
  the atomic seam.

**Exit:** Concurrent same-day capture never loses a section; the atomic seam is
covered; full suite + goldens green; format clean.

## Task 3 -- Final validation

- [ ] `dotnet build OKF4net.sln -warnaserror`, `dotnet test OKF4net.sln`,
  `dotnet format OKF4net.sln --verify-no-changes`, and the golden parity tests
  all pass.
- [ ] Grep confirms no lingering `EnableMemoryCapture`.

**Exit:** Lot 1 is mergeable; the Phase-3 memory story is closed
(secure-by-default explicit policy + no concurrent-capture data loss).

# Bundle provider, lot B: the write side — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the provider seam its write side. Every writable provider honours one tested contract (conditions, atomicity per object, implicit prefixes); the filesystem provider commits each object atomically under its guards (issue #157) and becomes writable; and every write the library makes — concepts, verifications, `log.md`, `index.md` — goes through a provider, conditioned on the version it read, retried a bounded number of times when it is a read-modify-write, with no byte of the CLI's output moving.

**Architecture:** The provider layer lives in `src/OKF4net/Providers/`: the write-side value types (`BundleWriteReport`, `BundleWriteFailure`, `BundleWriteConflictReason`, `BundleWriteOutcomeUnknownException`, `BundleWriteNotCommittedException`), the write contract pinned as an abstract xunit base (`WritableProviderContractTests`) that the writable in-memory test provider and `FileSystemBundleProvider` both inherit, and `FileSystemBundleProvider.Write`/`Delete`: exact-name resolution for write, a temporary `.okf-write-<32 hex>.tmp` beside the target, a re-check and commit (`File.Replace` / `File.Move` without overwrite) under a striped in-process lock, a replacement refused without effect made again, the classification of a failed commit, reads that retry the Windows replace window, `CanWrite = true` (Tasks 1, 2a–2c). The core consumers then move onto the provider: `BundleConceptWriter` (capability guard, key validation by the core, tree check before a creation, optional expected version, bounded retries, `log.md` through `AppendLog`, `BundleWriteReport` on `VerificationOutcome`) and `IndexGenerator` (provider overloads returning a `BundleWriteReport`, conditional writes, empty directories no longer referenced). Every existing public signature is kept; nothing in this lot is breaking at the API level.

**Tech Stack:** C# 14 / .NET 10, BCL only in `src/OKF4net` (zero `PackageReference`, no P/Invoke), xunit + `Xunit.SkippableFact` in `tests/OKF4net.Tests`; Docker image `mcr.microsoft.com/dotnet/sdk:10.0` for the mandatory Linux run.

**Spec:** `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, revision 6 as merged in `fa983e4`, read with the maintainer notes it carries (2026-10-19: `Native` is the default key policy and `Strict` an opt-in profile; the provider matches stored names exactly on every OS; the native Windows and macOS sets carry no sibling-collision flag; reserved device names are out of the native Windows set) and with lot A's rulings as recorded in `docs/superpowers/plans/2026-10-12-bundle-provider-a-contract-and-filesystem.md` (r10, r11). Sections implemented here: §1 (write half of the contract, `BundleWriteReport`), §3 (the rest: capability guard at every entry point, implicit parents), §4 (optimistic concurrency, atomic commit, retries), §8 (the filesystem write: guards, confinement re-check before every write, empty directories left in place), §9 rows `BundleConceptWriter` and `IndexGenerator`, and the §10/§11 items they carry. Issue #157 lists the commit pitfalls this lot starts from.

## Decomposition of the spec into lots

The spec is one design but four sequential deliveries; each later lot assumes the previous one is merged into `dev`. Only lot B is planned here.

| Lot | Scope | Spec sections | Status |
|---|---|---|---|
| **A** | `BundleKey` + three-layer validation; contract types; read-only `FileSystemBundleProvider`; `Bundle.Load(provider)`; §6.2 on keys; bounds; validator through the provider; display rule + Windows test | §1, §2, §3 (guard), §6, §7 (partial), §8, §9 (`Bundle`, `ConceptId`, `BundleValidator`), §10/§11 (its part) | merged (PR #207, `fa983e4`) |
| **B** (this plan) | Write side: provider `Write`/`Delete` (`CanWrite` flips to true), SHA-256 object versions on write, `File.Replace`/`File.Move` atomic commit **starting from issue #157's recorded pitfalls** (`ReplaceFile` 1176, temp permissions before content, Unix `File.Move` race, durability statement, orphaned temporaries), `BundleConceptWriter`/`IndexGenerator` on the provider, optional expected version, retries, `log.md` through the writer, `BundleWriteReport`, tree check before creation, empty directories (D2) | §3 (rest), §4, §9 (`BundleConceptWriter`, `IndexGenerator`) | this plan |
| **C** | `BundleHandle`, refresh policies, reload obligation, observations registry and purposes (D1, D5), `okf_run_computation` capture and pinning, executable-resource survey states, D6, `OkfBundleTools`/`OkfContextProvider` on handles | §5, §9 (agents, attestation) | after B |
| **D** | Catalog (snapshot owns handles, provider factories, `MemoryDeleteResult` replaced), Viewer, `okf-mcp` discovery bound, `BundleRoot` obsolete, breaking removals (`Concept.Path`, `Bundle.Root`, `Diagnostic.Path`, `AuditFinding.Path`, …), spec-conformance entry for D6, CHANGELOG consolidation, README/CLAUDE.md (#143) | §9 (catalog, viewer, CLI/MCP), §10 (breaking list) | after C |

Lot B is additive on the public API: new types and overloads beside the existing ones, two exception constructors added, `BundleProviderException`'s constructor made `protected` (it is abstract; only subclasses could ever call it). Behaviour changes are the ones §10 announces for the write side: atomic and conditional writes, `buildBody` called once per attempt, `log.md` written by the writer, an empty directory no longer referenced by its parent's index, `FileSystemBundleProvider.CanWrite`.

## Revision history

- **r1 (2026-10-19)** — first version, from spec revision 6, the decisions B1–B16 taken for this plan (maintainer to confirm) and lot A as merged (`fa983e4`). Every code block was compiled and every test run on Windows 11 (26200, .NET 10.0.8) and in the Linux container (Ubuntu 24.04, .NET 10.0.10) before being written here; the probes that decided the platform behaviour are reproduced with their measured output. The probes forced four decisions not in B1–B16 (B17–B20) and three deviations from B6's wording, listed at the top of this document.
- **r2 (2026-10-19)** — part B's three "Changes required in part A" folded into Tasks 2a and 2b: the early STRICT check of the whole path and the late check of the object's own entry (`ResolveParentForWrite`), the second listing before a name is called folded (`FindExactForWrite`) with its provider test, and the `ParentsResolvedForTest` hook (Task 2b). The whole plan was replayed from `origin/dev`; the counts in Tasks 2a and 2b are the replay's.

- **r3 (2026-10-20)** — the maintainer's revision 2 (the r1 and r2 entries above are the revision the maintainer reviewed), after an executed external review of that revision ("ready after targeted fixes"), each finding folded into the task that owns the code, with its regression test, every code block regenerated from a spike rebuilt one commit per task (`spike/lotb-d`), and the whole plan replayed literally from `origin/dev` again:
  - **F1** (Task 6) — the run's key policy now covers the `index.md` keys `IndexGenerator` produces: a key it refuses (a host's validator included) is neither read nor written, a `Failed` entry in the provider overloads' report and a silent skip under the `string` overloads' contract (B47).
  - **F2** (Tasks 4a, 4c, 5) — a write notification (`onWriteCommitted`) that throws after the commit no longer turns the committed write into a failure: the string methods keep their `Error: {message}` text, a verification batch keeps the concept in `Records` and `Report.Written`, goes on, keeps `Recorded` true when every stamp is on disk and says the notification failed (B46).
  - **F3** (Task 2b; B17, the probe's conclusion, the Review Focus, the CHANGELOG and README texts) — "`File.Replace` never fails the writer" is withdrawn: the review measured `ERROR_UNABLE_TO_REMOVE_REPLACED` (1175) twice in 1 500 commits of probe A3. A replacement refused without effect (1175, or a sharing violation) is made again, at most three times, from the re-check of its condition; then `BundleWriteNotCommittedException` (new), the temporary removed, the target untouched (B43).
  - **F4** (Task 2b) — the classification of a failed commit renews the confinement boundary and the STRICT guards (the temporary's chain, the target's entry) before it reads anything; a refusal is an unknown outcome, the temporary kept (B44). The windows after the last check stay #178's.
  - **F5** (Task 2a) — an unconditional write needs only the right to write its target again, as `File.WriteAllText` did: only `IfMatch` reads the target, and a file the process may write but not read cannot satisfy `IfMatch` (B42; the maintainer's ruling: preserve the old behaviour).
  - **M1** — every count of the plan regenerated from this revision's replay; Task 2c's Linux count and the appendix's description of the red steps corrected.
  - **R1** (Task 2b) — `Read` retries the `ReplaceFile` window on Windows, a bounded number of times (B45); B17 and the probes say what it narrows and what it does not.
  - **R2** (Task 8) — the cost of `okf index` on a large bundle (+19 % to +25 %), measured again and recorded in the CHANGELOG.
  - The two timing-dependent tests the review saw fail once each are named in the Global Constraints, with a third that failed once in this revision's replay.
  - The decisions B42–B47 record these changes; probes A4 (the read window through `Read`, before and after B45) and A5 (a file the process may write but not read) were added and run.

- **r4 (2026-10-20)** — after a second executed external review, of r3 ("ready after targeted fixes"), the fix folded into the tasks that own the code on a spike rebuilt from `spike/lotb-d` one commit per task (`spike/lotb-e`), every changed code block regenerated from it, and the whole plan replayed literally from `origin/dev` again:
  - **F6** (Tasks 4a, 4b, 4c, 5) — a write notification that threw `BundleWriteConflictException` (`ConditionFailed`) after a successful `provider.Write` was taken for a refused commit, because r3 invoked it inside the write primitive, inside the retry delegate and inside the catch that translates a refused condition: `AppendToConceptAtomic` and `AppendLog` ran `buildBody`, the write and the notification again (the review's repro: two or three appended copies on disk), `WriteConcept(…, IfMatch)` answered that its valid condition had been refused although the write was on disk, and `RecordVerifications` prepared, wrote and notified the concept three times. B46's tests used only `IOException`, which neither the retry loop nor the condition translation intercepts. The write primitive now reports its commit and never notifies; each entry point notifies once its whole write operation — every attempt included — has returned, outside every retry delegate and every catch of a conflict (B48). New regressions for each entry point with a conflict raised once and on every call, B46's tests extended to an `OkfException` and a conflict, a real conflict on the next concept of a batch after a failing notification, and a cancellation; every conflict case fails against r3's structure.
  - **Cancellation** (B48) — an exception the writer does not convert, `OperationCanceledException` included, propagates unchanged once the write is committed, and ends a verification batch there (r3 behaved the same but did not say so); now documented on the constructor's `onWriteCommitted` parameter and tested (Tasks 4a, 5).
  - **M1** — the appendix's count of Task 2a Step 4 (36/2/38 since F5), and every count of Tasks 4a–8, regenerated from this revision's replay; the counts that still said "in the drafting run" (Tasks 3, 4a, 4b, 4c, 5) are the replay's now, and Task 6's Linux count, left from r2, is the replay's too.
  - **Performance wording** (Task 8 Step 4's CHANGELOG bullet, Step 5 (g)) — "15–30 %" and "`validate` within ±2 %" replaced by the measured variability: `okf index` +17.7 % to +24.9 % slower in eleven of twelve series (three of them this revision's) and +37.5 % in one (the r3 review's first), `okf validate` between −1.3 % and +9.5 % with overlapping ranges, no stable slowdown established; accepted for lot B, tracked by #208.
  - The decision B48 records the separation of the commit from its notification.

- **r5 (2026-10-21)** — documentation only, after a third executed external review, of r4 ("ready", one minor finding); no code changed, so the plan was not replayed:
  - **F7** (Tasks 4a, 5; B48) — the summary of `WriteValidatedContentLocked` called it the single write primitive of the class, through which every byte it writes goes; since Task 5, `AppendLog` writes `log.md` through its own conditioned `provider.Write`. The summary now names the writes that go through it (every `WriteConcept` overload, `AppendToConceptAtomic`, `RecordVerifications`), a comment at `AppendLog`'s write says that it does not and that its commit still reaches `WriteThenNotify`, and B48 names `AppendLog` separately.
  - **Performance wording** (Task 8 Step 4's CHANGELOG bullet, Step 5 (g)) — the review's three series added (`okf index` +36.0 %, +26.8 %, +24.4 %; `okf validate` −0.1 %, +3.2 %, +2.2 %): over all fifteen series, `okf index` +17.7 % to +37.5 %, typically about +24 %, and `okf validate` −1.3 % to +9.5 % with overlapping ranges; still accepted for lot B and tracked by #208.
  - The three code blocks these changes touch were regenerated (`regen.js`) from a spike amended one task commit at a time (`spike/lotb-f`, from `spike/lotb-e`: Tasks 4a, 5 and 8), whose twelve commits are identical to `spike/lotb-e`'s once comment lines are removed, apart from Task 8's CHANGELOG bullet; the other 229 of the 232 code blocks are byte-identical to r4's, and every csharp/markdown block is found verbatim in `spike/lotb-f` (`checkblocks.js`, with r4's one exception, Task 7's scratch `toolsdiff/Program.cs`). The core library builds with 0 warnings at Task 4a's commit and the solution at the last one, `dotnet format --verify-no-changes` passes, and the writer, verification, tool, index and provider write classes give 493/7/500 on both spikes.

## Global Constraints

- **Branch and worktree.** Execute in a dedicated worktree on branch `feat/bundle-provider-b`, created from `origin/dev` — never in `E:\Sources\okf` (the shared checkout, where `dev` is checked out): `git fetch origin && git worktree add .claude/worktrees/bundle-provider-b -b feat/bundle-provider-b origin/dev`. A branch created from `origin/dev` **inherits `origin/dev` as its upstream**, so a bare `git push` would push to `dev`: run `git branch --unset-upstream` right after creating it, and push only with `git push -u origin feat/bundle-provider-b`. Never `git checkout dev`. Do not push before the lot is reviewed.
- **Zero third-party runtime dependencies in `src/OKF4net`** — BCL only, no `PackageReference`, **no P/Invoke**. `InvariantGlobalization` stays on in `OKF4net.Cli` and `OKF4net.Render`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on every public member (missing docs fail the build), nullable on, `TreatWarningsAsErrors`, LangVersion 14. **An XML `cref` may only name a type that exists at that task**; use `<c>…</c>` for one a later task adds. Platform-specific APIs (`File.GetUnixFileMode`, `File.SetUnixFileMode`, `FileStreamOptions.UnixCreateMode`) must sit behind `!OperatingSystem.IsWindows()` in the same condition, or in a test method marked `[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]` — CA1416 is an error, and `Skip.If(OperatingSystem.IsWindows(), …)` is not a guard the analyzer recognises.
- **Source files are CRLF in the working tree** (`core.autocrlf`; the index holds LF). The file-writing tool produces LF; `Edit` on an existing CRLF file keeps CRLF. After writing a **new** `.cs` file, convert it with `unix2dos -q <file>`. **Never run MSYS `sed -i` on a `.cs` file**: it strips every CR (it did, during the drafting of this plan). Before each commit, for every touched `.cs` file, `tr -cd '\r' < f | wc -c` must equal `wc -l < f`. Markdown files may be either; git stores LF.
- **Tool-layer pitfalls.** The file-writing tool decodes `\uXXXX` escapes and collapses `\\` in the text it writes: the code in this plan avoids `\\` in C# literals (it uses `Path.DirectorySeparatorChar` and verbatim `@"…"` strings); if you must write one, write a placeholder and substitute it with a script, then check the bytes with `cat -A` or `od -c`. A literal NFD string is indistinguishable on screen from its NFC twin — keep `\u` escapes in C# source (build the string at run time).
- **`tests/fixtures/` is never edited and no snapshot is regenerated.** If a golden comparison turns red, STOP and report: lot B promises byte-identical CLI output (`GoldenParityTests`' index test also counts exactly 8 files in its bundle, so a leftover temporary would turn it red).
- **Path-safety guards use the STRICT predicates** (`ReparsePoints.IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor`). This lot adds **no** lenient site (the orphan listing reads through the provider's existing walk, `Enumerate`) and removes two: Task 6 moves `IndexGenerator` onto the provider, which deletes its own two walks, and brings `CLAUDE.md`'s count from four sites to two in the same commit. A guard runs at the moment of the access — for a write, again under the commit lock, immediately before the commit call.
- **Provider errors are translated at the core boundary** (§7): a provider's own exceptions carry library-authored messages that name keys, never host paths; a raw `IOException`/`UnauthorizedAccessException` from the filesystem (a pre-commit failure) is passed up as is, and the core turns it into its own message.
- Each task adds its own `CHANGELOG.md` entries under `## [Unreleased]` (`### Added` for new API, `### Changed` for behaviour), describing behaviour, not commits.
- Commit messages say only what the code does. End each with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage files by name — **never `git add -A`**.
- **Tests.** `dotnet build OKF4net.sln` with 0 warnings, `dotnet format OKF4net.sln --verify-no-changes`, and `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` green at the end of every task. On Windows without Developer Mode the file-symlink tests skip; the junction tests run (junctions need no privilege). **The Linux container run is mandatory** for the `Providers` tests at the end of every task that touches them, as root (POSIX modes, symlinks) **and** as an unprivileged user (a read-only file only refuses a non-root writer). From the worktree root, in Git Bash:

  ```bash
  SRC="$(pwd -W)"   # Windows path of the worktree, for Docker Desktop
  MSYS_NO_PATHCONV=1 docker run --rm -v "$SRC:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -e; mkdir -p /work; cd /src
    tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" --exclude=./producers -cf - . | (cd /work && tar xf -)
    cd /work && dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Providers" 2>&1 | grep -E "Passed!|Failed!|^\s+Failed |Error Message" -A3
    useradd -m tester && chown -R tester /work
    su tester -c "cd /work && dotnet test tests/OKF4net.Tests --filter FullyQualifiedName~FileSystemBundleProvider 2>&1 | grep -E \"Passed!|Failed!|^\s+Failed \" -A3"'
  ```

  Expected: both runs `Passed!`, `Failed: 0`.
- **Two timing-dependent tests outside lot B.** If exactly `AttestationOrchestratorTests.A_token_ignoring_stage_is_abandoned_when_the_token_fires("blocking")` and/or `OkfComputationToolsTests.A_token_ignoring_attester_cannot_outlast_the_timeout_or_display_its_result("blocking")` fail alone in a full run (each about a 5 s timeout, seen once each under load by the external review), rerun their class before concluding anything: they are timing-dependent and lot B does not touch the code they test. The same holds for `CliContainerEngineRunTests.A_hung_engine_kill_does_not_hang_the_timed_out_run`, which failed once (11 s) in r3's second replay and passed three runs of its class right after.
- `dotnet test producers/OkfProducer.sln` green locally before the lot is declared done (the producer uses `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` and `IndexGenerator.RegenerateIndexes(string)`; `producers/` is outside CI by decision).

## Deviations from decisions.md

The drafting of this plan implemented every decision of `decisions.md` (B1–B16) except the points below,
each backed by a probe, a failing test or the spec. They are recorded as decisions further down.

### Deviations from decisions.md (provider)

Three points of B6 could not be implemented as worded. Each is backed by a probe in the
"Probes" section below; the plan implements the alternative and records it as a new decision.

1. **B6 (1) — "refuses to create a name that differs only by case/Unicode form from an
   existing sibling".** Implemented instead as: *refuses to create a name **the volume** would
   confuse with a stored entry* (B19). A fold-based refusal (`FoldSegment`, or an
   `OrdinalIgnoreCase` compare) would refuse on ext4 a name the lot A loader accepts under
   `Native`: probe P7 on Linux shows `Twin` and `twin` coexisting as two directories, and lot A
   resolves each to itself (`On_a_case_sensitive_volume_names_differing_only_by_case_each_resolve_to_themselves`;
   the native Linux set carries no collision flag, so the load accepts both).
   The provider therefore asks the volume: after the exact listing finds no entry of that name,
   an ordinary OS lookup (`File.Exists || Directory.Exists`) that *does* find one proves the
   volume folds it (probe P7 on Windows: `Directory.Exists("twin")` is `true` beside `Twin`, and
   `Directory.CreateDirectory("twin")` silently reuses `Twin`). That is exact on every volume —
   NTFS, APFS, a vfat or CIFS mount under Linux — and never over-refuses. Portability collisions
   (`A/x.md` + `a/y.md` on a case-sensitive volume) stay the core's business under the policy
   (B10), as decided.
2. **B6 (2)/(3) — "temporary … `UnixCreateMode = UserRead | UserWrite` (0600)" for every write,
   then "the `0666 & ~umask` default for a new object".** No BCL API reads the umask, and P/Invoke
   is excluded. Implemented as (B18): a **replacement's** temporary is created 0600 and gets the
   target's mode through its handle before it is closed (exactly B6); a **new object's**
   temporary is created with the default mode (`UnixCreateMode` unset), which is the mode the
   committed object must have — probe P3 shows the default equals `File.WriteAllText`'s under
   umask 022 (0644) and 077 (0600). The exposure #157 describes (a 0600 target's next content
   readable through a looser temporary) concerns replacements only; a new object's bytes are
   never more readable through its temporary than through the object itself.
3. **B6 (5) and B8 — the commit is not reader-atomic on Windows.** Probe A3 measured, with two
   readers looping while a writer commits 300 times: `File.Replace` never yields a torn object,
   but a concurrent reader finds **no entry under the name** or meets a **sharing violation**
   for a moment — over five runs of the same code (1 500 commits, 192 649 opens, the external
   review), 3 988 opens found no file (2.07 %) and 3 426 met a sharing violation (1.78 %) — and
   the writer itself is not immune: `File.Replace` failed twice with `ERROR_UNABLE_TO_REMOVE_REPLACED`
   (1175), which changes nothing; `File.Move(overwrite: true)` keeps the name visible but **fails
   the writer** (access denied) in 86 % to 92 % of commits while a reader holds the file, and
   drops the target's DACL. The plan keeps `File.Replace` (the only BCL primitive that is
   crash-atomic and survives open readers), documents the window (B17), makes a replacement
   refused without effect again (B43), and — the maintainer's ruling R1 — retries the window in
   `Read` a bounded number of times (B45), which narrows it without closing it. B8's goal — a
   read never denies a replacement the sharing it needs — holds and is tested. A POSIX-semantics
   rename through P/Invoke stays excluded by the no-P/Invoke rule.

Also measured, not a deviation: on Unix `File.Move(temp, target, overwrite: false)` is an `lstat`
of the target followed by `rename` (strace, probe P2), so another **process** creating the name
in between is silently replaced; the in-process commit lock closes the race inside one process,
and the inter-process window is the #178 one (B20).

### Deviations from decisions.md — writer and `log.md`

Each point below was forced by an executed test or by the code as it is; the plan implements the
alternative and records it as a decision (B29–B35, next section).

1. **B4 — the optional expected version is a `BundleWriteCondition`, not a `BundleObjectVersion?`.**
   Lot C's tools must write under three observations (spec §4, D1): a version read (`IfMatch(v)`),
   "absent" (`MustNotExist`), and none (refusal before the writer is called). A nullable version
   expresses only the first and "unconditional", so the "absent" observation could not reach the
   writer. The new overloads take `BundleWriteCondition` (`None` is today's unconditional write;
   the existing three-argument overloads are unchanged and delegate with `None`). Pinned by
   `BundleConceptWriterRetryTests.A_creation_conditioned_on_absence_refuses_an_existing_concept`.
2. **B15 — two refusal texts cannot be kept byte for byte.** The writer had two reparse-point
   messages: one for the path (`… resolves through a reparse point (symlink/junction), or an
   entry that could not be inspected, inside the bundle, which is not allowed.`) and one for the
   target's own entry (`… is a reparse point (symlink/junction) or could not be inspected, not a
   regular file -- refusing to overwrite it.`, and `… -- refusing to write through it.` for
   `log.md`). Behind the provider both are one `BundleAccessRefusedException` whose `Detail` is
   provider text the core must not show (spec §7), so the writer cannot tell them apart. Every
   refusal now carries the first text, the one B15 names. Two Windows tests asserted the second:
   `BundleConceptWriterTests.WriteConcept_refuses_a_target_file_that_is_an_uninspectable_junction`
   (Task 4a) and `OkfBundleToolsTests.AppendLog_refuses_a_log_file_that_is_an_uninspectable_junction`
   (Task 5) change their expected string, with the reason in a comment; executed: each fails
   with exactly that text difference before the edit (Task 4a Step 6, Task 5 Step 6).
   *Maintainer call:* keeping both would need a closed field on `BundleAccessRefusedException`
   (the path, or the object's own entry) in part A.
3. **B10 — what the tree check reads.** It does not enumerate every parent prefix one by one:
   when the effective constraints fold names (`CaseInsensitiveCollisions` or
   `UnicodeFormCollisions`) it reads one listing of the whole bundle, which holds the children of
   every parent prefix and every object on and under the key's path; otherwise one `Exists` per
   proper prefix and one listing under the key itself. And it checks only what the loader
   condemns under the same validator (spec §2: "sous la politique active"): sibling twins only
   under a folding flag, the file/directory conflict only under `FileDirectoryConflict`. Every
   native filesystem set has `FileDirectoryConflict` and no folding flag, so under the default
   `Native` policy a creation costs one existence check per proper prefix and one listing under
   the key; under `Strict` it lists the whole bundle once per creation (`BundleTreeCheck`'s
   summary says so).
4. **B16 — `BundleRoot` over another provider throws `NotSupportedException`**, as spec §9
   says; over the filesystem provider it is the root as given, as before.
5. **Part A needed three changes**: two for the writer's guards and its test hook to keep their
   meaning, and one race the writer's concurrency test found in `FindExactForWrite`. They are
   folded into part A's Tasks 2a and 2b (next section).

## Changes required in part A — folded

The writer needs three things of `FileSystemBundleProvider` that part A's first draft did not give: a STRICT check of the whole path before anything is listed (PA-1: otherwise a parent that denies listing surfaced as a raw `Access to the path '…' is denied.` instead of a refusal — `BundleConceptWriterTests.WriteConcept_refuses_a_path_crossing_a_junction_whose_link_status_cannot_be_inspected` failed that way), a late check of the object's own entry with a hook at its place (PA-2: `ParentsResolvedForTest`, which `BeforeLateReparseCheckForTest` forwards to, so that the five existing late-guard tests keep their meaning), and a second listing before a name is called folded (PA-3: two writers creating one new directory; `Two_writers_without_a_shared_lock_lose_no_append_over_fifty_rounds` failed twice in twelve runs before it). They are now part of part A: Task 2a's `ResolveParentForWrite` and `FindExactForWrite` and its test `Writers_creating_one_new_directory_at_the_same_moment_all_succeed`, and Task 2b's hook (its edits (a) and (f) — (h) before r3 relettered them). Nothing remains to apply here.

### Deviations from decisions.md (index, callers, verification)

Each point was forced by an executed run; the plan implements the alternative and records it as a decision (B36–B41, next section).

1. **B13 — "Index content is derived from the provider's listing of `.md` keys (no directory objects), so a directory with no `.md` beneath it is not referenced."** Implemented instead as the spec states it (§2 D2, §10, §11 "Dossiers vides"): a subdirectory is referenced when **an object of any extension** lies under it. B13's wording contradicts §11 ("un dossier qui ne contient que des fichiers non-`.md` y apparaît toujours") and would have changed written bytes on the repository's own fixtures: `tests/fixtures/okf_v02_computation` has three directories holding only non-`.md` files (`references/attesters` with `revenue.py`, `references/computations` with `revenue.sql`, `malformed/refs` with `query.sql`), which `origin/dev` lists in `references/index.md` and `malformed/index.md`. With the spec's rule the differential of Task 7 leaves every one of the 93 files of the eight sample and fixture bundles byte for byte as `origin/dev` does; the only written difference left is the empty directory itself (B36).
2. **B13 — "the string overloads … keep … their exception behaviour."** Kept for every cause the string overloads met before, with one exception that no test or fixture reaches: a **directory named `index.md`** with files under it, where its parent's index must go. `origin/dev` threw `UnauthorizedAccessException` from `File.WriteAllText` over the directory (`error: Access to the path '…\x\index.md' is denied.`); the provider refuses that creation before any write, and the string overload throws the exception the filesystem provider throws for a directory under a key, `BundleWriteConflictException` with `NameUnavailable` (`error: 'x/index.md' cannot be stored under that name: …`). Same exit code, same files written before it. Pinned by `On_the_filesystem_a_directory_named_index_md_ends_the_run_with_a_conflict`, recorded in the CHANGELOG and the spec (B38).
3. **D2's scope, one input wider than "empty".** A directory whose only entries are symlinks or junctions holds no object either (no listing follows a link), so it is no longer referenced; `origin/dev` referenced it, being a real directory. Executed on both builds with a junction (Windows): `origin/dev` writes `* [onlylinks](onlylinks/index.md)`, this branch does not. Pinned by `A_directory_holding_only_links_is_not_referenced`, recorded with the empty directories (B36).
4. **Not a deviation, a pre-existing failure found on the way:** `samples/acme-retail-agent` does not build on `origin/dev` (`fa983e4`): `NU1605 … Detected package downgrade: Microsoft.Agents.AI from 1.24.0 to 1.20.0` — a dependabot bump of `src/OKF4net.Agents` to 1.24.0 left the sample's own pin at 1.20.0. Lot B does not touch it; Task 7 shows the sample's code compiles against this branch once the pin is raised in a scratch copy (both builds, 0 errors), and the fix is left to a separate change.

## Decisions taken by this plan (not in the spec — for the maintainer to confirm)

B1–B16 were taken before drafting; B17 onwards were forced by the probes or by an executed test. Where the spec and the code disagree, the code is a fact and the spec a decision; where a lot A ruling changed the spec, the ruling wins.

**B1. The default filesystem provider becomes writable.** `FileSystemBundleProvider.Capabilities = new(CanWrite: true)`. `Bundle.Load(...)` never writes; a host that wants a read-only view uses `provider.AsReadOnly()` (already shipped). No new constructor flag.

**B2. Write keys are validated by the core under the same policy as loading.** The writer builds a `BundleKeyValidator` from `provider.NativeConstraints` and a `BundleLoadOptions` (default: `Native`, default bounds); a key the validator refuses is refused before any I/O with the validator's reason. The provider validates nothing itself (spec §1) except its own aliasing guarantees (exact-name matching, STRICT guards, B19).

**B3. The capability guard throws.** Every write entry point of the core — `BundleConceptWriter.WriteConcept` (both overloads), `AppendToConceptAtomic`, `RecordVerifications`, the new `AppendLog`, the `IndexGenerator` provider overloads — checks `provider.Capabilities.CanWrite` first and throws `BundleReadOnlyException` (library-authored message, no host path) with no side effect: no directory, no temporary, no log. It sits outside `RunTool`'s `Error:` channel (a read-only provider is a host mistake, not a content error); the provider repeats the guard as the last rampart. `CheckVerificationTargets` (read-only) is not guarded.

**B4. Optimistic concurrency per object.** A write carries a `BundleWriteCondition`: `None` (unconditional, today's behaviour), `MustNotExist`, `IfMatch(version)`. `WriteConcept` gains overloads taking an optional `BundleObjectVersion? expected` (existing signatures unchanged and unconditional). Filesystem object version = lowercase hex SHA-256 of the stored bytes, as `Read` already computes; the in-memory test provider uses the same.

**B5. Bounded retries.** Read-modify-write operations (`AppendToConceptAtomic`, `RecordVerifications`, `AppendLog`, index regeneration) retry on `BundleWriteConflictException`: 3 attempts, random growing delay (first 10–40 ms, then 30–120 ms), `Thread.Sleep` on the caller's thread (the contract is synchronous until #152), re-reading the current state each attempt. `AppendToConceptAtomic`'s `buildBody` is called once per attempt (at most 3), always on the freshly re-read body; its documented contract changes from "exactly once" to "pure function of the body it receives" (CHANGELOG). Writes conditioned on a caller-supplied version never retry. After the attempts, the conflict is returned as the writer's `Error: …` string.

**B6. Atomic commit on the filesystem (issue #157)** — as implemented, with the three adjustments listed under "Deviations": (1) the provider checks every segment storable and STRICT-checks the whole path as the OS would follow it before listing anything, then resolves the target with exact-name matching of every existing segment, STRICT-guards each traversed directory and the confinement boundary, creates each missing parent directory and lists it again to prove the volume stored it under that exact name, then re-checks the whole chain and the object's own entry; it refuses a name the volume would confuse with a stored entry (B19); (2) the temporary `.okf-write-<32 random hex>.tmp` is created in the target's directory with `FileMode.CreateNew` and `FileShare.None`, owner-only (0600) on Unix for a replacement (B18 for a new object); (3) the bytes are written, a replacement's temporary gets the target's mode **through its handle**, the stream is flushed to stable storage (`Flush(flushToDisk: true)`) and closed; (4) under the in-process commit lock (B26) the boundary, the chain, the temporary itself (no link put in its place) and the target's exact name are STRICT-checked again, the target's current SHA-256 is read and the condition checked; (5) existing object → `File.Replace(temp, target, null)`; absent → `File.Move(temp, target, overwrite: false)`; (6) a failure before the commit call removes the temporary and leaves the target untouched; a failure of the commit call is classified from the disk (B21); an unknown outcome keeps the temporary and raises `BundleWriteOutcomeUnknownException` (library-authored message naming the key, raw exception as `InnerException`).

**B7. Durability promise (documented, not stronger).** After `Write` returns, the bytes were flushed to stable storage before the rename. A process crash or an I/O error mid-write leaves the whole old or the whole new content. A power loss can also leave the old content, or for a brand-new object no object, and a temporary behind: the directory entry is not fsynced (no BCL API). Stated in the XML docs and the README.

**B8. Readers must not block a replacement.** `FileSystemBundleProvider.Read` opens with `FileShare.ReadWrite | FileShare.Delete`, so a read never denies a concurrent `File.Replace` or `File.Delete` the sharing it needs on Windows (it does not prevent the transient failure of B43). Tested by a reader that holds the file open and by readers looping over `Read` while a writer replaces (red before the change: the writer's sharing violation, in every one of five runs).

**B9. Orphaned temporaries.** The core ignores them (not `.md`); they count toward the entry bound like any file. The provider exposes an internal test seam (`ListOrphanedTemporaries`) that reads through `Enumerate` (no new walk). No cleanup API; the README says an indeterminate failure can leave one, that `okf validate` ignores it and that a producer's `RequireEmpty` sees it.

**B10. Tree check before creation, in the core, under the writer lock.** Before the first byte of a creation (key absent at the provider), the writer enumerates the children of every parent prefix at the provider, compares names with `validator.FoldSegment`, and refuses — before any I/O side effect — a sibling folding to the same name but different, a proper prefix of the new key that is an object, or an object under the new key taken as a prefix. The message names the conflicting name. Updates skip it. Documented as non-atomic across processes (#178).

**B11. Locks.** `EnterWriteLock()` and its test hooks keep their signatures. On the filesystem the existing lexical + resolved registry is kept unchanged; for any other provider the writer's lock key is `IBundleProvider.Id`. `IndexGenerator` stays lock-free; its writes are conditional, so a race surfaces as a conflict and a retry, not a lost update. The provider's own commit lock (B26) is a separate, inner lock.

**B12. `log.md` through the writer.** New `BundleConceptWriter.AppendLog(string kind, string text, DateOnly? date = null)` doing exactly what `OkfBundleTools.AppendLog` does today (field guards with `GuardLogField`'s messages moved verbatim, lock, strict UTF-8 read, `ChangeLog` parse/insert, render, write) through the provider with `IfMatch`/`MustNotExist` and the retries of B5, returning the same `Appended a '{kind}' entry under {date} in log.md.` / `Error: …` strings. `OkfBundleTools.AppendLog` delegates and keeps invalidating its cache through `onWriteCommitted`. `WriteConcept` still refuses the concept ids `log` and `index`.

**B13. `IndexGenerator`.** New `RegenerateIndexes(IBundleProvider provider, …)` overloads (with and without `Synthesize`) returning a `BundleWriteReport`; the existing `RegenerateIndexes(string)`/`RegenerateIndexesWith(string, Synthesize)` delegate to them through a `FileSystemBundleProvider` and keep their return type, order and exception behaviour. Index content is derived from the provider's listing of keys, so a directory with no object beneath it is not referenced (D2); a directory holding only non-`.md` files still is (spec §11; part C's B36 — the first wording, "of `.md` keys", would have dropped three directories of `tests/fixtures/okf_v02_computation` from their parents' indexes). `Synthesize.relativeDir`: OS separator for the `string` overload, `/` for the provider overload. Writes: `IfMatch(version read)` for an existing `index.md`, `MustNotExist` otherwise, with B5's retries; an unreadable root `index.md` is skipped as today; a STRICT refusal skips the directory silently in the `string` overload and is a `Failed` entry in the provider overload's report.

**B14. `BundleWriteReport`.** `public sealed record BundleWriteReport(IReadOnlyList<BundleKey> Written, IReadOnlyList<BundleKey> Deleted, IReadOnlyList<BundleWriteFailure> Failed)` with `Succeeded` and `Empty`, and `public sealed record BundleWriteFailure(BundleKey Key, string Reason)`; null lists and an empty reason are refused. `VerificationOutcome` is enriched with a `Report` property (never replaced); `RecordVerifications`' partial-failure message keeps its `— already written: …` suffix byte for byte.

**B15. Error strings are preserved.** Every `Error: …` text of the writer and of `AppendLog` is byte-identical after the move. Provider exceptions are translated into the existing texts where one existed (reparse point: `… resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.`); new situations get new library-authored texts.

**B16. Unchanged on purpose.** `BundleConceptWriter(string bundleRoot, Action? onWriteCommitted)` stays and builds `new FileSystemBundleProvider(bundleRoot)`; `BundleRoot` stays (its `[Obsolete]` is lot D); `FileMemoryStore` keeps constructing the writer from a string; `okf parse`/`fmt` stay outside the seam; `okf index` and `okf verify` output bytes do not change; no file under `tests/fixtures/` is touched.

**B17. On Windows the commit is atomic for the stored state, not for concurrent readers** (probe A3). While `ReplaceFile` runs, a reader can for a moment find no entry under the name, or meet a sharing violation; it never reads a torn object. Measured over five runs (1 500 commits, two readers, 192 649 opens, the external review): 3 988 opens found no file (2.07 %), 3 426 met a sharing violation (1.78 %), no torn read; and twice the writer's own `File.Replace` failed with 1175 (B43) — the commit is not immune to the readers' window either, although no cause is established. The plan keeps `File.Replace` and documents the window in `IBundleProvider.Write`'s contract, the provider's remarks, the README and the CHANGELOG. `Read` retries it on Windows (B45, the maintainer's ruling R1): the window narrows, it does not vanish — a listing made while the swap runs still finds no entry under the name, a key the listing misses is not retried, and a read can still fail after its last attempt (probe A4).

**B18. A new object's temporary gets the default create mode, a replacement's is 0600** (Deviation 2). No umask API exists in the BCL; the default mode is exactly what `File.WriteAllText` gave a new file (probe P3), and a new object's bytes are no more readable through the temporary than through the object. A replacement's temporary is created 0600 and gets the target's mode by handle before close.

**B19. "Would confuse" is decided by the volume, not by a fold** (Deviation 1). A name is refused with `BundleWriteConflictReason.NameUnavailable` when (a) Windows cannot store it literally (a character no Windows name carries, a drive form, a trailing dot or space — `x.` would be stored as `x`, `a:b.md` would be an alternate data stream of `a`), or (b) the exact listing has no entry of that name while an OS lookup finds one and a second listing still has none (the volume folds case or Unicode form; a name another writer creates between the listing and the lookup is not a folded name), or (c) a directory stands where the object would be, or a file where a parent directory is needed. A missing directory is created, then listed again: if the volume stored it under another name, the write is refused.

**B20. Unix creation is not race-proof across processes** (probe P2, strace). `File.Move(overwrite: false)` is `lstat` + `rename`; inside one process the commit lock serialises every check-and-commit of an object, across processes the window is #178's.

**B21. A failed commit is classified from the disk.** After the commit call throws: a replacement refused without effect is made again first (B43); Windows `ERROR_UNABLE_TO_MOVE_REPLACEMENT` (1176) and `_2` (1177) are always unknown; otherwise, once the boundary and the STRICT guards are renewed (B44), the temporary still present means the rename did not happen — target unchanged: the raw failure is rethrown (determinate, temporary removed); target changed: another writer landed, `BundleWriteConflictException` (temporary removed) — an unconditional replacement does not read its target (B42), so its failure with the temporary in place is the raw failure; temporary gone and target holding the new bytes: committed, success; anything else, or a state that cannot be read: unknown. 1176 cannot be provoked on demand, so it is tested through a commit hook that leaves the disk exactly as `ReplaceFile` documents it.

**B22. A target the process may not write is refused as before.** `File.WriteAllText` refused a read-only file; a rename needs only the directory's permission on Unix (and would silently replace a 0444 file). The commit opens the target for writing (nothing is written through that handle) before replacing it, so a read-only file, a mode or ACL denying writing, or another program's handle denying write sharing, refuses the write before anything changes. Only an `IfMatch` write opens it for reading too (B42).

**B23. Delete semantics.** A condition reads the same for `Delete` as for `Write`; deleting an absent object under a condition that holds (`None`, `MustNotExist`) does nothing and is not an error; `IfMatch` on an absent object is a conflict. A key that names a directory names no object. Empty directories are left in place.

**B24. Two exception shapes are added to the contract.** `BundleWriteConflictException` gains `Reason` (`BundleWriteConflictReason { ConditionFailed, NameUnavailable }`, a closed enum, so the message is built from it, never from provider text; the one-argument constructor and its message are unchanged). `BundleWriteOutcomeUnknownException(BundleKey, Exception)` reports a commit whose outcome could not be established. `BundleProviderException` gains a `(message, inner)` constructor; its constructors become `protected` (it is abstract).

**B25. Determinate filesystem failures stay raw.** A failure before the commit (a sharing violation, a denied directory, a full disk) leaves the provider as the original `IOException`/`UnauthorizedAccessException` — as `Read` does in lot A — and the core translates it; the writer's `RunTool` already turns both into `Error: …`.

**B26. The commit lock is striped and static.** 64 `System.Threading.Lock` objects, picked by `StringComparer.OrdinalIgnoreCase` hash of the target's full path, so every provider instance in the process — two instances over one root, a root and its subdirectory — serialises the check-and-commit of one object. Bounded memory; two objects sharing a stripe only wait for each other.

**B27. An unconditional creation that finds the object created meanwhile prepares again** (Unix only, at most 3 preparations, then a conflict): its temporary has a new file's mode, and replacing the object with it could widen that object's mode.

**B28. Writes never read a scope's memo, and drop it on their path.** `Write`/`Delete` resolve every segment with a fresh listing (never the #205 listing memo), and after a successful write or delete they drop the calling flow's memoised listings of every directory on the key's path. Only `Bundle.Load` and `BundleValidator.Validate` open a scope, around their own reads, and the writer never runs inside one; a write on another flow cannot make a scope serve bytes and a version that disagree (the bytes and their SHA-256 always come from one read), and a name it creates is seen by that scope only after it ends — the window already documented on `BeginScope`. Tested on the same flow and on another, inheriting the scope or not.

## Decisions added by part B (for the maintainer to confirm)

**B29. Caller-conditioned writes take a `BundleWriteCondition`** (Deviation 1): `WriteConcept(…, BundleWriteCondition)` in its YAML and `Frontmatter` forms. A condition that does not hold writes nothing and returns `Error: '{id}' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.` (`IfMatch`) or `Error: '{id}' already exists; the write was refused. Read it before writing it.` (`MustNotExist`). Never retried.

**B30. Retries run under the writer's lock.** In-process writers of one bundle never conflict (they share the lock), so a conflict always comes from outside it — another process, or a writer over a provider instance with another `Id`. The read-modify-write keeps its single lock hold across attempts, as it kept it across read and write before; a waiting retry delays this process's other writers of the bundle by at most 160 ms. The delay goes through an internal `Sleep` seam (`Thread.Sleep` by default) so tests record it. `RecordVerifications` retries per concept: a conflict prepares that concept again from what is there now; concepts already written stay written.

**B31. The tree check's reads** (Deviation 3): `BundleTreeCheck.CheckCreation(provider, validator, key, limits)`, an internal static class in `src/OKF4net/Providers/`, called by the writer under its lock for a key its read found absent, never for an update. It counts every listed entry against `limits.MaxEntries` itself (spec §6) and lets a provider exception propagate (a write refuses what it cannot see). Its refusal is a library-authored sentence naming the key and the conflicting name, quoted with `DebugQuote`.

**B32. One reparse-point message, one late-guard hook** (Deviation 2; part A's Task 2b hook): every `BundleAccessRefusedException` that names a key becomes `Error: {subject} resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.` (`{subject}` is `'tables/x'` for a concept, `log.md` for the log); `BundleWriteConflictException` with `NameUnavailable` becomes `Error: {subject} cannot be stored under that name: …`; anything else stays `Error: {exception message}`. `BeforeLateReparseCheckForTest` keeps its signature and forwards to the filesystem provider's `ParentsResolvedForTest`; over another provider it has no effect (script that provider instead).

**B33. One bounded reader.** The writer reads every object it rewrites through `Bundle.ReadText` (made `internal`, with `ReadChunkBytes`): strict UTF-8, counted against the `BundleLoadOptions.Limits` the writer was given. No second reader is forked.

**B34. A verification batch reports per object.** Every expected failure of one concept's write in `RecordVerifications` — not only the former late-guard string — ends the batch with `{error} — already written: …` when something was written, and is the `Failed` entry of `VerificationOutcome.Report` (reason = the message without `Error: ` and without the suffix). A batch that wrote nothing and failed no write reports `BundleWriteReport.Empty` itself. `VerificationOutcome.Report` is never null, `default` included.

**B35. `log.md` keeps its tool messages.** `BundleConceptWriter.AppendLog(kind, text, DateOnly? date = null)` carries `GuardLogField`, `FoldLogField`, `ContainsBidiControl` and `IsBidiControl` verbatim (the first, second and fourth become `internal static`, since `OkfBundleTools` still reads `IsBidiControl` for its read-side escaper and its docs cite the others). The log key is checked under the writer's key policy like a concept's; a log that keeps changing gives `Error: log.md kept changing while it was being written; the write was refused after 3 attempts.`

## Decisions added by part C (for the maintainer to confirm)

**B36. A directory is referenced when an object lies under it** (Deviation 1, 3). `IndexGenerator` builds every directory's children from the keys a load of the bundle accepts (the same listing, bounds and `ValidateTree`): a `.md` child (not `index.md`) gives an entry from its frontmatter, a child prefix gives a `Subdirectories` entry. Which directories get an `index.md` is unchanged: every directory of a `.md` key, the root included. A name that is no key (a backslash, a control character, over 1 024 bytes), an empty directory and a directory holding only links are not in the key space and are not listed — the three inputs where the written bytes move, recorded in the CHANGELOG.

**B37. A new `index.md` passes the tree check.** It is a creation like any other (§10: "une création qui produirait une collision ou un conflit fichier/dossier est refusée avant toute écriture"): `BundleTreeCheck.CheckCreation` runs before it, with the validator of the run's `BundleLoadOptions`. Under the default `Native` policy over the filesystem the one conflict it can find is objects under `…/index.md`, which the provider would also refuse.

**B38. The two failure contracts.** The provider overloads never stop at a directory: every `index.md` that is not written becomes a `BundleWriteFailure` whose reason is library-authored — the exception's message for a provider exception (keys only, never a host path, never `BundleAccessRefusedException.Detail`), `I/O error while writing the index ({type})` for a raw `IOException`/`UnauthorizedAccessException`, `index.md cannot be written under the bundle's key policy: {reason}` for a key the run's policy refuses (B47), the tree check's sentence, `index.md kept changing while it was being written; the write was refused after 3 attempts`, or `the existing index.md could not be read or parsed, so it was left as it is`. The `string` overloads keep today's contract: a provider refusal (`BundleAccessRefusedException`, the STRICT guards), an `index.md` key the policy refuses (under their default options none is) and an unreadable root `index.md` skip that directory silently; everything else propagates and ends the run, the tree check's refusal as `BundleWriteConflictException` (`NameUnavailable`). Listing failures propagate in both. `BundleReadOnlyException` always propagates.

**B39. Read, then write, outside any scope.** The listing and the read of every `.md` child run inside one provider scope (each directory listed once); the `index.md` versions are read and the `index.md` files written after the scope is closed, so no write ever runs inside a scope (B28). A non-root `index.md` is read through `provider.Read` only for its version (the content is not needed); the root one through `Bundle.ReadText`, strict UTF-8, for its `okf_version`.

**B40. One listing consumer, one reader, one retry policy.** `Bundle.Load`'s listing loop becomes `internal static Bundle.ListNames(provider, limits)`, which `IndexGenerator` calls too; reads go through `Bundle.ReadText`; retries use `BundleConceptWriter.MaxAttempts` and `BundleConceptWriter.RetryDelay` with `Thread.Sleep`. No second copy of any of them.

**B41. The late-reparse hook keeps its shape.** `IndexGenerator.BeforeLateReparseCheckForTest` stays `Action<string>`, fires once per directory after its entries are known and before its `index.md` is read and written, with the directory's display path — the bundle root, or the root and the segments joined with the OS separator, i.e. exactly the path `origin/dev` passed, since the `string` overload builds its provider over the trimmed root. The late checks it used to precede are now the provider's (Task 2a's whole-path STRICT check and the object's own entry, the commit-time re-check). The four `IndexTests` seam tests pass unchanged; how each substitution is now refused:

| `IndexTests` test | Substitution in the hook | Refused by | String-overload result |
|---|---|---|---|
| `Index_write_is_not_written_through_a_directory_substituted_after_collection` | `real` → junction to an external directory | the version read: `Read(real/index.md)` STRICT-inspects `real` before listing it | skipped, nothing in the external directory |
| `Index_write_is_skipped_for_a_directory_swapped_for_a_junction_whose_link_status_cannot_be_inspected` | `x/y` → uninspectable junction, `x` denies listing | `Write`'s whole-path STRICT check before any listing (Task 2a) | `x/y` skipped, `x/zz` written after the hook lifts the ACE |
| `Index_write_is_skipped_for_an_index_file_swapped_for_a_junction_whose_link_status_cannot_be_inspected` | `d/index.md` → uninspectable junction, `d` denies listing | the same check, on the object's own entry | skipped, root written |
| `Index_write_is_skipped_when_the_target_index_md_is_itself_a_planted_symlink` (no hook) | `tables/index.md` planted as a file symlink | the walk skips it; `Read` finds it and its leaf guard refuses | skipped, external file untouched |

## Decisions added by revision r3 (the executed external review, for the maintainer to confirm)

**B42. An unconditional write needs only the right to write its target** (F5; the maintainer's ruling: preserve the old behaviour). `File.WriteAllText` opened the target for writing only, so a file the process may write but not read (mode 0200, an ACE denying `ReadData`) was overwritten. Under `BundleWriteCondition.None` the commit opens an existing target with `FileAccess.Write` alone and reads nothing (`EnsureWritable`): the B22 refusal of a read-only target stands, and any failure to open the handle refuses the write rather than letting it through. Only `IfMatch` reads the target (`HashForWrite`, read and write), so a file the process cannot read cannot satisfy `IfMatch`: the write is refused with the raw `UnauthorizedAccessException` (B25), never treated as a conflict. `MustNotExist` on an existing target is a conflict without any read, and `Delete` reads the object only under `IfMatch` too. `File.Replace` over a write-only target succeeds on both systems (probe A5). Tested as a non-root user on Linux and with a `ReadData` deny ACE on Windows (Task 2a); root reads every file, so the test skips there.

**B43. A replacement refused without effect is made again** (F3). Windows `ReplaceFile`'s `ERROR_UNABLE_TO_REMOVE_REPLACED` (1175 — "the replaced and replacement files retain their original file names") and a sharing violation raised by the replacement change nothing, so the provider removes nothing, sleeps 5 to 25 ms outside the commit lock, and runs the commit step again from the re-check of the condition against the bytes stored then — a condition another writer broke meanwhile is a conflict, never an overwrite — at most 3 attempts in all. After the last it raises `BundleWriteNotCommittedException` (a new `BundleProviderException`: its message is built from the key and the raw exception's type name, the raw exception is `InnerException`), the temporary removed and the target untouched. 1176 and 1177 stay unknown outcomes (B21). Tested through the commit hook: one 1175 then success, 1175 on every attempt, another writer landing between two attempts (Task 2b).

**B44. The classification of a failed commit renews the guards first** (F4). Before `File.Exists`, `ListExact` or `HashForRead`, `ClassifyCommitFailure` re-checks the confinement boundary and STRICT-checks the temporary with every directory up to the root and the target's own entry — the checks the commit itself ran; a refusal (or an I/O failure) makes the outcome unknown and keeps the temporary, so an ancestor swapped for a link during the commit is never read back as a success that a public `Read` would then refuse. This narrows the window between a check and an access; it does not close it (#178). Tested with the review's scenario: the parent directory swapped for a junction during the simulated commit, the new bytes behind it, then an ordinary commit failure (Task 2b).

**B45. `Read` retries the replace window on Windows** (the maintainer's ruling R1). Only two failures are retried: a sharing or lock violation (`0x80070020`, `0x80070021`) raised by the open, and a not-found raised by the open after the exact-name listing had matched the entry; never a key the listing does not match (a validator probes many absent resources: it costs one listing, counted), never a refusal, never a version mismatch. At most 3 attempts in all, 5 to 20 ms apart, each redoing every check (the boundary, the exact match, the STRICT guards) and checking `expected` against the bytes it read; a directory under the key is not found at once; a real deletion ends as not found once the listing no longer matches it. Windows only (`OperatingSystem.IsWindows()`, as for the other Windows specifics): on Linux the same tests pin that nothing changed. The scope memo's semantics are unchanged (a retry inside a scope resolves through the memo, as the first attempt did). Tested through a hook before the open (Task 2b); measured in probe A4.

**B46. A write notification that fails does not unwrite a commit** (F2). `onWriteCommitted` runs once `provider.Write` has returned, once per committed write and outside every retry (B48); the writer records the commit before it (`WriteValidatedContentLocked`'s `committed` callback). If the notification throws an exception the writer converts, `WriteConcept`, `AppendToConceptAtomic` and `AppendLog` still return `Error: {message}` — byte for byte what they returned when the write was `File.WriteAllText` — and do not retry. `RecordVerifications` keeps that concept in `Records` and `Report.Written`, never in `Report.Failed`, and goes on with the batch (a notification failure is not a write failure); `Recorded` means every stamp of the batch is on disk, and when one notification failed `Message` is the confirmation followed by `; the write notification failed: {message}` (the first such message). An exception the writer does not convert propagates, as before — `OperationCanceledException` included: the write is committed, and a batch ends there (B48). Tested with the review's scenario, a batch of three whose second notification fails, and each string method, the notification throwing an I/O error, an `OkfException` and — since r4 (F6) — a provider conflict, once and on every call, the one kind the retry loop and the condition translation would catch; and with a cancellation (Tasks 4a, 4b, 4c, 5).

**B47. The index keys are checked under the run's key policy** (F1). `IndexGenerator` checks every `index.md` key it is about to read or write — creation or replacement — with the run's `BundleKeyValidator` (the provider's native rules, the policy and the host's `ExtraKeyValidators`) before any access: a refusal is a `Failed` entry (`index.md cannot be written under the bundle's key policy: {reason}`) in the provider overloads' report and a silent skip under the `string` overloads' contract (as for a STRICT refusal, B38); the other directories are written. Under the `string` overloads' default options no `index.md` key is ever refused (every segment of the directory was accepted by the same validator). Tested with a host validator refusing `a/index.md`, the index absent and present, through both provider overloads and the `string` overloads' contract (Task 6).

## Decisions added by revision r4 (the second executed external review, for the maintainer to confirm)

**B48. A write is final once `provider.Write` returns; its notification runs outside the write** (F6). The retry loop (`WithRetries`) and the translation of a refused condition (`WriteConditionedLocked`) handle only a failure of the storage operation, never anything that happens after it succeeded. `WriteValidatedContentLocked`, the write primitive of concept writes and verification stamps (`WriteConcept`, `AppendToConceptAtomic`, `RecordVerifications`), no longer invokes `onWriteCommitted`: once `provider.Write` has returned it calls its `committed` callback (now required) and returns its confirmation, and nothing after the commit can throw. `AppendLog` does not go through it: it writes `log.md` through its own conditioned `provider.Write` and calls the `committed` action `WriteThenNotify` gives it as soon as that write has returned. Each entry point invokes the notification itself, under the writer's lock (#131), exactly once per committed write, after its whole write operation — every attempt of a read-modify-write included — has returned: `WriteConcept` (all four overloads), `AppendToConceptAtomic` and `AppendLog` through `WriteThenNotify(subject, write)`, which runs the operation with a `committed` action and then notifies if it committed; `RecordVerifications` through `NotifyCommitted(subject)` after each concept's retry loop and outside its `catch`, once the concept is in `Records` and `Report.Written`; `IndexGenerator` has no notification. `_onWriteCommitted` is invoked in `NotifyCommitted` and nowhere else. What the notification throws is therefore never retried, never runs `buildBody`, the write or the notification again, and is never read as the write's own condition refused. An exception the writer converts (`IsExpectedFailure`: an `OkfException` — a provider conflict or refusal included —, `IOException`, `UnauthorizedAccessException`, `ArgumentException`, `DecoderFallbackException`) becomes the B46 text, through `Translate` as before: `Error: {message}` for the string methods, the `; the write notification failed: …` suffix for a verification batch, which goes on with the next concept. Any other exception — `OperationCanceledException`, a host's bug — propagates to the caller unchanged, the write committed; in a batch the concepts after it are not attempted. Cancellation is deliberately not converted: a caller that cancels stops, and the write it cancelled after is on disk. Tested in Tasks 4a (each converted kind; cancellation), 4b (a conflict raised once and on every call for `WriteConcept(…, IfMatch)` in both forms, `AppendToConceptAtomic` and `RecordVerifications`, and a real conflict on the next concept of a batch), 4c (the report) and 5 (`AppendLog`, conflict and cancellation); every conflict case of 4b, 4c and 5 fails against r3's structure.

## Probes

Every platform behaviour the write protocol relies on was measured before it was used, with a throwaway console project (`dotnet new console`, `net10.0`, `<NoWarn>CA1416</NoWarn>`), run on Windows 11 (26200, NTFS, no symlink privilege, so P8 plants a junction) and in `mcr.microsoft.com/dotnet/sdk:10.0` (Ubuntu 24.04, overlayfs `/tmp`, as root, `strace` installed with `apt-get`). Linux command, from the probe's directory in Git Bash:

```bash
MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  set -e; cp -r /src /work; cd /work
  apt-get update -qq >/dev/null && apt-get install -y -qq strace >/dev/null 2>&1
  dotnet build -c Release -v q -nologo >/dev/null; umask 022
  dotnet bin/Release/net10.0/probe-a.dll /tmp
  (umask 077; dotnet bin/Release/net10.0/probe-a.dll /tmp | grep -E "P3")
  strace -f -e trace=rename,link,openat,lstat,stat -o /tmp/st.txt dotnet bin/Release/net10.0/probe-a.dll /tmp > /dev/null 2>&1 || true
  grep -E "okf-probe-a.*/(s1|t1|s3|t3|s4|LOWER.md|u600)\"" /tmp/st.txt | grep -v unlink | sed -E "s/^[0-9]+ +//; s#/tmp/okf-probe-a-[0-9a-f]+/##g"'
```

### Probe A — the primitives (`probe-a/Program.cs`)

```csharp
// Lot B probe A: the platform behaviours the filesystem write protocol depends on.
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

var root = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "okf-probe-a-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} .NET={Environment.Version}");

string P(string n) => Path.Combine(root, n);
void W(string n, string s) => File.WriteAllText(P(n), s);
string Desc(Exception e) => $"{e.GetType().Name} HResult=0x{e.HResult:X8} (win32 {e.HResult & 0xFFFF})";
void Run(string label, Action a)
{
    try { a(); }
    catch (Exception e) { Console.WriteLine($"{label}: THREW {Desc(e)}"); }
}

// P1a: File.Replace over an existing target; mode of the result on Unix; temp gone.
Run("P1a", () =>
{
    W("t1", "old"); W("s1", "new");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(P("t1"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(P("s1"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
    File.Replace(P("s1"), P("t1"), null);
    var mode = OperatingSystem.IsWindows() ? "n/a" : File.GetUnixFileMode(P("t1")).ToString();
    Console.WriteLine($"P1a: replace ok, target='{File.ReadAllText(P("t1"))}', temp exists={File.Exists(P("s1"))}, result mode={mode}");
});

// P1b: File.Replace with no target.
Run("P1b", () => { W("s2", "new"); File.Replace(P("s2"), P("t2-absent"), null); Console.WriteLine("P1b: no exception"); });
Console.WriteLine($"P1b: temp still there={File.Exists(P("s2"))}");

// P1c/d/e: a reader holds the target open with various sharing modes.
foreach (var share in new[] { FileShare.Read, FileShare.ReadWrite, FileShare.Read | FileShare.Delete, FileShare.ReadWrite | FileShare.Delete })
{
    var t = $"t-share-{(int)share}"; var s = $"s-share-{(int)share}";
    W(t, "old"); W(s, "new");
    using var reader = new FileStream(P(t), FileMode.Open, FileAccess.Read, share);
    Run($"P1-share[{share}]", () =>
    {
        File.Replace(P(s), P(t), null);
        var buf = new byte[3]; reader.ReadExactly(buf);
        Console.WriteLine($"P1-share[{share}]: replace ok; open reader still reads '{Encoding.ASCII.GetString(buf)}'; target now '{File.ReadAllText(P(t))}'");
    });
    Console.WriteLine($"P1-share[{share}]: after: temp exists={File.Exists(P(s))}, target exists={File.Exists(P(t))}");
}

// P1f: Windows explicit DACL on the target: preserved by ReplaceFile? ReadOnly target?
if (OperatingSystem.IsWindows())
{
    Run("P1f-acl", () =>
    {
        W("t-acl", "old"); W("s-acl", "new");
        var fi = new FileInfo(P("t-acl"));
        var sec = fi.GetAccessControl();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var me = WindowsIdentity.GetCurrent().User!;
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier))) sec.RemoveAccessRuleSpecific(r);
        sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        fi.SetAccessControl(sec);
        string Sddl(string n) => new FileInfo(P(n)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var before = Sddl("t-acl"); var tempAcl = Sddl("s-acl");
        File.Replace(P("s-acl"), P("t-acl"), null);
        var after = Sddl("t-acl");
        Console.WriteLine($"P1f-acl: target DACL preserved={before == after}; temp had inherited DACL different={tempAcl != before}");
    });
    Run("P1g-readonly", () =>
    {
        W("t-ro", "old"); W("s-ro", "new");
        File.SetAttributes(P("t-ro"), FileAttributes.ReadOnly);
        try { File.Replace(P("s-ro"), P("t-ro"), null); Console.WriteLine($"P1g-readonly: replace ok, target='{File.ReadAllText(P("t-ro"))}', attrs={File.GetAttributes(P("t-ro"))}"); }
        finally { if (File.Exists(P("t-ro"))) File.SetAttributes(P("t-ro"), FileAttributes.Normal); }
    });
    Run("P1g-writealltext-readonly", () =>
    {
        W("t-ro2", "old"); File.SetAttributes(P("t-ro2"), FileAttributes.ReadOnly);
        try { File.WriteAllText(P("t-ro2"), "new"); Console.WriteLine("P1g-writealltext-readonly: ok"); }
        finally { File.SetAttributes(P("t-ro2"), FileAttributes.Normal); }
    });
}

// P2: File.Move(overwrite:false) onto an existing target; case-only twin.
Run("P2a", () => { W("t3", "old"); W("s3", "new"); File.Move(P("s3"), P("t3"), overwrite: false); Console.WriteLine("P2a: no exception"); });
Console.WriteLine($"P2a: target='{File.ReadAllText(P("t3"))}' temp exists={File.Exists(P("s3"))}");
Run("P2b", () => { W("lower.md", "old"); W("s4", "new"); File.Move(P("s4"), P("LOWER.md"), overwrite: false); Console.WriteLine("P2b: no exception (case-sensitive volume)"); });
Run("P2c-dir", () => { Directory.CreateDirectory(P("adir")); W("s5", "new"); File.Move(P("s5"), P("adir"), overwrite: false); Console.WriteLine("P2c-dir: no exception"); });

// P3: UnixCreateMode 0600 and the default create mode.
Run("P3", () =>
{
    var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
    using (var fs = new FileStream(P("u600"), opts)) { fs.Write("x"u8); }
    Console.WriteLine($"P3: UnixCreateMode 0600 -> {(OperatingSystem.IsWindows() ? "n/a" : File.GetUnixFileMode(P("u600")).ToString())}");
});
Run("P3-default", () =>
{
    File.WriteAllText(P("dflt"), "x");
    using (var fs = new FileStream(P("dflt2"), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None })) { }
    if (!OperatingSystem.IsWindows())
        Console.WriteLine($"P3-default: WriteAllText={File.GetUnixFileMode(P("dflt"))} FileStream default={File.GetUnixFileMode(P("dflt2"))}");
});

// P4: get/set the mode through a handle.
Run("P4", () =>
{
    using var fs = new FileStream(P("h"), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None });
    var before = File.GetUnixFileMode(fs.SafeFileHandle);
    File.SetUnixFileMode(fs.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    Console.WriteLine($"P4: handle mode before={before} after={File.GetUnixFileMode(fs.SafeFileHandle)}");
});

// P5: Flush(true).
Run("P5", () => { using var fs = new FileStream(P("fl"), FileMode.CreateNew, FileAccess.Write, FileShare.None); fs.Write("abc"u8); fs.Flush(flushToDisk: true); Console.WriteLine("P5: Flush(true) ok"); });

// P6: File.Delete while a reader holds the file open.
foreach (var share in new[] { FileShare.Read, FileShare.ReadWrite | FileShare.Delete })
{
    var t = $"d-share-{(int)share}"; W(t, "old");
    using var reader = new FileStream(P(t), FileMode.Open, FileAccess.Read, share);
    Run($"P6[{share}]", () => { File.Delete(P(t)); Console.WriteLine($"P6[{share}]: delete ok; exists after={File.Exists(P(t))}; name reusable now={TryCreate(P(t))}"); });
}

// P7: does an OS lookup find an entry under a name the listing does not hold exactly?
Directory.CreateDirectory(P("Twin"));
W("caf\u00e9.md", "nfc");
Console.WriteLine($"P7: Directory.Exists('twin')={Directory.Exists(P("twin"))} File.Exists(NFD cafe)={File.Exists(P("cafe\u0301.md"))} Directory.Exists('Twin.')={Directory.Exists(P("Twin."))}");
Run("P7-create-twin", () => { Directory.CreateDirectory(P("twin")); Console.WriteLine($"P7-create-twin: CreateDirectory('twin') ok; listing={string.Join(",", Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n!.Equals("twin", StringComparison.OrdinalIgnoreCase)))}"); });

// P8: CreateNew on an existing symlink at the name (temp-name planting).
Run("P8", () =>
{
    W("victim", "secret");
    try { File.CreateSymbolicLink(P("planted.tmp"), P("victim")); Console.WriteLine("P8: planted a file symlink"); }
    catch (IOException) when (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(P("victim-dir"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", P("planted.tmp"), P("victim-dir") }, RedirectStandardOutput = true, UseShellExecute = false })!.WaitForExit();
        Console.WriteLine($"P8: no symlink privilege; planted a junction instead (exists={Directory.Exists(P("planted.tmp"))})");
    }
    try { using var fs = new FileStream(P("planted.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None); fs.Write("pwn"u8); Console.WriteLine("P8: CreateNew opened through the link!"); }
    catch (IOException e) { Console.WriteLine($"P8: CreateNew on a planted symlink refused: {Desc(e)}; victim='{File.ReadAllText(P("victim"))}'"); }
});

// P9: File.Replace when the target is a directory / temp missing.
Run("P9-dir", () => { Directory.CreateDirectory(P("tdir")); W("s9", "new"); File.Replace(P("s9"), P("tdir"), null); Console.WriteLine("P9-dir: no exception"); });
Run("P9-nosrc", () => { W("t9", "old"); File.Replace(P("s9-absent"), P("t9"), null); Console.WriteLine("P9-nosrc: no exception"); });

try { Directory.Delete(root, recursive: true); } catch (Exception e) { Console.WriteLine($"cleanup: {Desc(e)}"); }

static bool TryCreate(string path)
{
    try { using var fs = new FileStream(path, FileMode.CreateNew); return true; }
    catch (IOException) { return false; }
}
```

Windows (the final `cleanup` line is the probe's own `Directory.Delete` refusing to recurse through the junction P8 planted):

```text
OS=Microsoft Windows 10.0.26200 .NET=10.0.8
P1a: replace ok, target='new', temp exists=False, result mode=n/a
P1b: THREW FileNotFoundException HResult=0x80070002 (win32 2)
P1b: temp still there=True
P1-share[Read]: THREW IOException HResult=0x80070020 (win32 32)
P1-share[Read]: after: temp exists=True, target exists=True
P1-share[ReadWrite]: THREW IOException HResult=0x80070020 (win32 32)
P1-share[ReadWrite]: after: temp exists=True, target exists=True
P1-share[Read, Delete]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[Read, Delete]: after: temp exists=False, target exists=True
P1-share[ReadWrite, Delete]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[ReadWrite, Delete]: after: temp exists=False, target exists=True
P1f-acl: target DACL preserved=True; temp had inherited DACL different=True
P1g-readonly: THREW UnauthorizedAccessException HResult=0x80070005 (win32 5)
P1g-writealltext-readonly: THREW UnauthorizedAccessException HResult=0x80070005 (win32 5)
P2a: THREW IOException HResult=0x800700B7 (win32 183)
P2a: target='old' temp exists=True
P2b: THREW IOException HResult=0x800700B7 (win32 183)
P2c-dir: THREW IOException HResult=0x800700B7 (win32 183)
P3: THREW PlatformNotSupportedException HResult=0x80131539 (win32 5433)
P4: THREW PlatformNotSupportedException HResult=0x80131539 (win32 5433)
P5: Flush(true) ok
P6[Read]: THREW IOException HResult=0x80070020 (win32 32)
P6[ReadWrite, Delete]: delete ok; exists after=False; name reusable now=True
P7: Directory.Exists('twin')=True File.Exists(NFD cafe)=False Directory.Exists('Twin.')=True
P7-create-twin: CreateDirectory('twin') ok; listing=Twin
P8: no symlink privilege; planted a junction instead (exists=True)
P8: THREW UnauthorizedAccessException HResult=0x80070005 (win32 5)
P9-dir: THREW UnauthorizedAccessException HResult=0x80070005 (win32 5)
P9-nosrc: THREW FileNotFoundException HResult=0x80070002 (win32 2)
cleanup: UnauthorizedAccessException HResult=0x80070005 (win32 5)
```

Linux (umask 022, then the P3 lines under umask 077, then the strace excerpt with the probe directory stripped from the paths):

```text
OS=Ubuntu 24.04.4 LTS .NET=10.0.10
P1a: replace ok, target='new', temp exists=False, result mode=OtherRead, GroupRead, UserWrite, UserRead
P1b: THREW FileNotFoundException HResult=0x80070002 (win32 2)
P1b: temp still there=True
P1-share[Read]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[Read]: after: temp exists=False, target exists=True
P1-share[ReadWrite]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[ReadWrite]: after: temp exists=False, target exists=True
P1-share[Read, Delete]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[Read, Delete]: after: temp exists=False, target exists=True
P1-share[ReadWrite, Delete]: replace ok; open reader still reads 'old'; target now 'new'
P1-share[ReadWrite, Delete]: after: temp exists=False, target exists=True
P2a: THREW IOException HResult=0x00000011 (win32 17)
P2a: target='old' temp exists=True
P2b: no exception (case-sensitive volume)
P2c-dir: THREW IOException HResult=0x00000011 (win32 17)
P3: UnixCreateMode 0600 -> UserWrite, UserRead
P3-default: WriteAllText=OtherRead, GroupRead, UserWrite, UserRead FileStream default=OtherRead, GroupRead, UserWrite, UserRead
P4: handle mode before=OtherRead, GroupRead, UserWrite, UserRead after=GroupRead, UserWrite, UserRead
P5: Flush(true) ok
P6[Read]: delete ok; exists after=False; name reusable now=True
P6[ReadWrite, Delete]: delete ok; exists after=False; name reusable now=True
P7: Directory.Exists('twin')=False File.Exists(NFD cafe)=False Directory.Exists('Twin.')=False
P7-create-twin: CreateDirectory('twin') ok; listing=Twin,twin
P8: planted a file symlink
P8: CreateNew on a planted symlink refused: IOException HResult=0x00000011 (win32 17); victim='secret'
P9-dir: THREW UnauthorizedAccessException HResult=0x80070005 (win32 5)
P9-nosrc: THREW FileNotFoundException HResult=0x80070002 (win32 2)
--- umask 077
P3: UnixCreateMode 0600 -> UserWrite, UserRead
P3-default: WriteAllText=UserWrite, UserRead FileStream default=UserWrite, UserRead
--- strace (File.Replace s1->t1; File.Move overwrite:false s3->t3 existing, s4->LOWER.md absent; UnixCreateMode u600)
openat(AT_FDCWD, "t1", O_WRONLY|O_CREAT|O_CLOEXEC, 0666) = 32
openat(AT_FDCWD, "s1", O_WRONLY|O_CREAT|O_CLOEXEC, 0666) = 32
lstat("s1", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
lstat("t1", {st_mode=S_IFREG|0600, st_size=3, ...}) = 0
stat("t1", {st_mode=S_IFREG|0600, st_size=3, ...}) = 0
rename("s1", "t1") = 0
lstat("t1", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
openat(AT_FDCWD, "t1", O_RDONLY|O_CLOEXEC) = 32
lstat("s1", 0x7ffc1cc30050) = -1 ENOENT (No such file or directory)
openat(AT_FDCWD, "t3", O_WRONLY|O_CREAT|O_CLOEXEC, 0666) = 32
openat(AT_FDCWD, "s3", O_WRONLY|O_CREAT|O_CLOEXEC, 0666) = 32
lstat("s3", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
lstat("s3", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
lstat("t3", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
link("s3", "t3") = -1 EEXIST (File exists)
openat(AT_FDCWD, "t3", O_RDONLY|O_CLOEXEC) = 32
lstat("s3", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
openat(AT_FDCWD, "s4", O_WRONLY|O_CREAT|O_CLOEXEC, 0666) = 32
lstat("s4", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
lstat("s4", {st_mode=S_IFREG|0644, st_size=3, ...}) = 0
lstat("LOWER.md", 0x7ffc1cc30060) = -1 ENOENT (No such file or directory)
rename("s4", "LOWER.md") = 0
openat(AT_FDCWD, "u600", O_WRONLY|O_CREAT|O_EXCL|O_CLOEXEC, 0600) = 32
lstat("u600", {st_mode=S_IFREG|0600, st_size=1, ...}) = 0
```

What the plan takes from it:

- **P1a/P1b, P9.** `File.Replace` needs an existing target (`FileNotFoundException` otherwise, temporary left in place) and refuses a directory. On Linux it is `lstat` + `stat` + `rename`, so **the result has the temporary's mode** (a 0600 target became 0644): the mode must be set on the temporary before the commit (B6 (3)).
- **P1-share, P6 (B8).** On Windows a reader that does not share deletion (`FileShare.Read`, `ReadWrite`) makes `File.Replace` and `File.Delete` fail with a sharing violation (`0x80070020`) and changes nothing; a reader that shares deletion does not, and keeps reading the old bytes. Linux ignores sharing. Hence `Read` shares `ReadWrite | Delete`, and a sharing violation from the commit is a determinate failure (B21).
- **P1f.** `ReplaceFile` keeps the replaced file's explicit DACL; the temporary had its directory's inherited DACL (documented window, Windows ACL paragraph of `Write`'s remarks).
- **P1g.** A read-only target refuses `File.Replace` exactly as it refused `File.WriteAllText` (`UnauthorizedAccessException`) on Windows; on Unix a `rename` would replace it — hence B22.
- **P2.** `File.Move(overwrite: false)` onto an existing name fails with `IOException` (`183` on Windows, `EEXIST` = 17 on Linux), including a case twin on NTFS and a directory. The strace shows the Unix shape: an existing target is caught by `lstat` and then `link` (`EEXIST`), an absent one gets `lstat` (`ENOENT`) and then a plain `rename` — the race of B20.
- **P3, P4.** `UnixCreateMode = 0600` creates `O_EXCL` with mode 0600 under any umask; `FileStreamOptions.UnixCreateMode` and the handle-based mode APIs throw `PlatformNotSupportedException` on Windows (guard them); the default create mode follows the umask exactly as `File.WriteAllText` does (B18). `File.GetUnixFileMode`/`SetUnixFileMode(SafeFileHandle, …)` work through the handle.
- **P5.** `Flush(flushToDisk: true)` works on both.
- **P7 (B19).** NTFS folds case and strips a trailing dot (`twin`, `Twin.` both find `Twin`) but keeps NFC and NFD apart; `CreateDirectory("twin")` reuses `Twin` silently. ext4 keeps `Twin` and `twin` as two directories.
- **P8.** `FileMode.CreateNew` never opens an entry planted under the temporary's name: a symlink on Linux (`EEXIST`, the victim untouched), a junction on Windows (`UnauthorizedAccessException`).

`.NET 10` has no hard-link API (`typeof(File)` exposes `Replace`, `CreateSymbolicLink`, `ResolveLinkTarget` only — a three-line reflection probe, `probe-a2`), so a `link`-based no-clobber create is not available without P/Invoke.

### Probe A3 — what a concurrent reader sees during a commit (`probe-a3/Program.cs`)

```csharp
// Probe A3: what a concurrent reader observes under the target name while a writer
// replaces it in a loop, for each commit primitive (and for the in-place write lot B replaces).
var root = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "okf-probe-a3-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} .NET={Environment.Version}");
var target = Path.Combine(root, "x.md");
const int Size = 64 * 1024;
byte[] Version(int i) { var b = new byte[Size]; Array.Fill(b, (byte)('a' + i % 26)); return b; }
foreach (var primitive in new[] { "File.Replace", "File.Move(overwrite:true)", "File.WriteAllBytes in place" })
{
    File.WriteAllBytes(target, Version(0));
    var stop = false;
    var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    void Count(string what) => counts.AddOrUpdate(what, 1, (_, n) => n + 1);
    var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
    {
        while (!Volatile.Read(ref stop))
        {
            if (!File.Exists(target)) Count("File.Exists=false");
            if (!Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) == "x.md")) Count("listing without the name");
            try
            {
                using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[Size + 1];
                var n = fs.ReadAtLeast(buf, Size + 1, throwOnEndOfStream: false);
                Count(n == Size && buf.AsSpan(0, n).IndexOfAnyExcept(buf[0]) < 0 ? "read whole" : $"read TORN");
            }
            catch (Exception e) { Count($"open/read {e.GetType().Name} 0x{e.HResult:X8}"); }
        }
    })).ToArray();
    foreach (var t in readers) t.Start();
    var writerFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    for (var i = 1; i <= 300; i++)
    {
        var temp = Path.Combine(root, $".okf-write-{i}.tmp");
        try
        {
            if (primitive == "File.WriteAllBytes in place") { File.WriteAllBytes(target, Version(i)); continue; }
            File.WriteAllBytes(temp, Version(i));
            if (primitive == "File.Replace") File.Replace(temp, target, null);
            else File.Move(temp, target, overwrite: true);
        }
        catch (Exception e) { writerFailures.AddOrUpdate($"{e.GetType().Name} 0x{e.HResult:X8}", 1, (_, n) => n + 1); if (File.Exists(temp)) File.Delete(temp); }
    }
    Volatile.Write(ref stop, true);
    foreach (var t in readers) t.Join();
    Console.WriteLine($"{primitive}: 300 commits; writer failures: [{string.Join(", ", writerFailures.Select(k => $"{k.Key}={k.Value}"))}]; reader observations: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
}
Directory.Delete(root, true);
```

```text
OS=Microsoft Windows 10.0.26200 .NET=10.0.8
File.Replace: 300 commits; writer failures: []; reader observations: File.Exists=false=2134, listing without the name=2121, open/read FileNotFoundException 0x80070002=1946, open/read IOException 0x80070020=778, read whole=37262
File.Move(overwrite:true): 300 commits; writer failures: [UnauthorizedAccessException 0x80070005=257]; reader observations: read whole=498
File.WriteAllBytes in place: 300 commits; writer failures: []; reader observations: read TORN=84, read whole=240
OS=Ubuntu 24.04.4 LTS .NET=10.0.10
File.Replace: 300 commits; writer failures: []; reader observations: read whole=2360
File.Move(overwrite:true): 300 commits; writer failures: []; reader observations: read whole=2184
File.WriteAllBytes in place: 300 commits; writer failures: []; reader observations: read TORN=5045, read whole=926
```

On Windows `File.Replace` never yields a torn object, but a reader finds no entry under the name or meets a sharing violation for a moment, and the writer can fail too. The run above (the drafting) saw 1 946 `FileNotFoundException` and 778 sharing violations out of 42 120 opens and no writer failure; the external review ran the same code five times (r3): 1 500 commits, 192 649 opens, **3 988 not found (2.07 %), 3 426 sharing violations (1.78 %)**, 185 235 whole reads, no torn read, and **two writer failures, `IOException 0x80070497` (1175, `ERROR_UNABLE_TO_REMOVE_REPLACED`)** — two of the five runs printed `writer failures: [IOException 0x80070497=1]`. 1175 leaves both files as they were, so the provider makes such a replacement again (B43); the review does not attribute it to the readers alone, and neither does this plan. `File.Move(overwrite: true)` keeps the name visible but fails 257 to 277 of 300 commits while readers hold the file. Writing in place — what `BundleConceptWriter` does today — is torn for readers on both systems. Hence B17. On Linux, 300 `File.Replace` gave no error in any run.

### Probe A4 — the window as `FileSystemBundleProvider.Read` sees it, before and after B45 (`probe-a4/Program.cs`, r3)

Two readers loop over `Read` (each through its own provider instance) while a third instance writes `x.md` 3 000 times, back to back, unconditionally. Built against each revision's `src/OKF4net` (`probe-a4.csproj` references `$(OkfRepo)/src/OKF4net/OKF4net.csproj`; `dotnet build -c Release -p:OkfRepo=<worktree> -o out-<rev>`), run three times each on Windows 11, alternating:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$(OkfRepo)/src/OKF4net/OKF4net.csproj" /></ItemGroup>
</Project>
```

```csharp
// Probe A4: what readers going through FileSystemBundleProvider.Read observe while another
// provider instance replaces the object 3 000 times — the window B17 describes, as the public API
// sees it (built against the revision under test: -p:OkfRepo=<worktree>).
using OKF4net.Providers;
var root = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "okf-probe-a4-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} .NET={Environment.Version}");
var key = BundleKey.Parse("x.md");
const int Size = 64 * 1024;
byte[] Version(int i) { var b = new byte[Size]; Array.Fill(b, (byte)('a' + i % 26)); return b; }
var writer = new FileSystemBundleProvider(root);
writer.Write(key, Version(0), BundleWriteCondition.None);
var stop = false;
var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
void Count(string what) => counts.AddOrUpdate(what, 1, (_, n) => n + 1);
var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
{
    var reader = new FileSystemBundleProvider(root);
    while (!Volatile.Read(ref stop))
    {
        try
        {
            using var obj = reader.Read(key);
            var buf = new byte[Size + 1];
            var n = obj.Content.ReadAtLeast(buf, Size + 1, throwOnEndOfStream: false);
            Count(n == Size && buf.AsSpan(0, n).IndexOfAnyExcept(buf[0]) < 0 ? "read whole" : "read TORN");
        }
        catch (Exception e) { Count($"read {e.GetType().Name} 0x{e.HResult:X8}"); }
    }
})).ToArray();
foreach (var t in readers) t.Start();
var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
for (var i = 1; i <= 3000; i++)
{
    try { writer.Write(key, Version(i), BundleWriteCondition.None); }
    catch (Exception e) { failures.AddOrUpdate($"{e.GetType().Name} 0x{(e.InnerException ?? e).HResult:X8}", 1, (_, n) => n + 1); }
}
Volatile.Write(ref stop, true);
foreach (var t in readers) t.Join();
var total = counts.Values.Sum();
Console.WriteLine($"3000 writes; writer failures: [{string.Join(", ", failures.Select(k => $"{k.Key}={k.Value}"))}]; {total} reads: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value} ({100.0 * k.Value / total:0.00} %)")));
Directory.Delete(root, true);
```

```text
r2: 3000 writes; writer failures: []; 154238 reads: read BundleObjectNotFoundException 0x80131500=56020 (36,32 %), read IOException 0x80070020=86 (0,06 %), read whole=98132 (63,62 %)
r3: 3000 writes; writer failures: []; 51850 reads: read BundleObjectNotFoundException 0x80131500=12472 (24,05 %), read whole=39378 (75,95 %)
r2: 3000 writes; writer failures: []; 161782 reads: read BundleObjectNotFoundException 0x80131500=56584 (34,98 %), read IOException 0x80070020=101 (0,06 %), read whole=105097 (64,96 %)
r3: 3000 writes; writer failures: []; 53782 reads: read BundleObjectNotFoundException 0x80131500=12382 (23,02 %), read whole=41400 (76,98 %)
r2: 3000 writes; writer failures: []; 165291 reads: read BundleObjectNotFoundException 0x80131500=57395 (34,72 %), read IOException 0x80070020=121 (0,07 %), read whole=107775 (65,20 %)
r3: 3000 writes; writer failures: []; 59520 reads: read BundleObjectNotFoundException 0x80131500=12257 (20,59 %), read whole=47263 (79,41 %)
```

Under a writer that replaces the object without pause, a third of r2's reads through the provider found no object — far more than probe A3's plain opens, because `Read` first lists the directory to match the name exactly, and a listing made while `ReplaceFile` runs finds no entry under the name. B45 removes every sharing violation (none in the three r3 runs) and brings the not-found share from 35–36 % down to 21–24 %; what remains is, by construction, mostly reads whose listing ran during the swap — B45 deliberately does not retry a key the listing does not match, so that an absent key never pays a delay — and the probe cannot tell those from a read that used its three attempts. The window narrows; it does not vanish. No torn read and no writer failure in any of the six runs (1175 did not occur here). This load is extreme on purpose: a bundle is not rewritten 3 000 times in a row while it is read.

### Probe A5 — a file the process may write but not read (`probe-f5/Program.cs`, r3)

The same project file as the other probes. On Linux it runs as root and then as an unprivileged user (`useradd -m tester`, the build directory made readable, `su tester -c "dotnet …/probe-f5.dll /tmp"`), since root reads a 0200 file anyway.

```csharp
// Probe F5: what a file the process may write but not read allows (Windows ACL deny ReadData / Unix 0200).
using System.Security.AccessControl;
using System.Security.Principal;
var root = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "okf-probe-f5-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} .NET={Environment.Version} user={Environment.UserName}");
string Make(string name)
{
    var p = Path.Combine(root, name);
    File.WriteAllText(p, "old");
    if (OperatingSystem.IsWindows())
    {
        var info = new FileInfo(p);
        var sec = info.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(sec);
    }
    else
    {
        File.SetUnixFileMode(p, UnixFileMode.UserWrite);
    }
    return p;
}
void Try(string what, Action a)
{
    try { a(); Console.WriteLine($"{what}: ok"); }
    catch (Exception e) { Console.WriteLine($"{what}: {e.GetType().Name} 0x{e.HResult:X8}"); }
}
var t1 = Make("a.md");
Try("read (FileAccess.Read)", () => File.OpenHandle(t1, FileMode.Open, FileAccess.Read).Dispose());
Try("open FileAccess.Write", () => File.OpenHandle(t1, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
Try("open FileAccess.ReadWrite", () => File.OpenHandle(t1, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete).Dispose());
Try("File.WriteAllText", () => File.WriteAllText(t1, "via WriteAllText"));
var t2 = Make("b.md");
var tmp = Path.Combine(root, ".okf-write-x.tmp");
File.WriteAllText(tmp, "via Replace");
Try("File.Replace(temp, target, null)", () => File.Replace(tmp, t2, null));
Console.WriteLine("temp left: " + File.Exists(tmp));
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(t2, UnixFileMode.UserRead | UnixFileMode.UserWrite);
else { var info = new FileInfo(t2); var sec = info.GetAccessControl(); sec.PurgeAccessRules(WindowsIdentity.GetCurrent().User!); info.SetAccessControl(sec); }
Try("read b.md after", () => Console.Write("  b.md=" + File.ReadAllText(t2) + " "));
if (OperatingSystem.IsWindows()) { foreach (var f in new[] { t1 }) { var info = new FileInfo(f); var sec = info.GetAccessControl(); sec.PurgeAccessRules(WindowsIdentity.GetCurrent().User!); info.SetAccessControl(sec); } }
else File.SetUnixFileMode(t1, UnixFileMode.UserRead | UnixFileMode.UserWrite);
Directory.Delete(root, true);
```

```text
OS=Microsoft Windows 10.0.26200 .NET=10.0.8 user=Julien
read (FileAccess.Read): UnauthorizedAccessException 0x80070005
open FileAccess.Write: ok
open FileAccess.ReadWrite: UnauthorizedAccessException 0x80070005
File.WriteAllText: ok
File.Replace(temp, target, null): ok
temp left: False
  b.md=via Replace read b.md after: ok
OS=Ubuntu 24.04.4 LTS .NET=10.0.10 user=root
read (FileAccess.Read): ok
open FileAccess.Write: ok
open FileAccess.ReadWrite: ok
File.WriteAllText: ok
File.Replace(temp, target, null): ok
temp left: False
  b.md=via Replace read b.md after: ok
OS=Ubuntu 24.04.4 LTS .NET=10.0.10 user=tester
read (FileAccess.Read): UnauthorizedAccessException 0x80070005
open FileAccess.Write: ok
open FileAccess.ReadWrite: UnauthorizedAccessException 0x80070005
File.WriteAllText: ok
File.Replace(temp, target, null): ok
temp left: False
  b.md=via Replace read b.md after: ok
```

A file the process may write but not read (an ACE denying `ReadData` on Windows, mode 0200 for a non-root user on Linux) refuses a handle opened for reading — `FileAccess.ReadWrite` included, which is what r2's `HashForWrite` opened for every replacement — but accepts one opened for writing alone, `File.WriteAllText`, and `File.Replace` over it. Hence B42: an unconditional write opens the target for writing only, and only `IfMatch`, which must hash the stored bytes, needs to read it.

## Review Focus

Inputs and failure modes the spec implies but that no requirement names, each pinned to the owning task:

1. **A Windows name that is an alternate data stream.** `a:b.md` is a valid layer-1 key; `Path.Join(dir, "a:b.md")` on Windows names stream `b.md` of a file `a`, so a naive write would land inside another file. Refused before any effect, with `x.`, `x `, `d./x.md` and `C:x.md` — Task 2a, `On_Windows_a_name_no_entry_can_carry_literally_is_refused_before_any_effect`.
2. **A directory created under a folded name.** `Directory.CreateDirectory("twin")` beside `Twin` succeeds silently on NTFS (probe P7), so a write of `twin/y.md` would land in `Twin/`. Refused, nothing created, on a folding volume; both names written on ext4 — Task 2a, `A_name_the_volume_would_confuse_with_a_stored_one_is_refused_and_creates_nothing`.
3. **A read-only file and a rename.** A rename needs only the directory's permission on Unix, so an atomic commit would silently replace a 0444 file that `File.WriteAllText` refused. Refused as before, as a non-root user on Linux and with the read-only attribute on Windows — Task 2a, `A_target_the_process_may_not_write_is_refused_as_before_and_left_as_it_was` (skipped as root: run the unprivileged container pass).
4. **The only copy of the new bytes.** `ReplaceFile` error 1176 can leave no object under the key and the new content under the temporary's name; cleaning up "on failure" would destroy it. Kept, reported as an unknown outcome, listed as an orphan — Task 2b, `A_commit_failing_like_ReplaceFile_1176_is_an_unknown_outcome_that_keeps_the_temporary`.
5. **A temporary that a link has replaced, or whose directory has become a junction.** Removing it "to clean up" would delete through the link. Refused at the commit, nothing reaches the link's target, nothing removed through it — Task 2b, `A_link_put_in_place_of_the_temporary_is_refused_at_the_commit` and `A_directory_swapped_for_a_junction_before_the_commit_is_refused_and_nothing_reaches_its_target`.
6. **A write meeting a load's listing scope** (#205). The scope memoises names per flow; a write must neither trust them (an object deleted behind the scope's back) nor leave them stale for its own flow, nor make another flow's scope serve bytes and a version that disagree — Task 2c, `A_write_inside_a_scope_ignores_the_scopes_listing_then_drops_it` and `A_write_on_another_flow_never_makes_a_scope_serve_bytes_and_version_that_disagree`.
7. **Readers during a Windows replacement.** Not a torn object, but a moment with no object under the name (B17), narrowed by `Read`'s bounded retry (B45), and a replacement `ReplaceFile` can refuse without effect (1175, B43). Pinned as the documented window, never a torn read, a hundred replacements that all land — Task 2b, `Concurrent_reads_never_block_a_replacement_and_never_see_a_torn_object`; the retries themselves through the hooks — `A_replacement_the_storage_refused_without_effect_once_is_made_again_and_succeeds`, `A_read_meeting_the_replace_window_once_is_retried_on_Windows_and_fails_as_before_elsewhere`.
8. **A file the process may write but not read** (F5). `File.WriteAllText` replaced it; a commit that hashes the target would refuse it. Replaced under `None`, refused under `IfMatch`, as a non-root user on Linux and with a deny ACE on Windows — Task 2a, `A_target_the_process_may_write_but_not_read_is_replaced_unconditionally_and_refused_under_IfMatch`.
9. **A failed commit read back through a link** (F4). An ancestor swapped for a junction during the commit, the new bytes behind it: not a success — Task 2b, `A_failed_commit_is_not_read_back_through_an_ancestor_swapped_for_a_link`.
10. **A condition broken between two commit attempts** (F3). A retry must start from the stored bytes, not from the first check — Task 2b, `A_condition_another_writer_breaks_between_two_commit_attempts_is_a_conflict_not_an_overwrite`.
11. **An absent key under the read retry** (R1). A validator probes many absent resources; none may pay a delay — Task 2b, `An_absent_key_a_refusal_and_a_version_mismatch_are_never_retried` (one listing, counted, not timed).

---
## Review Focus (part B)

1. **A concept id that a Windows volume aliases.** `a./b` used to land in `a/b`: a trailing dot is stripped by Windows (external review, 2026-10-09). Now refused by the core under the native Windows set before any I/O — Task 4a, `A_concept_id_the_providers_native_rules_refuse_is_refused_before_any_call` (any OS, through a provider declaring the rule) and `On_Windows_a_concept_id_segment_ending_with_a_dot_is_refused_instead_of_aliasing`.
2. **Two writers that do not share a lock.** Two provider instances with different ids over one directory give two writer locks; only the provider's conditional commit stands between them. Fifty rounds in which both read the same version before either writes: exactly one conflict per round, one retry, no append lost — Task 4b, `Two_writers_without_a_shared_lock_lose_no_append_over_fifty_rounds`.
3. **A `buildBody` that is called again.** The contract change of spec §4: on a retry `buildBody` sees the re-read body, not its first input, and is called at most three times — Task 4b, `An_append_that_meets_a_change_reads_again_and_builds_on_it` and `After_three_conflicting_attempts_the_append_gives_up_with_an_error_and_writes_nothing`.
4. **A provider refusal in the middle of a verification batch.** The late guard used to return a string the write loop recognised; behind the provider it is an exception. The batch must still say what was written — Task 4c, `A_path_refused_by_the_provider_mid_batch_keeps_the_writers_message_and_suffix` (a junction swapped in through the hook on the second write).
5. **A tree check meeting a provider that ignores bounds or cannot list.** The check enumerates at the provider; a provider that ignores `MaxEntries` must still be bounded by the core, and one that cannot list must refuse the write — Task 3, `The_entry_bound_is_enforced_by_the_core_even_when_the_provider_ignores_it` and `A_listing_or_an_existence_question_that_fails_propagates`.
6. **Creating `log.md` where objects sit under that name.** A key store can hold `log.md/x.md`; creating the log would make `log.md` both an object and a prefix — Task 5, `Creating_the_log_where_objects_already_sit_under_its_name_is_refused`.
7. **A write notification that throws after the commit** (F2, F6). The commit happened; neither the report nor `Recorded` may deny it, a batch must not stop half-written because of it, and nothing it throws — a conflict raised by another operation included, which r3's tests (I/O errors only) never threw — may be retried or read as the write's own condition refused; a cancellation propagates, the write committed (B48) — Task 4a, `A_notification_failing_in_a_verification_batch_leaves_every_stamp_recorded_and_says_so` and `A_notification_cancelled_after_the_commit_propagates_and_the_write_stays`; Task 4b, `A_conflict_raised_by_the_notification_of_a_conditioned_write_is_not_its_refusal`, `A_conflict_raised_by_the_notification_of_an_append_neither_retries_nor_appends_again`, `A_conflict_raised_by_the_notification_of_a_verification_writes_nothing_again` and `A_failing_notification_leaves_the_real_conflict_of_the_next_concept_reported_as_such`; Task 4c, `A_notification_failing_after_a_commit_is_written_in_the_report_never_failed`; Task 5, `A_notification_failing_after_the_commit_is_an_error_text_and_the_entry_is_appended_once` and `A_notification_cancelled_after_the_commit_propagates_and_the_entry_stays`.

---
## Review Focus (part C)

1. **A root `index.md` written by someone else between the read and the write.** Today's code read the root once; a conditional write that loses the race must read again, or it would drop the `okf_version` marker another writer just put there — Task 6, `A_root_index_written_meanwhile_is_read_again_and_its_marker_carried_forward`.
2. **A provider refusal's own text.** `BundleAccessRefusedException.Detail` may name storage internals; the report must carry the library's message only — Task 6, `A_provider_refusal_is_a_failure_in_the_report_and_a_silent_skip_for_the_string_overloads` (a `secret-token=123` detail never reaches the reason).
3. **A directory named `index.md`.** The one input whose `okf index` stderr changes; the tree check refuses it before a byte is written — Task 6, `A_new_index_that_would_make_the_tree_invalid_is_refused_before_it_is_written` (any provider) and `On_the_filesystem_a_directory_named_index_md_ends_the_run_with_a_conflict`.
4. **Directories that hold nothing the key space sees.** Empty, holding only links, holding only non-`.md` files: only the last one is referenced — Task 6, `An_empty_directory_is_no_longer_referenced_but_one_holding_only_non_markdown_files_still_is`, `A_directory_holding_only_links_is_not_referenced`; Task 7, `Index_no_longer_references_an_empty_directory` at the CLI surface.
5. **`relativeDir` on Windows.** The string overload must keep `a\b` while the provider overload passes `a/b` — Task 6, `Synthesize_gets_the_os_separator_from_the_string_overload_and_a_slash_from_the_provider_overload`.
6. **A failure that must end a `string`-overload run but not a provider-overload run** (an I/O error, a write that kept conflicting) — Task 6, `Any_other_failure_is_a_failure_in_the_report_and_ends_a_string_overload_run_as_before`.
7. **A host policy that excludes an index key** (F1). The validator filters what is loaded; it must also keep the generator from creating or replacing a key it refuses — Task 6, `An_index_the_key_policy_refuses_is_neither_read_nor_written`.

---
### Task 1: Write-side value types, the write contract as tests, and a writable in-memory provider

**Files:**
- Create: `src/OKF4net/Providers/BundleWriteReport.cs`
- Modify: `src/OKF4net/Providers/BundleProviderExceptions.cs` (base constructors, `BundleWriteConflictReason`, `BundleWriteOutcomeUnknownException`)
- Modify: `src/OKF4net/Providers/IBundleProvider.cs` (the contract of `Write` and `Delete`)
- Modify (rewrite): `tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs`
- Test: `tests/OKF4net.Tests/Providers/WritableProviderContractTests.cs` (abstract base), `tests/OKF4net.Tests/Providers/InMemoryBundleProviderWriteTests.cs`, `tests/OKF4net.Tests/Providers/BundleWriteReportTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes (lot A, unchanged): `IBundleProvider`, `BundleKey`, `BundleWriteCondition`/`BundleWriteConditionKind`, `BundleObjectVersion`, `BundleObject`, `BundleListing`, `BundleLimits`, `BundleProviderCapabilities`, `BundleKeyConstraints`, `BundleReadOnlyException(string providerId)`, `BundleObjectNotFoundException`, `BundleVersionMismatchException`, `BundleAccessRefusedException`, `BundleLimitExceededException`, `OkfException(string)` and `OkfException(string, Exception)` (both `protected`), `provider.AsReadOnly()`.
- Produces:
  - `public sealed record BundleWriteReport(IReadOnlyList<BundleKey> Written, IReadOnlyList<BundleKey> Deleted, IReadOnlyList<BundleWriteFailure> Failed)` with `bool Succeeded` and `static BundleWriteReport Empty`; a null list throws `ArgumentNullException`.
  - `public sealed record BundleWriteFailure(BundleKey Key, string Reason)`; a null key throws `ArgumentNullException`, a null or empty reason `ArgumentException`.
  - `public enum BundleWriteConflictReason { ConditionFailed, NameUnavailable }`; `BundleWriteConflictException(BundleKey key, BundleWriteConflictReason reason)` beside the unchanged `BundleWriteConflictException(BundleKey key)` (= `ConditionFailed`, same message as before); property `Reason`.
  - `public sealed class BundleWriteOutcomeUnknownException(BundleKey key, Exception inner) : BundleProviderException`, property `Key`; `InnerException` is `inner`.
  - `BundleProviderException`: `protected (string message)` and `protected (string message, Exception inner)`.
  - Test side: `InMemoryBundleProvider(BundleKeyConstraints native = None, string id = "memory", bool canWrite = false)` with `CanWrite`, `IReadOnlyList<string> Calls`, `ClearCalls()`, `ConflictOnNextWrite()`, `BeforeNextWrite(Action<InMemoryBundleProvider>)`, `FailWrite(int nth, Exception failure)`, and every existing member (`ObeyLimits`, `ListTwice`, `FailReadsWith`, `FailEnumerateWith`, `FailVersionWith`, `Add`, `FailReadOf`, `RefuseExistsOf`) unchanged in behaviour. `public abstract class WritableProviderContractTests` with `protected abstract IBundleProvider CreateEmptyProvider()`, `protected abstract IBundleProvider OpenSameStorage()`, `protected IBundleProvider Provider`, the helpers `K`, `B`, `ReadText`, and fifteen contract tests.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/Providers/WritableProviderContractTests.cs` — the contract every writable provider must pass (the filesystem provider inherits it in Task 2a):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The write half of the provider contract (spec §1, §3, §4) as tests every writable
/// provider must pass. A provider's test class derives from this one and hands it a fresh,
/// empty, writable provider; nothing here knows how the provider stores its bytes, so the
/// in-memory and the filesystem providers are held to exactly the same rules.
/// </summary>
public abstract class WritableProviderContractTests
{
    private IBundleProvider? _provider;

    /// <summary>A new, empty provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is true.</summary>
    protected abstract IBundleProvider CreateEmptyProvider();

    /// <summary>A second provider over the same storage as <see cref="Provider"/> (another instance, another client).</summary>
    protected abstract IBundleProvider OpenSameStorage();

    /// <summary>The provider under test, created on first use.</summary>
    protected IBundleProvider Provider => _provider ??= CreateEmptyProvider();

    protected static BundleKey K(string s) => BundleKey.Parse(s);

    protected static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    protected string ReadText(string key)
    {
        using var obj = Provider.Read(K(key));
        using var reader = new StreamReader(obj.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_provider_declares_that_it_can_write()
    {
        Assert.True(Provider.Capabilities.CanWrite);
    }

    [Fact]
    public void A_create_stores_the_bytes_and_returns_the_version_a_read_reports()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.MustNotExist);
        Assert.True(Provider.Exists(K("a.md")));
        using var obj = Provider.Read(K("a.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("one", ReadText("a.md"));
    }

    [Fact]
    public void Versions_follow_the_bytes()
    {
        var one = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var two = Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        var again = Provider.Write(K("c.md"), B("one"), BundleWriteCondition.None);
        Assert.NotEqual(one, two);
        Assert.Equal(one, again);
    }

    [Fact]
    public void An_empty_object_is_an_object()
    {
        var v = Provider.Write(K("empty.md"), ReadOnlyMemory<byte>.Empty, BundleWriteCondition.MustNotExist);
        using var obj = Provider.Read(K("empty.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal(-1, obj.Content.ReadByte());
    }

    [Fact]
    public void MustNotExist_refuses_an_existing_object_and_changes_nothing()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal("one", ReadText("a.md"));
    }

    [Fact]
    public void IfMatch_replaces_only_the_version_it_names()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var v2 = Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1));
        Assert.Equal("two", ReadText("a.md"));

        var stale = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("three"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, stale.Reason);
        Assert.Equal("two", ReadText("a.md"));

        Provider.Write(K("a.md"), B("three"), BundleWriteCondition.IfMatch(v2));
        Assert.Equal("three", ReadText("a.md"));
    }

    [Fact]
    public void IfMatch_never_holds_for_an_absent_object()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("b.md"), B("one"), BundleWriteCondition.IfMatch(v)));
        Assert.False(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void None_creates_then_replaces()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Provider.Write(K("a.md"), B("two"), BundleWriteCondition.None);
        Assert.Equal("two", ReadText("a.md"));
    }

    [Fact]
    public void Prefixes_are_implicit_on_write_and_on_delete()
    {
        Provider.Write(K("a/b/c.md"), B("deep"), BundleWriteCondition.MustNotExist);
        Assert.Equal(["a/b/c.md"], Provider.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName));
        Assert.Contains("a/b/c.md", Provider.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));

        Provider.Delete(K("a/b/c.md"), BundleWriteCondition.None);
        Assert.Empty(Provider.Enumerate(K("a"), BundleLimits.Default));
        Assert.DoesNotContain(Provider.Enumerate(null, BundleLimits.Default), l => l.RawName.StartsWith("a/", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_honours_every_condition()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);

        Assert.Equal(BundleWriteConflictReason.ConditionFailed, Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.MustNotExist)).Reason);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion("stale"))));
        Assert.Equal("one", ReadText("a.md"));

        Provider.Delete(K("a.md"), BundleWriteCondition.IfMatch(v1));
        Assert.False(Provider.Exists(K("a.md")));
        Assert.Throws<BundleObjectNotFoundException>(() => Provider.Read(K("a.md")));

        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        Provider.Delete(K("b.md"), BundleWriteCondition.None);
        Assert.False(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void Deleting_an_absent_object_is_a_no_op_unless_a_version_was_expected()
    {
        Provider.Delete(K("nope.md"), BundleWriteCondition.None);
        Provider.Delete(K("nope.md"), BundleWriteCondition.MustNotExist);
        Provider.Delete(K("no/such/prefix.md"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("nope.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion("x"))));
        Assert.False(Provider.Exists(K("nope.md")));
    }

    [Fact]
    public void The_bundle_version_moves_with_every_write_and_delete()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var v1 = Provider.GetVersion(BundleLimits.Default);
        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        var v2 = Provider.GetVersion(BundleLimits.Default);
        Provider.Delete(K("b.md"), BundleWriteCondition.None);
        var v3 = Provider.GetVersion(BundleLimits.Default);
        Assert.NotEqual(v1, v2);
        Assert.NotEqual(v2, v3);
    }

    [Fact]
    public void A_conflict_message_names_the_key_and_nothing_of_the_storage()
    {
        Provider.Write(K("dir/a.md"), B("one"), BundleWriteCondition.None);
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("dir/a.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Contains("'dir/a.md'", ex.Message);
        Assert.DoesNotContain(Provider.Location, ex.Message);
        Assert.DoesNotContain(Provider.Id, ex.Message);
    }

    [Fact]
    public void The_read_only_view_refuses_every_write_and_changes_nothing()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var ro = Provider.AsReadOnly();
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("two"), BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("new.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Equal("one", ReadText("a.md"));
        Assert.False(Provider.Exists(K("new.md")));
    }

    /// <summary>
    /// Two writers holding the same version race to replace it, each through its own provider
    /// instance over the same storage: exactly one wins, the other gets a conflict, and the
    /// stored bytes are the winner's. Fifty rounds, because a check-then-replace that is not
    /// atomic loses this race only sometimes.
    /// </summary>
    [Fact]
    public void Concurrent_IfMatch_writers_on_one_version_have_exactly_one_winner()
    {
        var other = OpenSameStorage();
        for (var round = 0; round < 50; round++)
        {
            var key = K($"race/{round}.md");
            var v0 = Provider.Write(key, B("v0"), BundleWriteCondition.None);
            using var start = new Barrier(2);
            var outcomes = new string?[2];
            var errors = new Exception?[2];
            var writers = new[] { Provider, other }.Select((p, i) => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    p.Write(key, B($"writer-{i}"), BundleWriteCondition.IfMatch(v0));
                    outcomes[i] = "won";
                }
                catch (BundleWriteConflictException)
                {
                    outcomes[i] = "conflict";
                }
                catch (Exception e)
                {
                    errors[i] = e;
                }
            })).ToArray();
            foreach (var t in writers)
            {
                t.Start();
            }

            foreach (var t in writers)
            {
                t.Join();
            }

            Assert.All(errors, e => Assert.Null(e));
            Assert.Equal(1, outcomes.Count(o => o == "won"));
            Assert.Equal(1, outcomes.Count(o => o == "conflict"));
            var winner = Array.IndexOf(outcomes, "won");
            Assert.Equal($"writer-{winner}", ReadText(key.ToString()));
        }
    }
}
```

`tests/OKF4net.Tests/Providers/InMemoryBundleProviderWriteTests.cs` — the contract on the in-memory provider, plus the hooks later tasks use to script a storage (a conflict, a change landing first, the failure of the Nth write, the record of every call):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The write contract on the in-memory test provider, plus the hooks later tasks use to script a storage's behaviour.</summary>
public class InMemoryBundleProviderWriteTests : WritableProviderContractTests
{
    protected override IBundleProvider CreateEmptyProvider() => new InMemoryBundleProvider(canWrite: true);

    /// <summary>A dictionary has no second client: the same instance stands for it.</summary>
    protected override IBundleProvider OpenSameStorage() => Provider;

    private InMemoryBundleProvider Memory => (InMemoryBundleProvider)Provider;

    [Fact]
    public void Versions_are_the_lowercase_hex_sha256_of_the_bytes_like_the_filesystem()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(B("one"))), v.Token);
    }

    [Fact]
    public void By_default_the_provider_is_read_only_and_refuses_before_storing()
    {
        var ro = new InMemoryBundleProvider().Add("a.md", "one");
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("two"), BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Equal(["write a.md None", "delete a.md None"], ro.Calls);
        using var obj = ro.Read(K("a.md"));
        Assert.Equal(3, obj.Length);
    }

    [Fact]
    public void Every_call_is_recorded_in_order()
    {
        Memory.Add("seed.md", "s");
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.MustNotExist);
        _ = Provider.Exists(K("a.md"));
        using (Provider.Read(K("a.md")))
        {
        }

        _ = Provider.Enumerate(K("a.md"), BundleLimits.Default).ToList();
        _ = Provider.GetVersion(BundleLimits.Default);
        Provider.Delete(K("a.md"), BundleWriteCondition.None);
        Assert.Equal(["write a.md MustNotExist", "exists a.md", "read a.md", "enumerate a.md", "version", "delete a.md None"], Memory.Calls);
        Memory.ClearCalls();
        Assert.Empty(Memory.Calls);
    }

    [Fact]
    public void A_scripted_conflict_hits_the_next_write_or_delete_once_and_stores_nothing()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Memory.ConflictOnNextWrite();
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Equal("one", ReadText("a.md"));
        Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)); // one-shot

        Memory.ConflictOnNextWrite();
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.True(Provider.Exists(K("a.md")));
    }

    [Fact]
    public void A_scripted_change_lands_just_before_the_next_write_checks_its_condition()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Memory.BeforeNextWrite(p => p.Add("a.md", "changed elsewhere"));
        Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal("changed elsewhere", ReadText("a.md"));
    }

    [Fact]
    public void A_scripted_failure_hits_exactly_the_nth_write_or_delete_and_stores_nothing()
    {
        var boom = new IOException("disk full");
        Memory.FailWrite(2, boom);
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Same(boom, Assert.Throws<IOException>(() => Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None)));
        Assert.False(Provider.Exists(K("b.md")));
        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        Assert.True(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void Writes_do_not_disturb_an_enumeration_in_progress()
    {
        Provider.Write(K("a.md"), B("1"), BundleWriteCondition.None);
        Provider.Write(K("b.md"), B("2"), BundleWriteCondition.None);
        var seen = new List<string>();
        foreach (var l in Provider.Enumerate(null, BundleLimits.Default))
        {
            seen.Add(l.RawName);
            Provider.Write(K("c.md"), B("3"), BundleWriteCondition.None);
        }

        Assert.Equal(["a.md", "b.md"], seen);
    }
}
```

`tests/OKF4net.Tests/Providers/BundleWriteReportTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleWriteReportTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    [Fact]
    public void A_report_succeeds_exactly_when_nothing_failed()
    {
        var clean = new BundleWriteReport([K("a.md")], [K("b.md")], []);
        Assert.True(clean.Succeeded);

        var partial = new BundleWriteReport([K("a.md")], [], [new BundleWriteFailure(K("c.md"), "'c.md' changed since it was read; the write was refused")]);
        Assert.False(partial.Succeeded);
        Assert.Equal(K("c.md"), Assert.Single(partial.Failed).Key);
    }

    [Fact]
    public void The_empty_report_has_nothing_and_succeeds()
    {
        Assert.Empty(BundleWriteReport.Empty.Written);
        Assert.Empty(BundleWriteReport.Empty.Deleted);
        Assert.Empty(BundleWriteReport.Empty.Failed);
        Assert.True(BundleWriteReport.Empty.Succeeded);
    }

    [Fact]
    public void Null_lists_and_a_null_or_empty_reason_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport([], null!, []));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport([], [], null!));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteFailure(null!, "why"));
        Assert.ThrowsAny<ArgumentException>(() => new BundleWriteFailure(K("a.md"), ""));
    }

    [Fact]
    public void Conflict_reasons_have_fixed_library_authored_messages()
    {
        var changed = new BundleWriteConflictException(K("a.md"));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, changed.Reason);
        Assert.Equal("'a.md' changed since it was read; the write was refused", changed.Message);

        var taken = new BundleWriteConflictException(K("a.md"), BundleWriteConflictReason.NameUnavailable);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, taken.Reason);
        Assert.Equal("'a.md' cannot be stored under that name: the storage holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name", taken.Message);
    }

    [Fact]
    public void An_unknown_outcome_names_the_key_and_keeps_the_raw_failure_for_the_host_log()
    {
        var raw = new IOException(@"ReplaceFile failed for C:\secret\a.md", unchecked((int)0x80070498));
        var ex = new BundleWriteOutcomeUnknownException(K("a.md"), raw);
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Same(raw, ex.InnerException);
        Assert.Equal("the write of 'a.md' failed with an unknown outcome: the object may hold its previous content, the new content, or nothing", ex.Message);
        Assert.IsAssignableFrom<BundleProviderException>(ex);
    }
}
```

Convert the three new files to CRLF: `unix2dos -q tests/OKF4net.Tests/Providers/WritableProviderContractTests.cs tests/OKF4net.Tests/Providers/InMemoryBundleProviderWriteTests.cs tests/OKF4net.Tests/Providers/BundleWriteReportTests.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only, each naming something this task adds — `CS0103`/`CS0246` for `BundleWriteConflictReason`, `BundleWriteReport`, `BundleWriteFailure`, `BundleWriteOutcomeUnknownException`; `CS1061` for `Reason`, `BeforeNextWrite`, `Calls`, `ClearCalls`, `ConflictOnNextWrite`, `FailWrite`; `CS1729` (the two-argument conflict constructor); `CS1739` (`canWrite`).

- [ ] **Step 3: Add the report types**

`src/OKF4net/Providers/BundleWriteReport.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// What an operation over several objects did (spec §1, §4): there is no multi-object
/// transaction, so each object is written or deleted atomically on its own and the report
/// says exactly which ones landed and which failed, with why. An operation that returns one
/// can be re-run safely.
/// </summary>
/// <param name="Written">The keys written, in the order they were written.</param>
/// <param name="Deleted">The keys deleted, in the order they were deleted.</param>
/// <param name="Failed">The keys that were not written or deleted, each with a library-authored reason.</param>
public sealed record BundleWriteReport(IReadOnlyList<BundleKey> Written, IReadOnlyList<BundleKey> Deleted, IReadOnlyList<BundleWriteFailure> Failed)
{
    /// <summary>A report of an operation that touched nothing.</summary>
    public static BundleWriteReport Empty { get; } = new([], [], []);

    /// <summary>The keys written, in the order they were written.</summary>
    public IReadOnlyList<BundleKey> Written { get; init; } = Written ?? throw new ArgumentNullException(nameof(Written));

    /// <summary>The keys deleted, in the order they were deleted.</summary>
    public IReadOnlyList<BundleKey> Deleted { get; init; } = Deleted ?? throw new ArgumentNullException(nameof(Deleted));

    /// <summary>The keys that were not written or deleted, each with a library-authored reason.</summary>
    public IReadOnlyList<BundleWriteFailure> Failed { get; init; } = Failed ?? throw new ArgumentNullException(nameof(Failed));

    /// <summary><see langword="true"/> when nothing failed.</summary>
    public bool Succeeded => Failed.Count == 0;
}

/// <summary>One object an operation could not write or delete, and why (a library-authored sentence that names keys, never host paths).</summary>
/// <param name="Key">The object's key.</param>
/// <param name="Reason">Why it failed.</param>
public sealed record BundleWriteFailure(BundleKey Key, string Reason)
{
    /// <summary>The object's key.</summary>
    public BundleKey Key { get; init; } = Key ?? throw new ArgumentNullException(nameof(Key));

    /// <summary>Why it failed.</summary>
    public string Reason { get; init; } = string.IsNullOrEmpty(Reason) ? throw new ArgumentException("a failure needs a reason", nameof(Reason)) : Reason;
}
```

- [ ] **Step 4: Extend the contract's exceptions** in `src/OKF4net/Providers/BundleProviderExceptions.cs` (with `Edit`, which keeps the file's CRLF)

Replace the primary-constructor base class

```csharp
public abstract class BundleProviderException(string message) : OkfException(message)
{
}
```

with

```csharp
public abstract class BundleProviderException : OkfException
{
    /// <summary>Creates the exception with a library-authored message.</summary>
    protected BundleProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a library-authored message and the raw cause as <see cref="Exception.InnerException"/> (the host-log channel, spec §7).</summary>
    protected BundleProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
```

and replace the whole `BundleWriteConflictException`, summary line included,

```csharp
/// <summary>A write or delete condition did not hold (spec §4).</summary>
public sealed class BundleWriteConflictException(BundleKey key)
    : BundleProviderException($"'{key}' changed since it was read; the write was refused")
{
    /// <summary>The key whose condition failed.</summary>
    public BundleKey Key { get; } = key;
}
```

with

```csharp
/// <summary>Why a write or delete was refused as a conflict — a closed set, so the message is built from it and never from text a provider wrote.</summary>
public enum BundleWriteConflictReason
{
    /// <summary>The condition did not hold: the object exists, is absent, or has another version than the one expected (spec §4).</summary>
    ConditionFailed,

    /// <summary>The storage cannot hold an object under that exact name: it holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form on a volume that folds them), or the name is one it cannot store literally (spec §2, §8).</summary>
    NameUnavailable,
}

/// <summary>A write or delete was refused because its condition did not hold, or because the storage cannot hold that exact name (spec §4); nothing was changed.</summary>
public sealed class BundleWriteConflictException(BundleKey key, BundleWriteConflictReason reason)
    : BundleProviderException(Describe(key, reason))
{
    /// <summary>A conflict on the condition (<see cref="BundleWriteConflictReason.ConditionFailed"/>).</summary>
    public BundleWriteConflictException(BundleKey key)
        : this(key, BundleWriteConflictReason.ConditionFailed)
    {
    }

    /// <summary>The key whose write or delete was refused.</summary>
    public BundleKey Key { get; } = key;

    /// <summary>Why it was refused.</summary>
    public BundleWriteConflictReason Reason { get; } = reason;

    private static string Describe(BundleKey key, BundleWriteConflictReason reason) => reason switch
    {
        BundleWriteConflictReason.NameUnavailable => $"'{key}' cannot be stored under that name: the storage holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name",
        _ => $"'{key}' changed since it was read; the write was refused",
    };
}

/// <summary>
/// A write or delete failed in a way that leaves its outcome unknown (spec §4): the storage
/// reported an error from the commit itself, and what it holds afterwards could not be
/// established. The object may hold its previous content, the new content, or nothing; a
/// provider keeps whatever it could not account for, and says where in its own documentation.
/// The raw failure is the <see cref="Exception.InnerException"/>, for the host's log only.
/// </summary>
public sealed class BundleWriteOutcomeUnknownException(BundleKey key, Exception inner)
    : BundleProviderException($"the write of '{key}' failed with an unknown outcome: the object may hold its previous content, the new content, or nothing", inner)
{
    /// <summary>The key whose write has an unknown outcome.</summary>
    public BundleKey Key { get; } = key;
}
```

- [ ] **Step 5: Write the contract down** — in `src/OKF4net/Providers/IBundleProvider.cs`, replace the two members that end the interface

```csharp
    /// <summary>Writes an object atomically if <paramref name="condition"/> holds; returns the new version.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>Deletes an object if <paramref name="condition"/> holds.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
```

with

```csharp
    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/> if
    /// <paramref name="condition"/> holds, and returns the version a later <see cref="Read"/> of
    /// these bytes reports (spec §1, §4).
    /// <para><b>Conditions</b>, checked atomically with the replacement:
    /// <see cref="BundleWriteCondition.None"/> always holds;
    /// <see cref="BundleWriteCondition.MustNotExist"/> holds while no object exists under the key;
    /// <see cref="BundleWriteCondition.IfMatch"/> holds while an object exists under the key and
    /// its current version is the expected one — never for an absent object.</para>
    /// <para><b>Atomic</b>, per object: whatever fails, the key holds the previous content (no
    /// object, for a creation) or the whole new content, never a part of it, and no reader, in
    /// this process or another, ever reads a part of the new content. A provider documents
    /// whether a concurrent reader can, while the commit runs, momentarily find no object under
    /// the key (<see cref="FileSystemBundleProvider"/> can, on Windows). When the call throws
    /// anything but <see cref="BundleWriteOutcomeUnknownException"/>, the stored object is
    /// unchanged. There is no transaction across objects.</para>
    /// <para><b>Prefixes are implicit</b>: there are no directories in a key space, so no parent
    /// needs creating; a provider whose storage has directories creates the missing ones itself,
    /// under its own path guards.</para>
    /// <para>The core has validated the key under its policy before the call (spec §2); the
    /// provider adds only its own aliasing guarantees — it matches stored names exactly, and
    /// refuses a name its storage would confuse with another.</para>
    /// </summary>
    /// <returns>The new version.</returns>
    /// <exception cref="BundleReadOnlyException"><see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/>; nothing was attempted.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold, or the storage cannot hold an object under that exact name (<see cref="BundleWriteConflictException.Reason"/>); nothing was changed.</exception>
    /// <exception cref="BundleAccessRefusedException">The key's path is refused (a reparse point, an entry that cannot be inspected, the confinement boundary); nothing was changed.</exception>
    /// <exception cref="BundleWriteOutcomeUnknownException">The commit itself failed and what the storage now holds could not be established.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>
    /// Removes the object under <paramref name="key"/> if <paramref name="condition"/> holds
    /// (spec §1, §4), atomically. The conditions read as for <see cref="Write"/>:
    /// <see cref="BundleWriteCondition.None"/> always holds,
    /// <see cref="BundleWriteCondition.MustNotExist"/> only when there is nothing to delete, and
    /// <see cref="BundleWriteCondition.IfMatch"/> only while the object exists at that version.
    /// Deleting an absent object under a condition that holds does nothing and is not an error. A
    /// prefix stops existing with the last object under it; a storage with directories may leave
    /// them in place, since an empty directory holds no key.
    /// </summary>
    /// <exception cref="BundleReadOnlyException"><see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/>; nothing was attempted.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold; nothing was changed.</exception>
    /// <exception cref="BundleAccessRefusedException">The key's path is refused; nothing was changed.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
```

- [ ] **Step 6: Make the in-memory provider writable**

Replace the whole of `tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs` with the following (then `unix2dos -q` it). Every existing member keeps its behaviour: `Enumerate` still throws `FailEnumerateWith` at its first step, and now lists a snapshot taken at that step, so a write during an enumeration does not invalidate it; `Add` still stores unconditionally, whatever `CanWrite` says.

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A provider over a dictionary, so loader tests can use names the host OS could not
/// create (reserved names, case-colliding siblings) and never touch the disk, and writer
/// tests can script a storage's behaviour. Read-only unless constructed with
/// <c>canWrite: true</c>; writable, it honours the write contract exactly as the filesystem
/// provider does — the same conditions, the same SHA-256 versions, the same exceptions
/// (<see cref="WritableProviderContractTests"/> holds both to it). Native constraints default
/// to <see cref="BundleKeyConstraints.None"/>. <see cref="ObeyLimits"/> is on by default; off,
/// it ignores every bound — the core must still enforce them (spec §6). <see cref="Add"/>
/// stores an object unconditionally, bypassing <see cref="CanWrite"/>, to model a change
/// behind the bundle's back. Every call is recorded in <see cref="Calls"/>. Thread-safe.
/// </summary>
public sealed class InMemoryBundleProvider : IBundleProvider
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listedTwice = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existsRefused = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Exception> _failingWrites = [];
    private readonly List<string> _calls = [];
    private int _writeCalls;
    private bool _conflictOnNextWrite;
    private Action<InMemoryBundleProvider>? _beforeNextWrite;

    public InMemoryBundleProvider(BundleKeyConstraints native = BundleKeyConstraints.None, string id = "memory", bool canWrite = false)
    {
        NativeConstraints = native;
        Id = id;
        CanWrite = canWrite;
    }

    public bool ObeyLimits { get; init; } = true;

    /// <summary>Whether <see cref="Write"/> and <see cref="Delete"/> are allowed (the constructor's <c>canWrite</c>).</summary>
    public bool CanWrite { get; }

    /// <summary>Every call so far, in order: <c>version</c>, <c>enumerate {prefix}</c>, <c>read {key}</c>, <c>exists {key}</c>, <c>write {key} {condition kind}</c>, <c>delete {key} {condition kind}</c> (refused calls included).</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Forgets the calls recorded so far.</summary>
    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    /// <summary>Makes <see cref="Enumerate"/> list <paramref name="rawName"/> twice — a buggy backend (Review Focus 5).</summary>
    public InMemoryBundleProvider ListTwice(string rawName)
    {
        lock (_gate)
        {
            _listedTwice.Add(rawName);
        }

        return this;
    }

    /// <summary>Thrown by every <see cref="Read"/> when set.</summary>
    public Exception? FailReadsWith { get; set; }

    /// <summary>Thrown by the first step of every <see cref="Enumerate"/> when set (an unlistable storage).</summary>
    public Exception? FailEnumerateWith { get; set; }

    /// <summary>Thrown by <see cref="GetVersion"/> when set.</summary>
    public Exception? FailVersionWith { get; set; }

    /// <summary>Stores an object unconditionally, whatever <see cref="CanWrite"/> says (a change made behind the bundle's back); not recorded in <see cref="Calls"/>.</summary>
    public InMemoryBundleProvider Add(string rawName, string text)
    {
        lock (_gate)
        {
            _objects[rawName] = Encoding.UTF8.GetBytes(text);
        }

        return this;
    }

    /// <summary>Makes <see cref="Read"/> of one object throw <paramref name="failure"/> instead.</summary>
    public InMemoryBundleProvider FailReadOf(string rawName, Exception failure)
    {
        lock (_gate)
        {
            _failures[rawName] = failure;
        }

        return this;
    }

    /// <summary>Makes the next <see cref="Write"/> or <see cref="Delete"/> that reaches the storage throw <see cref="BundleWriteConflictException"/> before checking its condition, storing nothing — once.</summary>
    public InMemoryBundleProvider ConflictOnNextWrite()
    {
        lock (_gate)
        {
            _conflictOnNextWrite = true;
        }

        return this;
    }

    /// <summary>Runs <paramref name="change"/> inside the next <see cref="Write"/> or <see cref="Delete"/> that reaches the storage, just before its condition is checked — another writer landing first. Once.</summary>
    public InMemoryBundleProvider BeforeNextWrite(Action<InMemoryBundleProvider> change)
    {
        lock (_gate)
        {
            _beforeNextWrite = change;
        }

        return this;
    }

    /// <summary>Makes the <paramref name="nth"/> (from 1) <see cref="Write"/> or <see cref="Delete"/> call that reaches the storage throw <paramref name="failure"/>, storing nothing.</summary>
    public InMemoryBundleProvider FailWrite(int nth, Exception failure)
    {
        lock (_gate)
        {
            _failingWrites[nth] = failure;
        }

        return this;
    }

    /// <summary>Makes <see cref="Exists"/> of one key throw <see cref="BundleAccessRefusedException"/>, like a provider refusing a link or a path outside its boundary.</summary>
    public InMemoryBundleProvider RefuseExistsOf(string rawName)
    {
        lock (_gate)
        {
            _existsRefused.Add(rawName);
        }

        return this;
    }

    public string Id { get; }
    public string Location => "memory://" + Id;
    public BundleProviderCapabilities Capabilities => new(CanWrite);
    public BundleKeyConstraints NativeConstraints { get; }

    public BundleVersion GetVersion(BundleLimits limits)
    {
        Record("version");
        if (FailVersionWith is { } failure)
        {
            throw failure;
        }

        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var (name, bytes) in _objects)
            {
                sb.Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
            }
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        Record($"enumerate {prefix}");
        return EnumerateCore(prefix, limits);
    }

    private IEnumerable<BundleListing> EnumerateCore(BundleKey? prefix, BundleLimits limits)
    {
        if (FailEnumerateWith is { } failure)
        {
            throw failure;
        }

        KeyValuePair<string, byte[]>[] snapshot;
        string[] twice;
        lock (_gate)
        {
            snapshot = [.. _objects];
            twice = [.. _listedTwice];
        }

        var count = 0;
        foreach (var (name, bytes) in snapshot)
        {
            if (prefix is not null && !(name == prefix.ToString() || name.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (ObeyLimits && ++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            yield return new BundleListing(name, bytes.Length, null);
            if (twice.Contains(name, StringComparer.Ordinal))
            {
                yield return new BundleListing(name, bytes.Length, null);
            }
        }
    }

    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        Record($"read {key}");
        if (FailReadsWith is { } failure)
        {
            throw failure;
        }

        byte[]? bytes;
        lock (_gate)
        {
            if (_failures.TryGetValue(key.ToString(), out var specific))
            {
                throw specific;
            }

            _objects.TryGetValue(key.ToString(), out bytes);
        }

        if (bytes is null)
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = VersionOf(bytes);
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }

    public bool Exists(BundleKey key)
    {
        Record($"exists {key}");
        lock (_gate)
        {
            return _existsRefused.Contains(key.ToString())
                ? throw new BundleAccessRefusedException(key, "secret-token=123 refused by the storage")
                : _objects.ContainsKey(key.ToString());
        }
    }

    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        Record($"write {key} {condition.Kind}");
        if (!CanWrite)
        {
            throw new BundleReadOnlyException(Id);
        }

        lock (_gate)
        {
            BeginMutation(key);
            _objects.TryGetValue(key.ToString(), out var current);
            Check(key, current, condition);
            var bytes = content.ToArray();
            _objects[key.ToString()] = bytes;
            return VersionOf(bytes);
        }
    }

    public void Delete(BundleKey key, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        Record($"delete {key} {condition.Kind}");
        if (!CanWrite)
        {
            throw new BundleReadOnlyException(Id);
        }

        lock (_gate)
        {
            BeginMutation(key);
            _objects.TryGetValue(key.ToString(), out var current);
            Check(key, current, condition);
            _objects.Remove(key.ToString());
        }
    }

    private static BundleObjectVersion VersionOf(byte[] bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>The scripted hooks, in order: the Nth-call failure, the one-shot conflict, the one-shot change. Caller holds the gate.</summary>
    private void BeginMutation(BundleKey key)
    {
        _writeCalls++;
        if (_failingWrites.Remove(_writeCalls, out var failure))
        {
            throw failure;
        }

        if (_conflictOnNextWrite)
        {
            _conflictOnNextWrite = false;
            throw new BundleWriteConflictException(key);
        }

        if (_beforeNextWrite is { } change)
        {
            _beforeNextWrite = null;
            change(this); // re-enters the gate: Lock is reentrant
        }
    }

    /// <summary>The condition rules of <see cref="IBundleProvider.Write"/> and <see cref="IBundleProvider.Delete"/>, on the current bytes (or none).</summary>
    private static void Check(BundleKey key, byte[]? current, BundleWriteCondition condition)
    {
        var holds = condition.Kind switch
        {
            BundleWriteConditionKind.None => true,
            BundleWriteConditionKind.MustNotExist => current is null,
            BundleWriteConditionKind.IfMatch => current is not null && condition.Expected is { } expected && VersionOf(current) == expected,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, "unknown condition"),
        };
        if (!holds)
        {
            throw new BundleWriteConflictException(key);
        }
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~InMemoryBundleProviderWriteTests|FullyQualifiedName~BundleWriteReportTests"`
Expected: `Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27` (15 contract tests, 7 in-memory tests, 5 report tests).

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers"`
Expected: `Failed: 0` — the loader tests that use the in-memory provider (`BundleProviderLoadTests`, `BundlePortabilityTests`) are unchanged, and `BundleProviderLoadTests`' read-only assertion still holds because the provider is read-only by default.

- [ ] **Step 8: CHANGELOG** — under `## [Unreleased]` → `### Added`, after the lot A bullets (the last one ends `code: OKF §11 imposes no naming constraint.`), add:

```markdown
- **Write side of the provider contract (#139, lot B).** `BundleWriteReport`
  and `BundleWriteFailure` (what an operation over several objects wrote,
  deleted and failed, with why); `BundleWriteConflictException.Reason`
  (`BundleWriteConflictReason`: the condition did not hold, or the storage
  cannot hold that exact name); `BundleWriteOutcomeUnknownException` (the
  commit itself failed and what the storage holds could not be established).
  `IBundleProvider.Write` and `Delete` now document their conditions,
  atomicity and implicit prefixes.
```

- [ ] **Step 9: Verify and commit**

```bash
for f in src/OKF4net/Providers/BundleWriteReport.cs src/OKF4net/Providers/BundleProviderExceptions.cs src/OKF4net/Providers/IBundleProvider.cs tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs tests/OKF4net.Tests/Providers/WritableProviderContractTests.cs tests/OKF4net.Tests/Providers/InMemoryBundleProviderWriteTests.cs tests/OKF4net.Tests/Providers/BundleWriteReportTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done   # equal numbers on every line
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
```

Then the Linux container command of the Global Constraints (both passes `Failed: 0`), and:

```bash
git add src/OKF4net/Providers/BundleWriteReport.cs src/OKF4net/Providers/BundleProviderExceptions.cs src/OKF4net/Providers/IBundleProvider.cs tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs tests/OKF4net.Tests/Providers/WritableProviderContractTests.cs tests/OKF4net.Tests/Providers/InMemoryBundleProviderWriteTests.cs tests/OKF4net.Tests/Providers/BundleWriteReportTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(providers): write-side value types, the write contract's tests, a writable in-memory provider

BundleWriteReport and BundleWriteFailure; BundleWriteConflictException gains a
closed Reason (condition failed, name unavailable); BundleWriteOutcomeUnknownException
for a commit whose result cannot be established. IBundleProvider.Write and Delete
document their conditions, atomicity and implicit prefixes. The in-memory test
provider can be constructed writable, honours the same conditions with the same
SHA-256 versions, records every call and can script a conflict, a change landing
first, or the failure of the Nth write; WritableProviderContractTests holds any
writable provider to the contract.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 2a: `FileSystemBundleProvider` writes and deletes — resolution for write, the temporary, the guarded commit

**Files:**
- Modify: `src/OKF4net/Providers/FileSystemBundleProvider.cs` (class summary, `Capabilities`, `Write`, `Delete` and their helpers)
- Modify: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs` (remove the lot A test that asserted the provider refuses writes)
- Test: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteTests.cs`, `tests/OKF4net.Tests/Providers/FileSystemBundleProviderContractTests.cs`
- Modify: `CHANGELOG.md`, `README.md`

**Interfaces:**
- Consumes: Task 1 (`WritableProviderContractTests`, `BundleWriteConflictException(key, reason)`, `BundleWriteConflictReason`, `BundleWriteOutcomeUnknownException`); lot A's private members of `FileSystemBundleProvider` — `_fullRoot`, `PathComparison`, `_directoryListings`, `CheckBoundary()`, `GuardResolved(BundleKey, string)`, `GuardStrict(BundleKey?, string)`, `CannotBeLiteralName(string)`, `Enumerate`; `OKF4net.Internal.ReparsePoints` (`IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor(root, path, comparison)`); `TempDir` (`Write`, `TryCreateJunctionToExternalDir`, `TryCreateFileSymlinkToExternalFile`).
- Produces:
  - `FileSystemBundleProvider.Capabilities` = `new(CanWrite: true)`; `Write` and `Delete` implement the Task 1 contract (B6, B18–B20, B22, B23, B26, B27, B42: only `IfMatch` reads the stored object). Until Task 2b a failure of the commit call itself is always an unknown outcome (the temporary is kept).
  - `internal const string TemporaryPrefix = ".okf-write-"`, `internal const string TemporarySuffix = ".tmp"`, `internal static bool IsTemporaryName(string name)`, `internal IReadOnlyList<string> ListOrphanedTemporaries(BundleLimits limits)`, `internal Action<string>? TemporaryCreatedForTest { get; set; }` (called with the temporary's path right after it is created, before any byte is written; `null` outside tests).
  - Guards the writer relies on (Task 4a): before anything is listed, a key segment no entry can carry literally is `NameUnavailable` and an entry on the path that is a link or cannot be inspected is `BundleAccessRefusedException` — never a raw `UnauthorizedAccessException` naming a host path; after any creation the whole chain **and the object's own entry** are STRICT-checked again; writers creating objects in one new directory at the same moment all succeed.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The filesystem provider's write side: resolution for write, the temporary, the commit, and the guards around them (spec §4, §8; issue #157).</summary>
public class FileSystemBundleProviderWriteTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(B(s)));

    /// <summary>Every entry under <paramref name="root"/>, relative, '/'-separated, sorted: what is really on disk, temporaries included.</summary>
    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void A_write_creates_the_object_and_its_missing_parents_and_leaves_nothing_else()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var v = p.Write(K("a/b/c.md"), B("deep"), BundleWriteCondition.MustNotExist);
        Assert.Equal(Sha("deep"), v.Token);
        Assert.Equal("deep", File.ReadAllText(Path.Combine(tmp.Path, "a", "b", "c.md")));
        Assert.Equal(["a", "a/b", "a/b/c.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_write_replaces_an_object_another_process_wrote()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(Sha("new"), p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))).Token);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void Conditions_are_checked_against_the_stored_bytes_and_a_refusal_leaves_nothing_behind()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.MustNotExist));
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("other")))));
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("y.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// On a volume that folds case (NTFS, APFS by default, a vfat or CIFS mount under Linux) the
    /// OS would open or create the stored twin for a name that differs only in case: the write is
    /// refused instead, with nothing created. On a volume that keeps both (ext4), both names are
    /// distinct objects and both writes succeed — the provider follows the volume, not the OS.
    /// </summary>
    [Fact]
    public void A_name_the_volume_would_confuse_with_a_stored_one_is_refused_and_creates_nothing()
    {
        using var tmp = new TempDir();
        tmp.Write("Twin/x.md", "x");
        var folds = Directory.Exists(Path.Combine(tmp.Path, "twin"));
        var p = new FileSystemBundleProvider(tmp.Path);
        if (folds)
        {
            var dir = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("twin/y.md"), B("y"), BundleWriteCondition.None));
            Assert.Equal(BundleWriteConflictReason.NameUnavailable, dir.Reason);
            var leaf = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("Twin/X.md"), B("X"), BundleWriteCondition.None));
            Assert.Equal(BundleWriteConflictReason.NameUnavailable, leaf.Reason);
            Assert.Equal(["Twin", "Twin/x.md"], Tree(tmp.Path));
            Assert.Equal("x", File.ReadAllText(Path.Combine(tmp.Path, "Twin", "x.md")));
        }
        else
        {
            p.Write(K("twin/y.md"), B("y"), BundleWriteCondition.None);
            p.Write(K("Twin/X.md"), B("X"), BundleWriteCondition.None);
            Assert.Equal(["Twin", "Twin/X.md", "Twin/x.md", "twin", "twin/y.md"], Tree(tmp.Path));
        }
    }

    [SkippableTheory]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("d./x.md")]
    [InlineData("a:b.md")]
    [InlineData("C:x.md")]
    public void On_Windows_a_name_no_entry_can_carry_literally_is_refused_before_any_effect(string key)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a Windows name rule: elsewhere these names are stored as written");
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K(key), B("x"), BundleWriteCondition.None));
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, ex.Reason);
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void A_file_where_a_directory_is_needed_and_a_directory_under_the_key_are_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        tmp.Write("d/x.md", "x");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("a.md/b.md"), B("b"), BundleWriteCondition.None)).Reason);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("d"), B("d"), BundleWriteCondition.None)).Reason);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("empty"), B("e"), BundleWriteCondition.None)).Reason);
        Assert.Equal(["a.md", "d", "d/x.md", "empty"], Tree(tmp.Path));
    }

    [SkippableFact]
    public void A_junction_on_the_path_is_refused_and_nothing_reaches_its_target()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("j/x.md"), B("x"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("j/new/x.md"), B("x"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("j/x.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(external.Path));
    }

    [SkippableFact]
    public void A_symlinked_object_is_refused_and_its_target_is_untouched()
    {
        using var external = new TempDir();
        var outside = external.Write("secret.md", "outside");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile("link.md", outside), "file symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("link.md"), B("pwned"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("link.md"), BundleWriteCondition.None));
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "link.md")));
    }

    [SkippableFact]
    public void The_confinement_boundary_is_re_checked_before_a_write()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("src/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);
        p.Write(K("a.md"), B("still inside"), BundleWriteCondition.None);

        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("a.md"), B("escaped"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(external.Path));
    }

    /// <summary>
    /// File.WriteAllText refused to overwrite a file the process may not write. A rename needs
    /// only the directory's permission on Unix, so the provider opens the target for writing
    /// first and refuses as before: a read-only object stays read-only.
    /// </summary>
    [SkippableFact]
    public void A_target_the_process_may_not_write_is_refused_as_before_and_left_as_it_was()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("ro.md", "old");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var privileged = true;
            try
            {
                using (File.OpenHandle(file, FileMode.Open, FileAccess.Write))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                privileged = false;
            }

            Skip.If(privileged, "the process writes read-only files anyway (root)");
        }

        try
        {
            var p = new FileSystemBundleProvider(tmp.Path);
            Assert.Throws<UnauthorizedAccessException>(() => p.Write(K("ro.md"), B("new"), BundleWriteCondition.None));
            Assert.Equal("old", File.ReadAllText(file));
            Assert.Equal(["ro.md"], Tree(tmp.Path));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    /// <summary>
    /// File.WriteAllText needed only the right to write: a file the process may write but not
    /// read (mode 0200 on Unix, an ACE denying ReadData on Windows) was overwritten. An
    /// unconditional write still replaces it — only IfMatch reads the target, to compare its
    /// SHA-256, and that write is refused with the access error, since the version cannot be
    /// established — and the temporary of the refused write is removed.
    /// </summary>
    [SkippableFact]
    public void A_target_the_process_may_write_but_not_read_is_replaced_unconditionally_and_refused_under_IfMatch()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("wo.md", "old");
        SetReadable(file, false);
        try
        {
            var readable = true;
            try
            {
                using (File.OpenHandle(file, FileMode.Open, FileAccess.Read))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }

            Skip.If(readable, "the process reads unreadable files anyway (root)");
            var p = new FileSystemBundleProvider(tmp.Path);
            Assert.Throws<UnauthorizedAccessException>(() => p.Write(K("wo.md"), B("conditional"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))));
            Assert.Equal(["wo.md"], Tree(tmp.Path));
            Assert.Equal(Sha("new"), p.Write(K("wo.md"), B("new"), BundleWriteCondition.None).Token);
            Assert.Equal(["wo.md"], Tree(tmp.Path));
        }
        finally
        {
            SetReadable(file, true);
        }

        Assert.Equal("new", File.ReadAllText(file));
    }

    /// <summary>Denies (or gives back) the current user the right to read <paramref name="file"/>: mode 0200 or 0600 on Unix, an explicit ReadData deny ACE on Windows.</summary>
    private static void SetReadable(string file, bool readable)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            if (readable)
            {
                security.RemoveAccessRule(deny);
            }
            else
            {
                security.AddAccessRule(deny);
            }

            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(file, readable ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The temporary that holds a replacement's bytes is created owner-only (0600) before any
    /// byte is written, so a 0600 object's next content is never readable through a looser
    /// mode; the committed object keeps the mode the target had. A new object's temporary gets
    /// the mode a new file gets in that directory (umask, default ACL), which is exactly what the
    /// committed object has and what <c>File.WriteAllText</c> gave it before.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void On_Unix_a_replacement_is_prepared_owner_only_and_keeps_the_target_mode()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX modes");
        using var tmp = new TempDir();
        var secret = tmp.Write("secret.md", "old");
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var shared = tmp.Write("shared.md", "old");
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var reference = tmp.Write("reference.txt", "a new file's mode here");
        var p = new FileSystemBundleProvider(tmp.Path);
        var seen = new List<UnixFileMode>();
        p.TemporaryCreatedForTest = temporary => seen.Add(File.GetUnixFileMode(temporary));

        p.Write(K("secret.md"), B("new"), BundleWriteCondition.None);
        p.Write(K("shared.md"), B("new"), BundleWriteCondition.None);
        p.Write(K("fresh.md"), B("new"), BundleWriteCondition.MustNotExist);

        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal([ownerOnly, ownerOnly, File.GetUnixFileMode(reference)], seen);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(secret));
        Assert.Equal(ownerOnly | UnixFileMode.GroupRead, File.GetUnixFileMode(shared));
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(Path.Combine(tmp.Path, "fresh.md")));
    }

    [Fact]
    public void The_temporary_lives_beside_the_target_under_a_reserved_name_while_it_is_written()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        string? temporary = null;
        p.TemporaryCreatedForTest = t => temporary = t;
        p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None);
        Assert.NotNull(temporary);
        Assert.Equal(Path.Combine(tmp.Path, "d"), Path.GetDirectoryName(temporary));
        Assert.Matches(@"^\.okf-write-[0-9a-f]{32}\.tmp$", Path.GetFileName(temporary));
        Assert.True(FileSystemBundleProvider.IsTemporaryName(Path.GetFileName(temporary!)));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void The_provider_can_write_and_its_read_only_view_still_refuses_without_naming_the_root()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.Capabilities.CanWrite);
        var ro = p.AsReadOnly();
        var ex = Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("x"), BundleWriteCondition.None));
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(p.Id, ex.ProviderId);
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void A_delete_removes_the_object_and_leaves_its_empty_directories()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        p.Write(K("a/b/c.md"), B("c"), BundleWriteCondition.None);
        p.Delete(K("a/b/c.md"), BundleWriteCondition.None);
        Assert.Equal(["a", "a/b"], Tree(tmp.Path));
        Assert.Empty(p.Enumerate(null, BundleLimits.Default));
        Assert.Empty(Bundle.Load(p).Concepts);
    }

    [Fact]
    public void A_delete_never_touches_a_directory_under_the_key()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.Delete(K("d"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => p.Delete(K("d"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("x")))));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// Writers creating objects in one directory that does not exist yet, at the same moment:
    /// each lists the parent, finds no entry, and may then find the directory another writer just
    /// created. That is a name created meanwhile, not a name the volume folds, so every write
    /// succeeds. Two hundred rounds of four writers, because the window is a few microseconds.
    /// </summary>
    [Fact]
    public void Writers_creating_one_new_directory_at_the_same_moment_all_succeed()
    {
        using var tmp = new TempDir();
        var providers = Enumerable.Range(0, 4).Select(_ => new FileSystemBundleProvider(tmp.Path)).ToArray();
        for (var round = 0; round < 200; round++)
        {
            using var start = new Barrier(providers.Length);
            var failures = new Exception?[providers.Length];
            var threads = providers.Select((p, i) => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    p.Write(K($"r{round}/w{i}.md"), B("x"), BundleWriteCondition.MustNotExist);
                }
                catch (Exception e)
                {
                    failures[i] = e;
                }
            })).ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            Assert.All(failures, f => Assert.Null(f));
        }
    }
}
```

`tests/OKF4net.Tests/Providers/FileSystemBundleProviderContractTests.cs` — Task 1's contract over a fresh directory, a second instance over the same root standing in for another client:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The write contract on the filesystem provider: the same tests the in-memory provider passes, over a fresh directory.</summary>
public sealed class FileSystemBundleProviderContractTests : WritableProviderContractTests, IDisposable
{
    private readonly TempDir _tmp = new();

    protected override IBundleProvider CreateEmptyProvider() => new FileSystemBundleProvider(_tmp.Path);

    /// <summary>A second instance over the same root: the commit lock must hold across instances, not per instance.</summary>
    protected override IBundleProvider OpenSameStorage() => new FileSystemBundleProvider(_tmp.Path);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Versions_are_the_lowercase_hex_sha256_of_the_bytes_like_the_in_memory_provider()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(B("one"))), v.Token);
    }

    [Fact]
    public void No_temporary_is_left_behind_by_the_whole_contract_run()
    {
        Concurrent_IfMatch_writers_on_one_version_have_exactly_one_winner();
        Assert.Empty(((FileSystemBundleProvider)Provider).ListOrphanedTemporaries(BundleLimits.Default));
    }
}
```

In `tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs`, delete the lot A test that asserted the opposite (its replacement is `The_provider_can_write_and_its_read_only_view_still_refuses_without_naming_the_root` above), with the blank line that follows it:

```csharp
    [Fact]
    public void Writes_are_refused_in_this_lot_and_the_message_carries_no_host_path()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.False(p.Capabilities.CanWrite);
        var ex = Assert.Throws<BundleReadOnlyException>(() => p.Write(K("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(p.Id, ex.ProviderId);
        Assert.Throws<BundleReadOnlyException>(() => p.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "a.md")));
    }
```

`unix2dos -q` the two new files.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only — `CS0117` for `IsTemporaryName`, `CS1061` for `ListOrphanedTemporaries` and `TemporaryCreatedForTest`.

- [ ] **Step 3: Implement the write side**

In `src/OKF4net/Providers/FileSystemBundleProvider.cs` (all three edits with `Edit`):

(a) The first two lines of the class summary,

```csharp
/// The default provider (spec §8): a bundle as a directory tree. Read-only in this
/// lot (<see cref="Capabilities"/> says so; lot B adds the atomic write).
```

become

```csharp
/// The default provider (spec §8): a bundle as a directory tree, writable
/// (<see cref="Write"/> commits each object atomically, issue #157).
```

(b) `public BundleProviderCapabilities Capabilities => new(CanWrite: false);` becomes `public BundleProviderCapabilities Capabilities => new(CanWrite: true);`.

(c) Replace the two throwing members

```csharp
    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);
```

with the following block. What it does, in order: `Write` resolves the parent directories for write (`ResolveParentForWrite`: every segment checked storable literally and the whole path, as the OS would follow it, STRICT-checked before anything is listed — so an entry that cannot be inspected, under a parent that denies listing, is refused as such rather than surfacing as the raw access-denied error of the listing; then exact names, a STRICT inspection of each traversed directory before it is listed, each missing directory created and listed again, and the whole chain plus the object's own entry STRICT-checked after any creation), refuses what the volume would confuse (`FindExactForWrite`, B19 — a name the exact listing misses but the OS finds is listed once more before being called folded, since another writer may just have created exactly that name), guards an existing object like a read's leaf and refuses a directory there; writes the temporary (`WriteTemporary`: `CreateNew`, `FileShare.None`, 0600 then the target's mode by handle for a replacement on Unix, `Flush(true)`); then `CommitTemporary` takes the striped commit lock (B26), re-checks the boundary, the temporary and every directory up to the root (STRICT), the exact name; for an `IfMatch` write it opens an existing target for reading and writing to hash it, and for an unconditional one it opens it for writing only and reads nothing (B22, B42 — a file the process may write but not read is replaced as `File.WriteAllText` replaced it); it checks the condition, and commits. Any failure before the commit call removes the temporary through `DeleteTemporary`, which never removes it through a path a link has entered. `Delete` resolves without creating, and re-checks and deletes under the same lock.

```csharp
    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>The commit protocol</b> (issue #157). (1) The parent directories are resolved
    /// segment by segment, each existing name matched exactly and each traversed directory
    /// STRICT-inspected before it is listed; a missing directory is created, then listed again to
    /// prove the volume stored it under exactly that name; the whole chain from the root is
    /// STRICT-checked once more after any creation. (2) The bytes go to a temporary in the target's
    /// own directory, <c>.okf-write-</c> plus 32 random hex digits plus <c>.tmp</c>, created with
    /// <see cref="FileMode.CreateNew"/> (an entry already there, a planted link included, is never
    /// opened) and <see cref="FileShare.None"/>; on Unix a replacement's temporary is created
    /// owner-only (0600) before any byte is written and gets the target's mode through its handle
    /// before it is closed, while a new object's temporary gets the mode any new file gets in that
    /// directory. The bytes are flushed to stable storage and the handle closed. (3) Under an
    /// in-process lock per object, shared by every instance of this provider, the boundary, the
    /// chain and the temporary are STRICT-checked again, the target's exact name and current
    /// SHA-256 are read again, and the condition is checked against them. (4) The commit:
    /// <see cref="File.Replace(string, string, string?)"/> over an existing object,
    /// <see cref="File.Move(string, string, bool)"/> without overwrite for a new one.</para>
    /// <para><b>Names.</b> A name the volume would confuse with a stored entry — a directory, a
    /// sibling differing only in case or Unicode form on a volume that folds them, a Windows name
    /// with a trailing dot or space, a character no Windows name carries — is refused with
    /// <see cref="BundleWriteConflictReason.NameUnavailable"/> before anything is created.</para>
    /// <para><b>Permissions.</b> A target the process may not write is refused as
    /// <c>File.WriteAllText</c> refused it, although a rename would need only the directory's
    /// permission on Unix: the target is opened for writing before the commit. Only
    /// <see cref="BundleWriteConditionKind.IfMatch"/> reads the target (to compare its SHA-256),
    /// so an unconditional write replaces a file the process may write but not read, as
    /// <c>File.WriteAllText</c> did, while an <see cref="BundleWriteConditionKind.IfMatch"/> write
    /// of such a file is refused with <see cref="UnauthorizedAccessException"/>: its version
    /// cannot be established.</para>
    /// <para><b>Failures.</b> A failure before the commit removes the temporary and leaves the
    /// target untouched; it is the raw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, whose message may name a host path (the core
    /// translates it). A failure of the commit call itself raises
    /// <see cref="BundleWriteOutcomeUnknownException"/> and keeps the temporary.</para>
    /// </remarks>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        var written = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(content.Span)));
        for (var attempt = 1; ; attempt++)
        {
            CheckBoundary();
            var parent = ResolveParentForWrite(key);
            var existing = FindExactForWrite(key, parent, key.Name);
            if (existing is not null)
            {
                GuardObject(key, existing);
            }

            var temporary = WriteTemporary(parent, existing, content);
            if (CommitTemporary(key, parent, temporary, preparedAsReplacement: existing is not null, written, condition))
            {
                return written;
            }

            if (attempt == MaxPreparations)
            {
                throw new BundleWriteConflictException(key);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The object's directories are left in place, empty or not: an empty directory holds no key.
    /// A key that names a directory names no object, so nothing is deleted. The same exact-name
    /// resolution, STRICT guards, boundary re-check and per-object lock as <see cref="Write"/>.
    /// </remarks>
    public void Delete(BundleKey key, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var parent = ResolveParentForDelete(key);
        if (parent is null)
        {
            if (!Holds(condition, exists: false, null))
            {
                throw new BundleWriteConflictException(key);
            }

            return;
        }

        lock (CommitLockFor(Path.Join(parent, key.Name)))
        {
            CheckBoundary();
            GuardStrict(key, parent);
            var current = ListExact(parent, key.Name);
            if (current is not null)
            {
                GuardResolved(key, current);
                if (Directory.Exists(current))
                {
                    current = null; // a directory is no object
                }
            }

            // Only IfMatch reads the object: deleting it needs no right to read it.
            BundleObjectVersion? version = current is not null && condition.Kind == BundleWriteConditionKind.IfMatch ? HashForRead(current) : null;
            if (!Holds(condition, current is not null, version))
            {
                throw new BundleWriteConflictException(key);
            }

            if (current is null)
            {
                return;
            }

            File.Delete(current);
        }
    }

    /// <summary>The temporaries' name prefix (<see cref="Write"/>); the rest is 32 random lowercase hex digits and <see cref="TemporarySuffix"/>.</summary>
    internal const string TemporaryPrefix = ".okf-write-";

    /// <summary>The temporaries' name suffix.</summary>
    internal const string TemporarySuffix = ".tmp";

    /// <summary>How many times <see cref="Write"/> prepares a temporary before giving up on an object that keeps appearing under an unconditional creation (Unix only, see <see cref="CommitTemporary"/>).</summary>
    private const int MaxPreparations = 3;

    /// <summary>
    /// The in-process commit locks: the check-and-commit of one object runs under one of them,
    /// picked by the target's full path (case-insensitively, so that two spellings of one root on
    /// a folding volume share it), whatever provider instance runs it. Striped, so the set is
    /// bounded; two objects that share a stripe only wait for each other.
    /// </summary>
    private static readonly Lock[] CommitLocks = [.. Enumerable.Range(0, 64).Select(_ => new Lock())];

    /// <summary>Test hook: called with the temporary's full path as soon as it is created, before any byte is written. <see langword="null"/> outside tests.</summary>
    internal Action<string>? TemporaryCreatedForTest { get; set; }

    /// <summary>Whether <paramref name="name"/> (one segment) is a temporary this provider creates.</summary>
    internal static bool IsTemporaryName(string name) =>
        name.Length == TemporaryPrefix.Length + 32 + TemporarySuffix.Length
        && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
        && name.EndsWith(TemporarySuffix, StringComparison.Ordinal)
        && !name.AsSpan(TemporaryPrefix.Length, 32).ContainsAnyExcept("0123456789abcdef");

    /// <summary>
    /// Test seam (issue #157, orphaned temporaries): the raw names of the temporaries present in
    /// the tree — left by a write whose outcome was unknown, or by a failure that could no longer
    /// reach its temporary safely. Read through <see cref="Enumerate"/>, so it adds no walk.
    /// </summary>
    internal IReadOnlyList<string> ListOrphanedTemporaries(BundleLimits limits) =>
        [.. Enumerate(null, limits).Select(l => l.RawName).Where(n => IsTemporaryName(n[(n.LastIndexOf('/') + 1)..]))];

    private static Lock CommitLockFor(string path) =>
        CommitLocks[(StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue) % CommitLocks.Length];

    /// <summary>Whether <paramref name="condition"/> holds for an object that <paramref name="exists"/> with version <paramref name="current"/> (read for <see cref="BundleWriteConditionKind.IfMatch"/> only).</summary>
    private static bool Holds(BundleWriteCondition condition, bool exists, BundleObjectVersion? current) => condition.Kind switch
    {
        BundleWriteConditionKind.None => true,
        BundleWriteConditionKind.MustNotExist => !exists,
        BundleWriteConditionKind.IfMatch => exists && current is { } c && condition.Expected is { } e && c == e,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, "unknown write condition"),
    };

    /// <summary>
    /// Resolves the parent directory of <paramref name="key"/> for a write: each segment matched
    /// exactly (<see cref="FindExactForWrite"/>, never from a scope's memo), each traversed
    /// directory STRICT-inspected before it is listed, each missing one created and then listed
    /// again to prove the volume stored it under exactly that name; finally the whole chain from
    /// the root and the object's own entry are STRICT-checked (<see cref="GuardStrict"/>), after
    /// any creation. Before anything is listed, every segment must be storable literally and the
    /// path as the OS would follow it passes the same STRICT check.
    /// </summary>
    private string ResolveParentForWrite(BundleKey key)
    {
        foreach (var segment in key.Segments)
        {
            if (CannotBeStoredLiterally(segment))
            {
                throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
            }
        }

        // Before any listing, the whole path as the OS would follow it: an entry on it that is a
        // link or cannot be inspected is refused as such, rather than surfacing as the raw
        // access-denied error of listing a directory that denies it (the early guard the writer
        // ran before #140).
        GuardStrict(key, Path.Join(_fullRoot, string.Join(Path.DirectorySeparatorChar, key.Segments)));
        var current = _fullRoot;
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            GuardTraversed(key, current);
            var segment = key.Segments[i];
            var next = FindExactForWrite(key, current, segment);
            if (next is null)
            {
                next = Path.Join(current, segment);
                Directory.CreateDirectory(next);
                if (FindExactForWrite(key, current, segment) is null)
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
                }
            }
            else
            {
                GuardTraversed(key, next);
                if (!Directory.Exists(next))
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable); // a file where a directory is needed
                }
            }

            current = next;
        }

        // The chain again after any creation, and the object's own entry (the writer's late
        // guard before #140), immediately before the temporary is created.
        GuardStrict(key, Path.Join(current, key.Name));
        return current;
    }

    /// <summary>
    /// The parent directory of <paramref name="key"/> for a delete, matched exactly and guarded as
    /// for a write, or <see langword="null"/> when a segment is absent or is not a directory —
    /// then no object exists under the key. Creates nothing.
    /// </summary>
    private string? ResolveParentForDelete(BundleKey key)
    {
        var current = _fullRoot;
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            GuardTraversed(key, current);
            var next = ListExact(current, key.Segments[i]);
            if (next is null)
            {
                return null;
            }

            GuardTraversed(key, next);
            if (!Directory.Exists(next))
            {
                return null;
            }

            current = next;
        }

        GuardStrict(key, current);
        return current;
    }

    /// <summary>The STRICT inspection of one traversed directory (the root excepted, as everywhere in the per-key guards).</summary>
    private void GuardTraversed(BundleKey key, string directory)
    {
        if (!string.Equals(directory, _fullRoot, PathComparison) && ReparsePoints.IsReparsePointOrUninspectable(directory))
        {
            throw new BundleAccessRefusedException(key, "it resolves through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>An existing entry under the key: guarded like a read's leaf (<see cref="GuardResolved"/>), and a directory there is no object to replace.</summary>
    private void GuardObject(BundleKey key, string path)
    {
        GuardResolved(key, path);
        if (Directory.Exists(path))
        {
            throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
        }
    }

    /// <summary>
    /// The entry of <paramref name="directory"/> named exactly <paramref name="segment"/>
    /// (ordinal), or <see langword="null"/>; refuses with
    /// <see cref="BundleWriteConflictReason.NameUnavailable"/> a segment no entry can carry
    /// literally (<see cref="CannotBeStoredLiterally"/>) and a segment the OS resolves to an entry
    /// stored under another name — the volume folds case or Unicode form, so writing it would
    /// open or create that other entry. Never from a scope's memo, and a listing that fails
    /// throws: a write refuses what it cannot see, where a read answers "no such entry".
    /// </summary>
    private string? FindExactForWrite(BundleKey key, string directory, string segment)
    {
        if (CannotBeStoredLiterally(segment))
        {
            throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
        }

        var match = ListExact(directory, segment);
        if (match is null)
        {
            var path = Path.Join(directory, segment);
            if (File.Exists(path) || Directory.Exists(path))
            {
                // Another writer may have created exactly this name between the listing and the
                // lookup (two writers creating one directory or one object): list once more before
                // concluding that the volume folds the name onto another entry.
                match = ListExact(directory, segment);
                if (match is null)
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
                }
            }
        }

        return match;
    }

    /// <summary>Lists <paramref name="directory"/> in full, ignoring any scope, for the entry named exactly <paramref name="segment"/>; a failing listing throws.</summary>
    private string? ListExact(string directory, string segment)
    {
        Interlocked.Increment(ref _directoryListings);
        foreach (var full in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(Path.GetFileName(full), segment, StringComparison.Ordinal))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>A segment no entry can be named literally: <see cref="CannotBeLiteralName"/>, and on Windows a trailing dot or space, which the OS strips (<c>x.</c> would be stored as <c>x</c>).</summary>
    private static bool CannotBeStoredLiterally(string segment) =>
        CannotBeLiteralName(segment)
        || (OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' ')));

    /// <summary>
    /// Creates the temporary beside the target, writes and flushes <paramref name="content"/>,
    /// closes it and returns its path; on any failure removes it (<see cref="DeleteTemporary"/>)
    /// and rethrows. See <see cref="Write"/> for the modes.
    /// </summary>
    private string WriteTemporary(string parent, string? existing, ReadOnlyMemory<byte> content)
    {
        var temporary = Path.Join(parent, TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + TemporarySuffix);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        UnixFileMode? targetMode = null;
        if (existing is not null && !OperatingSystem.IsWindows())
        {
            targetMode = File.GetUnixFileMode(existing);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(temporary, options);
        try
        {
            TemporaryCreatedForTest?.Invoke(temporary);
            stream.Write(content.Span);
            if (targetMode is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, mode);
            }

            stream.Flush(flushToDisk: true);
        }
        catch
        {
            stream.Dispose();
            DeleteTemporary(temporary);
            throw;
        }

        stream.Dispose();
        return temporary;
    }

    /// <summary>
    /// Re-checks and commits under the object's lock (see <see cref="Write"/>): <see langword="true"/>
    /// once committed; <see langword="false"/>, with the temporary removed, when on Unix the object
    /// appeared since an unconditional creation was prepared — its temporary has a new file's mode
    /// where the object has its own, so <see cref="Write"/> prepares again as a replacement.
    /// </summary>
    private bool CommitTemporary(BundleKey key, string parent, string temporary, bool preparedAsReplacement, BundleObjectVersion written, BundleWriteCondition condition)
    {
        var settled = false;
        try
        {
            var target = Path.Join(parent, key.Name);
            lock (CommitLockFor(target))
            {
                CheckBoundary();
                GuardStrict(key, temporary); // the temporary and every directory up to the root
                var current = FindExactForWrite(key, parent, key.Name);
                BundleObjectVersion? before = null;
                if (current is not null)
                {
                    GuardObject(key, current);
                    if (condition.Kind == BundleWriteConditionKind.IfMatch)
                    {
                        before = HashForWrite(current);
                    }
                }

                if (!Holds(condition, current is not null, before))
                {
                    throw new BundleWriteConflictException(key);
                }

                if (current is not null && condition.Kind == BundleWriteConditionKind.None)
                {
                    EnsureWritable(current);
                }

                if (current is not null && !preparedAsReplacement && !OperatingSystem.IsWindows())
                {
                    return false;
                }

                try
                {
                    if (current is not null)
                    {
                        File.Replace(temporary, current, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(temporary, target, overwrite: false);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    settled = true; // keep the temporary: what the commit did is not established
                    throw new BundleWriteOutcomeUnknownException(key, e);
                }

                settled = true;
                return true;
            }
        }
        finally
        {
            if (!settled)
            {
                DeleteTemporary(temporary);
            }
        }
    }

    /// <summary>
    /// Removes a temporary this provider created, only while nothing on its path has been replaced
    /// by a link: the boundary holds and neither the temporary nor any directory up to the root
    /// is a reparse point or uninspectable. Otherwise it is left where it is — removing it through
    /// a substituted link could remove another entry — and is an orphan. Never throws: it runs on
    /// a failure path whose own exception is the one to report.
    /// </summary>
    private void DeleteTemporary(string temporary)
    {
        try
        {
            CheckBoundary();
            if (ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, temporary, PathComparison))
            {
                return;
            }

            File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BundleAccessRefusedException)
        {
            // left in place: an orphan (ListOrphanedTemporaries)
        }
    }

    /// <summary>
    /// The current version of an object an <see cref="BundleWriteConditionKind.IfMatch"/> write is
    /// about to replace, read through a handle opened for writing too: a target the process may
    /// not write (a read-only file, a mode or an ACL that denies writing, another program's handle
    /// that denies write sharing) is refused here, before the commit, exactly as
    /// <c>File.WriteAllText</c> refused it — a rename would otherwise replace it, since on Unix it
    /// needs only the directory's permission. A target the process may write but not read cannot
    /// satisfy <see cref="BundleWriteConditionKind.IfMatch"/>: its version cannot be established,
    /// and the write is refused with the same <see cref="UnauthorizedAccessException"/>. Nothing
    /// is written through this handle.
    /// </summary>
    private static BundleObjectVersion HashForWrite(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>
    /// What an unconditional replacement checks of its target (<see cref="BundleWriteConditionKind.None"/>):
    /// the right to write it, and only that — the handle is opened for writing alone and nothing
    /// is read, so a file the process may write but not read (mode 0200, an ACL denying reading)
    /// is replaced, as <c>File.WriteAllText</c> replaced it. A target the process may not write
    /// is refused exactly as <see cref="HashForWrite"/> refuses it; any failure to open the handle
    /// refuses the write, so an answer that cannot be established never lets it through.
    /// Nothing is written through this handle.
    /// </summary>
    private static void EnsureWritable(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();

    /// <summary>The current version of an object, read without blocking any other reader, writer or replacement.</summary>
    private static BundleObjectVersion HashForRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderWriteTests|FullyQualifiedName~FileSystemBundleProviderContractTests"`
Expected on Windows without Developer Mode: `Passed!  - Failed:     0, Passed:    36, Skipped:     2, Total:    38` — the two skips are `On_Unix_a_replacement_is_prepared_owner_only_and_keeps_the_target_mode` (POSIX) and `A_symlinked_object_is_refused_and_its_target_is_untouched` (no file-symlink privilege; it runs with Developer Mode and on Linux). The junction tests run.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers"`
Expected: `Failed: 0`.

Run the Linux container command of the Global Constraints. Expected: the root pass `Failed: 0` with `A_target_the_process_may_not_write_is_refused_as_before_and_left_as_it_was` and the five `On_Windows_…` rows skipped; the unprivileged pass `Failed: 0` with only the five `On_Windows_…` rows skipped among the write tests — the read-only test and `A_target_the_process_may_write_but_not_read_is_replaced_unconditionally_and_refused_under_IfMatch` run there and pass (the root pass skips both: root reads and writes any file).

- [ ] **Step 5: CHANGELOG and README**

In `CHANGELOG.md`, in the lot A `### Added` bullet, `` `FileSystemBundleProvider` (read-only in this release), `` becomes `` `FileSystemBundleProvider` (writable, see Changed), ``. Under `### Changed`, after the last bullet of the `[Unreleased]` section (the "Known, temporary gap" bullet, which ends with "still reads."), add:

```markdown
- **`FileSystemBundleProvider` is writable** (`Capabilities.CanWrite` is
  `true`; `provider.AsReadOnly()` gives a read-only view). `Write` commits each
  object atomically (issue #157): the bytes go to a temporary
  `.okf-write-<32 hex>.tmp` beside the target, are flushed to stable storage,
  and replace the target with `File.Replace` (or appear with `File.Move`
  without overwrite) only after the boundary, the path and the condition
  (`None`, `MustNotExist`, `IfMatch` on the SHA-256 of the stored bytes) are
  checked again under a per-object lock. On Unix a replacement's temporary is
  owner-only (0600) until it takes the target's mode. A name the volume would
  confuse with a stored entry (a case or Unicode-form twin on a folding
  volume, a Windows trailing dot or space, a directory) is refused before
  anything is created; a read-only target is refused as before. `Delete`
  removes one object and leaves its directories in place.
```

In `README.md` ("Loading through a provider"), the sentence

```markdown
`ParseErrors` line); other rejected names are skipped silently. In this
release the filesystem provider is read-only and no other provider ships; the
write side and the first remote providers are tracked in #139.
```

becomes

```markdown
`ParseErrors` line); other rejected names are skipped silently. The
filesystem provider is writable (`Capabilities.CanWrite`); no other provider
ships yet, and the first remote providers are tracked in #139.
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/Providers/FileSystemBundleProvider.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteTests.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderContractTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
git add src/OKF4net/Providers/FileSystemBundleProvider.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteTests.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderContractTests.cs CHANGELOG.md README.md
git commit -F - <<'MSG'
feat(providers): the filesystem provider writes and deletes, through a temporary and an atomic commit

Write checks every segment storable and the whole path STRICT before listing
anything, resolves each parent segment exactly, STRICT-guards it before
listing, creates a missing directory and lists it again to prove the volume
kept the name (a name the OS finds but the listing misses is listed once more
before it is called folded), re-checks the chain and the object's own entry,
then writes a .okf-write-<hex>.tmp beside the target (CreateNew,
FileShare.None, owner-only on Unix for a replacement, the target's mode set
through the handle), flushes it to disk, and under a striped per-object lock
re-checks the boundary, the chain, the temporary and the condition against the
stored SHA-256 before File.Replace or File.Move without overwrite. A name the
volume would confuse with a stored entry, a Windows trailing dot or space, and
a directory under the key are refused as NameUnavailable; a target the process
may not write is refused before the commit. A failure before the commit
removes the temporary; a failure of the commit keeps it and raises
BundleWriteOutcomeUnknownException. Delete removes one object under the same
guards and lock and leaves directories in place. CanWrite is true.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 2b: A failed commit classified from the disk, the fault-injection hooks, and reads that never block a write

**Files:**
- Modify: `src/OKF4net/Providers/FileSystemBundleProvider.cs` (`Read`'s sharing and its bounded retry; `Write`'s remarks; six test hooks; `WriteTemporary`'s name; `CommitTemporary`'s commit attempts; `ClassifyCommitFailure`; `Delete`'s hook)
- Modify: `src/OKF4net/Providers/BundleProviderExceptions.cs` (`BundleWriteNotCommittedException`)
- Test: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderCommitTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 2a (`Write`, `Delete`, `CommitTemporary`, `WriteTemporary`, `DeleteTemporary`, `ListExact`, `HashForRead`, `TemporaryPrefix`, `TemporarySuffix`, `TemporaryCreatedForTest`, `ListOrphanedTemporaries`); `TempDir` (`Write`, `TryCreateJunctionToExternalDir`, `TryCreateFileSymlinkToExternalFile`).
- Produces (all `internal`, `null` by default, never set outside tests — a production caller cannot reach them, the assembly grants `InternalsVisibleTo` only to the repository's own projects):
  - `Func<string>? TemporaryNameForTest` — the temporary's file name instead of a random one.
  - `Action<string>? AfterTemporaryWrittenForTest` — called with the temporary's path once it is written, flushed and closed, before the commit lock is taken.
  - `Action<string?, string>? BeforeCommitForTest` — called under the commit lock after every re-check and the condition, just before the commit call, with the temporary (`null` for a delete) and the target path.
  - `Action<string, string, bool>? CommitForTest` — replaces the commit call `(temporary, target, replace)`; what it throws is classified like the real call's failure.
  - `Action<string>? ParentsResolvedForTest` — called by `Write` with the parent directory's full path once every parent directory exists (found, or created), before the STRICT re-check of the whole path and of the object's own entry, and before the temporary is created.
  - `Action<string>? ReadOpeningForTest` — called by `Read` with the object's full path on every attempt, once every check has passed and right before the object is opened; what it throws is what the open threw.
  - `public sealed class BundleWriteNotCommittedException(BundleKey key, Exception inner) : BundleProviderException` with `BundleKey Key` — a replacement the storage refused without effect on every attempt (B43); its message is built from the key and the raw exception's type name only.
  - Behaviour: B21's classification, the guards renewed first (B44), an unconditional replacement's target never read (B42); a replacement refused without effect made again, at most 3 attempts (B43); `Read` opens with `FileShare.ReadWrite | FileShare.Delete` (B8) and, on Windows only, retries the `ReplaceFile` window, at most 3 attempts (B45).

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/Providers/FileSystemBundleProviderCommitTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The filesystem commit under faults (issue #157): what a failure before, during and after
/// the commit leaves behind, the guards re-run at the moment of the commit, and readers that
/// must not block a replacement (B8). Faults are injected through the provider's internal test
/// hooks, which are <see langword="null"/> outside tests; Windows' <c>ReplaceFile</c> error 1176
/// cannot be provoked on demand, so its classification is driven by a commit hook that leaves
/// the disk exactly as that error does.
/// </summary>
public class FileSystemBundleProviderCommitTests
{
    private const int ErrorUnableToMoveReplacement = unchecked((int)0x80070498); // HRESULT of Win32 1176

    private const int ErrorUnableToRemoveReplaced = unchecked((int)0x80070497); // HRESULT of Win32 1175

    private const int ErrorSharingViolation = unchecked((int)0x80070020); // HRESULT of Win32 32

    private const int ErrorLockViolation = unchecked((int)0x80070021); // HRESULT of Win32 33

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private static string ReadVia(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public void A_failure_between_the_temporary_and_the_commit_leaves_the_target_byte_identical_and_no_temporary()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var before = File.ReadAllBytes(file);
        var p = new FileSystemBundleProvider(tmp.Path);
        var injected = new IOException("injected after the temporary was written");
        p.AfterTemporaryWrittenForTest = _ => throw injected;
        Assert.Same(injected, Assert.Throws<IOException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void Readers_see_the_whole_old_object_until_the_commit_and_the_whole_new_one_after()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var reader = new FileSystemBundleProvider(tmp.Path);
        var seen = new List<string>();
        p.TemporaryCreatedForTest = _ => seen.Add(ReadVia(reader, "x.md"));
        p.AfterTemporaryWrittenForTest = t =>
        {
            seen.Add(ReadVia(reader, "x.md"));
            Assert.Equal("new", File.ReadAllText(t)); // the whole new content, under the temporary's name only
        };
        p.BeforeCommitForTest = (_, _) => seen.Add(ReadVia(reader, "x.md"));
        p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        seen.Add(ReadVia(reader, "x.md"));
        Assert.Equal(["old", "old", "old", "new"], seen);
    }

    /// <summary>The per-object lock covers the check and the commit (issue #157): a second writer of the same object, through another instance, waits for the first commit, then finds the version it expected gone.</summary>
    [Fact]
    public void The_commit_lock_covers_the_check_and_the_commit_across_instances()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var v0 = p.Write(K("x.md"), B("v0"), BundleWriteCondition.None);
        var other = new FileSystemBundleProvider(tmp.Path);
        Task? second = null;
        p.BeforeCommitForTest = (_, _) =>
        {
            second = Task.Run(() => other.Write(K("x.md"), B("second"), BundleWriteCondition.IfMatch(v0)));
            Assert.False(second.Wait(TimeSpan.FromMilliseconds(200)), "the second writer passed the lock the first one holds");
        };
        p.Write(K("x.md"), B("first"), BundleWriteCondition.IfMatch(v0));
        var thrown = Assert.Throws<AggregateException>(() => second!.Wait());
        Assert.IsType<BundleWriteConflictException>(thrown.InnerException);
        Assert.Equal("first", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_commit_that_fails_without_effect_is_the_raw_failure_and_leaves_nothing_behind()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("a device attached to the system is not functioning (simulated)", unchecked((int)0x8007001F));
        p.CommitForTest = (_, _, _) => throw failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// <c>ReplaceFile</c> can refuse a replacement while readers hold the object open, with error
    /// 1175 (<c>ERROR_UNABLE_TO_REMOVE_REPLACED</c>: both files keep their names) — measured
    /// twice in five runs of probe A3 by the external review — or with a sharing violation. Both
    /// change nothing, so the commit is made again, from the re-check.
    /// </summary>
    [Theory]
    [InlineData(ErrorUnableToRemoveReplaced)]
    [InlineData(ErrorSharingViolation)]
    public void A_replacement_the_storage_refused_without_effect_once_is_made_again_and_succeeds(int hresult)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var calls = 0;
        p.CommitForTest = (temporary, target, replace) =>
        {
            Assert.True(replace);
            if (++calls == 1)
            {
                throw new IOException("simulated", hresult);
            }

            File.Replace(temporary, target, null);
        };
        var v = p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        Assert.Equal(2, calls);
        using var obj = p.Read(K("x.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Theory]
    [InlineData(ErrorUnableToRemoveReplaced)]
    [InlineData(ErrorSharingViolation)]
    public void A_replacement_refused_on_every_attempt_is_not_committed_the_temporary_removed_and_the_target_untouched(int hresult)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var before = File.ReadAllBytes(file);
        var p = new FileSystemBundleProvider(tmp.Path);
        var calls = 0;
        var failure = new IOException("simulated", hresult);
        p.CommitForTest = (_, _, _) =>
        {
            calls++;
            throw failure;
        };
        var ex = Assert.Throws<BundleWriteNotCommittedException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Assert.Equal(3, calls);
        Assert.Same(failure, ex.InnerException);
        Assert.Equal(K("d/x.md"), ex.Key);
        Assert.Equal("the write of 'd/x.md' was not committed: the storage kept refusing it (IOException); the object is unchanged", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    /// <summary>A retry starts from the re-check of the condition against the bytes stored now: another writer landing between two attempts is a conflict, never overwritten.</summary>
    [Fact]
    public void A_condition_another_writer_breaks_between_two_commit_attempts_is_a_conflict_not_an_overwrite()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var current = p.Read(K("x.md"));
        var calls = 0;
        p.CommitForTest = (_, target, _) =>
        {
            calls++;
            File.WriteAllText(target, "other writer");
            throw new IOException("simulated", ErrorUnableToRemoveReplaced);
        };
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(current.Version)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal(1, calls);
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_commit_that_reports_a_failure_after_taking_effect_is_a_success()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.CommitForTest = (temporary, target, replace) =>
        {
            Assert.True(replace);
            File.Replace(temporary, target, null);
            throw new IOException("simulated metadata error after the rename");
        };
        var v = p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        using var obj = p.Read(K("x.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// <c>ReplaceFile</c> without a backup name can fail with error 1176 after removing the
    /// target and before moving the replacement in: no object under the key, the new bytes under
    /// the temporary's name. The outcome is unknown to the caller; the temporary — the only copy
    /// of the new content — is kept, and is what <see cref="FileSystemBundleProvider.ListOrphanedTemporaries"/> reports.
    /// </summary>
    [Fact]
    public void A_commit_failing_like_ReplaceFile_1176_is_an_unknown_outcome_that_keeps_the_temporary()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("Unable to move the replacement file to the file to be replaced (simulated)", ErrorUnableToMoveReplacement);
        p.CommitForTest = (_, target, _) =>
        {
            File.Delete(target);
            throw failure;
        };
        var ex = Assert.Throws<BundleWriteOutcomeUnknownException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Assert.Same(failure, ex.InnerException);
        Assert.Equal(K("d/x.md"), ex.Key);
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.False(File.Exists(file));
        var orphan = Assert.Single(p.ListOrphanedTemporaries(BundleLimits.Default));
        Assert.StartsWith("d/.okf-write-", orphan, StringComparison.Ordinal);
        Assert.Equal("new", File.ReadAllText(Path.Combine(tmp.Path, orphan)));
    }

    [Fact]
    public void A_commit_failure_whose_effect_cannot_be_read_back_is_an_unknown_outcome()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.CommitForTest = (temporary, target, _) =>
        {
            File.Delete(temporary);
            File.WriteAllText(target, "someone else");
            throw new IOException("simulated");
        };
        Assert.Throws<BundleWriteOutcomeUnknownException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None));
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
    }

    [Fact]
    public void Another_writer_landing_while_the_commit_fails_is_a_conflict_and_the_temporary_is_removed()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var current = p.Read(K("x.md"));
        p.CommitForTest = (_, target, _) =>
        {
            File.WriteAllText(target, "other writer");
            throw new IOException("simulated");
        };
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(current.Version)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// An unconditional replacement never read its target (it needs no right to read it, F5), so
    /// a commit that failed with its temporary still in place is that write's raw failure,
    /// whatever the target holds now: nothing of the write happened, and the temporary is removed.
    /// </summary>
    [Fact]
    public void An_unconditional_replacement_whose_commit_fails_is_the_raw_failure_whatever_the_target_holds()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("simulated");
        p.CommitForTest = (_, target, _) =>
        {
            File.WriteAllText(target, "other writer");
            throw failure;
        };
        Assert.Same(failure, Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void An_object_that_appears_before_a_conditional_creation_commits_is_a_conflict()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        p.AfterTemporaryWrittenForTest = _ => File.WriteAllText(Path.Combine(tmp.Path, "x.md"), "created elsewhere");
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("mine"), BundleWriteCondition.MustNotExist));
        Assert.Equal("created elsewhere", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// An unconditional creation that finds the object created meanwhile replaces it; on Unix it
    /// first prepares again, so the replacement takes the mode of the object it replaces (here
    /// owner-only) rather than the mode a new file gets.
    /// </summary>
    [Fact]
    public void An_object_that_appears_before_an_unconditional_creation_commits_is_replaced_keeping_its_mode()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "x.md");
        var p = new FileSystemBundleProvider(tmp.Path);
        var preparations = 0;
        p.AfterTemporaryWrittenForTest = _ =>
        {
            if (++preparations == 1)
            {
                File.WriteAllText(file, "created elsewhere");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        };
        p.Write(K("x.md"), B("mine"), BundleWriteCondition.None);
        Assert.Equal("mine", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(2, preparations);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        else
        {
            Assert.Equal(1, preparations);
        }
    }

    /// <summary>
    /// Another process swaps the target's directory, or one of its ancestors, for a junction
    /// to an outside directory after the temporary is written: the STRICT re-check at the commit
    /// refuses, nothing reaches the junction's target, and the temporary — now reachable only
    /// through the junction — is left where it is rather than removed through the link.
    /// </summary>
    [SkippableTheory]
    [InlineData("a/b")]
    [InlineData("a")]
    public void A_directory_swapped_for_a_junction_before_the_commit_is_refused_and_nothing_reaches_its_target(string swapped)
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("a/b/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var created = false;
        p.AfterTemporaryWrittenForTest = _ =>
        {
            var dir = Path.Combine(tmp.Path, swapped.Replace('/', Path.DirectorySeparatorChar));
            Directory.Move(dir, dir + "-moved");
            created = tmp.TryCreateJunctionToExternalDir(swapped.Replace('/', Path.DirectorySeparatorChar), external.Path);
        };
        var thrown = Record.Exception(() => p.Write(K("a/b/x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(created, "junctions unavailable");
        Assert.IsType<BundleAccessRefusedException>(thrown);
        Assert.Empty(Tree(external.Path));
        var moved = swapped == "a" ? Path.Combine(tmp.Path, "a-moved", "b", "x.md") : Path.Combine(tmp.Path, "a", "b-moved", "x.md");
        Assert.Equal("old", File.ReadAllText(moved));
    }

    /// <summary>
    /// The classification of a failed commit renews the boundary and the STRICT guards before it
    /// reads anything back (external review, F4): an ancestor swapped for a link during the
    /// commit, behind which the new bytes happen to sit, is not read as a success that a public
    /// <see cref="FileSystemBundleProvider.Read"/> would then refuse. The outcome is unknown and
    /// the temporary, now reachable only through the moved directory, is kept.
    /// </summary>
    [SkippableFact]
    public void A_failed_commit_is_not_read_back_through_an_ancestor_swapped_for_a_link()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "old");
        external.Write("x.md", "outside");
        var p = new FileSystemBundleProvider(tmp.Path, confinementBoundary: tmp.Path);
        var linked = false;
        p.CommitForTest = (_, _, _) =>
        {
            Directory.Move(Path.Combine(tmp.Path, "d"), Path.Combine(tmp.Path, "saved"));
            linked = tmp.TryCreateJunctionToExternalDir("d", external.Path);
            File.WriteAllText(Path.Combine(external.Path, "x.md"), "new");
            throw new IOException("simulated commit failure", unchecked((int)0x80070005));
        };
        var thrown = Record.Exception(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(linked, "junctions unavailable");
        Assert.IsType<BundleWriteOutcomeUnknownException>(thrown);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("d/x.md")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(tmp.Path, "saved", "x.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(tmp.Path, "saved"), FileSystemBundleProvider.TemporaryPrefix + "*"));
    }

    /// <summary>A link put in place of the temporary after it was written (a junction on Windows, which needs no privilege; a symlink elsewhere) is refused at the commit and is not followed.</summary>
    [SkippableFact]
    public void A_link_put_in_place_of_the_temporary_is_refused_at_the_commit()
    {
        using var external = new TempDir();
        var victim = external.Write("victim.md", "outside");
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var linked = false;
        p.AfterTemporaryWrittenForTest = temporary =>
        {
            File.Delete(temporary);
            var name = Path.GetFileName(temporary);
            linked = OperatingSystem.IsWindows()
                ? tmp.TryCreateJunctionToExternalDir(name, external.Path)
                : tmp.TryCreateFileSymlinkToExternalFile(name, victim);
        };
        var thrown = Record.Exception(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(linked, "links unavailable");
        Assert.IsType<BundleAccessRefusedException>(thrown);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal("outside", File.ReadAllText(victim));
    }

    /// <summary>The temporary is created with <see cref="FileMode.CreateNew"/>: an entry planted under its name beforehand — a link to a victim — is never opened, never written through and never removed.</summary>
    [SkippableFact]
    public void A_link_planted_under_the_temporarys_name_is_never_opened()
    {
        using var external = new TempDir();
        var victim = external.Write("victim.md", "outside");
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var name = FileSystemBundleProvider.TemporaryPrefix + new string('a', 32) + FileSystemBundleProvider.TemporarySuffix;
        var planted = OperatingSystem.IsWindows()
            ? tmp.TryCreateJunctionToExternalDir(name, external.Path)
            : tmp.TryCreateFileSymlinkToExternalFile(name, victim);
        Skip.IfNot(planted, "links unavailable");
        var p = new FileSystemBundleProvider(tmp.Path) { TemporaryNameForTest = () => name };
        var thrown = Record.Exception(() => p.Write(K("x.md"), B("pwned"), BundleWriteCondition.None));
        Assert.True(thrown is IOException or UnauthorizedAccessException, $"unexpected {thrown?.GetType().Name}");
        Assert.Equal("outside", File.ReadAllText(victim));
        Assert.Equal(["victim.md"], Tree(external.Path));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.True(Path.Exists(Path.Combine(tmp.Path, name)));
    }

    /// <summary>
    /// B8: a reader that opened the object the way <see cref="FileSystemBundleProvider.Read"/>
    /// does (<see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>) never blocks a
    /// replacement or a delete, and keeps reading the bytes it opened. A reader that denies
    /// deletion (<see cref="FileShare.Read"/>) blocks both on Windows — the failure is the raw
    /// sharing violation, and nothing changed — and blocks nothing elsewhere.
    /// </summary>
    [Theory]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(FileShare.Read)]
    public void A_reader_holding_the_object_open_blocks_a_replacement_only_on_Windows_and_only_when_it_denies_deletion(FileShare readerShare)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        tmp.Write("y.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, readerShare))
        using (var other = new FileStream(Path.Combine(tmp.Path, "y.md"), FileMode.Open, FileAccess.Read, readerShare))
        {
            var blocks = OperatingSystem.IsWindows() && !readerShare.HasFlag(FileShare.Delete);
            if (blocks)
            {
                Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)).HResult);
                Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(() => p.Delete(K("y.md"), BundleWriteCondition.None)).HResult);
                Assert.Equal("old", File.ReadAllText(file));
                Assert.Equal(["x.md", "y.md"], Tree(tmp.Path));
            }
            else
            {
                p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
                p.Delete(K("y.md"), BundleWriteCondition.None);
                Assert.Equal("new", File.ReadAllText(file));
                Assert.Equal(["x.md"], Tree(tmp.Path));
            }

            var buffer = new byte[3];
            reader.ReadExactly(buffer);
            Assert.Equal("old", Encoding.UTF8.GetString(buffer));
        }
    }

    /// <summary>
    /// B8 through the provider itself: readers looping over <see cref="FileSystemBundleProvider.Read"/>
    /// while a writer replaces the object a hundred times never keep one of those replacements from
    /// landing, and never see anything but a whole old or a whole new object. Before <c>Read</c> shared deletion, a replacement
    /// on Windows failed with a sharing violation whenever it met an open read; a replacement
    /// <c>ReplaceFile</c> refuses without effect (1175) is made again. On Windows a reader can
    /// still, for a moment, find no object under the name or meet a sharing violation while
    /// <c>ReplaceFile</c> runs — after its retries, far less often (B17, probe A3); on Unix a
    /// rename leaves no such moment. Never a torn object, anywhere.
    /// </summary>
    [Fact]
    public void Concurrent_reads_never_block_a_replacement_and_never_see_a_torn_object()
    {
        using var tmp = new TempDir();
        var a = new string('a', 256 * 1024);
        var b = new string('b', 256 * 1024);
        tmp.Write("x.md", a);
        var p = new FileSystemBundleProvider(tmp.Path);
        var stop = false;
        var readerErrors = new List<string>();
        var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            var reader = new FileSystemBundleProvider(tmp.Path);
            while (!Volatile.Read(ref stop))
            {
                string? problem;
                try
                {
                    var text = ReadVia(reader, "x.md");
                    problem = text == a || text == b ? null : $"torn read of {text.Length} chars";
                }
                catch (BundleObjectNotFoundException) when (OperatingSystem.IsWindows())
                {
                    problem = null; // the ReplaceFile window (B17)
                }
                catch (IOException e) when (OperatingSystem.IsWindows() && e.HResult == unchecked((int)0x80070020))
                {
                    problem = null; // the ReplaceFile window (B17)
                }
                catch (Exception e)
                {
                    problem = $"{e.GetType().Name}: {e.Message}";
                }

                if (problem is not null)
                {
                    lock (readerErrors)
                    {
                        readerErrors.Add(problem);
                    }
                }
            }
        })).ToArray();
        foreach (var t in readers)
        {
            t.Start();
        }

        try
        {
            for (var i = 0; i < 100; i++)
            {
                p.Write(K("x.md"), B(i % 2 == 0 ? b : a), BundleWriteCondition.None);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (var t in readers)
            {
                t.Join();
            }
        }

        Assert.Empty(readerErrors);
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// R1: on Windows a read retries the <c>ReplaceFile</c> window — a sharing or lock violation,
    /// or an entry the exact listing had just matched that the open no longer finds — and succeeds
    /// once it has passed. Elsewhere the read fails at once, as before.
    /// </summary>
    [Theory]
    [InlineData("sharing")]
    [InlineData("lock")]
    [InlineData("not found")]
    public void A_read_meeting_the_replace_window_once_is_retried_on_Windows_and_fails_as_before_elsewhere(string window)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = path =>
        {
            if (++opens == 1)
            {
                throw Window(window, path);
            }
        };
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("content", ReadVia(p, "x.md"));
            Assert.Equal(2, opens);
        }
        else
        {
            var thrown = Record.Exception(() => ReadVia(p, "x.md"));
            Assert.IsType(window == "not found" ? typeof(BundleObjectNotFoundException) : typeof(IOException), thrown);
            Assert.Equal(1, opens);
        }
    }

    [Theory]
    [InlineData("sharing")]
    [InlineData("not found")]
    public void A_read_meeting_the_replace_window_on_every_attempt_gives_up_after_three_on_Windows(string window)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = path =>
        {
            opens++;
            throw Window(window, path);
        };
        var thrown = Record.Exception(() => ReadVia(p, "x.md"));
        Assert.IsType(window == "not found" ? typeof(BundleObjectNotFoundException) : typeof(IOException), thrown);
        Assert.Equal(OperatingSystem.IsWindows() ? 3 : 1, opens);
    }

    /// <summary>An object really deleted is not found as soon as the listing no longer matches it: the retry lists again and never opens it again.</summary>
    [Fact]
    public void An_object_deleted_while_it_is_read_is_not_found_without_another_open()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = _ =>
        {
            opens++;
            File.Delete(file);
        };
        Assert.Throws<BundleObjectNotFoundException>(() => ReadVia(p, "x.md"));
        Assert.Equal(1, opens);
    }

    /// <summary>
    /// What the retry never covers, counted rather than timed: a key the listing does not match
    /// (validators probe many absent resources), a refusal and a version mismatch each cost one
    /// attempt — one listing of the root, at most one open.
    /// </summary>
    [SkippableFact]
    public void An_absent_key_a_refusal_and_a_version_mismatch_are_never_retried()
    {
        using var external = new TempDir();
        external.Write("x.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = _ => opens++;

        var listings = p.DirectoryListings;
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("absent.md")));
        Assert.Equal(1, p.DirectoryListings - listings);
        Assert.Equal(0, opens);

        Assert.Throws<BundleVersionMismatchException>(() => p.Read(K("x.md"), new BundleObjectVersion(new string('0', 64))));
        Assert.Equal(1, opens);

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "links unavailable");
        listings = p.DirectoryListings;
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("j/x.md")));
        Assert.Equal(1, p.DirectoryListings - listings);
        Assert.Equal(1, opens);
    }

    private static IOException Window(string window, string path) => window switch
    {
        "sharing" => new IOException("simulated sharing violation", ErrorSharingViolation),
        "lock" => new IOException("simulated lock violation", ErrorLockViolation),
        _ => new FileNotFoundException("simulated: the entry is gone for a moment", path),
    };
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only — `CS0117` for `TemporaryNameForTest` (object initializer), `CS0246` for `BundleWriteNotCommittedException`, `CS1061` for `AfterTemporaryWrittenForTest`, `BeforeCommitForTest`, `CommitForTest`, `ReadOpeningForTest`.

- [ ] **Step 3: Add the hooks and the classification**

All edits in `src/OKF4net/Providers/FileSystemBundleProvider.cs`, with `Edit`.

(a) Right after the `TemporaryCreatedForTest` property (`    internal Action<string>? TemporaryCreatedForTest { get; set; }`), insert:

```csharp

    /// <summary>
    /// Test hook: called by <see cref="Write"/> with the parent directory's full path once every
    /// parent directory of the key exists (found, or created), before the STRICT re-check of the
    /// whole path and before the temporary is created — another process acting in that window (a
    /// directory just created swapped for a junction, a link planted under the object's name).
    /// <c>BundleConceptWriter.BeforeLateReparseCheckForTest</c> forwards to it.
    /// <see langword="null"/> outside tests.
    /// </summary>
    internal Action<string>? ParentsResolvedForTest { get; set; }

    /// <summary>Test hook: the temporary's file name instead of a random one (to plant an entry under it beforehand). <see langword="null"/> outside tests.</summary>
    internal Func<string>? TemporaryNameForTest { get; set; }

    /// <summary>Test hook: called with the temporary's full path once it is written, flushed and closed, before the commit lock is taken — another process acting in that window. <see langword="null"/> outside tests.</summary>
    internal Action<string>? AfterTemporaryWrittenForTest { get; set; }

    /// <summary>Test hook: called under the commit lock once every re-check and the condition have passed, just before the commit call, with the temporary (<see langword="null"/> for a delete) and the target's full path. <see langword="null"/> outside tests.</summary>
    internal Action<string?, string>? BeforeCommitForTest { get; set; }

    /// <summary>
    /// Test hook: replaces the commit call itself — <c>(temporary, target, replace)</c> — so a test
    /// can leave the disk as a failing <c>ReplaceFile</c> or <c>rename</c> does and throw what it
    /// throws; the provider then classifies the failure exactly as for the real call.
    /// <see langword="null"/> outside tests.
    /// </summary>
    internal Action<string, string, bool>? CommitForTest { get; set; }

    /// <summary>Test hook: called by <see cref="Read"/> with the object's full path on every attempt, once every check has passed and right before the object is opened — what it throws is what the open threw. <see langword="null"/> outside tests.</summary>
    internal Action<string>? ReadOpeningForTest { get; set; }
```

(b) In `WriteTemporary`, the first line

```csharp
        var temporary = Path.Join(parent, TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + TemporarySuffix);
```

becomes

```csharp
        var name = TemporaryNameForTest?.Invoke() ?? TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + TemporarySuffix;
        var temporary = Path.Join(parent, name);
```

(c) Replace the whole of `CommitTemporary` — its signature line through its closing brace, i.e. exactly

```csharp
    private bool CommitTemporary(BundleKey key, string parent, string temporary, bool preparedAsReplacement, BundleObjectVersion written, BundleWriteCondition condition)
    {
        var settled = false;
        try
        {
            var target = Path.Join(parent, key.Name);
            lock (CommitLockFor(target))
            {
                CheckBoundary();
                GuardStrict(key, temporary); // the temporary and every directory up to the root
                var current = FindExactForWrite(key, parent, key.Name);
                BundleObjectVersion? before = null;
                if (current is not null)
                {
                    GuardObject(key, current);
                    if (condition.Kind == BundleWriteConditionKind.IfMatch)
                    {
                        before = HashForWrite(current);
                    }
                }

                if (!Holds(condition, current is not null, before))
                {
                    throw new BundleWriteConflictException(key);
                }

                if (current is not null && condition.Kind == BundleWriteConditionKind.None)
                {
                    EnsureWritable(current);
                }

                if (current is not null && !preparedAsReplacement && !OperatingSystem.IsWindows())
                {
                    return false;
                }

                try
                {
                    if (current is not null)
                    {
                        File.Replace(temporary, current, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(temporary, target, overwrite: false);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    settled = true; // keep the temporary: what the commit did is not established
                    throw new BundleWriteOutcomeUnknownException(key, e);
                }

                settled = true;
                return true;
            }
        }
        finally
        {
            if (!settled)
            {
                DeleteTemporary(temporary);
            }
        }
    }
```

with the following block, which also adds, right after it, the bound on commit attempts, the test of a replacement refused without effect, and the classification of any other failure. What it does: the hook for "another process acts once the temporary is written" goes first in the `try`, so that what it throws removes the temporary like any pre-commit failure; the re-check, the condition and the commit run in a loop of at most `MaxCommitAttempts` (3) attempts, the lock released and 5 to 25 ms slept between two; a replacement that fails with Windows `ERROR_UNABLE_TO_REMOVE_REPLACED` (1175) or a sharing violation changed nothing (both files keep their names), so it is made again from the re-check, and after the last attempt it is `BundleWriteNotCommittedException`, the temporary removed (B43); any other failure of the commit call is classified (B21) — after the boundary and the STRICT guards the commit ran, the temporary's chain and the target's entry, are renewed, a refusal being an unknown outcome (B44), and without reading the target of an unconditional replacement, whose failure with its temporary in place is the raw failure (B42):

```csharp
    private bool CommitTemporary(BundleKey key, string parent, string temporary, bool preparedAsReplacement, BundleObjectVersion written, BundleWriteCondition condition)
    {
        var settled = false;
        try
        {
            AfterTemporaryWrittenForTest?.Invoke(temporary);
            var target = Path.Join(parent, key.Name);
            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 1)
                {
                    // Outside the lock: the other writers of this stripe are not held up.
                    Thread.Sleep(Random.Shared.Next(5, 26));
                }

                lock (CommitLockFor(target))
                {
                    CheckBoundary();
                    GuardStrict(key, temporary); // the temporary and every directory up to the root
                    var current = FindExactForWrite(key, parent, key.Name);
                    BundleObjectVersion? before = null;
                    if (current is not null)
                    {
                        GuardObject(key, current);
                        if (condition.Kind == BundleWriteConditionKind.IfMatch)
                        {
                            before = HashForWrite(current);
                        }
                    }

                    if (!Holds(condition, current is not null, before))
                    {
                        throw new BundleWriteConflictException(key);
                    }

                    if (current is not null && condition.Kind == BundleWriteConditionKind.None)
                    {
                        EnsureWritable(current);
                    }

                    if (current is not null && !preparedAsReplacement && !OperatingSystem.IsWindows())
                    {
                        return false;
                    }

                    BeforeCommitForTest?.Invoke(temporary, target);
                    try
                    {
                        if (CommitForTest is { } commit)
                        {
                            commit(temporary, target, current is not null);
                        }
                        else if (current is not null)
                        {
                            File.Replace(temporary, current, destinationBackupFileName: null);
                        }
                        else
                        {
                            File.Move(temporary, target, overwrite: false);
                        }
                    }
                    catch (IOException e) when (current is not null && RefusedBeforeAnyEffect(e))
                    {
                        // Nothing changed: the target and the temporary are as they were. Try
                        // again from the re-check, so that a condition another writer broke
                        // meanwhile is a conflict, never an overwrite; after the last attempt the
                        // temporary is removed below and the target is left as it is.
                        if (attempt == MaxCommitAttempts)
                        {
                            throw new BundleWriteNotCommittedException(key, e);
                        }

                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        switch (ClassifyCommitFailure(key, parent, temporary, current is not null, before, written, e))
                        {
                            case CommitFailure.NothingChanged:
                                throw; // the raw failure; the temporary is removed below
                            case CommitFailure.ChangedByAnother:
                                throw new BundleWriteConflictException(key);
                            case CommitFailure.Committed:
                                settled = true;
                                return true;
                            default:
                                settled = true; // keep the temporary: it may be the only copy of the new bytes
                                throw new BundleWriteOutcomeUnknownException(key, e);
                        }
                    }

                    settled = true;
                    return true;
                }
            }
        }
        finally
        {
            if (!settled)
            {
                DeleteTemporary(temporary);
            }
        }
    }

    /// <summary>How many times <see cref="CommitTemporary"/> makes a replacement that the storage refused without any effect (<see cref="RefusedBeforeAnyEffect"/>).</summary>
    private const int MaxCommitAttempts = 3;

    /// <summary>
    /// Whether a replacement call failed before it changed anything, so that the target and the
    /// temporary are untouched and the commit can be made again: Windows <c>ReplaceFile</c>'s
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (1175 — "the replaced and replacement files retain
    /// their original file names"; measured while readers held the object open), or a sharing
    /// violation.
    /// </summary>
    private static bool RefusedBeforeAnyEffect(IOException e) =>
        e.HResult is unchecked((int)0x80070497) or unchecked((int)0x80070020);

    /// <summary>What a failed commit call left, as <see cref="ClassifyCommitFailure"/> establishes it.</summary>
    private enum CommitFailure
    {
        /// <summary>The temporary is still there and the target is as it was — for an unconditional replacement, whose target is not read, the temporary is still there: nothing of this write happened.</summary>
        NothingChanged,

        /// <summary>The temporary is still there and the target changed: another writer landed, ours did not.</summary>
        ChangedByAnother,

        /// <summary>The temporary is gone and the target holds the new bytes: the commit took effect.</summary>
        Committed,

        /// <summary>Anything else, a state that could not be read, or a path a renewed guard refuses.</summary>
        Unknown,
    }

    /// <summary>
    /// After the commit call threw, decides from what the storage now holds. Windows
    /// <c>ReplaceFile</c>'s <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c> (1176) and
    /// <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c> (1177) are unknown by definition — without a
    /// backup name the first can leave no object under the key and the new bytes under the
    /// temporary's name. Otherwise the confinement boundary and the STRICT guards the commit ran —
    /// the temporary and every directory up to the root, and the target's own entry — are renewed
    /// before anything is read: a guard that refuses, or a state that cannot be read, is
    /// <see cref="CommitFailure.Unknown"/> (the temporary is kept), never a success read through a
    /// path a link has entered since the commit's checks. That narrows the window between a check
    /// and an access; it does not close it (issue #178). Then a temporary still present means the
    /// rename did not happen: the target is as before (<see cref="CommitFailure.NothingChanged"/>)
    /// or changed by another writer (<see cref="CommitFailure.ChangedByAnother"/>) — except for an
    /// unconditional replacement (<paramref name="existed"/> without <paramref name="before"/>),
    /// whose target is not read, since its version was never needed and it may not be readable:
    /// nothing of that write happened. A temporary gone and a target holding the new bytes means
    /// the commit took effect; anything else is unknown. Reads only; never throws.
    /// </summary>
    private CommitFailure ClassifyCommitFailure(BundleKey key, string parent, string temporary, bool existed, BundleObjectVersion? before, BundleObjectVersion written, Exception failure)
    {
        if (failure.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
        {
            return CommitFailure.Unknown;
        }

        try
        {
            CheckBoundary();
            GuardStrict(key, temporary);
            GuardStrict(key, Path.Join(parent, key.Name));
            var temporaryLeft = File.Exists(temporary);
            if (temporaryLeft && existed && before is null)
            {
                return CommitFailure.NothingChanged;
            }

            var now = ListExact(parent, key.Name);
            BundleObjectVersion? after = now is null ? null : HashForRead(now);
            if (temporaryLeft)
            {
                return after == before ? CommitFailure.NothingChanged : CommitFailure.ChangedByAnother;
            }

            return after == written ? CommitFailure.Committed : CommitFailure.Unknown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BundleAccessRefusedException)
        {
            return CommitFailure.Unknown;
        }
    }
```

(`throw;` inside the `switch` is legal: it is still lexically inside the `catch`; `continue` leaves the `catch` and the `lock` for the next attempt.)

(d) In `Delete`, the line `            File.Delete(current);` becomes

```csharp
            BeforeCommitForTest?.Invoke(null, current);
            File.Delete(current);
```

(e) In `Write`'s `<remarks>`, replace the `Failures` paragraph

```csharp
    /// <para><b>Failures.</b> A failure before the commit removes the temporary and leaves the
    /// target untouched; it is the raw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, whose message may name a host path (the core
    /// translates it). A failure of the commit call itself raises
    /// <see cref="BundleWriteOutcomeUnknownException"/> and keeps the temporary.</para>
```

with

```csharp
    /// <para><b>Failures.</b> A failure before the commit removes the temporary and leaves the
    /// target untouched; it is the raw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, whose message may name a host path (the core
    /// translates it). A replacement that the storage refuses without any effect — Windows'
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (1175), measured while readers held the object
    /// open, or a sharing violation — is made again, at most three times in all, 5 to 25 ms
    /// apart, each time from the re-check of the condition against the bytes stored then; after
    /// the last attempt it raises <see cref="BundleWriteNotCommittedException"/>, the temporary
    /// removed and the target untouched. Any other failure of the commit call is classified from
    /// what the disk then holds, once the boundary and the STRICT guards have been renewed: the
    /// temporary still there and the target as it was — nothing happened, the raw failure, the
    /// temporary removed; the temporary still there and the target changed — another writer
    /// landed, <see cref="BundleWriteConflictException"/>, the temporary removed (an unconditional
    /// replacement does not read its target: the raw failure); the temporary gone and the target
    /// holding the new bytes — committed, success. Anything else, a guard that now refuses, and
    /// always Windows' <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c> (1176, 1177) — which can leave no
    /// object under the key and the new bytes under the temporary's name — raises
    /// <see cref="BundleWriteOutcomeUnknownException"/> and keeps the temporary, the only copy of
    /// the new content. The temporary is never removed through a path that a link has entered
    /// since it was created; it is then left in place.</para>
    /// <para><b>What a concurrent reader sees.</b> Never a part of the new content. On Unix the
    /// commit is a <c>rename</c>, so a reader finds the old object or the new one. On Windows
    /// <c>ReplaceFile</c> is atomic for the stored state but not for readers: while it runs, a
    /// reader can for a moment find no entry under the name or meet a sharing violation (measured
    /// on Windows 11); <see cref="Read"/> shares writing and deletion, so it never denies a
    /// replacement the sharing it needs, and retries that window a bounded number of times.</para>
```

(f) In `ResolveParentForWrite`, right before the line `        // The chain again after any creation, and the object's own entry (the writer's late`, insert the hook call and a blank line:

```csharp
        ParentsResolvedForTest?.Invoke(current);

```

Nothing in this task sets it: `BundleConceptWriter.BeforeLateReparseCheckForTest` forwards to it from Task 4a, so the writer's five existing late-guard tests keep firing where the writer's own `Directory.CreateDirectory` and late re-check used to be.

(g) In `src/OKF4net/Providers/BundleProviderExceptions.cs`, right after `BundleWriteOutcomeUnknownException` (after the `}` that closes it, which follows its `Key` property), insert a blank line and:

```csharp
/// <summary>
/// A write was not committed because the storage kept refusing the commit with a failure that
/// changes nothing (spec §4) — on Windows, <c>ReplaceFile</c>'s
/// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (1175) or a sharing violation — on every one of a
/// bounded number of attempts. The object holds its previous content and nothing of the write
/// remains. The message names the key and the raw failure's type only; the raw failure is the
/// <see cref="Exception.InnerException"/>, for the host's log.
/// </summary>
public sealed class BundleWriteNotCommittedException(BundleKey key, Exception inner)
    : BundleProviderException($"the write of '{key}' was not committed: the storage kept refusing it ({(inner ?? throw new ArgumentNullException(nameof(inner))).GetType().Name}); the object is unchanged", inner)
{
    /// <summary>The key whose write was not committed.</summary>
    public BundleKey Key { get; } = key;
}
```

- [ ] **Step 4: Run the tests — the reads still fail, for the reasons B8 and B45 exist**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderCommitTests"`
Expected on Windows: `Failed: 8, Passed: 23, Total: 31` — `Concurrent_reads_never_block_a_replacement_and_never_see_a_torn_object` fails with `System.IO.IOException : The process cannot access the file '…\x.md' because it is being used by another process.`, thrown by the **writer** (its `EnsureWritable` or `File.Replace` met a reader that `Read` opened with `FileShare.Read`; it failed this way in five runs out of five during drafting), and the seven rows of the read-retry tests fail because `Read` does not call `ReadOpeningForTest` yet (`A_read_meeting_the_replace_window_once_…` ×3, `A_read_meeting_the_replace_window_on_every_attempt_…` ×2, `An_object_deleted_while_it_is_read_…`, `An_absent_key_a_refusal_and_a_version_mismatch_…`). The commit tests — the 1175 retries, the classification behind a swapped ancestor included — pass. On Linux the same seven read rows fail and the concurrent test passes (sharing modes do not exist there).

- [ ] **Step 5: Let reads share deletion (B8) and retry the replace window (B45)**

In `Read`, replace the method — its signature line through its closing brace; the `/// <inheritdoc/>` line above it stays —

```csharp
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
        GuardResolved(key, path);
        if (!File.Exists(path))
        {
            throw new BundleObjectNotFoundException(key);
        }

        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > _limits.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, fs.Length, _limits.MaxObjectBytes);
            }

            bytes = ReadBounded(fs, _limits.MaxObjectBytes, key);
        }
        catch (FileNotFoundException)
        {
            throw new BundleObjectNotFoundException(key);
        }
        catch (DirectoryNotFoundException)
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }
```

with the following — its remarks, the method, and two members after it. Every attempt redoes every check (the boundary, the exact match, the STRICT guards) and checks `expected` against the bytes it read; only a sharing or lock violation, or a not-found raised by the open after the listing matched the entry, is retried, on Windows only, at most 3 attempts in all, 5 to 20 ms apart; a key the listing does not match, a refusal and a version mismatch are never retried; a directory under the key is "not found" at once:

```csharp
    /// <remarks>
    /// <para>Shares writing and deletion, so on Windows a read never denies a concurrent replacement
    /// or delete the sharing it needs. While Windows' <c>ReplaceFile</c> replaces the object, a reader can for a
    /// moment meet a sharing violation, or find that the entry its exact listing had just matched
    /// is no longer there (measured, issue #157): on Windows only, a read retries those two —
    /// and nothing else — at most <see cref="MaxReadAttempts"/> times in all, 5 to 20 ms apart,
    /// redoing every check on each attempt (the confinement boundary, the exact-name match, the
    /// STRICT guards) and checking <paramref name="expected"/> against the bytes it actually
    /// read. A key the listing does not match is answered at once, never retried; nor is a
    /// refusal or a version mismatch. An object really deleted is not found once its listing no
    /// longer matches it, or after the last attempt. This narrows the window, it does not close
    /// it: a listing made while <c>ReplaceFile</c> runs can still find no entry under the name,
    /// and a read can still fail after its last attempt.</para>
    /// </remarks>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var attempt = 1; ; attempt++)
        {
            CheckBoundary();
            var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
            GuardResolved(key, path);
            if (!File.Exists(path) && Directory.Exists(path))
            {
                throw new BundleObjectNotFoundException(key); // a directory is no object
            }

            byte[] bytes;
            try
            {
                ReadOpeningForTest?.Invoke(path);

                // Shares writing and deletion: a read must never make a concurrent replacement
                // (File.Replace) or delete fail on Windows with a sharing violation.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > _limits.MaxObjectBytes)
                {
                    throw new BundleObjectTooLargeException(key, fs.Length, _limits.MaxObjectBytes);
                }

                bytes = ReadBounded(fs, _limits.MaxObjectBytes, key);
            }
            catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException || IsSharingViolation(failure))
            {
                // The entry was matched a moment ago: on Windows, the ReplaceFile window.
                if (attempt < MaxReadAttempts && OperatingSystem.IsWindows())
                {
                    Thread.Sleep(Random.Shared.Next(5, 21));
                    continue;
                }

                if (failure is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new BundleObjectNotFoundException(key);
                }

                throw;
            }

            var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            if (expected is { } e && e != version)
            {
                throw new BundleVersionMismatchException(key);
            }

            return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
        }
    }

    /// <summary>How many times in all <see cref="Read"/> opens an object on Windows when it meets the <c>ReplaceFile</c> window.</summary>
    private const int MaxReadAttempts = 3;

    /// <summary>A sharing or lock violation (<c>ERROR_SHARING_VIOLATION</c>, <c>ERROR_LOCK_VIOLATION</c>).</summary>
    private static bool IsSharingViolation(Exception e) =>
        e is IOException && e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderCommitTests"` five times.
Expected each time: `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31` — on Windows the junction-based link tests run without any privilege.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers"` — `Failed: 0`. Then the Linux container command (both passes `Failed: 0`; the commit tests run in full there, symlinks included).

- [ ] **Step 7: CHANGELOG** — under `### Changed`, after Task 2a's bullet, add:

```markdown
- **A failed commit says what happened.** A failure before the commit leaves
  the target untouched and no temporary. A replacement that Windows
  `ReplaceFile` refuses without any effect — error 1175, seen twice in 1 500
  commits while readers held the object open, or a sharing violation — is made
  again, at most three times, each time from the re-check of its condition;
  after that it is `BundleWriteNotCommittedException` (new), the object
  unchanged and no temporary left. Any other failure of the commit call is
  classified from the disk once the boundary and the path guards have been
  checked again: nothing happened (the raw error), another writer landed
  (`BundleWriteConflictException`), or it took effect (success); otherwise, and
  always for Windows `ReplaceFile` error 1176/1177, it is
  `BundleWriteOutcomeUnknownException` and the temporary — possibly the only
  copy of the new content — is kept. `okf validate` ignores it (not `.md`).
- **Reads no longer block writes (B8).** `FileSystemBundleProvider.Read`
  opens with `FileShare.ReadWrite | FileShare.Delete`, so on Windows a read
  never denies a replacement or a delete the sharing it needs. Readers never
  see a torn object any more. On Windows, while `ReplaceFile` runs, a
  concurrent reader can for a moment find no object under the name or meet a
  sharing violation (measured on plain opens: 2.07 % and 1.78 % of 192 649);
  `Read` retries both — only when the name had just been listed — at most
  three times, 5 to 20 ms apart, which narrows that window without closing it.
  A key that does not exist is answered at once.
```

and under `### Added`, after Task 1's bullet (it ends `  atomicity and implicit prefixes.`), add:

```markdown
- **`BundleWriteNotCommittedException`**: a write the storage kept refusing
  without any effect, on every attempt; the object is unchanged (see Changed,
  "A failed commit says what happened").
```

- [ ] **Step 8: Verify and commit**

```bash
for f in src/OKF4net/Providers/FileSystemBundleProvider.cs src/OKF4net/Providers/BundleProviderExceptions.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderCommitTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
git add src/OKF4net/Providers/FileSystemBundleProvider.cs src/OKF4net/Providers/BundleProviderExceptions.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderCommitTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(providers): classify a failed commit from the disk, keep the temporary when unknown; reads share deletion

When the commit call throws, the provider first renews the boundary and the
STRICT path guards, then reads the disk back: the temporary still there and the
target unchanged rethrows the raw failure and removes the temporary; the target
changed by another writer is a conflict (an unconditional replacement does not
read its target: the raw failure); the temporary gone and the new bytes in
place is a success; anything else, a guard that now refuses, and always
ReplaceFile error 1176/1177, raises BundleWriteOutcomeUnknownException and keeps
the temporary. A replacement ReplaceFile refuses without effect (1175, or a
sharing violation) is made again from the re-check, at most three attempts,
then BundleWriteNotCommittedException with the temporary removed. Internal
test hooks (parents resolved, temporary name, after the temporary is written,
before the commit, the commit call itself, before a read opens the object) are
null outside tests.
Read opens with FileShare.ReadWrite | FileShare.Delete so that it never denies a
replacement or a delete the sharing it needs on Windows, and there retries a
sharing violation, or an object the listing had just matched, at most three
attempts.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 2c: Writes against the listing scope (#205), orphaned temporaries, and the documented promises

**Files:**
- Modify: `src/OKF4net/Providers/FileSystemBundleProvider.cs` (`ListingMemo.Forget`, `ForgetListings` and its two call sites, the durability/orphan/ACL/inter-process paragraphs of `Write`'s remarks)
- Test: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteScopeTests.cs`
- Modify: `README.md` (new subsection "Writing through a provider"), `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 2a/2b (`Write`, `Delete`, `ListOrphanedTemporaries`, `TemporaryPrefix`, `TemporarySuffix`); lot A's scope (`IProviderScope.BeginScope()`, the private `ActiveMemo` and `ListingMemo` with its `_gate`, `_listings`, `_held`).
- Produces: `ListingMemo.Forget(string directory)` (private nested class); `private void ForgetListings(BundleKey key)`; B28's behaviour — a successful `Write` or `Delete` drops the calling flow's memoised listings of the root and of every directory on the key's path. No public API.

Why this task exists: only `Bundle.Load` and `BundleValidator.Validate` open a provider scope, around their own reads (`grep -rn "ProviderScopes.Begin" src` → `Bundle.cs` and `Validate.cs`), and the writer never runs inside one — but a host callback runs inside a load (`BundleLoadOptions.ExtraKeyValidators`) and a validation (`IOkfClock`), and a load can run on one thread while a write runs on another. Task 2a already resolves every write segment with a fresh listing (`FindExactForWrite`/`ListExact`, never the memo); this task pins that, and drops the memo after a write so that the writing flow is not served its stale names.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteScopeTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// Writes against the listing scope of issue #205, and the temporaries a write can leave
/// (issue #157). In the repository only <c>Bundle.Load</c> and <c>BundleValidator.Validate</c>
/// open a scope, around their own reads, and the writer never runs inside one; these tests pin
/// what happens if a write does meet a scope — on its own flow, or on another while a load runs.
/// </summary>
public class FileSystemBundleProviderWriteScopeTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// Inside a scope the write resolves the key against the directory as it is now, not as the
    /// scope first listed it: the object deleted behind the scope's back is absent for the write,
    /// so <c>MustNotExist</c> holds. Afterwards the scope's listings on the key's path are dropped,
    /// so the same flow's next lookup sees the new object.
    /// </summary>
    [Fact]
    public void A_write_inside_a_scope_ignores_the_scopes_listing_then_drops_it()
    {
        using var tmp = new TempDir();
        var gone = tmp.Write("d/x.md", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("d/x.md")));   // memoises d = [x.md]
            Assert.False(p.Exists(K("d/new.md")));
            File.Delete(gone);                      // behind the scope's back

            p.Write(K("d/x.md"), B("recreated"), BundleWriteCondition.MustNotExist);
            p.Write(K("d/new.md"), B("new"), BundleWriteCondition.MustNotExist);

            Assert.True(p.Exists(K("d/new.md")));
            using var obj = p.Read(K("d/x.md"));
            Assert.Equal(9, obj.Length);
        }
    }

    /// <summary>
    /// A load's scope is open on one flow while another flow writes. A write on a flow that did
    /// not inherit the scope never touches its memo: the scope's reads still get each object's
    /// current bytes with the version of those bytes — never one paired with the other — while a
    /// name created meanwhile stays unseen until the scope ends (the window documented on
    /// <c>BeginScope</c>). A write on a flow that inherited the scope drops the scope's listings
    /// on its path, so the new name is seen at once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_write_on_another_flow_never_makes_a_scope_serve_bytes_and_version_that_disagree(bool writerInheritsTheScope)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "v1");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            using (var before = p.Read(K("x.md")))
            {
                Assert.Equal(2, before.Length); // memoises the root
            }

            Assert.False(p.Exists(K("y.md")));
            Exception? failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    p.Write(K("x.md"), B("version two"), BundleWriteCondition.None);
                    p.Write(K("y.md"), B("y"), BundleWriteCondition.MustNotExist);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            if (writerInheritsTheScope)
            {
                writer.Start();
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    writer.Start();
                }
            }

            writer.Join();
            Assert.Null(failure);

            using (var after = p.Read(K("x.md")))
            {
                var bytes = new byte[after.Length!.Value];
                after.Content.ReadExactly(bytes);
                Assert.Equal("version two", Encoding.UTF8.GetString(bytes));
                Assert.Equal(new BundleObjectVersion(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))), after.Version);
            }

            Assert.Equal(writerInheritsTheScope, p.Exists(K("y.md")));
        }

        Assert.True(p.Exists(K("y.md")));
    }

    /// <summary>
    /// A temporary left behind (B9) is listed by the test seam, counts toward the entry bound
    /// like any file, and is ignored as a concept by the load and the validation (it is no
    /// <c>.md</c>): nothing reports it, which is why the README says how to find it.
    /// </summary>
    [Fact]
    public void An_orphaned_temporary_is_listed_counted_and_otherwise_ignored()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: guide\ntitle: A\ndescription: d\n---\n\nbody\n");
        var orphan = FileSystemBundleProvider.TemporaryPrefix + new string('0', 32) + FileSystemBundleProvider.TemporarySuffix;
        tmp.Write("d/" + orphan, "new content of d/x.md");
        var p = new FileSystemBundleProvider(tmp.Path);

        Assert.Equal(["d/" + orphan], p.ListOrphanedTemporaries(BundleLimits.Default));
        Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));

        var bundle = Bundle.Load(p);
        Assert.Equal(1, bundle.Count);
        Assert.Empty(bundle.ParseProblems);
        Assert.DoesNotContain(BundleValidator.Validate(bundle).Diagnostics, d => d.Message.Contains(orphan, StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderWriteScopeTests"`
Expected: `Failed: 2, Passed: 2` — `A_write_inside_a_scope_ignores_the_scopes_listing_then_drops_it` fails at `Assert.True(p.Exists(K("d/new.md")))` (the scope still serves `d` as first listed), and `A_write_on_another_flow_never_makes_a_scope_serve_bytes_and_version_that_disagree(writerInheritsTheScope: True)` fails at `Assert.Equal(writerInheritsTheScope, p.Exists(K("y.md")))` (`Expected: True, Actual: False`). The other row and the orphan test pass already: they pin Task 2a's behaviour (the write itself never trusted the memo, and orphans were never concepts).

- [ ] **Step 3: Drop the scope's listings after a write**

In `src/OKF4net/Providers/FileSystemBundleProvider.cs`, with `Edit`:

(a) In the nested `ListingMemo` class, right before `        public void Close()`, insert the following, followed by one blank line:

```csharp
        /// <summary>Drops one directory's names (a write changed them); the next lookup lists it again.</summary>
        public void Forget(string directory)
        {
            lock (_gate)
            {
                if (_listings.Remove(directory, out var names))
                {
                    _held -= 1L + names.Length;
                }
            }
        }
```

(b) Right after `HashForRead` (its closing `    }`), insert a blank line and:

```csharp
    /// <summary>After a write or a delete, drops the calling flow's memoised listings of every directory on the key's path, so that a later lookup in the same scope lists them again.</summary>
    private void ForgetListings(BundleKey key)
    {
        if (ActiveMemo is not { } memo)
        {
            return;
        }

        var current = _fullRoot;
        memo.Forget(current);
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            current = Path.Join(current, key.Segments[i]);
            memo.Forget(current);
        }
    }
```

(c) In `Write`,

```csharp
            if (CommitTemporary(key, parent, temporary, preparedAsReplacement: existing is not null, written, condition))
            {
                return written;
            }
```

becomes

```csharp
            if (CommitTemporary(key, parent, temporary, preparedAsReplacement: existing is not null, written, condition))
            {
                ForgetListings(key);
                return written;
            }
```

(d) In `Delete`, the end of the method

```csharp
            BeforeCommitForTest?.Invoke(null, current);
            File.Delete(current);
        }
    }
```

becomes

```csharp
            BeforeCommitForTest?.Invoke(null, current);
            File.Delete(current);
        }

        ForgetListings(key);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderWriteScopeTests|FullyQualifiedName~FileSystemBundleProviderScopeTests"`
Expected: `Failed: 0` (4 new tests, and lot A's scope tests unchanged).

- [ ] **Step 5: Document what a write promises**

(a) In `Write`'s `<remarks>`, after the paragraph `<para><b>What a concurrent reader sees.</b> …</para>` and before `/// </remarks>`, insert:

```csharp
    /// <para><b>Durability.</b> When this method returns, the new bytes reached stable
    /// storage (<see cref="FileStream.Flush(bool)"/>) before the rename. A process crash or an I/O
    /// error during the write leaves the object holding its whole old content or its whole new
    /// content. A power loss can also leave the old content after a replacement returned, or no
    /// object after a creation returned, and a temporary behind: the directory entry is not
    /// flushed (no .NET API flushes a directory).</para>
    /// <para><b>Orphaned temporaries.</b> A temporary left behind — an unknown outcome, or a
    /// failure whose temporary could no longer be reached safely — is not a <c>.md</c>, so the
    /// core ignores it as a concept; it still counts toward <see cref="BundleLimits.MaxEntries"/>
    /// and a producer that requires an empty output directory sees it. Nothing removes it
    /// automatically: it may be the only copy of the new content.</para>
    /// <para><b>Windows ACL.</b> The temporary inherits its directory's ACL while it is written;
    /// <see cref="File.Replace(string, string, string?)"/> keeps the replaced object's own DACL on
    /// the committed object (measured). An object whose explicit ACL is narrower than its
    /// directory's is therefore readable under the directory's ACL, through the temporary, while
    /// its next content is being written.</para>
    /// <para><b>Inter-process window.</b> The commit lock is in-process: between the re-check and
    /// the commit another process can act (issue #178). On Unix
    /// <see cref="File.Move(string, string, bool)"/> without overwrite is an <c>lstat</c> then a
    /// <c>rename</c> (measured), so a name another process creates in that window is
    /// replaced.</para>
```

(b) In `README.md`, the two lines Task 2a wrote

```markdown
filesystem provider is writable (`Capabilities.CanWrite`); no other provider
ships yet, and the first remote providers are tracked in #139.
```

become

```markdown
filesystem provider is writable (see "Writing through a provider" below); no
other provider ships yet, and the first remote providers are tracked in #139.
```

and after the paragraph that ends `case or backslash reason — a known, temporary gap.` (the end of "Loading through a provider", before `### As a CLI`), insert a blank line and:

```markdown
### Writing through a provider

`provider.Write(key, bytes, condition)` stores one object atomically if its
condition holds — `BundleWriteCondition.None`, `MustNotExist`, or
`IfMatch(version)` with the version a `Read` returned — and returns the new
version; `provider.Delete(key, condition)` removes one. A key space has no
directories: a write creates the missing prefixes, and a delete leaves empty
directories behind, which hold no key. A provider whose `Capabilities.CanWrite`
is `false`, or the view `provider.AsReadOnly()` returns, throws
`BundleReadOnlyException` before attempting anything. A condition that does
not hold throws `BundleWriteConflictException` and changes nothing.

`FileSystemBundleProvider` writes the bytes to a temporary
`.okf-write-<32 hex>.tmp` beside the target, flushes it to stable storage,
then checks the confinement boundary, the path (no symlink or junction, the
exact stored names) and the condition (on the SHA-256 of the stored bytes)
again under a per-object lock, and commits with `File.Replace` or, for a new
object, `File.Move` without overwrite. On Unix a replacement's temporary is
owner-only until it takes the target's mode, and a read-only target is
refused as before; an unconditional write needs only the right to write its
target, as before (only `IfMatch` reads it). What this promises:

- **Crash or I/O error mid-write:** the object holds its whole old content or
  its whole new content, never a part.
- **Power loss:** the old content, the new content, or for a new object no
  object at all — the directory entry is not flushed (.NET has no API for
  it).
- **Concurrent readers:** never a part of the new content. On Unix the commit
  is a `rename`. On Windows `ReplaceFile` can, for a moment, make a reader
  find no object under the name or meet a sharing violation; `Read` retries
  both a few times, which narrows that moment without removing it.
- **A replacement Windows refuses without effect** (`ReplaceFile` error 1175,
  or a sharing violation) is made again, at most three times in all, then
  fails with `BundleWriteNotCommittedException`, the object unchanged.
- **Other processes:** the lock is per process; between the last check and
  the commit another process can still write the same object (#178).

A write whose outcome cannot be established (`BundleWriteOutcomeUnknownException`,
for instance Windows `ReplaceFile` error 1176, which can leave no object
under the key) keeps its temporary: it may be the only copy of the new
content. `okf validate` ignores such a file (it is not `.md`), but it counts
toward the entry bound and an empty-directory check sees it; look for
`.okf-write-*.tmp` files and remove them by hand once you have checked them.
```

(c) In `CHANGELOG.md`, under `### Changed`, after Task 2b's bullets, add:

```markdown
- **Durability of a filesystem write, stated** (README, "Writing through a
  provider"): after `Write` returns, the bytes were flushed to stable storage
  before the rename; a crash or an I/O error leaves the whole old or the whole
  new content; a power loss can also lose a just-created object, since the
  directory entry is not flushed. A write or a delete made while a provider
  scope is open resolves against the directory as it is, and drops that
  scope's listings on its path.
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/Providers/FileSystemBundleProvider.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteScopeTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
```

Then the Linux container command, and once more the whole suite in the container (`dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` in place of the `Providers` filter) — `Failed: 0` in both passes (r3's replay at this point: 3 281 tests; root `Passed: 3218, Skipped: 63`, `tester` `Passed: 3222, Skipped: 59` — the skips are the Windows-only cases, and as root the tests that need an unprivileged user; on Windows the same run gives `Passed: 3253, Skipped: 28, Total: 3281`).

```bash
git add src/OKF4net/Providers/FileSystemBundleProvider.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderWriteScopeTests.cs README.md CHANGELOG.md
git commit -F - <<'MSG'
feat(providers): a write drops the scope's listings on its path; document what a filesystem write promises

After a successful Write or Delete, the calling flow's memoised listings of
the root and of each directory on the key's path are dropped, so the same
scope's next lookup lists them again; the write itself never read them. Write's
XML remarks and the README's new "Writing through a provider" section state
what a write promises after a crash, an I/O error and a power loss, what a
concurrent reader can see on Windows, the per-process lock, and how an orphaned
temporary is found. Tests pin a write meeting a provider scope on its own flow
and on another, and an orphan being listed, counted and ignored.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 3: The tree check before a creation

**Files:**
- Create: `src/OKF4net/Providers/BundleTreeCheck.cs`
- Test: `tests/OKF4net.Tests/Providers/BundleTreeCheckTests.cs`

**Interfaces:**
- Consumes (lot A and Task 1): `IBundleProvider` (`Enumerate`, `Exists`), `BundleKey` (`Segments`, `FromSegments`), `BundleKeyValidator` (`Effective`, `FoldSegment`), `BundleKeyConstraints`, `BundleLimits`, `BundleLimitExceededException(BundleBound, long)`, `OKF4net.Internal.DebugQuote.Quote`; test side `InMemoryBundleProvider` (`Add`, `Calls`, `ObeyLimits`, `FailEnumerateWith`, `RefuseExistsOf`).
- Produces: `internal static class BundleTreeCheck` with `internal static string? CheckCreation(IBundleProvider provider, BundleKeyValidator validator, BundleKey key, BundleLimits limits)` — `null` when the tree that results from creating `key` (absent at the provider) is valid under `validator`; otherwise a sentence without final period, one of:
  - `"{key}" cannot be created: "{existing}" already exists and differs from "{mine}" only in case or Unicode form` (a twin at any level; `existing` and `mine` are the prefixes down to that level);
  - `"{key}" cannot be created: "{prefix}" is an object, so it cannot also be a prefix of other keys`;
  - `"{key}" cannot be created: objects exist under it (such as "{object}"), so it cannot also be an object`.
  Quoting is `DebugQuote.Quote`. Provider exceptions propagate; more than `limits.MaxEntries` listed entries throw `BundleLimitExceededException(BundleBound.Entries, …)`. No behaviour of the library changes in this task: Task 4a calls it.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/Providers/BundleTreeCheckTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The tree check before a creation (spec §2, decision B10), through the in-memory provider, which
/// can hold any set of names: case and Unicode-form twins, an object that is also a prefix. The
/// validators are built the way the loader builds them, so what the check refuses is exactly what
/// the next load would discard.
/// </summary>
public class BundleTreeCheckTests
{
    private const BundleKeyConstraints Case = BundleKeyConstraints.CaseInsensitiveCollisions;
    private const BundleKeyConstraints Form = BundleKeyConstraints.UnicodeFormCollisions;
    private const BundleKeyConstraints FileDir = BundleKeyConstraints.FileDirectoryConflict;

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static InMemoryBundleProvider Storage(params string[] names)
    {
        var p = new InMemoryBundleProvider();
        foreach (var name in names)
        {
            p.Add(name, "x");
        }

        return p;
    }

    private static string? Check(InMemoryBundleProvider p, BundleKeyConstraints constraints, string key, BundleLimits? limits = null) =>
        BundleTreeCheck.CheckCreation(p, new BundleKeyValidator(constraints, BundleKeyPolicy.Native), K(key), limits ?? BundleLimits.Default);

    [Fact]
    public void A_validator_without_collision_or_conflict_rules_reads_nothing_and_accepts()
    {
        var p = Storage("A/x.md", "a.md");
        Assert.Null(Check(p, BundleKeyConstraints.TrailingDotOrSpace, "a/y.md"));
        Assert.Null(Check(p, BundleKeyConstraints.None, "a.md/b.md"));
        Assert.Empty(p.Calls);
    }

    [Theory]
    [InlineData("A/x.md", "a/y.md", "\"a/y.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form")]
    [InlineData("d/B/x.md", "d/b/y.md", "\"d/b/y.md\" cannot be created: \"d/B\" already exists and differs from \"d/b\" only in case or Unicode form")]
    [InlineData("d/e/X.md", "d/e/x.md", "\"d/e/x.md\" cannot be created: \"d/e/X.md\" already exists and differs from \"d/e/x.md\" only in case or Unicode form")]
    [InlineData("a/b/c/D/e.md", "a/b/c/d/f.md", "\"a/b/c/d/f.md\" cannot be created: \"a/b/c/D\" already exists and differs from \"a/b/c/d\" only in case or Unicode form")]
    public void A_case_twin_at_any_level_is_refused_and_named(string existing, string created, string expected)
    {
        Assert.Equal(expected, Check(Storage(existing), Case, created));
    }

    [Fact]
    public void Exact_siblings_and_twins_off_the_path_are_no_collision()
    {
        var p = Storage("a/x.md", "a/b/c/d/e.md", "X.md", "x.md", "other/Y.md");
        Assert.Null(Check(p, Case | FileDir, "a/y.md"));
        Assert.Null(Check(p, Case | FileDir, "a/b/c/d/f.md"));
        Assert.Null(Check(p, Case | FileDir, "other/z.md"));
        Assert.Null(Check(p, Case | FileDir, "y.md"));
    }

    [Fact]
    public void A_case_twin_is_accepted_when_the_validator_does_not_fold_case()
    {
        Assert.Null(Check(Storage("A/x.md"), FileDir, "a/y.md"));
    }

    [Fact]
    public void A_Unicode_form_twin_is_refused_only_under_a_validator_that_folds_forms()
    {
        var nfc = "caf" + (char)0x00E9 + ".md";
        var nfd = "cafe" + (char)0x0301 + ".md";
        var p = Storage("d/" + nfc);
        var refused = Check(p, Form, "d/" + nfd);
        Assert.NotNull(refused);
        Assert.Contains("only in case or Unicode form", refused, StringComparison.Ordinal);
        Assert.Null(Check(p, Case, "d/" + nfd));
        Assert.Null(Check(p, BundleKeyConstraints.None, "d/" + nfd));
    }

    [Theory]
    [InlineData(FileDir)]
    [InlineData(FileDir | Case | Form)]
    public void A_proper_prefix_that_is_an_object_is_refused_and_named(BundleKeyConstraints constraints)
    {
        Assert.Equal(
            "\"a.md/b.md\" cannot be created: \"a.md\" is an object, so it cannot also be a prefix of other keys",
            Check(Storage("a.md"), constraints, "a.md/b.md"));
        Assert.Equal(
            "\"d/a.md/b/c.md\" cannot be created: \"d/a.md\" is an object, so it cannot also be a prefix of other keys",
            Check(Storage("d/a.md"), constraints, "d/a.md/b/c.md"));
    }

    [Theory]
    [InlineData(FileDir)]
    [InlineData(FileDir | Case | Form)]
    public void A_key_that_is_already_a_prefix_is_refused_and_names_an_object_under_it(BundleKeyConstraints constraints)
    {
        Assert.Equal(
            "\"a.md\" cannot be created: objects exist under it (such as \"a.md/b.md\"), so it cannot also be an object",
            Check(Storage("a.md/b.md"), constraints, "a.md"));
    }

    [Fact]
    public void Without_the_file_directory_rule_an_object_may_also_be_a_prefix()
    {
        Assert.Null(Check(Storage("a.md"), Case, "a.md/b.md"));
        Assert.Null(Check(Storage("a.md/b.md"), Case, "a.md"));
    }

    [Fact]
    public void Under_folding_the_shallowest_conflict_is_the_one_named()
    {
        var p = Storage("A/x.md", "a/b.md", "a/B/c.md");
        Assert.Equal(
            "\"a/b.md/c.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form",
            Check(p, Case | FileDir, "a/b.md/c.md"));
    }

    [Fact]
    public void Without_folding_it_asks_one_existence_per_proper_prefix_and_lists_only_under_the_key()
    {
        var p = Storage("other.md");
        Assert.Null(Check(p, FileDir, "a/b/c.md"));
        Assert.Equal(["exists a", "exists a/b", "enumerate a/b/c.md"], p.Calls);
    }

    [Fact]
    public void With_folding_it_lists_the_bundle_once()
    {
        var p = Storage("other.md");
        Assert.Null(Check(p, Case | FileDir, "a/b/c.md"));
        Assert.Equal(["enumerate "], p.Calls);
    }

    [Fact]
    public void The_entry_bound_is_enforced_by_the_core_even_when_the_provider_ignores_it()
    {
        var limits = new BundleLimits { MaxEntries = 3 };
        var obeying = Storage("1.md", "2.md", "3.md", "4.md");
        Assert.Throws<BundleLimitExceededException>(() => Check(obeying, Case, "new.md", limits));

        var ignoring = new InMemoryBundleProvider { ObeyLimits = false };
        foreach (var name in new[] { "1.md", "2.md", "3.md", "4.md" })
        {
            ignoring.Add(name, "x");
        }

        var ex = Assert.Throws<BundleLimitExceededException>(() => Check(ignoring, Case, "new.md", limits));
        Assert.Equal(BundleBound.Entries, ex.Bound);
        Assert.Null(Check(ignoring, Case, "new.md", new BundleLimits { MaxEntries = 4 }));
    }

    [Fact]
    public void A_listing_or_an_existence_question_that_fails_propagates()
    {
        var unlistable = Storage("a.md");
        var failure = new IOException("the storage cannot be listed");
        unlistable.FailEnumerateWith = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => Check(unlistable, Case, "b.md")));
        Assert.Same(failure, Assert.Throws<IOException>(() => Check(unlistable, FileDir, "b.md")));

        var refusing = Storage("a/x.md").RefuseExistsOf("a");
        Assert.Throws<BundleAccessRefusedException>(() => Check(refusing, FileDir, "a/y.md"));
    }
}
```

`unix2dos -q tests/OKF4net.Tests/Providers/BundleTreeCheckTests.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only, all `CS0103: The name 'BundleTreeCheck' does not exist in the current context`.

- [ ] **Step 3: Implement the check**

`src/OKF4net/Providers/BundleTreeCheck.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The tree check before a creation (spec §2, "the writes must not create these conflicts";
/// decision B10). Validating a listing does not stop a write from introducing a collision: on a
/// case-sensitive volume holding <c>A/x.md</c>, creating <c>a/y.md</c> passes the key-by-key
/// validation and <see cref="BundleWriteCondition.MustNotExist"/>, and the next load would
/// discard both subtrees. So before the first byte of a creation the core asks the provider —
/// never an image, which may be stale — whether the tree that would result is one the loader
/// accepts under the same <see cref="BundleKeyValidator"/>:
/// <list type="bullet">
/// <item><description>under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/> or
/// <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, no existing sibling of any segment
/// of the new key differs from it but folds to the same name through
/// <see cref="BundleKeyValidator.FoldSegment"/> — the loader's own fold, not a second one;</description></item>
/// <item><description>under <see cref="BundleKeyConstraints.FileDirectoryConflict"/>, no proper
/// prefix of the new key is an existing object, and no object exists under the new key taken
/// as a prefix.</description></item>
/// </list>
/// A validator that sets none of these flags needs no check, and none is made.
/// <para><b>What it reads.</b> When names fold, one listing of the whole bundle
/// (<see cref="IBundleProvider.Enumerate"/> with no prefix): the children of every parent prefix
/// of the new key are in it, and so are the objects on and under its path. Otherwise one
/// <see cref="IBundleProvider.Exists"/> per proper prefix and one listing under the key itself.
/// Every listed entry counts against <see cref="BundleLimits.MaxEntries"/>, enforced here even
/// when the provider ignores it (spec §6). A provider exception propagates: a write refuses what
/// it cannot see.</para>
/// <para><b>Not atomic across processes</b> (issue #178): the check and the write that follows
/// are two operations; the caller holds its in-process write lock across both, and a conflict
/// another process creates in between is discarded, with a diagnostic, by the next load.</para>
/// </summary>
internal static class BundleTreeCheck
{
    /// <summary>
    /// Checks the creation of <paramref name="key"/>, which the caller has just found absent at
    /// <paramref name="provider"/>; an update needs no check and the caller makes none.
    /// </summary>
    /// <returns><see langword="null"/> when the tree that results is valid; otherwise a
    /// library-authored sentence (no final period) naming the key and the conflicting name.</returns>
    internal static string? CheckCreation(IBundleProvider provider, BundleKeyValidator validator, BundleKey key, BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(limits);
        var folds = validator.Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions)
            || validator.Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);
        var fileDirectory = validator.Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict);
        if (folds)
        {
            return CheckAgainstListing(provider, validator, key, limits, fileDirectory);
        }

        return fileDirectory ? CheckPrefixes(provider, key, limits) : null;
    }

    /// <summary>Without folding: an exact object on a proper prefix, or an object under the key.</summary>
    private static string? CheckPrefixes(IBundleProvider provider, BundleKey key, BundleLimits limits)
    {
        for (var depth = 1; depth < key.Segments.Count; depth++)
        {
            var prefix = BundleKey.FromSegments([.. key.Segments.Take(depth)]);
            if (provider.Exists(prefix))
            {
                return PrefixIsObject(key, prefix.ToString());
            }
        }

        foreach (var listing in provider.Enumerate(key, limits))
        {
            return ObjectsUnder(key, listing.RawName); // the first one is enough
        }

        return null;
    }

    /// <summary>With folding: one listing of the bundle answers the three questions; the shallowest conflict is reported.</summary>
    private static string? CheckAgainstListing(IBundleProvider provider, BundleKeyValidator validator, BundleKey key, BundleLimits limits, bool fileDirectory)
    {
        var segments = key.Segments;
        var folded = segments.Select(validator.FoldSegment).ToArray();
        (int Level, int Kind, string Name)? found = null;
        var count = 0;
        foreach (var listing in provider.Enumerate(null, limits))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = listing.RawName;
            var parts = raw.Split('/');
            for (var level = 0; level < parts.Length && level < segments.Count; level++)
            {
                var name = parts[level];
                if (string.Equals(name, segments[level], StringComparison.Ordinal))
                {
                    if (fileDirectory && level == parts.Length - 1 && level < segments.Count - 1)
                    {
                        Keep(ref found, (level, 0, raw)); // an object on a proper prefix of the key
                    }

                    if (fileDirectory && level == segments.Count - 1 && parts.Length > segments.Count)
                    {
                        Keep(ref found, (segments.Count, 2, raw)); // an object under the key
                    }

                    continue;
                }

                if (string.Equals(validator.FoldSegment(name), folded[level], StringComparison.Ordinal))
                {
                    Keep(ref found, (level, 1, string.Join('/', parts.Take(level + 1)))); // a twin
                }

                break;
            }
        }

        return found switch
        {
            null => null,
            { Kind: 0 } f => PrefixIsObject(key, f.Name),
            { Kind: 1 } f => Twin(key, f.Name, string.Join('/', segments.Take(f.Level + 1))),
            { } f => ObjectsUnder(key, f.Name),
        };
    }

    /// <summary>Keeps the conflict that sorts first: shallowest level, then kind, then name (ordinal), so the message does not depend on listing order.</summary>
    private static void Keep(ref (int Level, int Kind, string Name)? found, (int Level, int Kind, string Name) candidate)
    {
        if (found is not { } current
            || candidate.Level < current.Level
            || (candidate.Level == current.Level && candidate.Kind < current.Kind)
            || (candidate.Level == current.Level && candidate.Kind == current.Kind && string.CompareOrdinal(candidate.Name, current.Name) < 0))
        {
            found = candidate;
        }
    }

    private static string PrefixIsObject(BundleKey key, string prefix) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: {DebugQuote.Quote(prefix)} is an object, so it cannot also be a prefix of other keys";

    private static string ObjectsUnder(BundleKey key, string under) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: objects exist under it (such as {DebugQuote.Quote(under)}), so it cannot also be an object";

    private static string Twin(BundleKey key, string existing, string mine) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: {DebugQuote.Quote(existing)} already exists and differs from {DebugQuote.Quote(mine)} only in case or Unicode form";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleTreeCheckTests"`
Expected: `Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18`.

- [ ] **Step 5: Verify and commit**

```bash
for f in src/OKF4net/Providers/BundleTreeCheck.cs tests/OKF4net.Tests/Providers/BundleTreeCheckTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done   # equal numbers on every line
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0 (3 299 tests in the replay, 28 skipped)
git add src/OKF4net/Providers/BundleTreeCheck.cs tests/OKF4net.Tests/Providers/BundleTreeCheckTests.cs
git commit -F - <<'MSG'
feat(providers): the tree check before a creation

BundleTreeCheck.CheckCreation tells whether creating a key absent at the
provider leaves a tree the loader accepts under the same validator: no sibling
of any segment that folds to the same name through FoldSegment, under a
folding policy; no object on a proper prefix and no object under the key,
under FileDirectoryConflict. One listing of the bundle when names fold,
otherwise one existence check per proper prefix and one listing under the
key; the entry bound is counted by the check itself.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

No CHANGELOG entry here: nothing calls the check until Task 4a, whose entry describes the behaviour.

### Task 4a: `BundleConceptWriter` writes through its provider

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (type docs, constructors, `BundleRoot`, the lock key, the capability guard, `WriteConcept` ×2, `AppendToConceptAtomic`, `RecordVerifications` and the new `PrepareStamp`, `ValidateConceptTarget`, `CheckVerificationTargets`, the write primitive, `RunTool`/`Translate`)
- Modify: `src/OKF4net/Bundle.cs` (`ReadChunkBytes`, `ReadText` become `internal`)
- Modify: `tests/OKF4net.Tests/BundleConceptWriterTests.cs` (one expected string, Deviation 2)
- Test: `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs`
- Modify: `CHANGELOG.md`, `README.md`

**Interfaces:**
- Consumes: Task 3 (`BundleTreeCheck.CheckCreation`); Task 1 (`InMemoryBundleProvider(native, id, canWrite)`, `Calls`, `ClearCalls`); Tasks 2a–2c (`FileSystemBundleProvider.Write` refuses a link or uninspectable entry anywhere on the path, the object included, with `BundleAccessRefusedException`; `ParentsResolvedForTest`); lot A (`BundleKeyValidator(native, policy, extra)`, `CheckKey`, `BundleLoadOptions`, `BundleKey.TryParse`, `Bundle.ReadText`, `IBundleProvider.AsReadOnly()`).
- Produces:
  - `public BundleConceptWriter(IBundleProvider provider, Action? onWriteCommitted = null, BundleLoadOptions? options = null)`; the string constructor now builds `new FileSystemBundleProvider(bundleRoot)` (a null root still throws `ArgumentNullException`).
  - `public string BundleRoot` — the filesystem provider's `Root`; `NotSupportedException` over another provider.
  - Every write method (`WriteConcept` ×2, `AppendToConceptAtomic`, `RecordVerifications`) throws `BundleReadOnlyException(provider.Id)` first, with no effect, when `Capabilities.CanWrite` is false; `CheckVerificationTargets` is not guarded.
  - Keys: `{id}.md` checked by `BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators).CheckKey` → `Error: '{id}' cannot be written under the bundle's key policy: {reason}.`; a creation checked by `BundleTreeCheck` → `Error: {its sentence}.`
  - Writes: one `provider.Write` per object — `None` for `WriteConcept`, `IfMatch(version read)` / `MustNotExist` for `AppendToConceptAtomic` and `RecordVerifications` (no retry yet: a conflict is an `Error:`); reads through `Bundle.ReadText` within `options.Limits`.
  - Lock (B11): over `FileSystemBundleProvider` the registry key is its root, exactly as before; otherwise `"provider:" + provider.Id`.
  - Notification (B46, B48): `onWriteCommitted` runs once per committed write, under the lock, once the write operation has returned; if it throws an exception the writer converts, `WriteConcept` and `AppendToConceptAtomic` return `Error: {message}` as before, while `RecordVerifications` keeps that concept in `Records`, goes on with the batch, keeps `Recorded` true when every stamp is on disk and ends `Message` with `; the write notification failed: {message}`; any other exception (`OperationCanceledException` included) propagates, the write committed.
  - Internal: `private string WriteValidatedContentLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed, bool? existedBefore = null)` (`committed` is called as soon as `provider.Write` returns; the primitive never notifies), `private string WriteThenNotify(string subject, Func<Action, string> write)` and `private string? NotifyCommitted(string subject)` (the notification, once the write operation has returned, B48), `ReadCurrent`, `ConditionFor`, `GuardWritable`, `RunTool(Func<string> body, Func<string?>? subject = null)`, `IsExpectedFailure`, `Translate(Exception, string subject)`, `PrepareStamp`/`PreparedStamp`; `BeforeLateReparseCheckForTest` forwards to `ParentsResolvedForTest`.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="BundleConceptWriter"/> over a provider (spec §3, §9; decisions B2, B3, B10, B11,
/// B15, B16): the capability guard at every write entry point, keys validated by the core under
/// the load's policy, the tree check before a creation, the lock keyed by provider, and the
/// provider's refusals turned into the writer's own messages. The in-memory provider records
/// every call, so the tests can pin what the writer asks the storage, in which order.
/// </summary>
public class BundleConceptWriterProviderTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>Every write entry point of the writer, by name (B3).</summary>
    private static readonly Dictionary<string, Action<BundleConceptWriter>> WriteCalls = new()
    {
        ["WriteConcept(yaml)"] = w => w.WriteConcept("metrics/dau", Fm, "body\n"),
        ["WriteConcept(frontmatter)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n"),
        ["AppendToConceptAtomic"] = w => w.AppendToConceptAtomic("metrics/dau", Fm, b => (b ?? string.Empty) + "more\n"),
        ["RecordVerifications"] = w => w.RecordVerifications(["metrics/dau"], "human:ada"),
    };

    /// <summary>The names of <see cref="WriteCalls"/>, for the matrix theories.</summary>
    public static TheoryData<string> WriteEntryPoints => new(WriteCalls.Keys);

    /// <summary>
    /// B3, spec §11 "Lecture seule": on a read-only view of a filesystem bundle, every write
    /// entry point throws <see cref="BundleReadOnlyException"/> — never an <c>Error:</c> string —
    /// and nothing on disk changes: no directory, no temporary, no byte. Even an invalid argument
    /// is not looked at first.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteEntryPoints))]
    public void Every_write_entry_point_on_a_read_only_filesystem_view_throws_and_changes_nothing(string entryPoint)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Doc);
        var before = Tree(tmp.Path);
        var bytes = File.ReadAllBytes(Path.Combine(tmp.Path, "metrics", "dau.md"));
        var writer = new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path).AsReadOnly());

        var ex = Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer));

        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(before, Tree(tmp.Path));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(tmp.Path, "metrics", "dau.md")));
    }

    /// <summary>The same matrix on a read-only in-memory provider: the guard runs before the writer asks the storage anything.</summary>
    [Theory]
    [MemberData(nameof(WriteEntryPoints))]
    public void Every_write_entry_point_on_a_read_only_provider_throws_before_any_call(string entryPoint)
    {
        var storage = new InMemoryBundleProvider().Add("metrics/dau.md", Doc);
        var writer = new BundleConceptWriter(storage);

        Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer));
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void Checking_verification_targets_reads_and_is_allowed_on_a_read_only_provider()
    {
        var storage = new InMemoryBundleProvider().Add("metrics/dau.md", Doc);
        Assert.Null(new BundleConceptWriter(storage).CheckVerificationTargets(["metrics/dau"]));
        Assert.Equal(["exists metrics/dau.md", "read metrics/dau.md"], storage.Calls);
    }

    [Fact]
    public void A_write_lands_in_the_provider_under_the_concepts_key()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var fired = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => fired++);

        var result = writer.WriteConcept("metrics/dau", Fm, "body\n");

        Assert.Equal($"Written metrics/dau (new, {Encoding.UTF8.GetByteCount(Doc)} bytes). Remember to run okf_regenerate_indexes.", result);
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.Equal(1, fired);
        Assert.Equal(["exists metrics/dau.md", "write metrics/dau.md None", "read metrics/dau.md"], storage.Calls);
    }

    [Fact]
    public void A_read_modify_write_is_conditioned_on_what_it_read()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var writer = new BundleConceptWriter(storage);

        Assert.StartsWith("Written metrics/dau (new,", writer.AppendToConceptAtomic("metrics/dau", Fm, b => "first\n"), StringComparison.Ordinal);
        Assert.StartsWith("Written metrics/dau (updated,", writer.AppendToConceptAtomic("metrics/dau", Fm, b => b + "\nsecond\n"), StringComparison.Ordinal);
        Assert.True(writer.RecordVerifications(["metrics/dau"], "human:ada").Recorded);

        Assert.Equal(
            [
                "read metrics/dau.md", "write metrics/dau.md MustNotExist",
                "read metrics/dau.md", "write metrics/dau.md IfMatch",
                "exists metrics/dau.md", "read metrics/dau.md", "read metrics/dau.md", "write metrics/dau.md IfMatch",
            ],
            storage.Calls);
    }

    /// <summary>
    /// B2: the key is checked under the provider's native constraints before any I/O. A Windows
    /// filesystem strips a trailing dot, so <c>a./b</c> used to land in <c>a/b</c> (external
    /// review, 2026-10-09); a provider that declares the rule now refuses it with the validator's
    /// reason, whatever the host OS.
    /// </summary>
    [Fact]
    public void A_concept_id_the_providers_native_rules_refuse_is_refused_before_any_call()
    {
        var storage = new InMemoryBundleProvider(BundleKeyConstraints.TrailingDotOrSpace, canWrite: true);
        var result = new BundleConceptWriter(storage).WriteConcept("a./b", Fm, "body\n");
        Assert.Equal("Error: 'a./b' cannot be written under the bundle's key policy: segment \"a.\" ends with '.' or a space.", result);
        Assert.Empty(storage.Calls);
    }

    [SkippableFact]
    public void On_Windows_a_concept_id_segment_ending_with_a_dot_is_refused_instead_of_aliasing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the native Windows set refuses a trailing dot; elsewhere 'a.' is stored as written");
        using var tmp = new TempDir();
        var result = new BundleConceptWriter(tmp.Path).WriteConcept("a./b", Fm, "body\n");
        Assert.StartsWith("Error: 'a./b' cannot be written under the bundle's key policy:", result, StringComparison.Ordinal);
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void Under_the_strict_policy_a_creation_beside_a_case_twin_is_refused_and_an_update_is_not_checked()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("A/x.md", Doc);
        var writer = new BundleConceptWriter(storage, options: new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Strict });

        var refused = writer.WriteConcept("a/y", Fm, "body\n");

        Assert.Equal("Error: \"a/y.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form.", refused);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));

        storage.ClearCalls();
        Assert.StartsWith("Written A/x (updated,", writer.WriteConcept("A/x", Fm, "new body\n"), StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("enumerate", StringComparison.Ordinal));
    }

    /// <summary>B10 on the filesystem: <c>x.md</c> is a concept, so the concept <c>x.md/y</c> would need it to be a directory too.</summary>
    [Fact]
    public void A_creation_under_an_existing_concept_file_is_refused_and_names_it()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", Doc);
        var before = Tree(tmp.Path);

        var result = new BundleConceptWriter(tmp.Path).WriteConcept("x.md/y", Fm, "body\n");

        Assert.Equal("Error: \"x.md/y.md\" cannot be created: \"x.md\" is an object, so it cannot also be a prefix of other keys.", result);
        Assert.Equal(before, Tree(tmp.Path));
    }

    [Fact]
    public void Reads_are_bounded_by_the_writers_options()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var writer = new BundleConceptWriter(storage, options: new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 10 } });

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, b => b + "more\n");

        Assert.StartsWith("Error: 'metrics/dau.md' is larger than the 10-byte bound", result, StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bundle_root_is_the_filesystem_root_and_has_no_meaning_elsewhere()
    {
        using var tmp = new TempDir();
        Assert.Equal(tmp.Path, new BundleConceptWriter(tmp.Path).BundleRoot);
        Assert.Equal(tmp.Path, new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path)).BundleRoot);
        Assert.Throws<NotSupportedException>(() => new BundleConceptWriter(new InMemoryBundleProvider()).BundleRoot);
    }

    /// <summary>B11: the filesystem keeps its lexical-and-resolved registry, whichever constructor built the writer; any other provider is keyed by its Id.</summary>
    [Fact]
    public void The_lock_is_keyed_by_the_root_on_the_filesystem_and_by_the_id_elsewhere()
    {
        using var tmp = new TempDir();
        Assert.Same(
            new BundleConceptWriter(tmp.Path).CurrentLockObjectForTest(),
            new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path)).CurrentLockObjectForTest());

        var storage = new InMemoryBundleProvider(id: "lot-b-lock");
        Assert.Same(new BundleConceptWriter(storage).CurrentLockObjectForTest(), new BundleConceptWriter(storage).CurrentLockObjectForTest());
        Assert.Same(
            new BundleConceptWriter(storage).CurrentLockObjectForTest(),
            new BundleConceptWriter(new InMemoryBundleProvider(id: "lot-b-lock")).CurrentLockObjectForTest());
        Assert.NotSame(
            new BundleConceptWriter(storage).CurrentLockObjectForTest(),
            new BundleConceptWriter(new InMemoryBundleProvider(id: "lot-b-other")).CurrentLockObjectForTest());
    }

    /// <summary>
    /// The late-guard test hook keeps its meaning over the filesystem provider: it fires inside
    /// the provider's write, once the parent directory exists (created by the write) and before
    /// the object or its temporary does, which is where the writer's own late re-check used to be.
    /// </summary>
    [Fact]
    public void The_late_guard_hook_fires_once_the_parent_exists_and_before_anything_is_written()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var seen = new List<string[]>();
        writer.BeforeLateReparseCheckForTest = () => seen.Add(Tree(tmp.Path));

        Assert.StartsWith("Written new/dir/x (new,", writer.WriteConcept("new/dir/x", Fm, "body\n"), StringComparison.Ordinal);
        Assert.Equal([["new", "new/dir"]], seen);

        writer.BeforeLateReparseCheckForTest = null;
        writer.WriteConcept("new/dir/y", Fm, "body\n");
        Assert.Single(seen);
    }

    /// <summary>What a failing write notification throws, by kind: an I/O error, an <see cref="OkfException"/>, a conflict raised by another operation (external review, F6).</summary>
    private static Exception NotificationFailure(string kind) => kind switch
    {
        "io" => new IOException("notification failed"),
        "okf" => new DocumentParseException("notification failed"),
        _ => new BundleWriteConflictException(K("elsewhere.md")),
    };

    /// <summary>
    /// A write notification (<c>onWriteCommitted</c>) that throws once the provider has committed
    /// (external review, F2, F6): the string methods return what they returned before the
    /// provider, <c>Error: {message}</c>, but the write is on disk, made once — <c>buildBody</c>
    /// ran once, the notification ran once — whatever the writer-converted exception, a conflict
    /// included (B48).
    /// </summary>
    [Theory]
    [InlineData("io")]
    [InlineData("okf")]
    [InlineData("conflict")]
    public void A_notification_failing_after_the_commit_keeps_the_error_text_and_the_single_write(string kind)
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "metrics", "dau.md");
        var failure = NotificationFailure(kind);
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw failure;
        });

        Assert.Equal("Error: " + failure.Message, writer.WriteConcept("metrics/dau", Fm, "body\n"));
        Assert.EndsWith("\nbody\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(1, notifications);

        var builds = 0;
        Assert.Equal("Error: " + failure.Message, writer.AppendToConceptAtomic("metrics/dau", Fm, b =>
        {
            builds++;
            return b + " more";
        }));
        Assert.Equal(1, builds);
        Assert.Equal(2, notifications);
        Assert.EndsWith("\nbody more\n", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same in a verification batch, where a record is kept: a concept whose notification
    /// failed is on disk, so it is in <see cref="VerificationOutcome.Records"/> and the batch goes
    /// on; <see cref="VerificationOutcome.Recorded"/> stays true once every stamp is on disk, and
    /// the message says the notification failed. The reviewer's case (one concept, the
    /// notification always failing) and a batch whose second notification of three fails, for
    /// each kind of converted exception.
    /// </summary>
    [Theory]
    [InlineData(1, 1, "io")]
    [InlineData(3, 2, "io")]
    [InlineData(1, 1, "okf")]
    [InlineData(3, 2, "okf")]
    [InlineData(1, 1, "conflict")]
    [InlineData(3, 2, "conflict")]
    public void A_notification_failing_in_a_verification_batch_leaves_every_stamp_recorded_and_says_so(int concepts, int failing, string kind)
    {
        using var tmp = new TempDir();
        string[] ids = [.. Enumerable.Range(0, concepts).Select(i => $"c{i}")];
        foreach (var id in ids)
        {
            tmp.Write(id + ".md", Doc);
        }

        var failure = NotificationFailure(kind);
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            if (++notifications == failing)
            {
                throw failure;
            }
        });

        var outcome = writer.RecordVerifications(ids, "human:ada", "2026-10-19T00:00:00Z");

        Assert.True(outcome.Recorded);
        Assert.Equal(ids, outcome.Records.Select(r => r.ConceptId));
        Assert.Equal($"Recorded {concepts} verification(s) by human:ada at 2026-10-19T00:00:00Z; the write notification failed: {failure.Message}", outcome.Message);
        Assert.Equal(concepts, notifications);
        Assert.All(ids, id => Assert.Single(OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, id + ".md"))).Frontmatter.Verified));
    }

    /// <summary>
    /// A cancellation the notification raises is not converted (B48): it propagates to the caller
    /// unchanged once the write is on disk, made once — nothing is retried, nothing written again —
    /// and it ends a verification batch there, the concepts after it not attempted.
    /// </summary>
    [Fact]
    public void A_notification_cancelled_after_the_commit_propagates_and_the_write_stays()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Doc);
        tmp.Write("b.md", Doc);
        var file = Path.Combine(tmp.Path, "metrics", "dau.md");
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw new OperationCanceledException("cancelled");
        });

        Assert.Equal("cancelled", Assert.Throws<OperationCanceledException>(() => writer.WriteConcept("metrics/dau", Fm, "body\n")).Message);
        Assert.EndsWith("\nbody\n", File.ReadAllText(file), StringComparison.Ordinal);

        var builds = 0;
        Assert.Throws<OperationCanceledException>(() => writer.AppendToConceptAtomic("metrics/dau", Fm, b =>
        {
            builds++;
            return b + " more";
        }));
        Assert.Equal(1, builds);
        Assert.EndsWith("\nbody more\n", File.ReadAllText(file), StringComparison.Ordinal);

        Assert.Throws<OperationCanceledException>(() => writer.RecordVerifications(["a", "b"], "human:ada", "2026-10-19T00:00:00Z"));
        Assert.Equal(3, notifications);
        Assert.Single(OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "a.md"))).Frontmatter.Verified);
        Assert.Equal(Doc, File.ReadAllText(Path.Combine(tmp.Path, "b.md")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only, each naming what this task adds — `CS1503` (argument 1: cannot convert from `InMemoryBundleProvider`, `FileSystemBundleProvider` or `IBundleProvider` to `string`: the provider constructor does not exist yet) and `CS1739` (no parameter named `options`).

- [ ] **Step 3: Make `Bundle.ReadText` the writer's reader too**

In `src/OKF4net/Bundle.cs`, replace

```csharp
    private const int ReadChunkBytes = 81920;

    /// <summary>
    /// Reads one object as strict UTF-8, counting bytes against the bound (spec §6) whatever the
    /// provider says. <paramref name="chunk"/> is the caller's copy buffer, reused across the
    /// objects of one load. The destination is pre-sized from <see cref="BundleObject.Length"/>
    /// when the provider declares one within the bound — a hint only: the bytes are still
    /// counted and an overrun still throws, whatever the declared length.
    /// </summary>
    private static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, out BundleObjectVersion version)
```

with

```csharp
    internal const int ReadChunkBytes = 81920;

    /// <summary>
    /// Reads one object as strict UTF-8, counting bytes against the bound (spec §6) whatever the
    /// provider says. <paramref name="chunk"/> is the caller's copy buffer, reused across the
    /// objects of one load. The destination is pre-sized from <see cref="BundleObject.Length"/>
    /// when the provider declares one within the bound — a hint only: the bytes are still
    /// counted and an overrun still throws, whatever the declared length. The one bounded reader
    /// of the core: <see cref="BundleConceptWriter"/> reads the objects it rewrites through it.
    /// </summary>
    internal static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, out BundleObjectVersion version)
```

- [ ] **Step 4: Put the writer on the provider** — every edit in `src/OKF4net/BundleConceptWriter.cs`, with `Edit` (the file stays CRLF). Each "replace" names the old text exactly as it stands; the blocks are long because the members are, and each must match once.

(a) The usings:

```csharp
using System.Collections.Concurrent;
using OKF4net.Internal;
using OKF4net.Yaml;
```

becomes

```csharp
using System.Collections.Concurrent;
using OKF4net.Internal;
using OKF4net.Providers;
using OKF4net.Yaml;
```

(b) In `VerificationOutcome`'s summary, the paragraph on the scope of `Records` (the old one described an in-place write that no longer exists), through the `Recorded` and `Message` parameter lines (a write notification that fails is not a failed write, B46):

```csharp
/// <b>The precise scope of that list.</b> An entry means "this file's write
/// call completed", which is stronger than "was validated" (the reason
/// <c>Records</c> is built in the write loop, not the prepare loop) and
/// weaker than "the file on disk is now exactly one of these two versions".
/// The underlying primitive is <see cref="File.WriteAllText(string, string?, System.Text.Encoding)"/>,
/// which truncates and writes IN PLACE, so a failure part-way through one
/// file — a full disk, a device error — can leave that file truncated or
/// half-written while <c>Records</c> omits it, having never returned. That is
/// a property of the write primitive shared by EVERY write path in this class,
/// not something verification introduced; closing it needs write-then-rename,
/// which has to be designed against the reparse-point guard and the bundle
/// lock it sits between (issue #157). Until then, the honest reading of a
/// failed batch is: the concepts listed are stamped, the ones after them were
/// not written, and the one it stopped on is unknown — re-run it, or check
/// that file.
/// </summary>
/// <param name="Recorded">Whether the whole batch was written.</param>
/// <param name="Message">A confirmation, or what went wrong and how far it got.</param>
```

becomes

```csharp
/// <b>The precise scope of that list.</b> An entry means "this concept's write
/// was committed", which is stronger than "was validated" (the reason
/// <c>Records</c> is built in the write loop, not the prepare loop). Each
/// write is atomic per object (<see cref="Providers.IBundleProvider.Write"/>,
/// issue #157): the concept the batch stopped on holds its whole previous
/// content, unless the provider reported an outcome it could not establish
/// (<see cref="Providers.BundleWriteOutcomeUnknownException"/>), which the
/// message then says. The honest reading of a failed batch is: the concepts
/// listed are stamped, the one it stopped on is named in the message, and the
/// ones after it were not attempted — re-run the batch, which is safe: a stamp
/// already there is replaced, never duplicated.
///
/// <b>A write notification that fails</b> (the <c>onWriteCommitted</c> callback
/// throwing an exception the writer converts: an I/O error, an <see cref="OkfException"/>,
/// a conflict raised by another operation included) is not a failed write: the
/// concept is on disk, so it is in <see cref="Records"/>, and the batch goes on.
/// <see cref="Recorded"/> stays true when every stamp is on disk; <see cref="Message"/>
/// is then the confirmation followed by <c>; the write notification failed: …</c>.
/// An exception the writer does not convert (an <see cref="OperationCanceledException"/>,
/// for one) propagates once that concept is on disk, and the batch ends there.
/// </summary>
/// <param name="Recorded">Whether every stamp of the batch is on disk.</param>
/// <param name="Message">A confirmation (saying so when a write notification failed), or what went wrong and how far it got.</param>
```

(c) `VerificationTargetProblemKind.InvalidId`:

```csharp
    /// <summary><see cref="ConceptId.TryParse(string, out ConceptId?)"/> rejects it, it resolves outside the
    /// bundle root, it is the reserved <c>index</c>/<c>log</c> name, or it resolves through/to a reparse
    /// point -- see <see cref="BundleConceptWriter"/>'s private <c>ValidateConceptTarget</c>.</summary>
    InvalidId,
```

becomes

```csharp
    /// <summary><see cref="ConceptId.TryParse(string, out ConceptId?)"/> rejects it, it is the reserved
    /// <c>index</c>/<c>log</c> name, the writer's key policy refuses its key -- see
    /// <see cref="BundleConceptWriter"/>'s private <c>ValidateConceptTarget</c> -- or the provider refuses
    /// its path (a reparse point, an entry that could not be inspected).</summary>
    InvalidId,
```

(d) In `VerificationTargetProblem`'s `Detail` parameter doc, the line

```csharp
/// private <c>ValidateConceptTarget</c>'s own message for <see cref="VerificationTargetProblemKind.InvalidId"/>, or the underlying
```

becomes

```csharp
/// private <c>ValidateConceptTarget</c>'s own message, or its message for a path the provider refuses, for <see cref="VerificationTargetProblemKind.InvalidId"/>, or the underlying
```

(e) The class summary, and the lock-key prefix right after the opening brace:

```csharp
/// The core, thread-safe write primitive for OKF bundles: producer-validated,
/// reparse-guarded, atomically-serialized create/update of a concept and an
/// atomic read-modify-write append-to-concept, over a single bundle root.
/// Promoted verbatim from <c>OKF4net.Agents.OkfBundleTools</c> so both that
/// type and <c>OKF4net.Catalog.FileMemoryStore</c> share one write path and one
/// process-wide per-bundle lock registry (no duplicate lock registry, no divergent
/// second write path). Never throws for an expected error — I/O, YAML,
/// validation, and reparse-point rejections are returned as an
/// <c>Error: ...</c> result string.
/// </summary>
public sealed class BundleConceptWriter
{
    /// <summary>
    /// One monitor per resolved bundle root, shared across writers and aliases.
```

becomes

```csharp
/// The core, thread-safe write primitive for OKF bundles: producer-validated,
/// serialized create/update of a concept and an atomic read-modify-write
/// append-to-concept, over one bundle provider (spec §3, §4, §9). Every byte it
/// writes goes through <see cref="IBundleProvider.Write"/>, which commits each
/// object atomically under its own path guards; the writer validates every key
/// under the same policy as a load (spec §2), refuses a creation that would make
/// the tree invalid, and checks <see cref="BundleProviderCapabilities.CanWrite"/>
/// before anything else. Both <c>OKF4net.Agents.OkfBundleTools</c> and
/// <c>OKF4net.Catalog.FileMemoryStore</c> share this one write path and one
/// process-wide per-bundle lock registry (no duplicate lock registry, no divergent
/// second write path). Never throws for an expected error — I/O, YAML,
/// validation, provider refusals and conflicts are returned as an
/// <c>Error: ...</c> result string; a provider that cannot write is a host
/// mistake and throws <see cref="BundleReadOnlyException"/>.
/// </summary>
public sealed class BundleConceptWriter
{
    /// <summary>
    /// The prefix of the lock key of a writer over any provider but the filesystem one
    /// (decision B11): the key is this prefix and <see cref="IBundleProvider.Id"/>, which no
    /// canonical root path can equal (those are absolute).
    /// </summary>
    private const string ProviderLockPrefix = "provider:";

    /// <summary>
    /// One monitor per resolved bundle root, shared across writers and aliases.
```

(f) The `_lexicalRoot` field and the new fields:

```csharp
    /// <see cref="BundleRoot"/> through <see cref="ReparsePoints.CanonicalizeRoot"/>,
    /// with Windows namespace forms normalized, computed once: the key of
    /// <see cref="t_heldLocks"/>, and the starting
    /// point <see cref="ReparsePoints.ResolveLockKey"/> resolves from.
    /// </summary>
    private readonly string _lexicalRoot;

    private readonly Action? _onWriteCommitted;
```

becomes

```csharp
    /// Over the filesystem provider, <see cref="BundleRoot"/> through
    /// <see cref="ReparsePoints.CanonicalizeRoot"/>, with Windows namespace forms
    /// normalized, computed once: the key of <see cref="t_heldLocks"/>, and the
    /// starting point <see cref="ReparsePoints.ResolveLockKey"/> resolves from.
    /// Over any other provider, <see cref="ProviderLockPrefix"/> and its
    /// <see cref="IBundleProvider.Id"/>, used as it is (nothing to resolve).
    /// </summary>
    private readonly string _lexicalRoot;

    private readonly Action? _onWriteCommitted;

    /// <summary>Where every byte is read and written.</summary>
    private readonly IBundleProvider _provider;

    /// <summary><see cref="_provider"/> when it is the filesystem provider: its root keys the lock registry and backs <see cref="BundleRoot"/>, and its test hooks back the writer's.</summary>
    private readonly FileSystemBundleProvider? _fileSystem;

    /// <summary>The key policy every write is checked against: the provider's native constraints under the options' policy, exactly as a load builds it (spec §2, decision B2).</summary>
    private readonly BundleKeyValidator _validator;

    /// <summary>The bounds of every read and listing the writer makes (spec §6).</summary>
    private readonly BundleLimits _limits;

    private Action? _beforeLateReparseCheckForTest;
```

(g) The constructor and `BundleRoot`:

```csharp
    /// <summary>Creates a writer rooted at <paramref name="bundleRoot"/>.</summary>
    /// <param name="bundleRoot">The bundle's root directory.</param>
    /// <param name="onWriteCommitted">
    /// Optional callback invoked inside the write lock immediately after a
    /// successful file write, before the lock is released — the seam
    /// <c>OkfBundleTools</c> uses to invalidate its bundle cache atomically with
    /// the write. <see langword="null"/> (the default) for callers with no cache.
    /// </param>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null)
    {
        ArgumentNullException.ThrowIfNull(bundleRoot);
        BundleRoot = bundleRoot;
        _onWriteCommitted = onWriteCommitted;

        // Only the LEXICAL form is computed here (and an invalid path still
        // throws here, as before). The write lock's registry key is NOT: it
        // is resolved through reparse points each time a thread takes the
        // lock (EnterWriteLock), because the topology can change after
        // construction -- a writer built over `later/bundle` while `later` did
        // not exist must still meet a writer over the junction target once
        // `later` becomes a junction (#86).
        _lexicalRoot = ReparsePoints.NormalizeNamespaceForLockKey(ReparsePoints.CanonicalizeRoot(bundleRoot));
    }

    /// <summary>The bundle root, as passed to the constructor.</summary>
    public string BundleRoot { get; }
```

becomes

```csharp
    /// <summary>Creates a writer over the directory <paramref name="bundleRoot"/>, through a <see cref="FileSystemBundleProvider"/> built over it.</summary>
    /// <param name="bundleRoot">The bundle's root directory.</param>
    /// <param name="onWriteCommitted">
    /// Optional callback invoked inside the write lock once after each
    /// successful write, before the lock is released — the seam
    /// <c>OkfBundleTools</c> uses to invalidate its bundle cache atomically with
    /// the write. <see langword="null"/> (the default) for callers with no cache.
    /// It runs once the write is final, after any retry, and never causes one:
    /// an exception it throws that the writer converts (an I/O error, an
    /// <see cref="OkfException"/>, a provider conflict included) is reported as
    /// each write method says, the write committed; any other (an
    /// <see cref="OperationCanceledException"/>, for one) propagates to the
    /// caller unchanged, the write committed.
    /// </param>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null)
        : this(new FileSystemBundleProvider(bundleRoot ?? throw new ArgumentNullException(nameof(bundleRoot))), onWriteCommitted)
    {
    }

    /// <summary>Creates a writer over <paramref name="provider"/> (spec §9).</summary>
    /// <param name="provider">Where the bundle is read and written. A provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> makes every write method throw <see cref="BundleReadOnlyException"/>.</param>
    /// <param name="onWriteCommitted">As for <see cref="BundleConceptWriter(string, Action?)"/>.</param>
    /// <param name="options">The key policy, extra key validators and bounds the writer applies — the same as a load of this bundle should use, so that the writer never creates a key the load would discard. <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    public BundleConceptWriter(IBundleProvider provider, Action? onWriteCommitted = null, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _fileSystem = provider as FileSystemBundleProvider;
        _onWriteCommitted = onWriteCommitted;
        options ??= BundleLoadOptions.Default;
        _validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);
        _limits = options.Limits;

        // Only the LEXICAL form is computed here (and an invalid path still
        // throws here, as before). The write lock's registry key is NOT: it
        // is resolved through reparse points each time a thread takes the
        // lock (EnterWriteLock), because the topology can change after
        // construction -- a writer built over `later/bundle` while `later` did
        // not exist must still meet a writer over the junction target once
        // `later` becomes a junction (#86). Any other provider is keyed by its
        // Id, which nothing resolves (decision B11).
        _lexicalRoot = _fileSystem is not null
            ? ReparsePoints.NormalizeNamespaceForLockKey(ReparsePoints.CanonicalizeRoot(_fileSystem.Root))
            : ProviderLockPrefix + provider.Id;
    }

    /// <summary>The bundle root, as passed to the constructor (the filesystem provider's <see cref="FileSystemBundleProvider.Root"/>).</summary>
    /// <exception cref="NotSupportedException">The writer was built over another provider: a path has no meaning for it (spec §9).</exception>
    public string BundleRoot => _fileSystem?.Root
        ?? throw new NotSupportedException("this writer is not over a filesystem provider, so it has no bundle root");
```

(h) `ResolveLockObject`:

```csharp
    private object ResolveLockObject() =>
        BundleLocks.GetOrAdd(ReparsePoints.ResolveLockKey(_lexicalRoot), static _ => new object());
```

becomes

```csharp
    private object ResolveLockObject() =>
        BundleLocks.GetOrAdd(_fileSystem is not null ? ReparsePoints.ResolveLockKey(_lexicalRoot) : _lexicalRoot, static _ => new object());
```

(i) The late-guard test hook, now forwarded to the provider (`ParentsResolvedForTest`, Task 2b):

```csharp
    /// reparse-point re-check in <see cref="WriteValidatedContentLocked"/>
    /// (after its own <see cref="Directory.CreateDirectory(string)"/> call).
    /// Lets a test deterministically simulate a filesystem substitution
    /// racing the final write -- e.g. deleting the just-created parent
    /// directory and replacing it with a junction to an external directory --
    /// at exactly the point such a race would need to land, instead of
    /// relying on real (flaky, unreliable) thread timing.
    /// <see langword="internal"/>, always <see langword="null"/> outside
    /// tests, so it has zero effect on production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest { get; set; }
```

becomes

```csharp
    /// reparse-point re-check of a write. Over a <see cref="FileSystemBundleProvider"/>
    /// it is that provider's <c>ParentsResolvedForTest</c>: it fires inside
    /// <see cref="IBundleProvider.Write"/> once the object's parent directories
    /// exist (found, or just created), before the provider's STRICT re-check of
    /// the whole path and before its temporary is created — exactly where the
    /// writer's own <see cref="Directory.CreateDirectory(string)"/> and late
    /// re-check sat before the write moved behind the provider. Lets a test
    /// deterministically simulate a filesystem substitution racing the final
    /// write -- e.g. deleting the just-created parent directory and replacing
    /// it with a junction to an external directory -- at exactly the point
    /// such a race would need to land, instead of relying on real (flaky,
    /// unreliable) thread timing. Over any other provider it has no effect
    /// (script that provider instead). <see langword="internal"/>, always
    /// <see langword="null"/> outside tests, so it has zero effect on
    /// production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest
    {
        get => _beforeLateReparseCheckForTest;
        set
        {
            _beforeLateReparseCheckForTest = value;
            if (_fileSystem is not null)
            {
                _fileSystem.ParentsResolvedForTest = value is null ? null : _ => value();
            }
        }
    }
```

(j) `WriteConcept(string, string, string)` — its declaration and body (its summary is unchanged):

```csharp
    public string WriteConcept(string conceptId, string frontmatterYaml, string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
            if (buildError is not null)
            {
                return buildError;
            }

            // Serialized under the bundle's write lock (shared with
            // AppendToConceptAtomic and, since EnterWriteLock takes it from
            // the process-wide BundleLocks registry under the root's resolved
            // key, with every OTHER BundleConceptWriter instance whose root
            // resolves to this same directory, not just this instance) so
            // concurrent writers can't interleave an
            // existence check with another writer's write, and so the
            // committed callback below is atomic with the write it follows.
            using (EnterWriteLock())
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }
```

becomes

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
                if (buildError is not null)
                {
                    return buildError;
                }

                // Serialized under the bundle's write lock (shared with
                // AppendToConceptAtomic and, since EnterWriteLock takes it from
                // the process-wide BundleLocks registry under the root's resolved
                // key, with every OTHER BundleConceptWriter instance whose root
                // resolves to this same directory, not just this instance) so
                // concurrent writers can't interleave an
                // existence check with another writer's write, and so the
                // committed callback below is atomic with the write it follows.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteValidatedContentLocked(target, content!, BundleWriteCondition.None, committed));
                }
            },
            () => subject);
    }
```

(k) `WriteConcept(string, Frontmatter, string)`:

```csharp
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            var mapping = ShallowCopy(frontmatter.AsMapping());
            MaybeStampGenerated(mapping);

            var (content, buildError) = BuildValidatedContent(mapping, body);
            if (buildError is not null)
            {
                return buildError;
            }

            using (EnterWriteLock())
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }
```

becomes

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var mapping = ShallowCopy(frontmatter.AsMapping());
                MaybeStampGenerated(mapping);

                var (content, buildError) = BuildValidatedContent(mapping, body);
                if (buildError is not null)
                {
                    return buildError;
                }

                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteValidatedContentLocked(target, content!, BundleWriteCondition.None, committed));
                }
            },
            () => subject);
    }
```

(l) `AppendToConceptAtomic` — reads through the provider, writes conditioned on what it read:

```csharp
    public string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            // The read of the current body, the caller's transform, and the
            // validated write all happen inside this ONE lock acquisition —
            // never released and reacquired in between — which is what makes
            // the whole read-modify-write atomic against a concurrent
            // WriteConcept/AppendToConceptAtomic call for the same concept.
            using (EnterWriteLock())
            {
                string frontmatterYaml;
                string? currentBody;
                var existedBefore = File.Exists(target.TargetPath);
                if (existedBefore)
                {
                    // Strict UTF-8: a non-UTF-8 concept file throws
                    // DecoderFallbackException (caught by RunTool below)
                    // rather than being silently re-decoded and rewritten.
                    var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                    // Fail-closed: if the existing concept has malformed
                    // frontmatter (hand-edited, or a prior partial/crashed
                    // write), OkfDocument.Parse throws DocumentParseException
                    // (caught by RunTool -> Error text) so this append is
                    // dropped rather than overwriting a possibly important
                    // file. This is stricter than a permissive bundle-load
                    // path, which would treat an unparseable file as absent
                    // and silently recreate it.
                    var existingDoc = OkfDocument.Parse(text);
                    frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                    currentBody = existingDoc.Body;
                }
                else
                {
                    frontmatterYaml = frontmatterYamlIfCreating;
                    currentBody = null;
                }

                var newBody = buildBody(currentBody);
                if (newBody is null)
                {
                    return "Error: body must not be null.";
                }

                if (newBody.Contains('\0'))
                {
                    return "Error: invalid body — it must not contain a null character.";
                }

                var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                if (buildError is not null)
                {
                    return buildError;
                }

                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!, existedBefore);
            }
        });
    }
```

becomes

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";

                // The read of the current body, the caller's transform, and the
                // validated write all happen inside this ONE lock acquisition —
                // never released and reacquired in between — which is what makes
                // the whole read-modify-write atomic against a concurrent
                // WriteConcept/AppendToConceptAtomic call for the same concept.
                // The write is conditioned on the version read, so a writer
                // outside this lock (another process) cannot be overwritten.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed =>
                    {
                        string frontmatterYaml;
                        string? currentBody;

                        // Strict UTF-8: a non-UTF-8 concept file throws
                        // DecoderFallbackException (caught by RunTool below)
                        // rather than being silently re-decoded and rewritten.
                        var current = ReadCurrent(target.Key);
                        if (current is { } existing)
                        {
                            // Fail-closed: if the existing concept has malformed
                            // frontmatter (hand-edited, or a prior partial/crashed
                            // write), OkfDocument.Parse throws DocumentParseException
                            // (caught by RunTool -> Error text) so this append is
                            // dropped rather than overwriting a possibly important
                            // file. This is stricter than a permissive bundle-load
                            // path, which would treat an unparseable file as absent
                            // and silently recreate it.
                            var existingDoc = OkfDocument.Parse(existing.Text);
                            frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                            currentBody = existingDoc.Body;
                        }
                        else
                        {
                            frontmatterYaml = frontmatterYamlIfCreating;
                            currentBody = null;
                        }

                        var newBody = buildBody(currentBody);
                        if (newBody is null)
                        {
                            return "Error: body must not be null.";
                        }

                        if (newBody.Contains('\0'))
                        {
                            return "Error: invalid body — it must not contain a null character.";
                        }

                        var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                        if (buildError is not null)
                        {
                            return buildError;
                        }

                        return WriteValidatedContentLocked(target, content!, ConditionFor(current), committed, existedBefore: current is not null);
                    });
                }
            },
            () => subject);
    }
```

(m) `RecordVerifications`, in five edits. Its summary's paragraph on transactions:

```csharp
    /// It is NOT a transaction. Writing several files cannot be atomic in
    /// .NET, so a failure during the write phase (I/O, permissions, a reparse
    /// point appearing after the late re-check) leaves the concepts already
    /// written stamped. That case reports <c>Recorded = false</c> with
    /// <c>Records</c> listing the concepts whose write returned — which is not
    /// quite the same as "the file on disk is intact", since the write
    /// primitive truncates in place; <see cref="VerificationOutcome"/> states
    /// exactly what the list does and does not promise.
    /// The lock is also in-process, so an external actor mutating the bundle
    /// mid-batch is not stopped: the same documented limit as this class's
    /// reparse-point guard.
```

becomes

```csharp
    /// It is NOT a transaction (spec §4: no multi-object transaction). Each
    /// concept is written atomically, conditioned on the version it was
    /// prepared from, but a failure during the write phase (I/O, permissions,
    /// a path the provider refuses at its late re-check, a conflict with a
    /// writer outside this lock) leaves the concepts already written stamped.
    /// That case reports <c>Recorded = false</c> with <c>Records</c> listing
    /// the concepts whose write was committed, and a message ending
    /// <c>— already written: …</c>; <see cref="VerificationOutcome"/> states
    /// exactly what the list does and does not promise.
    /// The lock is also in-process, so an external actor mutating the bundle
    /// mid-batch is not stopped: the version condition refuses to overwrite
    /// its change rather than losing it.
```

its declaration and first check:

```csharp
    public VerificationOutcome RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null)
    {
        if (conceptIds is null || conceptIds.Count == 0)
        {
            return Failed("Error: no concept id given.");
```

becomes

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public VerificationOutcome RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null)
    {
        GuardWritable();
        if (conceptIds is null || conceptIds.Count == 0)
        {
            return Failed("Error: no concept id given.");
```

the start of its `RunTool` call:

```csharp
        var message = RunTool(() =>
```

becomes

```csharp
        string? subject = null;
        var message = RunTool(() =>
```

the prepare and write loops, through the end of the `RunTool` call — everything from the line `                var prepared = new List<(ConceptTarget Target, string Content, string ConceptId, string? ReplacedAt)>(targets.Count);` through the line `        });` that closes `RunTool` (about 140 lines: the per-concept body moves, verbatim apart from its indentation and `conceptIds[i]` → `conceptId`, into `PrepareStamp` below) — becomes

```csharp
                var prepared = new List<PreparedStamp>(targets.Count);
                for (var i = 0; i < targets.Count; i++)
                {
                    subject = $"'{targets[i].Id}'";
                    var prepareError = PrepareStamp(targets[i], conceptIds[i], by, stampedAt, out var stamp);
                    if (prepareError is not null)
                    {
                        return prepareError;
                    }

                    prepared.Add(stamp);
                }

                // Writing N objects cannot be atomic: the provider commits each
                // one atomically, but a failure here — I/O, permissions, a
                // refusal of the provider's late path guard, a conflict with a
                // writer outside this lock — leaves the earlier concepts
                // stamped. `records` is built HERE, one entry per committed
                // write, deliberately NOT in the prepare loop above: that is
                // what makes it mean "landed", not "was validated". A
                // batch rejected during PREPARE never reaches this loop, so
                // `records` stays empty; a batch that fails partway through
                // WRITE leaves `records` holding exactly the prefix that
                // actually landed — no separate trim/rollback step
                // to keep in sync, and no way for a future early return in
                // this loop to under- or over-report what landed.
                string? notificationFailure = null;
                for (var i = 0; i < prepared.Count; i++)
                {
                    var stamp = prepared[i];
                    subject = $"'{stamp.Target.Id}'";
                    var committed = false;
                    string writeResult;
                    try
                    {
                        writeResult = WriteValidatedContentLocked(stamp.Target, stamp.Content, BundleWriteCondition.IfMatch(stamp.Version), () => committed = true, existedBefore: true);
                    }
                    catch (Exception ex) when (IsExpectedFailure(ex))
                    {
                        writeResult = Translate(ex, subject);
                    }

                    if (committed)
                    {
                        // On disk: recorded, and the batch goes on. The notification runs
                        // now that the write is final, outside the catch above (B48), so
                        // nothing it throws is taken for a failure of the write; a failed
                        // one is said once the batch ends.
                        records.Add(new VerificationRecord(stamp.ConceptId, stampedAt, stamp.ReplacedAt));
                        if (NotifyCommitted(subject) is { } notificationError)
                        {
                            notificationFailure ??= notificationError["Error: ".Length..];
                        }

                        continue;
                    }

                    return records.Count == 0
                        ? writeResult
                        : $"{writeResult} — already written: {string.Join(", ", records.Select(r => r.ConceptId))}";
                }

                return notificationFailure is null
                    ? $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}."
                    : $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}; the write notification failed: {notificationFailure}";
            }
        }, () => subject);
```

and, right after `RecordVerifications`' closing brace (the `    }` that follows `        static VerificationOutcome Failed(string message) => new(false, message, []);`), insert a blank line and:

```csharp
    /// <summary>A concept <see cref="PrepareStamp"/> read, stamped and checked, ready to be written: its new content and the version that content was built from.</summary>
    private readonly record struct PreparedStamp(ConceptTarget Target, string ConceptId, string Content, BundleObjectVersion Version, string? ReplacedAt);

    /// <summary>
    /// Reads <paramref name="target"/>'s current text through the provider, upserts
    /// <paramref name="by"/>'s stamp into its <c>verified</c> block in place and checks the
    /// result, exactly as <see cref="RecordVerifications"/> describes: <see langword="null"/> and
    /// <paramref name="prepared"/>, or the <c>Error: ...</c> refusal. Writes nothing. CALLER
    /// MUST hold the write lock.
    /// </summary>
    private string? PrepareStamp(ConceptTarget target, string conceptId, string by, string stampedAt, out PreparedStamp prepared)
    {
        prepared = default;
        if (ReadCurrent(target.Key) is not { } current)
        {
            return $"Error: concept {DebugQuote.Quote(conceptId)} does not exist.";
        }

        var text = current.Text;
        var document = OkfDocument.Parse(text);
        var map = document.Frontmatter.AsMapping();

        var upserted = UpsertStamp(map.Get("verified"), by, stampedAt, out var replacedAt);

        // LOAD-BEARING, not just "kept for replacedAt" (replacedAt
        // is UpsertStamp's own out-parameter, unaffected by this
        // call): `map` is the LIVE mapping `document.Frontmatter`
        // wraps (Frontmatter.AsMapping returns the instance
        // itself, no defensive copy -- see its own doc comment),
        // so this mutates `document` in place into exactly the
        // structure the edit below is SUPPOSED to produce. The
        // equality check further down compares the edit's actual
        // re-parsed result against THIS object -- without this
        // call, `document` would still hold the OLD `verified`
        // value and the equality check would fail on every
        // legitimate stamp.
        map.Insert("verified", upserted);

        // Surgical, not a re-emit: only the `verified:` block is
        // touched in the RAW text, so every other frontmatter key
        // -- comments, CRLF endings, folded/flow scalar spellings,
        // key order -- survives byte for byte. This is what
        // RecordVerifications's own doc comment already promises
        // ("preserving every other frontmatter key and the body")
        // and a full-frontmatter re-emit through YamlEmitter did
        // not deliver: YamlEmitter normalizes CRLF to LF, drops
        // comments, and reflows folded/flow scalars (see
        // FrontmatterBlockEdit's own doc comment). Deliberately
        // does NOT go through MaybeStampGenerated: this path
        // stamps `verified` only, never `generated`.
        var one = new YamlMapping();
        one.Insert("verified", upserted);
        var stampBlock = YamlEmitter.Emit(one);
        var content = FrontmatterBlockEdit.ReplaceTopLevelKey(text, "verified", stampBlock);

        // The edit is textual (FrontmatterBlockEdit does not
        // itself validate YAML), so re-parsing and checking FULL
        // STRUCTURAL EQUALITY against `document` -- which already
        // holds the exact intended result, per the load-bearing
        // `map.Insert` above -- is the proof that nothing else
        // moved: same frontmatter (key set, order, and every
        // OTHER value) and the same body, ordinally
        // (OkfDocument.Equals). A weaker check (e.g. only
        // counting `verified` entries) can pass on a genuinely
        // corrupted edit -- a mis-located key spelling that
        // inserted a shadowed duplicate elsewhere still re-parses
        // to the same COUNT while leaving a stale value in the
        // file (finding #C7-1). ValidateConformance (§11) runs
        // first since equality alone would not refuse a document
        // that was already missing `type` before this edit ran.
        var reparsed = OkfDocument.Parse(content);

        // Attributed here, at the SAME point this check has
        // always run (deliberately not moved earlier -- see the
        // early CheckVerificationTargets call above, which
        // excludes NotConformant for exactly this reason): a
        // bare DocumentValidationException naming no concept at
        // all is what CmdVerify and okf_verify used to work
        // around with a pre-loop of their own.
        try
        {
            reparsed.ValidateConformance();
        }
        catch (DocumentValidationException)
        {
            return FormatVerificationTargetProblem(
                new VerificationTargetProblem(VerificationTargetProblemKind.NotConformant, conceptId));
        }

        if (!reparsed.Equals(document))
        {
            // Diagnosable, not just "no": a bare "could not be
            // edited in place" gives a reader no way to tell a
            // real corruption apart from the one KNOWN false
            // positive this check has -- a NaN float anywhere in
            // the frontmatter never compares equal to itself
            // (YamlValue's float equality is IEEE-754 `==`), so a
            // concept with, say, `threshold: .nan` fails this
            // check on every verify, even a perfectly clean edit.
            // Not fixed here (changing YamlValue's equality is out
            // of scope for this method); named explicitly instead,
            // alongside the other diagnosable cases below. The
            // body case is checked first since this edit never
            // touches the body on purpose, so a body difference
            // is worth calling out distinctly from any
            // frontmatter-shaped explanation.
            var detail = !string.Equals(reparsed.Body, document.Body, StringComparison.Ordinal)
                ? "the body changed, which this edit never does on purpose"
                : ContainsNaN(document.Frontmatter.AsMapping())
                    ? "the frontmatter contains a NaN value, which never compares equal to "
                        + "itself, so this check cannot confirm the edit either way -- inspect "
                        + "the file by hand"
                    : DescribeFrontmatterDivergence(document.Frontmatter.AsMapping(), reparsed.Frontmatter.AsMapping());
            return $"Error: concept {DebugQuote.Quote(conceptId)}: the verified block could not be edited in place ({detail}).";
        }

        prepared = new PreparedStamp(target, conceptId, content, current.Version, replacedAt);
        return null;
    }
```

(n) `ConceptTarget` and `ValidateConceptTarget` — no more paths: a key, checked under the policy:

```csharp
    /// <summary>A validated concept id and the absolute path it resolves to, produced by <see cref="ValidateConceptTarget"/>.</summary>
    private readonly record struct ConceptTarget(ConceptId Id, string TargetPath);

    /// <summary>
    /// Validates <paramref name="conceptId"/> (parseable, not the reserved
    /// <c>index</c>/<c>log</c> name) and the filesystem path it resolves to
    /// (within the bundle root; no reparse point, nor any entry whose link
    /// status cannot be inspected, among its parent directories or at the
    /// target itself) — shared by <see cref="WriteConcept(string, string, string)"/>
    /// and <see cref="AppendToConceptAtomic"/> so the two can never diverge
    /// on what counts as a valid write target. Pure: performs no I/O beyond
    /// the reparse-point/existence checks themselves, and does not take the
    /// write lock (<see cref="EnterWriteLock"/>).
    /// </summary>
    /// <remarks>
    /// <b>Scope of the reparse-point guarantee (read this before assuming
    /// "no reparse point" holds for the whole write, not just this check):</b>
    /// this method is a point-in-time check, not an ongoing guarantee — it
    /// rejects a reparse point that is PRESENT AT THE MOMENT THIS METHOD
    /// RUNS. The actual write happens later, in <see cref="WriteValidatedContentLocked"/>
    /// (after YAML parsing and producer validation in between), so this is a
    /// classic check-then-write (TOCTOU): a concurrent local actor able to
    /// replace a path component with a symlink/junction between this check
    /// and that later write is not stopped by this method, and the write lock
    /// (<see cref="EnterWriteLock"/>) this class otherwise relies on for atomicity is a C# in-process lock —
    /// it has no effect on what a separate, unsynchronized filesystem
    /// mutation can do to the same paths. <see cref="WriteValidatedContentLocked"/>
    /// re-runs the same two checks immediately before its actual
    /// <see cref="File.WriteAllText(string, string)"/> call, still inside the
    /// same lock hold, which narrows this window considerably but — since
    /// .NET has no portable "open/write only if not a symlink, atomically" —
    /// cannot close it completely; a substitution racing that final,
    /// immediately-preceding check is a residual, documented limitation, not
    /// something this design claims to eliminate. The threat model this
    /// guard IS effective against: an actor who does not already have write
    /// access to the bundle tree planting a reparse point ahead of time (or
    /// a stale one left over from a previous, unrelated operation) — since an
    /// actor who DOES already have concurrent write access to the bundle
    /// tree could corrupt its content directly and has no need to race this
    /// check at all.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> and a populated <paramref name="target"/> on
    /// success; otherwise the <c>Error: ...</c> message to return to the
    /// caller (and <paramref name="target"/> is <see langword="default"/>).
    /// </returns>
    private string? ValidateConceptTarget(string conceptId, out ConceptTarget target)
    {
        target = default;

        if (!ConceptId.TryParse(conceptId, out var id))
        {
            return $"Error: invalid concept id {DebugQuote.Quote(conceptId)}. Concept ids are '/'-separated "
                + "segments matching [A-Za-z0-9_][A-Za-z0-9_.-]*.";
        }

        if (string.Equals(id.Name, "index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id.Name, "log", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: '{id}' is a reserved concept id — the last segment must not be "
                + "'index' or 'log' in any casing (these would collide with the bundle's "
                + "index.md/log.md files on case-insensitive filesystems).";
        }

        var targetPath = id.ToPath(BundleRoot);

        // Belt and braces alongside ConceptId's own segment validation
        // (which already forbids '..' and '/' inside a segment): the same
        // defense-in-depth check the reparse helpers below use before
        // touching disk.
        if (!ReparsePoints.IsWithinBundleRoot(BundleRoot, targetPath))
        {
            return $"Error: '{id}' resolves outside the bundle root.";
        }

        // Reject a reparse point (symlink/junction) anywhere between the
        // bundle root and the target's parent directory: the lexical check
        // above would happily accept "tables/refunds" even if "tables" is a
        // junction pointing outside the bundle -- the OS follows it when
        // Directory.CreateDirectory/File.WriteAllText actually touch disk.
        // The STRICT walk: a directory whose link status cannot be read is
        // refused like a link (this is a guard, and a guard fails closed --
        // see ReparsePoints.IsReparsePointOrUninspectable); a directory that
        // does not exist yet is not, so new subdirectories are still allowed.
        var targetParentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetParentDir) && ReparsePoints.HasReparsePointOrUninspectableAncestor(BundleRoot, targetParentDir))
        {
            return $"Error: '{id}' resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.";
        }

        // Also reject the target FILE node itself being a reparse point (a
        // planted file symlink at e.g. tables/x.md pointing at an external
        // file): the ancestor walk above only covers directory
        // ANCESTORS of targetPath, it never inspects targetPath itself, so
        // an existing symlinked concept file would otherwise sail through
        // both checks and a later read/write would follow the link. Strict
        // for the same reason as the walk above.
        if (ReparsePoints.IsReparsePointOrUninspectable(targetPath))
        {
            return $"Error: '{id}' is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to overwrite it.";
        }

        target = new ConceptTarget(id, targetPath);
        return null;
    }
```

becomes

```csharp
    /// <summary>A validated concept id and the bundle key of its document, produced by <see cref="ValidateConceptTarget"/>.</summary>
    private readonly record struct ConceptTarget(ConceptId Id, BundleKey Key);

    /// <summary>
    /// Validates <paramref name="conceptId"/> — parseable, not the reserved
    /// <c>index</c>/<c>log</c> name — and the bundle key of its document
    /// (<c>{id}.md</c>) under the writer's key policy (spec §2, decision B2):
    /// the provider's native constraints under the options' policy, plus any
    /// extra validator, exactly as a load checks the same key. Shared by every
    /// write method so they can never diverge on what counts as a valid write
    /// target. Pure: no I/O, no lock.
    /// </summary>
    /// <remarks>
    /// <b>Path guards live behind the provider.</b> Whether the key's path goes
    /// through a symlink or a junction, or an entry that cannot be inspected,
    /// whether it stays inside the bundle root, and whether the volume would
    /// confuse the name with another, is the provider's to decide at the moment
    /// of the write (<see cref="FileSystemBundleProvider"/>: a STRICT check of
    /// the whole path before anything is listed, again after the parent
    /// directories exist, and again under its commit lock just before the
    /// commit). Its refusals come back as exceptions, which <see cref="Translate"/>
    /// turns into this class's own messages. The checks and the commit are still
    /// separate system calls, so a substitution by another local actor racing
    /// the last of them is a residual, documented limitation (issue #178); the
    /// threat model is unchanged — an actor without write access to the bundle
    /// tree planting a reparse point ahead of time, or a stale one left over.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> and a populated <paramref name="target"/> on
    /// success; otherwise the <c>Error: ...</c> message to return to the
    /// caller (and <paramref name="target"/> is <see langword="default"/>).
    /// </returns>
    private string? ValidateConceptTarget(string conceptId, out ConceptTarget target)
    {
        target = default;

        if (!ConceptId.TryParse(conceptId, out var id))
        {
            return $"Error: invalid concept id {DebugQuote.Quote(conceptId)}. Concept ids are '/'-separated "
                + "segments matching [A-Za-z0-9_][A-Za-z0-9_.-]*.";
        }

        if (string.Equals(id.Name, "index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id.Name, "log", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: '{id}' is a reserved concept id — the last segment must not be "
                + "'index' or 'log' in any casing (these would collide with the bundle's "
                + "index.md/log.md files on case-insensitive filesystems).";
        }

        if (!BundleKey.TryParse(id + ".md", out var key, out var reason))
        {
            return $"Error: '{id}' cannot be a bundle key: {reason}.";
        }

        if (_validator.CheckKey(key) is { } refused)
        {
            return $"Error: '{id}' cannot be written under the bundle's key policy: {refused}.";
        }

        target = new ConceptTarget(id, key);
        return null;
    }
```

(o) `CheckVerificationTargets` — its summary's cost paragraph:

```csharp
    /// Reads only the <c>k</c> concept files named in <paramref name="conceptIds"/> (via
    /// <see cref="ValidateConceptTarget"/> and a direct <see cref="File.Exists(string)"/>/parse of
    /// each target path) — never <see cref="Bundle.Load(string)"/> — so answering a k-id question
    /// costs O(k) file reads, not a full bundle parse.
```

becomes

```csharp
    /// Reads only the <c>k</c> concept documents named in <paramref name="conceptIds"/>, through the
    /// provider (<see cref="IBundleProvider.Exists"/> and <see cref="IBundleProvider.Read"/>, whose
    /// names match exactly) — never <see cref="Bundle.Load(string)"/> — so answering a k-id question
    /// costs O(k) reads, not a full bundle parse. Read-only: it is not guarded by
    /// <see cref="BundleProviderCapabilities.CanWrite"/>.
```

and in `CheckVerificationTargetsLocked`, everything from the line `        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);` through the end of the method `ExistsWithExactCase` that follows it (its closing `    }`, after `        return true;`) — the duplicate rule on keys, existence and reads through the provider; `ExistsWithExactCase` disappears, the provider matches names exactly — becomes

```csharp
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < targets.Count; i++)
        {
            if (!seenKeys.Add(targets[i].Key.ToString()))
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.DuplicateName, conceptIds[i]);
            }
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            try
            {
                // The provider matches every segment of the key EXACTLY
                // (spec §2): an id like "METRICS/DAU" is not found for an
                // on-disk metrics/dau.md on any OS or volume, the same answer
                // Bundle.Load gives — it builds each concept's id from the
                // stored names, and ConceptId equality is ordinal.
                if (!_provider.Exists(target.Key))
                {
                    return new VerificationTargetProblem(VerificationTargetProblemKind.NotFound, conceptIds[i]);
                }

                // One spelling of §11 conformance (OkfDocument.ValidateConformance
                // itself), not a second hand-rolled `Get("type")` check beside it.
                OkfDocument.Parse(Bundle.ReadText(_provider, target.Key, _limits, new byte[Bundle.ReadChunkBytes], out _)).ValidateConformance();
            }
            catch (BundleAccessRefusedException ex)
            {
                // A path the provider refuses (a reparse point, an entry that
                // cannot be inspected) is an invalid target, as when the
                // writer checked the path itself; same message as a write.
                return new VerificationTargetProblem(VerificationTargetProblemKind.InvalidId, conceptIds[i], Translate(ex, $"'{target.Id}'"));
            }
            catch (BundleObjectNotFoundException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.NotFound, conceptIds[i]);
            }
            catch (DocumentValidationException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.NotConformant, conceptIds[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let an expected I/O condition (a file held open
                // exclusively by another process, a permissions error)
                // escape as an unhandled exception -- Bundle.Load wraps the
                // same two exception types into BundleLoadException so
                // `okf validate`/`info`/etc. report a clean `error:` line
                // instead of a stack trace; this check owes verify the same.
                return new VerificationTargetProblem(VerificationTargetProblemKind.Unreadable, conceptIds[i], ex.Message);
            }
            catch (Exception ex) when (ex is OkfException or System.Text.DecoderFallbackException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.ParseFailure, conceptIds[i], ex.Message);
            }
        }

        return null;
    }
```

(p) In `FormatVerificationTargetProblem`'s summary:

```csharp
    /// <see cref="CheckVerificationTargets"/> from <see cref="ValidateConceptTarget"/>'s own return
    /// value — not re-derived here, since <see cref="ValidateConceptTarget"/> also probes the
    /// filesystem for a reparse point, and re-running it a second time is both wasted I/O and a
    /// second window for that answer to change underneath it.
```

becomes

```csharp
    /// <see cref="CheckVerificationTargets"/> from <see cref="ValidateConceptTarget"/>'s own return
    /// value, or from the provider's refusal of the path — not re-derived here, since asking the
    /// provider a second time is both wasted I/O and a second window for that answer to change
    /// underneath it.
```

(q) The end of the class: everything from the summary of `LateReparseGuard` (the `    /// <summary>` line above `    /// Late, best-effort reparse-point re-check used by`) through `RunTool`'s closing `    }` (the last line before the class's closing `}`) — `LateReparseGuard` disappears (the provider guards), the write primitive goes through the provider and reports the commit through its `committed` callback without notifying, `WriteThenNotify` and `NotifyCommitted` invoke the notification once the whole write operation has returned (B48), `RunTool` translates provider exceptions — becomes

```csharp
    /// <summary>
    /// Writes already-validated <paramref name="content"/> as <paramref name="target"/>'s
    /// document through the provider, under <paramref name="condition"/>, and reports the
    /// commit through <paramref name="committed"/>. CALLER MUST already hold the write lock
    /// (<see cref="EnterWriteLock"/>) — this method does not acquire it itself, so that
    /// <see cref="AppendToConceptAtomic"/> and <see cref="RecordVerifications"/> can enclose
    /// their own preceding read-and-transform in the SAME lock acquisition as this write.
    /// The write primitive of concept writes and verification stamps: every overload of
    /// <see cref="WriteConcept(string, string, string)"/>, <see cref="AppendToConceptAtomic"/> and
    /// <see cref="RecordVerifications"/> write through here.
    /// </summary>
    /// <remarks>
    /// <para>A creation — the key absent at the provider — is first checked against the tree
    /// it would produce (<see cref="BundleTreeCheck"/>, spec §2, decision B10): a case or
    /// Unicode-form twin under a policy that folds names, an object on a proper prefix, or
    /// objects under the key, is refused here with a message naming the conflicting name,
    /// before anything is written. An update skips it.</para>
    /// <para>Provider exceptions propagate (the caller's <see cref="RunTool(Func{string}, Func{string?}?)"/>
    /// translates them, or the caller retries a conflict): nothing has then been written
    /// and <paramref name="committed"/> is not called, except for an outcome the
    /// provider could not establish (<see cref="BundleWriteOutcomeUnknownException"/>).</para>
    /// <para>Once <see cref="IBundleProvider.Write"/> has returned, the write is final: this
    /// method calls <paramref name="committed"/> and returns its confirmation, and nothing it
    /// does after the commit can throw. It never invokes <see cref="_onWriteCommitted"/>: the
    /// caller does, once its whole write operation — every attempt of a read-modify-write
    /// included — has returned (<see cref="WriteThenNotify"/>, <see cref="NotifyCommitted"/>), so
    /// that nothing the notification throws can be taken for a failure of the storage
    /// operation (decision B48).</para>
    /// </remarks>
    /// <param name="target">The concept being written.</param>
    /// <param name="content">The fully serialized, already-validated document content to write.</param>
    /// <param name="condition">The provider condition: <see cref="BundleWriteCondition.None"/> for an unconditional write.</param>
    /// <param name="existedBefore">
    /// Whether the object already existed, if the caller already knows it from a read made
    /// under the same write-lock hold. <see langword="null"/> means "not checked yet": this
    /// method asks the provider (<see cref="IBundleProvider.Exists"/>).
    /// </param>
    /// <param name="committed">Called once the provider has committed the write: the caller then notifies (<see cref="NotifyCommitted"/>).</param>
    private string WriteValidatedContentLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed, bool? existedBefore = null)
    {
        var existed = existedBefore ?? _provider.Exists(target.Key);
        if (!existed && BundleTreeCheck.CheckCreation(_provider, _validator, target.Key, _limits) is { } conflict)
        {
            return $"Error: {conflict}.";
        }

        var bytes = OkfEncodings.NoBom.GetBytes(content);
        _provider.Write(target.Key, bytes, condition);
        committed();

        var status = existed ? "updated" : "new";
        return $"Written {target.Id} ({status}, {bytes.Length} bytes). Remember to run okf_regenerate_indexes.";
    }

    /// <summary>
    /// Runs one write operation of a <c>string</c> entry point — a single write, or every attempt
    /// of a read-modify-write — and, if it committed, invokes the write notification once that
    /// operation has returned (<see cref="NotifyCommitted"/>). CALLER MUST hold the write lock:
    /// the notification runs under it, atomically with the write it follows (#131).
    /// </summary>
    /// <param name="subject">How a converted notification failure names the object (<c>'tables/x'</c>, <c>log.md</c>).</param>
    /// <param name="write">The operation; it calls the action it is given once the provider has committed.</param>
    /// <returns>What <paramref name="write"/> returned, or the <c>Error: …</c> text of a notification that failed.</returns>
    private string WriteThenNotify(string subject, Func<Action, string> write)
    {
        var committed = false;
        var result = write(() => committed = true);
        return committed && NotifyCommitted(subject) is { } failure ? failure : result;
    }

    /// <summary>
    /// Invokes <see cref="_onWriteCommitted"/> once, after a committed write, outside every retry
    /// and every catch of a conflict (decision B48): the write is final, so nothing the
    /// notification throws is a failure of the write — it is never retried, never read as a
    /// refused condition. An exception the writer converts (<see cref="IsExpectedFailure"/>: an
    /// I/O error, an <see cref="OkfException"/>, a conflict raised by another operation included)
    /// is returned as the writer's <c>Error: …</c> text; any other — an
    /// <see cref="OperationCanceledException"/>, a host's bug — propagates to the caller unchanged,
    /// the write committed. CALLER MUST hold the write lock.
    /// </summary>
    /// <param name="subject">How the message names the object written.</param>
    /// <returns><see langword="null"/>, or the <c>Error: …</c> text of the notification's failure.</returns>
    private string? NotifyCommitted(string subject)
    {
        try
        {
            _onWriteCommitted?.Invoke();
            return null;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return Translate(ex, subject);
        }
    }

    /// <summary>
    /// The current text and version of <paramref name="key"/>, read through the provider as
    /// strict UTF-8 within the writer's bounds (<see cref="Bundle.ReadText"/>, the core's one
    /// bounded reader), or <see langword="null"/> when no object exists under it. A non-UTF-8
    /// object throws <see cref="System.Text.DecoderFallbackException"/> rather than being
    /// silently re-decoded and rewritten.
    /// </summary>
    private (string Text, BundleObjectVersion Version)? ReadCurrent(BundleKey key)
    {
        try
        {
            var text = Bundle.ReadText(_provider, key, _limits, new byte[Bundle.ReadChunkBytes], out var version);
            return (text, version);
        }
        catch (BundleObjectNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The condition of a read-modify-write: the version read, or no object at all.</summary>
    private static BundleWriteCondition ConditionFor((string Text, BundleObjectVersion Version)? current) =>
        current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist;

    /// <summary>
    /// The capability guard of every write method (spec §3, decision B3): checked first,
    /// before any argument, read, lock or listing, so a refused call has no effect at all.
    /// A read-only provider is a host mistake, not a content error: the exception is thrown,
    /// never returned as an <c>Error:</c> string.
    /// </summary>
    private void GuardWritable()
    {
        if (!_provider.Capabilities.CanWrite)
        {
            throw new BundleReadOnlyException(_provider.Id);
        }
    }

    /// <summary>
    /// Runs a write-method body, converting any exception that a well-formed
    /// but unlucky input could still trigger — a producer-validation failure
    /// (<see cref="OkfException"/>, e.g. <see cref="DocumentValidationException"/>),
    /// a provider refusal or conflict (<see cref="BundleProviderException"/>),
    /// a rejected argument surfaced late by a BCL API (<see cref="ArgumentException"/>),
    /// a storage read/write failure (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>), or a strict-UTF-8 decode
    /// failure reading an existing document (<see cref="System.Text.DecoderFallbackException"/>)
    /// — into a plain-text message (<see cref="Translate"/>). This is the single
    /// enforcement point for the "never throw for an expected error" rule.
    /// <see cref="BundleReadOnlyException"/> is not converted: it is a host mistake.
    /// </summary>
    /// <param name="body">The method body.</param>
    /// <param name="subject">How the message names the object being written, read when an exception is converted (<c>'tables/x'</c>, <c>log.md</c>); <see langword="null"/>, or returning <see langword="null"/>, before the body knows it.</param>
    private static string RunTool(Func<string> body, Func<string?>? subject = null)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return subject?.Invoke() is { } s ? Translate(ex, s) : $"Error: {ex.Message}";
        }
    }

    /// <summary>The exceptions <see cref="RunTool"/> converts into an <c>Error:</c> string.</summary>
    private static bool IsExpectedFailure(Exception ex) =>
        ex is not BundleReadOnlyException
        && ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException;

    /// <summary>
    /// The writer's message for a failure while writing <paramref name="subject"/> (spec §7: built
    /// from the exception's type and fields, never from a provider's own text where the writer has
    /// one of its own). A path the provider refuses keeps the message the writer gave its own
    /// reparse-point guard before the write moved behind the provider (decision B15); a name the
    /// storage cannot hold gets a library-authored one; anything else is the exception's message.
    /// </summary>
    private static string Translate(Exception ex, string subject) => ex switch
    {
        BundleAccessRefusedException { Key: not null } =>
            $"Error: {subject} resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.",
        BundleWriteConflictException { Reason: BundleWriteConflictReason.NameUnavailable } =>
            $"Error: {subject} cannot be stored under that name: the bundle holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name.",
        _ => $"Error: {ex.Message}",
    };
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterProviderTests"`
Expected: `Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29` (the notification tests of B46 and B48 included: a three-row and a six-row theory, and the cancellation test).

- [ ] **Step 6: Run the whole suite — one Windows test fails, for the reason of Deviation 2**

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`
Expected on Windows: `Failed: 1` — `BundleConceptWriterTests.WriteConcept_refuses_a_target_file_that_is_an_uninspectable_junction`, `Expected: "Error: 'x/refunds' is a reparse point (sy"···` / `Actual: "Error: 'x/refunds' resolves through a rep"···`. Elsewhere it skips (it needs a junction and deny ACEs) and the run is green.

- [ ] **Step 7: Rewrite that expectation**

In `tests/OKF4net.Tests/BundleConceptWriterTests.cs`, replace

```csharp
        // H1 fix round (M1): "x/refunds.md" is a junction named like the
        // concept file, whose attributes cannot be read ("x" denies listing).
        // Only the early check on the target FILE node can refuse it -- "x"
        // itself is readable -- so its message pins that check. (Not an escape:
        // with it removed, the late strict guard, or the OS writing into a
        // directory, still refuses.)
        using var tmp = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "refunds.md"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("x/refunds", ValidFrontmatter, "# Refunds\n");

        Assert.Equal("Error: 'x/refunds' is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to overwrite it.", result);
```

with

```csharp
        // H1 fix round (M1): "x/refunds.md" is a junction named like the
        // concept file, whose attributes cannot be read ("x" denies listing).
        // Only the early check on the target FILE node can refuse it -- "x"
        // itself is readable. (Not an escape: with it removed, the late strict
        // guard, or the OS writing into a directory, still refuses.) Since the
        // write moved behind the provider (lot B), that early check is the
        // provider's STRICT check of the whole path before anything is listed;
        // the provider reports every refused path, the object's own entry
        // included, as one BundleAccessRefusedException, which the writer
        // translates into its one reparse-point message (decision B15) -- the
        // former "is a reparse point ... refusing to overwrite it" wording,
        // which only the writer's own target-node check produced, is gone.
        using var tmp = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "refunds.md"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("x/refunds", ValidFrontmatter, "# Refunds\n");

        Assert.Equal("Error: 'x/refunds' resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.", result);
```

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` — `Failed: 0` (3 328 tests in the replay, 28 skipped).

- [ ] **Step 8: CHANGELOG and README**

In `CHANGELOG.md`, under `### Added`, after Task 1's bullet (it ends `  atomicity and implicit prefixes.`), add:

```markdown
- **`BundleConceptWriter` over a provider.** `new BundleConceptWriter(provider,
  onWriteCommitted, options)` writes through any writable `IBundleProvider`,
  under the key policy and bounds of the `BundleLoadOptions` it is given
  (the defaults of a load otherwise); the string constructor is unchanged and
  builds a `FileSystemBundleProvider`. `BundleRoot` throws
  `NotSupportedException` for a writer over any other provider.
```

and under `### Changed`, after Task 2c's bullet (it ends `  scope's listings on its path.`), add:

```markdown
- **`BundleConceptWriter` writes through its provider** (spec §3, §4, §9).
  Every concept write is one atomic `IBundleProvider.Write`: a crash or an
  I/O error no longer leaves a truncated concept file. Every write method
  first checks `Capabilities.CanWrite` and throws `BundleReadOnlyException`,
  with no effect, on a read-only provider. Keys are checked by the core under
  the load's policy before anything is written: on Windows a concept id
  whose segment ends with `.` (`a./b`), which used to land in `a/b`, is
  refused. A creation that would make the tree invalid under that policy — a
  concept file on its path, objects under it, or a case or Unicode-form twin
  under a folding policy — is refused with a message naming the conflicting
  name. `AppendToConceptAtomic` and `RecordVerifications` write
  conditioned on the version they read. The path guards are the provider's:
  a concept file that is itself a link or cannot be inspected is now refused
  with the same message as a path through one (`… resolves through a reparse
  point (symlink/junction), or an entry that could not be inspected, inside
  the bundle, which is not allowed.`), and any write failure in the middle of
  a `RecordVerifications` batch now ends with `— already written: …`.
- **A write notification that fails no longer hides the write.** When
  `onWriteCommitted` throws once the provider has committed, `WriteConcept`
  and `AppendToConceptAtomic` still return `Error: {message}`, as before, but
  the write is on disk and was made once; a `RecordVerifications` batch
  records that concept in `Records`, goes on, keeps `Recorded` true when every
  stamp is on disk, and ends its message with `; the write notification
  failed: …`. The notification runs once per write, once the write is final,
  and never makes it retried or refused, whatever it throws (a conflict
  included); an exception the writer does not convert, such as
  `OperationCanceledException`, propagates unchanged, the write committed.
```

In `README.md`, at the end of "Writing through a provider" (after the line `` `.okf-write-*.tmp` files and remove them by hand once you have checked them. ``), add:

```markdown

`BundleConceptWriter` writes concepts through a provider:
`new BundleConceptWriter(provider, options: loadOptions)` (the string
constructor builds a `FileSystemBundleProvider` over the directory). Give it
the `BundleLoadOptions` the bundle is loaded with: it checks every key under
that policy before writing, and refuses a creation that would make the tree
invalid under it — a concept file on the new key's path, objects under it,
or, under a policy that folds names, a sibling differing only in case or
Unicode form — so it never writes what the next load would discard. A
read-only provider makes every write method throw `BundleReadOnlyException`.
```

- [ ] **Step 9: Verify and commit**

```bash
for f in src/OKF4net/Bundle.cs src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/BundleConceptWriterTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
```

Then the Linux container command of the Global Constraints with the filter `FullyQualifiedName~BundleConceptWriter|FullyQualifiedName~RecordVerification|FullyQualifiedName~OKF4net.Tests.Providers|FullyQualifiedName~OkfWriteTools|FullyQualifiedName~OkfVerifyTool|FullyQualifiedName~OkfBundleTools|FullyQualifiedName~FileMemoryStore|FullyQualifiedName~OkfContextProviderMemory` for both passes — `Failed: 0` (the symlink tests that skip on Windows run there).

```bash
git add src/OKF4net/Bundle.cs src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/BundleConceptWriterTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs CHANGELOG.md README.md
git commit -F - <<'MSG'
feat(writer): BundleConceptWriter writes through its provider

A new constructor takes any IBundleProvider and BundleLoadOptions; the string
constructor builds a FileSystemBundleProvider. Every write method checks
Capabilities.CanWrite first and throws BundleReadOnlyException with no effect.
Concept keys are checked by the core under the options' key policy, and a
creation is checked against the tree it would make (BundleTreeCheck). Writes
go through IBundleProvider.Write, unconditional for WriteConcept, conditioned
on the version read for AppendToConceptAtomic and RecordVerifications; reads
go through Bundle.ReadText within the options' bounds. The path guards are the
provider's: its refusals become the writer's reparse-point message, and a
write failure mid-batch in RecordVerifications keeps the already-written
suffix. The lock key stays the root on the filesystem and is the provider's
Id elsewhere; BundleRoot throws NotSupportedException over another provider.
The write primitive reports its commit without notifying: each write method
invokes onWriteCommitted once its write has returned, so an exception the
notification throws is never taken for a failure of the write.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 4b: Conditional concept writes, bounded retries, `buildBody` once per attempt

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`WriteConcept` ×4, `AppendToConceptAtomic`, `RecordVerifications`' write loop, the write primitive, the retry members)
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`AppendToConceptAtomic`'s `buildBody` doc)
- Modify: `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` (two more rows in the read-only matrix)
- Test: `tests/OKF4net.Tests/BundleConceptWriterRetryTests.cs`
- Modify: `CHANGELOG.md`, `README.md`

**Interfaces:**
- Consumes: Task 4a; Task 1's `InMemoryBundleProvider.BeforeNextWrite`, `ConflictOnNextWrite`, `FailWrite`; Task 2c's `ListOrphanedTemporaries`.
- Produces:
  - `public string WriteConcept(string conceptId, string frontmatterYaml, string body, BundleWriteCondition condition)` and `public string WriteConcept(string conceptId, Frontmatter frontmatter, string body, BundleWriteCondition condition)` (B29); the three-argument overloads delegate with `BundleWriteCondition.None`.
  - `internal const int MaxAttempts = 3`; `internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;`; `internal static TimeSpan RetryDelay(int attempt)` — 10–40 ms after attempt 1, 30–120 ms after attempt 2.
  - `AppendToConceptAtomic` and `RecordVerifications` retry a `ConditionFailed` conflict (B5, B30); after three: `Error: {subject} kept changing while it was being written; the write was refused after 3 attempts.`
  - The notification (B48): the conditional overloads, `AppendToConceptAtomic` and `RecordVerifications` invoke it once their write operation — every attempt included — has returned; `WithRetries` and `private string WriteConditionedLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed)` see the storage operation only, so a conflict the notification raises is neither retried nor read as the caller's condition refused.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/BundleConceptWriterRetryTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Optimistic concurrency in <see cref="BundleConceptWriter"/> (spec §4, decisions B4, B5): a write
/// conditioned by its caller is never retried, a read-modify-write is retried on a conflict at most
/// three times with a growing random delay, re-reading each time and calling <c>buildBody</c> once
/// per attempt. The conflicts come from a writer outside the writer's lock, scripted through the
/// in-memory provider, or real through two provider instances over one directory.
/// </summary>
public class BundleConceptWriterRetryTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";
    private const string Changed = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\nowner: elsewhere\n---\n\nchanged elsewhere\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static BundleObjectVersion VersionOf(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return obj.Version;
    }

    private static (BundleConceptWriter Writer, List<TimeSpan> Delays) WriterOver(IBundleProvider p)
    {
        var delays = new List<TimeSpan>();
        var writer = new BundleConceptWriter(p) { Sleep = delays.Add, UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };
        return (writer, delays);
    }

    /// <summary>What a write returns when its notification raised <see cref="WriterNotifyingWithAConflict"/>'s conflict: the notification's own text (B46).</summary>
    private const string NotificationConflict = "Error: 'elsewhere.md' changed since it was read; the write was refused";

    /// <summary>
    /// A writer whose write notification raises a conflict of another operation — on its first
    /// call only, or on every call — after the write it follows is committed (external review,
    /// F6), with the number of its calls.
    /// </summary>
    private static (BundleConceptWriter Writer, List<TimeSpan> Delays, Func<int> Notifications) WriterNotifyingWithAConflict(IBundleProvider p, bool always)
    {
        var delays = new List<TimeSpan>();
        var notifications = 0;
        var writer = new BundleConceptWriter(p, onWriteCommitted: () =>
        {
            if (++notifications == 1 || always)
            {
                throw new BundleWriteConflictException(K("elsewhere.md"));
            }
        })
        {
            Sleep = delays.Add,
            UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc),
        };
        return (writer, delays, () => notifications);
    }

    [Fact]
    public void A_write_conditioned_on_the_version_read_lands_only_on_that_version()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, _) = WriterOver(storage);
        var v1 = VersionOf(storage, "metrics/dau.md");

        Assert.StartsWith("Written metrics/dau (updated,", writer.WriteConcept("metrics/dau", Fm, "second\n", BundleWriteCondition.IfMatch(v1)), StringComparison.Ordinal);
        Assert.Equal(
            "Error: 'metrics/dau' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.",
            writer.WriteConcept("metrics/dau", Fm, "third\n", BundleWriteCondition.IfMatch(v1)));
        Assert.EndsWith("second\n", Text(storage, "metrics/dau.md"), StringComparison.Ordinal);

        Assert.Equal(
            "Error: 'metrics/new' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.",
            writer.WriteConcept("metrics/new", Fm, "x\n", BundleWriteCondition.IfMatch(v1)));
        Assert.False(storage.Exists(K("metrics/new.md")));
    }

    [Fact]
    public void A_creation_conditioned_on_absence_refuses_an_existing_concept()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, _) = WriterOver(storage);
        var frontmatter = Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm));

        Assert.Equal(
            "Error: 'metrics/dau' already exists; the write was refused. Read it before writing it.",
            writer.WriteConcept("metrics/dau", frontmatter, "x\n", BundleWriteCondition.MustNotExist));
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.StartsWith("Written metrics/rev (new,", writer.WriteConcept("metrics/rev", frontmatter, "x\n", BundleWriteCondition.MustNotExist), StringComparison.Ordinal);
    }

    /// <summary>A caller's condition is never retried: retrying would overwrite what the caller has not seen (spec §4).</summary>
    [Fact]
    public void A_write_conditioned_by_its_caller_is_never_retried()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        var v1 = VersionOf(storage, "metrics/dau.md");
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));
        storage.ClearCalls();

        Assert.StartsWith("Error: 'metrics/dau' changed since it was read", writer.WriteConcept("metrics/dau", Fm, "mine\n", BundleWriteCondition.IfMatch(v1)), StringComparison.Ordinal);

        Assert.Single(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
        Assert.Empty(delays);
        Assert.Equal(Changed, Text(storage, "metrics/dau.md"));
    }

    [Fact]
    public void An_append_that_meets_a_change_reads_again_and_builds_on_it()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));
        var seen = new List<string?>();

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            seen.Add(body);
            return body + "\nappended\n";
        });

        Assert.StartsWith("Written metrics/dau (updated,", result, StringComparison.Ordinal);
        Assert.Equal(["body", "changed elsewhere"], seen);
        var after = OkfDocument.Parse(Text(storage, "metrics/dau.md"));
        Assert.Equal("elsewhere", after.Frontmatter.Get("owner")!.AsDisplayString());
        Assert.Equal("changed elsewhere\nappended", after.Body.TrimEnd('\n'));
        var delay = Assert.Single(delays);
        Assert.InRange(delay.TotalMilliseconds, 10, 40);
    }

    [Fact]
    public void A_creation_that_meets_a_concept_created_meanwhile_reads_it_and_builds_on_it()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var (writer, _) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Doc));
        var seen = new List<string?>();

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            seen.Add(body);
            return (body ?? "first") + "\nmine\n";
        });

        Assert.StartsWith("Written metrics/dau (updated,", result, StringComparison.Ordinal);
        Assert.Equal([null, "body"], seen);
        Assert.Equal(["read metrics/dau.md", "write metrics/dau.md MustNotExist", "read metrics/dau.md", "write metrics/dau.md IfMatch"], storage.Calls);
    }

    [Fact]
    public void After_three_conflicting_attempts_the_append_gives_up_with_an_error_and_writes_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        var calls = 0;

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            calls++;
            storage.ConflictOnNextWrite();
            return body + "\nappended\n";
        });

        Assert.Equal("Error: 'metrics/dau' kept changing while it was being written; the write was refused after 3 attempts.", result);
        Assert.Equal(3, calls);
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.Equal(2, delays.Count);
        Assert.InRange(delays[0].TotalMilliseconds, 10, 40);
        Assert.InRange(delays[1].TotalMilliseconds, 30, 120);
    }

    [Fact]
    public void The_retry_delays_are_random_and_grow()
    {
        var first = Enumerable.Range(0, 200).Select(_ => BundleConceptWriter.RetryDelay(1).TotalMilliseconds).ToList();
        var second = Enumerable.Range(0, 200).Select(_ => BundleConceptWriter.RetryDelay(2).TotalMilliseconds).ToList();
        Assert.All(first, d => Assert.InRange(d, 10, 40));
        Assert.All(second, d => Assert.InRange(d, 30, 120));
        Assert.True(first.Distinct().Count() > 1 && second.Distinct().Count() > 1);
        Assert.True(second.Average() > first.Average());
    }

    [Fact]
    public void A_verification_that_meets_a_change_stamps_the_new_content()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));

        var outcome = writer.RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded, outcome.Message);
        var after = OkfDocument.Parse(Text(storage, "metrics/dau.md"));
        Assert.Equal("elsewhere", after.Frontmatter.Get("owner")!.AsDisplayString());
        Assert.Equal("human:ada", Assert.Single(after.Frontmatter.Verified).By!.Value.Raw);
        Assert.Single(delays);
    }

    [Fact]
    public void A_verification_batch_whose_second_concept_keeps_changing_reports_the_first_as_written()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, _) = WriterOver(storage);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: 'b' kept changing while it was being written; the write was refused after 3 attempts. — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(Doc, Text(storage, "b.md"));
    }

    /// <summary>
    /// A conflict the write notification raises once the write is committed (external review, F6)
    /// is not a refusal of the write's own condition (B48): a caller's <c>IfMatch</c> write, through
    /// either overload, lands once and notifies once, and returns the notification's text — never
    /// "changed since it was read". Once or on every call, the same.
    /// </summary>
    [Theory]
    [InlineData(false, "yaml")]
    [InlineData(true, "yaml")]
    [InlineData(false, "frontmatter")]
    [InlineData(true, "frontmatter")]
    public void A_conflict_raised_by_the_notification_of_a_conditioned_write_is_not_its_refusal(bool always, string overload)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        var condition = BundleWriteCondition.IfMatch(VersionOf(storage, "metrics/dau.md"));
        storage.ClearCalls();

        var result = overload == "yaml"
            ? writer.WriteConcept("metrics/dau", Fm, "mine\n", condition)
            : writer.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "mine\n", condition);

        Assert.Equal(NotificationConflict, result);
        Assert.Equal(["write metrics/dau.md IfMatch"], storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)));
        Assert.Equal(1, notifications());
        Assert.Empty(delays);
        Assert.EndsWith("\nmine\n", Text(storage, "metrics/dau.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a read-modify-write (F6): the reviewer's append, its notification raising a
    /// conflict once or every time, is committed once — <c>buildBody</c> ran once, the body holds
    /// one appended copy, nothing was retried, the notification ran once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflict_raised_by_the_notification_of_an_append_neither_retries_nor_appends_again(bool always)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        storage.ClearCalls();
        var builds = 0;

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            builds++;
            return body + "\nADDED\n";
        });

        Assert.Equal(NotificationConflict, result);
        Assert.Equal(1, builds);
        Assert.Equal(1, notifications());
        Assert.Empty(delays);
        Assert.Equal(["read metrics/dau.md", "write metrics/dau.md IfMatch"], storage.Calls);
        Assert.Equal(2, Text(storage, "metrics/dau.md").Split("ADDED").Length);
    }

    /// <summary>
    /// And for a verification batch (F6): a notification raising a conflict, once or every time,
    /// prepares and writes nothing again — one write and one notification per concept, both
    /// concepts recorded, stamped once, and the message says the notification failed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflict_raised_by_the_notification_of_a_verification_writes_nothing_again(bool always)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        storage.ClearCalls();

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada", "2026-10-19T00:00:00Z");

        Assert.True(outcome.Recorded, outcome.Message);
        Assert.Equal("Recorded 2 verification(s) by human:ada at 2026-10-19T00:00:00Z; the write notification failed: 'elsewhere.md' changed since it was read; the write was refused", outcome.Message);
        Assert.Equal(["a", "b"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(2, notifications());
        Assert.Empty(delays);
        Assert.Equal(["write a.md IfMatch", "write b.md IfMatch"], storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)));
        Assert.All(["a.md", "b.md"], key => Assert.Single(OkfDocument.Parse(Text(storage, key)).Frontmatter.Verified));
    }

    /// <summary>
    /// A notification that fails does not hide a real conflict of the next concept (F6): the first
    /// concept is written and notified once, the second meets a conflict on every attempt and is
    /// reported as that concept's conflict, after its three attempts.
    /// </summary>
    [Fact]
    public void A_failing_notification_leaves_the_real_conflict_of_the_next_concept_reported_as_such()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always: true);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: 'b' kept changing while it was being written; the write was refused after 3 attempts. — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(1, notifications());
        Assert.Equal(2, delays.Count);
        Assert.Single(OkfDocument.Parse(Text(storage, "a.md")).Frontmatter.Verified);
        Assert.Equal(Doc, Text(storage, "b.md"));
    }

    /// <summary>
    /// Two writers append to one concept through two provider instances whose ids differ, so the
    /// writers do not share a lock: only the provider's conditional commit stands between them.
    /// Each round both read the same version before either writes (a barrier in <c>buildBody</c>'s
    /// first call), so exactly one meets a conflict, reads again and retries — fifty rounds, and
    /// no append is ever lost.
    /// </summary>
    [Fact]
    public void Two_writers_without_a_shared_lock_lose_no_append_over_fifty_rounds()
    {
        using var tmp = new TempDir();
        var writers = new[] { "alias-1", "alias-2" }
            .Select(id => new BundleConceptWriter(new IdAlias(new FileSystemBundleProvider(tmp.Path), id)) { Sleep = _ => { } })
            .ToArray();
        Assert.NotSame(writers[0].CurrentLockObjectForTest(), writers[1].CurrentLockObjectForTest());

        for (var round = 0; round < 50; round++)
        {
            using var bothRead = new Barrier(2);
            var builds = new int[2];
            var results = new string?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                results[i] = writers[i].AppendToConceptAtomic("notes/shared", Fm, body =>
                {
                    if (++builds[i] == 1)
                    {
                        bothRead.SignalAndWait(TimeSpan.FromSeconds(10));
                    }

                    return (body ?? string.Empty) + $"[{round}.{i}]";
                });
            })).ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a writer did not finish");
            }

            Assert.All(results, r => Assert.StartsWith("Written notes/shared", r, StringComparison.Ordinal));
            Assert.Equal(3, builds.Sum()); // one writer built once, the other twice
            var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "notes", "shared.md"))).Body;
            Assert.Contains($"[{round}.0]", body, StringComparison.Ordinal);
            Assert.Contains($"[{round}.1]", body, StringComparison.Ordinal);
        }

        Assert.Empty(new FileSystemBundleProvider(tmp.Path).ListOrphanedTemporaries(BundleLimits.Default));
    }

    /// <summary>A provider seen under another <see cref="IBundleProvider.Id"/>: same storage, another writer lock (B11).</summary>
    private sealed class IdAlias(IBundleProvider inner, string id) : IBundleProvider
    {
        public string Id => id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);

        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);

        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);

        public bool Exists(BundleKey key) => inner.Exists(key);

        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);

        public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }
}
```

and in `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` the matrix gains the two new overloads — replace

```csharp
        ["RecordVerifications"] = w => w.RecordVerifications(["metrics/dau"], "human:ada"),
    };
```

with

```csharp
        ["RecordVerifications"] = w => w.RecordVerifications(["metrics/dau"], "human:ada"),
        ["WriteConcept(yaml, condition)"] = w => w.WriteConcept("metrics/dau", Fm, "body\n", BundleWriteCondition.MustNotExist),
        ["WriteConcept(frontmatter, condition)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
    };
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only — `CS0117` for `Sleep` and `RetryDelay`, and `CS1501` (no overload of `WriteConcept` takes 4 arguments).

- [ ] **Step 3: The conditional overloads** — in `src/OKF4net/BundleConceptWriter.cs`:

(a) `WriteConcept(string, string, string)` becomes a delegate, and its body moves to the conditional overload — replace

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
                if (buildError is not null)
                {
                    return buildError;
                }

                // Serialized under the bundle's write lock (shared with
                // AppendToConceptAtomic and, since EnterWriteLock takes it from
                // the process-wide BundleLocks registry under the root's resolved
                // key, with every OTHER BundleConceptWriter instance whose root
                // resolves to this same directory, not just this instance) so
                // concurrent writers can't interleave an
                // existence check with another writer's write, and so the
                // committed callback below is atomic with the write it follows.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteValidatedContentLocked(target, content!, BundleWriteCondition.None, committed));
                }
            },
            () => subject);
    }
```

with

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body) =>
        WriteConcept(conceptId, frontmatterYaml, body, BundleWriteCondition.None);

    /// <summary>
    /// Like <see cref="WriteConcept(string, string, string)"/>, under a condition the provider checks
    /// atomically with the write (spec §4): <see cref="BundleWriteCondition.IfMatch"/> with the version a
    /// read returned replaces the concept only if it has not changed since;
    /// <see cref="BundleWriteCondition.MustNotExist"/> creates it only if it does not exist yet;
    /// <see cref="BundleWriteCondition.None"/> is the unconditional write of the three-argument overload.
    /// A condition that does not hold writes nothing and returns an <c>Error: …</c> that tells the caller
    /// to read the concept (again). Never retried: a retry would overwrite what the caller has not seen.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    /// <param name="condition">The condition on the concept's current version.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body, BundleWriteCondition condition)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
                if (buildError is not null)
                {
                    return buildError;
                }

                // Serialized under the bundle's write lock (shared with
                // AppendToConceptAtomic and, since EnterWriteLock takes it from
                // the process-wide BundleLocks registry under the root's resolved
                // key, with every OTHER BundleConceptWriter instance whose root
                // resolves to this same directory, not just this instance) so
                // concurrent writers can't interleave an
                // existence check with another writer's write, and so the
                // committed callback below is atomic with the write it follows.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteConditionedLocked(target, content!, condition, committed));
                }
            },
            () => subject);
    }
```

(b) the same for `WriteConcept(string, Frontmatter, string)` — replace

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var mapping = ShallowCopy(frontmatter.AsMapping());
                MaybeStampGenerated(mapping);

                var (content, buildError) = BuildValidatedContent(mapping, body);
                if (buildError is not null)
                {
                    return buildError;
                }

                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteValidatedContentLocked(target, content!, BundleWriteCondition.None, committed));
                }
            },
            () => subject);
    }
```

with

```csharp
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body) =>
        WriteConcept(conceptId, frontmatter, body, BundleWriteCondition.None);

    /// <summary>
    /// Like <see cref="WriteConcept(string, Frontmatter, string)"/>, under a condition the provider checks
    /// atomically with the write — see <see cref="WriteConcept(string, string, string, BundleWriteCondition)"/>.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatter">The frontmatter to write. Not mutated by this call.</param>
    /// <param name="body">The markdown body.</param>
    /// <param name="condition">The condition on the concept's current version.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body, BundleWriteCondition condition)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var mapping = ShallowCopy(frontmatter.AsMapping());
                MaybeStampGenerated(mapping);

                var (content, buildError) = BuildValidatedContent(mapping, body);
                if (buildError is not null)
                {
                    return buildError;
                }

                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WriteConditionedLocked(target, content!, condition, committed));
                }
            },
            () => subject);
    }
```

(c) a caller's `IfMatch` never creates, so the tree check skips it — in `WriteValidatedContentLocked`, replace

```csharp
        if (!existed && BundleTreeCheck.CheckCreation(_provider, _validator, target.Key, _limits) is { } conflict)
```

with

```csharp
        if (!existed
            && condition.Kind != BundleWriteConditionKind.IfMatch // never a creation
            && BundleTreeCheck.CheckCreation(_provider, _validator, target.Key, _limits) is { } conflict)
```

(d) after `ConditionFor`, the conditioned write and the retry members — replace

```csharp
    private static BundleWriteCondition ConditionFor((string Text, BundleObjectVersion Version)? current) =>
        current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist;
```

with

```csharp
    private static BundleWriteCondition ConditionFor((string Text, BundleObjectVersion Version)? current) =>
        current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist;

    /// <summary>
    /// A write under a caller's condition (<see cref="WriteConcept(string, string, string, BundleWriteCondition)"/>):
    /// never retried, and a condition that does not hold becomes the writer's own message. CALLER
    /// MUST hold the write lock. The conflict caught here can only be the provider's refusal of the
    /// write: the notification runs after this method has returned (<see cref="WriteThenNotify"/>,
    /// decision B48).
    /// </summary>
    private string WriteConditionedLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed)
    {
        try
        {
            return WriteValidatedContentLocked(target, content, condition, committed);
        }
        catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && condition.Kind != BundleWriteConditionKind.None)
        {
            return condition.Kind == BundleWriteConditionKind.MustNotExist
                ? $"Error: '{target.Id}' already exists; the write was refused. Read it before writing it."
                : $"Error: '{target.Id}' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.";
        }
    }

    /// <summary>How many times a read-modify-write is attempted before a conflict is returned (spec §4, decision B5).</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// How a read-modify-write waits before its next attempt; <see cref="Thread.Sleep(TimeSpan)"/>,
    /// on the caller's thread, while the contract is synchronous (issue #152). A test replaces it
    /// to record the delays instead of sleeping.
    /// </summary>
    internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

    /// <summary>The random, growing delay after failed attempt <paramref name="attempt"/>: 10–40 ms after the first, 30–120 ms after the second (decision B5).</summary>
    internal static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 1 ? Random.Shared.Next(10, 41) : Random.Shared.Next(30, 121));

    /// <summary>
    /// Runs one read-modify-write <paramref name="attempt"/> (called with 1, 2, 3) until it does not
    /// meet a <see cref="BundleWriteConflictReason.ConditionFailed"/> conflict, waiting
    /// <see cref="RetryDelay"/> between attempts. Each attempt must re-read the current state. After
    /// <see cref="MaxAttempts"/> conflicting attempts, returns the writer's message for
    /// <paramref name="subject"/>. CALLER MUST hold the write lock: the conflicts come from writers
    /// outside it (another process, a writer over another provider instance with another lock).
    /// An attempt holds the storage operation only — never the write notification, which its
    /// caller invokes once this method has returned (decision B48) — so a conflict caught here is
    /// always a refused write, never something that happened after a commit.
    /// </summary>
    private string WithRetries(string subject, Func<string> attempt)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                return attempt();
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed)
            {
                if (n == MaxAttempts)
                {
                    return KeptChanging(subject);
                }

                Sleep(RetryDelay(n));
            }
        }
    }

    /// <summary>The writer's message once every attempt of a read-modify-write met a conflict.</summary>
    private static string KeptChanging(string subject) =>
        $"Error: {subject} kept changing while it was being written; the write was refused after {MaxAttempts} attempts.";
```

- [ ] **Step 4: Retries** — still in `src/OKF4net/BundleConceptWriter.cs`:

(a) `AppendToConceptAtomic`: the end of its summary, its `buildBody` contract (spec §4: once per attempt), and its body wrapped in `WithRetries` — replace

```csharp
    /// it does not serialize a second process writing the same bundle path,
    /// and a C# lock cannot stop a concurrent external actor from mutating
    /// the target file/its ancestor directories on disk out from under this
    /// method (see <see cref="ValidateConceptTarget"/>'s remarks for that
    /// separate, residual check-then-write limitation, which this lock does
    /// not close). Reuses the exact same target validation (<see cref="ValidateConceptTarget"/>),
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent(string, string)"/>),
    /// and write/callback (<see cref="WriteValidatedContentLocked"/>)
    /// steps <see cref="WriteConcept(string, string, string)"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>memory/2026-07-24</c>.</param>
    /// <param name="frontmatterYamlIfCreating">
    /// Frontmatter used only when the concept does not yet exist. When it
    /// already exists, its own current frontmatter is re-read and
    /// re-serialized unchanged (mirroring how a caller that read-then-called
    /// <see cref="WriteConcept(string, string, string)"/> would carry it forward) and this parameter
    /// is ignored.
    /// </param>
    /// <param name="buildBody">
    /// Given the concept's current body (<see langword="null"/> if it does
    /// not yet exist), returns the full new body to write. Invoked exactly
    /// once, inside the lock, against the freshly re-read current body —
    /// never a caller's own stale, pre-lock snapshot.
    /// </param>
    /// <returns>
    /// The same style of result text as <see cref="WriteConcept(string, string, string)"/> (a
    /// <c>Written ...</c> confirmation) or an <c>Error: ...</c> message;
    /// never throws.
    /// </returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";

                // The read of the current body, the caller's transform, and the
                // validated write all happen inside this ONE lock acquisition —
                // never released and reacquired in between — which is what makes
                // the whole read-modify-write atomic against a concurrent
                // WriteConcept/AppendToConceptAtomic call for the same concept.
                // The write is conditioned on the version read, so a writer
                // outside this lock (another process) cannot be overwritten.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed =>
                    {
                        string frontmatterYaml;
                        string? currentBody;

                        // Strict UTF-8: a non-UTF-8 concept file throws
                        // DecoderFallbackException (caught by RunTool below)
                        // rather than being silently re-decoded and rewritten.
                        var current = ReadCurrent(target.Key);
                        if (current is { } existing)
                        {
                            // Fail-closed: if the existing concept has malformed
                            // frontmatter (hand-edited, or a prior partial/crashed
                            // write), OkfDocument.Parse throws DocumentParseException
                            // (caught by RunTool -> Error text) so this append is
                            // dropped rather than overwriting a possibly important
                            // file. This is stricter than a permissive bundle-load
                            // path, which would treat an unparseable file as absent
                            // and silently recreate it.
                            var existingDoc = OkfDocument.Parse(existing.Text);
                            frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                            currentBody = existingDoc.Body;
                        }
                        else
                        {
                            frontmatterYaml = frontmatterYamlIfCreating;
                            currentBody = null;
                        }

                        var newBody = buildBody(currentBody);
                        if (newBody is null)
                        {
                            return "Error: body must not be null.";
                        }

                        if (newBody.Contains('\0'))
                        {
                            return "Error: invalid body — it must not contain a null character.";
                        }

                        var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                        if (buildError is not null)
                        {
                            return buildError;
                        }

                        return WriteValidatedContentLocked(target, content!, ConditionFor(current), committed, existedBefore: current is not null);
                    });
                }
            },
            () => subject);
    }
```

with

```csharp
    /// it does not serialize a second process writing the same bundle path.
    /// The write is therefore conditioned on the version read
    /// (<see cref="BundleWriteCondition.IfMatch"/>, or
    /// <see cref="BundleWriteCondition.MustNotExist"/> for a creation): a
    /// change another writer committed in between is never overwritten. The
    /// whole read-transform-write is then tried again on what is there now —
    /// at most <see cref="MaxAttempts"/> attempts, with a short random delay
    /// between them (spec §4) — and after the last one the conflict is
    /// returned as an <c>Error: ...</c> message. Reuses the exact same target validation (<see cref="ValidateConceptTarget"/>),
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent(string, string)"/>),
    /// and write (<see cref="WriteValidatedContentLocked"/>) and notification (<see cref="WriteThenNotify"/>)
    /// steps <see cref="WriteConcept(string, string, string)"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>memory/2026-07-24</c>.</param>
    /// <param name="frontmatterYamlIfCreating">
    /// Frontmatter used only when the concept does not yet exist. When it
    /// already exists, its own current frontmatter is re-read and
    /// re-serialized unchanged (mirroring how a caller that read-then-called
    /// <see cref="WriteConcept(string, string, string)"/> would carry it forward) and this parameter
    /// is ignored.
    /// </param>
    /// <param name="buildBody">
    /// Given the concept's current body (<see langword="null"/> if it does
    /// not yet exist), returns the full new body to write. Invoked once per
    /// attempt — at most <see cref="MaxAttempts"/> times — inside the lock,
    /// each time against the freshly re-read current body, never a caller's
    /// own stale, pre-lock snapshot. It must therefore be a pure function of
    /// the body it receives: no side effect, nothing counted per call.
    /// </param>
    /// <returns>
    /// The same style of result text as <see cref="WriteConcept(string, string, string)"/> (a
    /// <c>Written ...</c> confirmation) or an <c>Error: ...</c> message;
    /// never throws (except as below, or what <paramref name="buildBody"/> itself throws).
    /// </returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";

                // The read of the current body, the caller's transform, and the
                // validated write all happen inside this ONE lock acquisition —
                // never released and reacquired in between — which is what makes
                // the whole read-modify-write atomic against a concurrent
                // WriteConcept/AppendToConceptAtomic call for the same concept.
                // The write is conditioned on the version read, so a writer
                // outside this lock (another process) cannot be overwritten;
                // its conflict re-reads and runs buildBody again (WithRetries).
                using (EnterWriteLock())
                {
                    return WriteThenNotify(subject, committed => WithRetries(subject, () =>
                    {
                        string frontmatterYaml;
                        string? currentBody;

                        // Strict UTF-8: a non-UTF-8 concept file throws
                        // DecoderFallbackException (caught by RunTool below)
                        // rather than being silently re-decoded and rewritten.
                        var current = ReadCurrent(target.Key);
                        if (current is { } existing)
                        {
                            // Fail-closed: if the existing concept has malformed
                            // frontmatter (hand-edited, or a prior partial/crashed
                            // write), OkfDocument.Parse throws DocumentParseException
                            // (caught by RunTool -> Error text) so this append is
                            // dropped rather than overwriting a possibly important
                            // file. This is stricter than a permissive bundle-load
                            // path, which would treat an unparseable file as absent
                            // and silently recreate it.
                            var existingDoc = OkfDocument.Parse(existing.Text);
                            frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                            currentBody = existingDoc.Body;
                        }
                        else
                        {
                            frontmatterYaml = frontmatterYamlIfCreating;
                            currentBody = null;
                        }

                        var newBody = buildBody(currentBody);
                        if (newBody is null)
                        {
                            return "Error: body must not be null.";
                        }

                        if (newBody.Contains('\0'))
                        {
                            return "Error: invalid body — it must not contain a null character.";
                        }

                        var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                        if (buildError is not null)
                        {
                            return buildError;
                        }

                        return WriteValidatedContentLocked(target, content!, ConditionFor(current), committed, existedBefore: current is not null);
                    }));
                }
            },
            () => subject);
    }
```

(b) `RecordVerifications`' write loop: a conflict prepares that concept again — replace

```csharp
                    var stamp = prepared[i];
                    subject = $"'{stamp.Target.Id}'";
                    var committed = false;
                    string writeResult;
                    try
                    {
                        writeResult = WriteValidatedContentLocked(stamp.Target, stamp.Content, BundleWriteCondition.IfMatch(stamp.Version), () => committed = true, existedBefore: true);
                    }
```

with

```csharp
                    var stamp = prepared[i];
                    var name = $"'{stamp.Target.Id}'";
                    subject = name;
                    var committed = false;
                    string writeResult;
                    try
                    {
                        // Conditioned on the version the stamp was prepared
                        // from. A conflict (a writer outside this lock) prepares
                        // THIS concept again from what is there now and retries;
                        // the concepts already written stay written.
                        var attempt = 0;
                        writeResult = WithRetries(name, () =>
                        {
                            if (++attempt > 1)
                            {
                                var prepareError = PrepareStamp(stamp.Target, stamp.ConceptId, by, stampedAt, out var fresh);
                                if (prepareError is not null)
                                {
                                    return prepareError;
                                }

                                stamp = fresh;
                            }

                            return WriteValidatedContentLocked(stamp.Target, stamp.Content, BundleWriteCondition.IfMatch(stamp.Version), () => committed = true, existedBefore: true);
                        });
                    }
```

(c) In `src/OKF4net.Agents/OkfBundleTools.cs`, `AppendToConceptAtomic`'s `buildBody` doc says the same — replace

```csharp
    /// not yet exist), returns the full new body to write. Invoked exactly
    /// once, inside the lock, against the freshly re-read current body —
    /// never a caller's own stale, pre-lock snapshot.
```

with

```csharp
    /// not yet exist), returns the full new body to write. Invoked once per
    /// attempt (at most <see cref="BundleConceptWriter.MaxAttempts"/>), inside
    /// the lock, each time against the freshly re-read current body — never a
    /// caller's own stale, pre-lock snapshot — so it must be a pure function
    /// of the body it receives.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterRetryTests|FullyQualifiedName~BundleConceptWriterProviderTests"`
Expected: `Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52` (19 retry tests — ten, and B48's four regressions in nine cases; the matrix now 6 × 2; Task 4a's tests).

Then the #131 test and the fifty-round test, twenty times in a row:

```bash
dotnet build tests/OKF4net.Tests -v q -nologo
for i in $(seq 1 20); do dotnet test tests/OKF4net.Tests --no-build --filter "FullyQualifiedName~Verification_reads_wait_for_an_in_progress_write|FullyQualifiedName~Two_writers_without_a_shared_lock" 2>&1 | grep -oE "Passed!|Failed!"; done | sort | uniq -c
```

Expected: `     20 Passed!` (5 tests per run). A red run is a regression, never a flake (spec §11, "Concurrence").

- [ ] **Step 6: CHANGELOG and README**

In `CHANGELOG.md`, under `### Added`, after Task 4a's bullet (it ends `` `NotSupportedException` for a writer over any other provider. ``), add:

```markdown
- **Conditional concept writes.** `BundleConceptWriter.WriteConcept(…,
  BundleWriteCondition)` (both the YAML and the `Frontmatter` forms):
  `IfMatch(version)` replaces a concept only if it has not changed since that
  version was read, `MustNotExist` creates it only if it does not exist yet.
  A condition that does not hold writes nothing and returns an `Error:` that
  tells the caller to read the concept (again); it is never retried. The
  three-argument overloads are unchanged and unconditional.
```

and under `### Changed`, after Task 4a's bullet (it ends `` a `RecordVerifications` batch now ends with `— already written: …`. ``), add:

```markdown
- **Read-modify-writes retry a conflict, and `buildBody` runs once per
  attempt** (spec §4). When another writer — another process, or a writer
  over another provider instance — commits between the read and the write,
  `AppendToConceptAtomic` and `RecordVerifications` read again and retry, at
  most 3 attempts, waiting 10–40 ms then 30–120 ms (`Thread.Sleep` on the
  caller's thread); after the third the conflict is an `Error: … kept
  changing while it was being written; the write was refused after 3
  attempts.` Only a refused write is retried: the write notification
  (`onWriteCommitted`) runs once, after the attempt that committed, and
  nothing it throws is retried. **Contract change:**
  `AppendToConceptAtomic`'s `buildBody`,
  documented as invoked exactly once, is now invoked once per attempt (at
  most 3), always on the freshly re-read body, and must be a pure function of
  the body it receives. The repository's own caller (memory capture in
  `OkfContextProvider`, `FileMemoryStore`) already is.
```

In `README.md`, after the paragraph Task 4a added (it ends `` read-only provider makes every write method throw `BundleReadOnlyException`. ``), continue the same paragraph with:

```markdown
`AppendToConceptAtomic` and `RecordVerifications` write conditioned on the
version they read and retry a conflict at most three times, re-reading each
time (so `buildBody` must be a pure function of the body it receives);
`WriteConcept(…, BundleWriteCondition)` lets a caller condition a rewrite on
the version it read itself, and is never retried.
```

- [ ] **Step 7: Verify and commit**

```bash
for f in src/OKF4net/BundleConceptWriter.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/BundleConceptWriterRetryTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0 (3 351 tests in the replay, 28 skipped)
git add src/OKF4net/BundleConceptWriter.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/BundleConceptWriterRetryTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs CHANGELOG.md README.md
git commit -F - <<'MSG'
feat(writer): conditional concept writes, bounded retries, buildBody once per attempt

WriteConcept gains overloads taking a BundleWriteCondition (IfMatch, MustNotExist,
None); a condition that does not hold returns an Error telling the caller to
read the concept, and is never retried. AppendToConceptAtomic and
RecordVerifications retry a conflict at most three times, re-reading each time
and waiting 10-40 ms then 30-120 ms; buildBody is invoked once per attempt and
documented as a pure function of the body it receives. RecordVerifications
prepares only the conflicting concept again. The delay goes through an
internal Sleep seam. Only the storage operation is retried or read as the
caller's condition refused: the write notification runs once, after the
retries.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 4c: `VerificationOutcome.Report`

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`VerificationOutcome`, `RecordVerifications`)
- Test: `tests/OKF4net.Tests/VerificationReportTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 4b; Task 1's `BundleWriteReport`, `BundleWriteFailure`, `BundleWriteReport.Empty`; `TempDir.TryCreateJunctionToExternalDir`.
- Produces: `public BundleWriteReport VerificationOutcome.Report { get; init; }` — never null (B34). `VerificationOutcome`'s positional members, `Recorded`, `Message`, `Records` and the `— already written: …` suffix are unchanged.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/VerificationReportTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="VerificationOutcome.Report"/> (spec §1, §4, decision B14): a verification batch is
/// several objects with no transaction, so the outcome says object by object what was written and
/// what failed, beside the unchanged <see cref="VerificationOutcome.Records"/> and the message's
/// <c>— already written: …</c> suffix.
/// </summary>
public class VerificationReportTests
{
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static BundleConceptWriter WriterOver(IBundleProvider p) =>
        new(p) { Sleep = _ => { }, UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    [Fact]
    public void A_recorded_batch_reports_every_key_written_in_order()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("b.md", Doc).Add("a/c.md", Doc);

        var outcome = WriterOver(storage).RecordVerifications(["b", "a/c"], "human:ada");

        Assert.True(outcome.Recorded, outcome.Message);
        Assert.Equal([K("b.md"), K("a/c.md")], outcome.Report.Written);
        Assert.Empty(outcome.Report.Deleted);
        Assert.Empty(outcome.Report.Failed);
        Assert.True(outcome.Report.Succeeded);
    }

    [Fact]
    public void A_batch_refused_before_its_first_write_reports_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc);

        var outcome = WriterOver(storage).RecordVerifications(["a", "missing"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: concept \"missing\" does not exist.", outcome.Message);
        Assert.Same(BundleWriteReport.Empty, outcome.Report);
    }

    [Fact]
    public void A_write_failing_mid_batch_is_reported_with_its_reason_beside_what_was_written()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc).Add("c.md", Doc);
        storage.FailWrite(2, new IOException("disk full"));

        var outcome = WriterOver(storage).RecordVerifications(["a", "b", "c"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: disk full — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal([K("a.md")], outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("b.md"), failed.Key);
        Assert.Equal("disk full", failed.Reason);
        Assert.False(outcome.Report.Succeeded);
    }

    [Fact]
    public void The_report_of_a_default_outcome_is_empty_never_null()
    {
        Assert.Same(BundleWriteReport.Empty, default(VerificationOutcome).Report);
    }

    /// <summary>
    /// B14 and B15 on the real filesystem: the second concept's directory is swapped for a
    /// junction after its parent was resolved (the late-guard hook), the provider refuses the path,
    /// and the batch reports it with the writer's reparse-point message and the same
    /// <c>— already written: …</c> suffix as before the write moved behind the provider.
    /// </summary>
    [SkippableFact]
    public void A_path_refused_by_the_provider_mid_batch_keeps_the_writers_message_and_suffix()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("a/x.md", Doc);
        tmp.Write("b/y.md", Doc);
        var writer = new BundleConceptWriter(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };
        var writes = 0;
        var swapped = false;
        writer.BeforeLateReparseCheckForTest = () =>
        {
            if (++writes == 2)
            {
                Directory.Move(Path.Combine(tmp.Path, "b"), Path.Combine(tmp.Path, "b-moved"));
                swapped = tmp.TryCreateJunctionToExternalDir("b", external.Path);
            }
        };

        var outcome = writer.RecordVerifications(["a/x", "b/y"], "human:ada");

        Skip.IfNot(swapped, "junctions unavailable");
        Assert.Equal(
            "Error: 'b/y' resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed. — already written: a/x",
            outcome.Message);
        Assert.Equal([K("a/x.md")], outcome.Report.Written);
        Assert.Equal(K("b/y.md"), Assert.Single(outcome.Report.Failed).Key);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
        Assert.Equal(Doc, File.ReadAllText(Path.Combine(tmp.Path, "b-moved", "y.md")));
    }

    /// <summary>
    /// A write notification that throws after the commit (external review, F2, F6) never turns the
    /// committed concept into a failure: the reviewer's case — one concept, the notification
    /// always failing — and a batch whose second notification of three fails both report every
    /// key written, once, nothing failed, and <see cref="VerificationOutcome.Recorded"/> true,
    /// whether the notification threw an I/O error or a conflict of another operation.
    /// </summary>
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, true)]
    public void A_notification_failing_after_a_commit_is_written_in_the_report_never_failed(int concepts, int failing, bool conflict)
    {
        using var tmp = new TempDir();
        string[] ids = [.. Enumerable.Range(0, concepts).Select(i => $"c{i}")];
        foreach (var id in ids)
        {
            tmp.Write(id + ".md", Doc);
        }

        Exception failure = conflict ? new BundleWriteConflictException(K("elsewhere.md")) : new IOException("notification failed");
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            if (++notifications == failing)
            {
                throw failure;
            }
        });

        var outcome = writer.RecordVerifications(ids, "human:ada", "2026-10-19T00:00:00Z");

        Assert.True(outcome.Recorded);
        Assert.Equal([.. ids.Select(id => K(id + ".md"))], outcome.Report.Written);
        Assert.Empty(outcome.Report.Failed);
        Assert.True(outcome.Report.Succeeded);
        Assert.Equal(concepts, notifications);
        Assert.EndsWith("; the write notification failed: " + failure.Message, outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A notification that fails on every write (F6) does not hide the real conflict of the next
    /// concept: the first is written, the second — a conflict on every attempt — is the report's
    /// <c>Failed</c> entry with the writer's reason, nothing else.
    /// </summary>
    [Fact]
    public void A_failing_notification_leaves_the_next_concepts_real_conflict_in_the_report()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var notifications = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () =>
        {
            notifications++;
            throw new BundleWriteConflictException(K("elsewhere.md"));
        })
        {
            Sleep = _ => { },
        };

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal([K("a.md")], outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("b.md"), failed.Key);
        Assert.Equal("'b' kept changing while it was being written; the write was refused after 3 attempts.", failed.Reason);
        Assert.Equal(1, notifications);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only — `CS1061`: `VerificationOutcome` does not contain a definition for `Report`.

- [ ] **Step 3: The report** — in `src/OKF4net/BundleConceptWriter.cs`:

(a) replace the one-line declaration

```csharp
public readonly record struct VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records);
```

with

```csharp
public readonly record struct VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records)
{
    private readonly BundleWriteReport? _report;

    /// <summary>
    /// What the batch wrote, object by object (spec §1, §4): the keys of the concepts whose write
    /// was committed, in order — the concepts of <see cref="Records"/>, a concept whose write
    /// notification then failed included — and, when a write failed,
    /// that concept's key with the reason (<see cref="Message"/> without its <c>Error: </c> prefix
    /// and without the <c>— already written: …</c> suffix). Empty for a batch refused before its
    /// first write; the concepts after a failed one were not attempted and are in neither list.
    /// Never <see langword="null"/>, <see langword="default"/> included.
    /// </summary>
    public BundleWriteReport Report
    {
        get => _report ?? BundleWriteReport.Empty;
        init => _report = value;
    }
}
```

(b) in `RecordVerifications`, replace

```csharp
        var records = new List<VerificationRecord>(conceptIds.Count);
```

with

```csharp
        var records = new List<VerificationRecord>(conceptIds.Count);
        var written = new List<BundleKey>(conceptIds.Count);
        BundleWriteFailure? failure = null;
```

(c) replace

```csharp
                        continue;
                    }

                    return records.Count == 0
```

with

```csharp
                        continue;
                    }

                    failure = new BundleWriteFailure(stamp.Target.Key, writeResult["Error: ".Length..]);
                    return records.Count == 0
```

(d) replace

```csharp
                        records.Add(new VerificationRecord(stamp.ConceptId, stampedAt, stamp.ReplacedAt));
```

with

```csharp
                        records.Add(new VerificationRecord(stamp.ConceptId, stampedAt, stamp.ReplacedAt));
                        written.Add(stamp.Target.Key);
```

(e) and the outcome — replace

```csharp
        return message.StartsWith("Error:", StringComparison.Ordinal)
            ? new VerificationOutcome(false, message, records)
            : new VerificationOutcome(true, message, records);
```

with

```csharp
        var report = written.Count == 0 && failure is null
            ? BundleWriteReport.Empty
            : new BundleWriteReport(written, [], failure is null ? [] : [failure]);
        return message.StartsWith("Error:", StringComparison.Ordinal)
            ? new VerificationOutcome(false, message, records) { Report = report }
            : new VerificationOutcome(true, message, records) { Report = report };
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~VerificationReportTests|FullyQualifiedName~RecordVerificationTests|FullyQualifiedName~OkfVerifyToolTests"`
Expected: `Passed!  - Failed:     0, Passed:    87, Skipped:     0, Total:    87` — the ten new test cases (six tests, and the four rows of the notification theory) (the junction one runs on Windows without any privilege, and on Linux as a directory symlink), and the verification suites unchanged.

- [ ] **Step 5: CHANGELOG** — under `### Added`, after Task 4b's bullet (it ends `  three-argument overloads are unchanged and unconditional.`), add:

```markdown
- **`VerificationOutcome.Report`** (a `BundleWriteReport`): the keys of the
  concepts a `RecordVerifications` batch wrote, in order — a concept whose
  write notification failed after its commit included — and the one whose
  write failed, with the reason. `Recorded`, `Message` and `Records` are
  unchanged, and a partial failure's message still ends with `— already
  written: …`.
```

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/VerificationReportTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0 (3 361 tests in the replay, 28 skipped)
```

Then the Linux container command with the filter `FullyQualifiedName~BundleConceptWriter|FullyQualifiedName~RecordVerification|FullyQualifiedName~VerificationReport|FullyQualifiedName~OKF4net.Tests.Providers`, both passes `Failed: 0`. Then the differential check of the lot A discipline — the same `okf verify` and `okf index` commands, from a build of `origin/dev` and from this branch, on two fresh copies of `bundles/acme_retail`, compared byte for byte (`$BASE` is a checkout of `origin/dev`, built with `dotnet build src/OKF4net.Cli`; this worktree is built the same way). Save this script outside the worktree as `differential.sh`:

```bash
#!/bin/sh
# usage: differential.sh <base worktree> <branch worktree> <scratch dir>
# Runs the same okf verify/index commands on two fresh copies of bundles/acme_retail, one per build, and compares every byte.
BASE="$1"; BRANCH="$2"; OUT="$3"
rm -rf "$OUT"; mkdir -p "$OUT"
for side in base branch; do
  if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
  OKF="dotnet $WT/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll"
  cp -r "$WT/bundles/acme_retail" "$OUT/$side"
  cd "$OUT"
  {
    $OKF verify $side metrics/revenue metrics/gross-margin --by human:ada --at 2026-10-19T00:00:00Z; echo "exit=$?"
    $OKF verify $side metrics/revenue --by human:ada --at 2026-10-19T01:00:00Z; echo "exit=$?"
    $OKF verify $side metrics/revenue metrics/nope --by human:ada --at 2026-10-19T02:00:00Z; echo "exit=$?"
    $OKF verify $side metrics/revenue metrics/revenue --by human:ada --at 2026-10-19T02:00:00Z; echo "exit=$?"
    $OKF index $side; echo "exit=$?"
  } > "$side.out" 2>&1
  sed -i "s#$side#BUNDLE#g" "$side.out"
  cd - > /dev/null
done
cmp "$OUT/base.out" "$OUT/branch.out" && echo "outputs identical ($(wc -l < "$OUT/base.out") lines)"
diff -r "$OUT/base" "$OUT/branch" > /dev/null && echo "bundles identical ($(find "$OUT/base" -type f | wc -l) files)"
```

Run it from this worktree's root as `sh /path/to/differential.sh "$BASE" "$(pwd)" "$TMP/okf-diff"` (Git Bash). Expected: `outputs identical (18 lines)` and `bundles identical (18 files)` — verify's records, its replacement suffix, its two refusals, and `index`'s files, all unchanged.

```bash
git add src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/VerificationReportTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(writer): VerificationOutcome reports what a verification batch wrote and what failed

VerificationOutcome gains Report, a BundleWriteReport: the keys of the
concepts written, in order, and the one whose write failed with its reason.
It is BundleWriteReport.Empty for a batch that wrote nothing, default
included. Recorded, Message, Records and the already-written suffix are
unchanged.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 5: `log.md` through the writer

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`AppendLog`, `LogKey`, and the log field guards moved from the tool)
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`AppendLog` delegates; the guards leave; the hook forwards)
- Modify: `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs` (one expected string, Deviation 2), `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` (the matrix row)
- Test: `tests/OKF4net.Tests/BundleConceptWriterLogTests.cs`
- Modify: `CHANGELOG.md`, `README.md`

**Interfaces:**
- Consumes: Tasks 4a–4c (`WithRetries`, `ReadCurrent`, `ConditionFor`, `GuardWritable`, `RunTool`, `BundleTreeCheck`); lot A's `ChangeLog`, `LogDay`, `LogEntry`.
- Produces:
  - `public string BundleConceptWriter.AppendLog(string kind, string text, DateOnly? date = null)` — `Appended a '{folded kind}' entry under {yyyy-MM-dd} in log.md.` or `Error: …` (B12, B35); throws `BundleReadOnlyException` on a read-only provider.
  - `internal const string BundleConceptWriter.LogKey = "log.md"`; `internal static string? GuardLogField(string value, string fieldName)`, `internal static bool IsBidiControl(char c)`, `internal static string FoldLogField(string value)`, `private static bool ContainsBidiControl(string value)` — moved verbatim from `OkfBundleTools`.
  - `OkfBundleTools.AppendLog(kind, text)` is `_writer.AppendLog(kind, text)`; its `[Description]` attributes and result strings are unchanged; the cache is still invalidated through `onWriteCommitted`; `OkfBundleTools.BeforeLateReparseCheckForTest` forwards to the writer's.
  - `OkfContextProvider.CaptureMemory` is unchanged: it still calls `AppendToConceptAtomic` then `AppendLog` on the tools.

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/BundleConceptWriterLogTests.cs` (then `unix2dos -q` it):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="BundleConceptWriter.AppendLog"/> (spec §3, §9; decision B12): <c>log.md</c> is written
/// by the writer, through the provider, conditioned on the version read and retried on a conflict,
/// with the field guards and result strings <c>OkfBundleTools.AppendLog</c> had (that tool now
/// delegates here, and its own tests pin the rest of the behaviour).
/// </summary>
public class BundleConceptWriterLogTests
{
    private static readonly DateTime Noon = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static (BundleConceptWriter Writer, List<TimeSpan> Delays) WriterOver(IBundleProvider p)
    {
        var delays = new List<TimeSpan>();
        return (new BundleConceptWriter(p) { Sleep = delays.Add, UtcNow = () => Noon }, delays);
    }

    [Fact]
    public void The_log_is_created_then_extended_through_the_provider_under_the_version_read()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var fired = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => fired++) { UtcNow = () => Noon };

        Assert.Equal("Appended a 'Creation' entry under 2026-07-22 in log.md.", writer.AppendLog("Creation", "Bootstrapped."));
        Assert.Equal("Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Second."));
        Assert.Equal("Appended a 'Update' entry under 2026-07-23 in log.md.", writer.AppendLog("Update", "Next day.", new DateOnly(2026, 7, 23)));

        Assert.Equal(3, fired);
        Assert.Equal(
            ["read log.md", "write log.md MustNotExist", "read log.md", "write log.md IfMatch", "read log.md", "write log.md IfMatch"],
            storage.Calls);
        var log = ChangeLog.Parse(Text(storage, "log.md"));
        Assert.Equal(["2026-07-23", "2026-07-22"], log.Days.Select(d => d.Date));
        Assert.Equal(["Bootstrapped.", "Second."], log.Days[1].Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("", "text", "Error: invalid kind — it must not be empty.")]
    [InlineData("Update", "a\nb", "Error: invalid text — it must not contain a line break (this would let it forge fake '## date' or '* entry' lines in log.md).")]
    public void The_field_guards_moved_with_their_messages_and_write_nothing(string kind, string text, string expected)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        Assert.Equal(expected, WriterOver(storage).Writer.AppendLog(kind, text));
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void An_entry_appended_by_another_writer_meanwhile_is_kept()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var (writer, delays) = WriterOver(storage);
        writer.AppendLog("Creation", "First.");
        storage.BeforeNextWrite(p => p.Add("log.md", "# Log\n\n## 2026-07-22\n\n* **Creation**: First.\n* **Update**: From elsewhere.\n"));

        Assert.Equal("Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Mine."));

        Assert.Equal(["First.", "From elsewhere.", "Mine."], ChangeLog.Parse(Text(storage, "log.md")).Days.Single().Entries.Select(e => e.Text));
        Assert.Single(delays);
    }

    [Fact]
    public void A_log_that_keeps_changing_is_refused_after_three_attempts()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("log.md", "# Log\n");
        var (writer, delays) = WriterOver(storage);
        for (var nth = 1; nth <= 3; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("log.md")));
        }

        Assert.Equal("Error: log.md kept changing while it was being written; the write was refused after 3 attempts.", writer.AppendLog("Update", "Mine."));
        Assert.Equal("# Log\n", Text(storage, "log.md"));
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public void Creating_the_log_where_objects_already_sit_under_its_name_is_refused()
    {
        var storage = new InMemoryBundleProvider(BundleKeyConstraints.FileDirectoryConflict, canWrite: true).Add("log.md/x.md", "x");
        var result = WriterOver(storage).Writer.AppendLog("Update", "Mine.");
        Assert.Equal("Error: \"log.md\" cannot be created: objects exist under it (such as \"log.md/x.md\"), so it cannot also be an object.", result);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_that_is_not_UTF8_is_refused_and_left_as_it_was()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "log.md");
        byte[] bytes = [.. "# Log\n\n## 2026-07-21\n\n* caf"u8, 0xE9, .. "\n"u8];
        File.WriteAllBytes(path, bytes);

        var result = new BundleConceptWriter(tmp.Path) { UtcNow = () => Noon }.AppendLog("Update", "Mine.");

        Assert.StartsWith("Error: ", result, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(["log.md"], Directory.GetFileSystemEntries(tmp.Path).Select(Path.GetFileName));
    }

    /// <summary>
    /// A write notification that throws once the entry is committed (external review, F2, F6):
    /// the result is <c>Error: {message}</c>, as for the other string methods, but the entry is in
    /// <c>log.md</c>, appended once — the failure is not a conflict of the log's write, so nothing
    /// is retried, even when what the notification throws is a conflict of another operation
    /// (B48). Failing once, the next entry is appended normally; failing always, each entry is
    /// still appended once.
    /// </summary>
    [Theory]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("conflict", false)]
    [InlineData("conflict", true)]
    public void A_notification_failing_after_the_commit_is_an_error_text_and_the_entry_is_appended_once(string kind, bool always)
    {
        using var tmp = new TempDir();
        Exception failure = kind == "io" ? new IOException("notification failed") : new BundleWriteConflictException(K("elsewhere.md"));
        var notifications = 0;
        var delays = new List<TimeSpan>();
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            if (++notifications == 1 || always)
            {
                throw failure;
            }
        })
        { Sleep = delays.Add, UtcNow = () => Noon };

        Assert.Equal("Error: " + failure.Message, writer.AppendLog("Update", "Mine."));
        Assert.Equal(1, notifications);
        Assert.Equal(always ? "Error: " + failure.Message : "Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Then."));
        Assert.Equal(2, notifications);
        Assert.Empty(delays);
        var lines = File.ReadAllText(Path.Combine(tmp.Path, "log.md")).Split('\n');
        Assert.Single(lines, line => line.Contains("Mine.", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("Then.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cancellation the notification raises is not converted (B48): it propagates unchanged once
    /// the entry is committed — the entry is in <c>log.md</c>, once, and nothing was retried.
    /// </summary>
    [Fact]
    public void A_notification_cancelled_after_the_commit_propagates_and_the_entry_stays()
    {
        using var tmp = new TempDir();
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw new OperationCanceledException();
        })
        { UtcNow = () => Noon };

        Assert.Throws<OperationCanceledException>(() => writer.AppendLog("Update", "Mine."));
        Assert.Equal(1, notifications);
        Assert.Single(File.ReadAllText(Path.Combine(tmp.Path, "log.md")).Split('\n'), line => line.Contains("Mine.", StringComparison.Ordinal));
    }
}
```

and the read-only matrix in `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` gains its last row — replace

```csharp
        ["WriteConcept(frontmatter, condition)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
    };
```

with

```csharp
        ["WriteConcept(frontmatter, condition)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
        ["AppendLog"] = w => w.AppendLog("Update", "entry"),
    };
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only — `CS1061`: `BundleConceptWriter` does not contain a definition for `AppendLog`.

- [ ] **Step 3: `AppendLog` in the writer** — in `src/OKF4net/BundleConceptWriter.cs`:

(a) the usings — replace

```csharp
using System.Collections.Concurrent;
using OKF4net.Internal;
```

with

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using OKF4net.Internal;
```

(b) right after `PrepareStamp`'s closing brace (the `    }` that follows `        return null;`, after `        prepared = new PreparedStamp(target, conceptId, content, current.Version, replacedAt);`), insert a blank line and the following — `AppendLog`, then the four log field members **moved verbatim** from `OkfBundleTools` (Step 4 removes them there), their doc's two references to tool members turned into `<c>` text, three of them `internal`, and the two sentences that said "this tool" now saying "the log.md write path":

```csharp
    /// <summary>The key of the bundle root's change log (spec §9).</summary>
    internal const string LogKey = "log.md";

    /// <summary>
    /// Appends one entry to the bundle root's <c>log.md</c> (spec §9) under
    /// <paramref name="date"/> (today, UTC, by default), creating the file if it
    /// does not yet exist. If a heading for that date already exists, the entry
    /// is appended to the end of that day's entries (days are newest-first by
    /// convention, but entries within a day stay chronological). The single
    /// write path of <c>log.md</c> (spec §3): the read-modify-write runs under
    /// this writer's lock, is written through the provider conditioned on the
    /// version read (<see cref="BundleWriteCondition.IfMatch"/>, or
    /// <see cref="BundleWriteCondition.MustNotExist"/> for a new log), and is
    /// retried on a conflict like <see cref="AppendToConceptAtomic"/>. The
    /// existing file, if any, is read as strict UTF-8, then re-rendered through
    /// <see cref="ChangeLog.ToMarkdown"/> — the strict §9 model — so any
    /// non-conforming prose or comments in a hand-authored <c>log.md</c> are not
    /// preserved. Never throws for expected errors (a <paramref name="kind"/> or
    /// <paramref name="text"/> that <see cref="GuardLogField"/> refuses — empty,
    /// or carrying a line break, a control character or a bidirectional control
    /// character — a <c>log.md</c> that fails strict UTF-8 decoding, a path the
    /// provider refuses): those are reported as a plain-text message instead.
    /// A write notification (<c>onWriteCommitted</c>) runs once the entry is committed,
    /// after the attempt that committed it; an exception it throws that the writer
    /// converts (a conflict raised by another operation included) is reported the same
    /// way, <c>Error: {message}</c>, although the entry is in <c>log.md</c>, appended
    /// once: it is never retried (decision B48).
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    /// <param name="date">The day the entry goes under; <see langword="null"/>: the UTC date of <see cref="UtcNow"/>.</param>
    /// <returns><c>Appended a '{kind}' entry under {date} in log.md.</c>, with the folded kind, or an <c>Error: ...</c> message.</returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string AppendLog(string kind, string text, DateOnly? date = null)
    {
        GuardWritable();
        if (GuardLogField(kind, "kind") is { } kindError)
        {
            return kindError;
        }

        if (GuardLogField(text, "text") is { } textError)
        {
            return textError;
        }

        var day = (date ?? DateOnly.FromDateTime(UtcNow())).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var foldedKind = FoldLogField(kind);
        var entry = new LogEntry(foldedKind, FoldLogField(text));
        var key = BundleKey.Parse(LogKey);
        return RunTool(
            () =>
            {
                if (_validator.CheckKey(key) is { } refused)
                {
                    return $"Error: log.md cannot be written under the bundle's key policy: {refused}.";
                }

                // Serialized under the bundle write lock (shared with every
                // other write of this writer): without it, two concurrent
                // AppendLog calls could both read the same "before" text, each
                // append their own entry to it, and the second write would
                // silently clobber the first (a lost update). A writer outside
                // the lock is met by the version condition instead.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(LogKey, committed => WithRetries(LogKey, () =>
                    {
                        // Strict UTF-8, matching ChangesSince: a non-UTF-8
                        // log.md throws DecoderFallbackException (caught by
                        // RunTool) instead of being silently re-decoded with
                        // U+FFFD replacement characters and rewritten that way.
                        var current = ReadCurrent(key);

                        // ChangeLog.Parse is permissive (never throws); used here
                        // only to locate the day (if any) among the existing entries.
                        var changeLog = ChangeLog.Parse(current?.Text ?? string.Empty);
                        var days = changeLog.Days.ToList();
                        var dayIndex = days.FindIndex(d => string.Equals(d.Date, day, StringComparison.Ordinal));
                        if (dayIndex >= 0)
                        {
                            var existing = days[dayIndex];
                            days[dayIndex] = existing with { Entries = [.. existing.Entries, entry] };
                        }
                        else
                        {
                            // New LogDay at the head: days are newest-first (§9).
                            days.Insert(0, new LogDay(day, [entry]));
                        }

                        if (current is null && BundleTreeCheck.CheckCreation(_provider, _validator, key, _limits) is { } conflict)
                        {
                            return $"Error: {conflict}.";
                        }

                        // Not WriteValidatedContentLocked, the primitive of concept
                        // writes and verification stamps: log.md is written by this
                        // conditioned Write of its own. Its commit is reported to
                        // WriteThenNotify, as AppendToConceptAtomic's is, so the
                        // notification runs once, after the retries (decision B48).
                        _provider.Write(key, OkfEncodings.NoBom.GetBytes(new ChangeLog(changeLog.Title, days).ToMarkdown()), ConditionFor(current));
                        committed();

                        // The FOLDED kind, not the raw argument: this message is
                        // itself a line-structured result, and echoing the
                        // argument verbatim would put back the separator the
                        // write just removed.
                        return $"Appended a '{foldedKind}' entry under {day} in log.md.";
                    }));
                }
            },
            () => LogKey);
    }

    /// <summary>
    /// Validates one <see cref="AppendLog"/> argument, returning the rejection
    /// message or <see langword="null"/> when it is acceptable.
    ///
    /// Both of that method's arguments get the identical checks, so they share
    /// one validator rather than two copies that could drift.
    ///
    /// <para><b>Three treatments, one boundary.</b> A caller-supplied field is
    /// written into the user's repository AND echoed into a tool result, so
    /// every character that survives does so for a stated reason:</para>
    ///
    /// <list type="bullet">
    /// <item><c>\n</c> and <c>\r</c> — <b>REJECTED</b>. They split
    /// <c>log.md</c> on re-read: a newline lets an entry forge a fabricated
    /// <c>## &lt;date&gt;</c> heading or <c>* entry</c> bullet that a later
    /// <c>ChangeLog.Parse</c> reads back as genuine audit-trail history (§9).
    /// Rejected rather than stripped, so the caller learns the write did not
    /// happen.</item>
    /// <item>U+000C, U+0085, U+2028 and U+2029 — <b>FOLDED</b> to a space at
    /// the write (<see cref="FoldLogField"/>), not rejected. They cannot forge
    /// a §9 line, they only split a downstream renderer, and most callers
    /// cannot see them, so failing a call over one would cost more than it
    /// buys.</item>
    /// <item>Every other control character — <b>REJECTED</b>: ESC, backspace,
    /// BEL, VT, DEL and the rest of C0/C1. None has a legitimate use in a log
    /// entry, and each forges what a HUMAN reading the audit trail sees rather
    /// than what it says: <c>ESC[2K ESC[1A</c> rewrites the terminal line
    /// <c>cat log.md</c> just printed, backspace erases it. Folding them would
    /// silently rewrite the caller's words, and persisting them writes a
    /// rendering attack into the repository.</item>
    /// <item>Every bidirectional CONTROL character — <b>REJECTED</b>: U+061C,
    /// U+200E, U+200F, U+202A–U+202E and U+2066–U+2069
    /// (<see cref="IsBidiControl"/>). Each reorders the text a reader is shown
    /// without changing the bytes stored, so the <c>log.md</c> a human reads
    /// and the <c>log.md</c> a tool reads disagree. Ordinary right-to-left
    /// TEXT is NOT affected: Arabic and Hebrew letters carry their own strong
    /// direction and need none of these.</item>
    /// <item><c>\t</c> — <b>ACCEPTED, verbatim in the interior of the field.</b>
    /// The one control character a log message may legitimately carry. Not
    /// folded to a space the way the four soft separators are: this guard
    /// rewrites a caller's words only when the character would otherwise
    /// break structure, and an interior tab cannot — <c>ChangeLog.Parse</c>
    /// splits on LF, a tab is not a terminator, and it can never reach the
    /// START of a line (the renderer always emits <c>* </c> first), the only
    /// position where markdown would read it as an indented code block. A
    /// LEADING or TRAILING tab does not survive, though: <see cref="FoldLogField"/>
    /// trims the field before this guard or the writer ever sees it, exactly
    /// as it has always trimmed a leading/trailing space — so <c>"\tUpdate\t"</c>
    /// is written (and echoed) as <c>Update</c>, not <c>&lt;TAB&gt;Update&lt;TAB&gt;</c>.</item>
    /// </list>
    ///
    /// <para>The control-character half reuses
    /// <see cref="LineSafeText.ContainsControlCharacter"/> — the repo's shared
    /// predicate, already used at <c>okf_verify</c>'s <c>by</c> — rather than a
    /// second character list here, which is exactly the drift that predicate's
    /// own doc comment exists to prevent. Two adjustments are made to the value
    /// it sees, not to the predicate: it runs over the FOLDED text (by then the
    /// four soft separators are spaces, and the predicate would otherwise
    /// refuse U+2028/U+2029 and undo the fold), and over a probe in which tabs
    /// are spaces (the exemption above). The value WRITTEN keeps its tabs.</para>
    /// </summary>
    /// <param name="value">The argument's value.</param>
    /// <param name="fieldName">The argument's name, as it appears in the message.</param>
    internal static string? GuardLogField(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Error: invalid {fieldName} — it must not be empty.";
        }

        if (value.Contains('\0'))
        {
            return $"Error: invalid {fieldName} — it must not contain a null character.";
        }

        if (value.Contains('\n') || value.Contains('\r'))
        {
            return $"Error: invalid {fieldName} — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        var folded = FoldLogField(value);
        var probe = folded.Replace('\t', ' ');

        if (LineSafeText.ContainsControlCharacter(probe))
        {
            return $"Error: invalid {fieldName} — it must not contain a control character other than a "
                + "tab (ESC, backspace and BEL forge what a human reading log.md sees).";
        }

        if (ContainsBidiControl(folded))
        {
            return $"Error: invalid {fieldName} — it must not contain a bidirectional control character "
                + "(U+061C, U+200E, U+200F, U+202A-U+202E, U+2066-U+2069 reorder the stored text on "
                + "display; ordinary right-to-left text needs none of them).";
        }

        return null;
    }

    /// <summary>
    /// True when the value carries any Unicode bidirectional CONTROL
    /// character — see <see cref="IsBidiControl"/> for the list and for why
    /// the whole class is refused rather than the two overrides alone.
    ///
    /// Local to the log.md write path rather than added to
    /// <see cref="LineSafeText.ContainsControlCharacter"/>: none of these code
    /// points is a <see cref="char.IsControl(char)"/> character, so putting
    /// them there would widen the shared cross-assembly predicate that also
    /// gates §7 actors and <c>at</c> timestamps — a decision for those call
    /// sites, not one this write path may take on their behalf.
    /// </summary>
    /// <param name="value">The guarded argument's value.</param>
    private static bool ContainsBidiControl(string value)
    {
        foreach (var c in value)
        {
            if (IsBidiControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The twelve Unicode bidirectional control characters: the marks U+061C
    /// (ALM), U+200E (LRM) and U+200F (RLM); the embeddings and overrides
    /// U+202A (LRE), U+202B (RLE), U+202C (PDF), U+202D (LRO) and U+202E
    /// (RLO); and the isolates U+2066 (LRI), U+2067 (RLI), U+2068 (FSI) and
    /// U+2069 (PDI).
    ///
    /// <para>The WHOLE class, not just the two overrides: an embedding or an
    /// isolate reorders a rendered line as effectively as U+202E, a
    /// terminator can unbalance one an author opened, and a mark reorders a
    /// neutral run. A rule that stopped at "override" would refuse
    /// <c>RLO</c> and pass <c>RLE</c>, which is not a distinction any reader
    /// of a rendered <c>log.md</c> can see.</para>
    ///
    /// <para><b>One list, two call sites</b> — <see cref="ContainsBidiControl"/>
    /// (the write guard) and <c>OkfBundleTools.NeedsVisibleEscape</c> (the read
    /// rendering). They must agree: a character the write refuses is exactly a
    /// character the read has to make visible when an older build, another
    /// producer or a human already put it in the file.</para>
    /// </summary>
    /// <param name="c">The character to classify.</param>
    internal static bool IsBidiControl(char c) =>
        // Numeric constants on purpose: every one of these is invisible in
        // source, in a diff and in a review (the convention
        // Internal/LineSafeText.cs sets).
        c is (char)0x061C or (char)0x200E or (char)0x200F
            or (char)0x202A or (char)0x202B or (char)0x202C or (char)0x202D or (char)0x202E
            or (char)0x2066 or (char)0x2067 or (char)0x2068 or (char)0x2069;

    /// <summary>
    /// The four separators <see cref="GuardLogField"/> deliberately does NOT
    /// reject, folded to a single space before the value is written. Leading
    /// and trailing whitespace, tabs included, is removed first by the
    /// <see cref="string.Trim()"/> this method also does — the same trimming
    /// an ordinary leading/trailing space has always had, and the reason a
    /// tab is "verbatim" only in the interior of a field (see the <c>\t</c>
    /// item on <see cref="GuardLogField"/>).
    ///
    /// <para><b>Why the asymmetry.</b> <c>\n</c> and <c>\r</c> are REFUSED
    /// (<see cref="GuardLogField"/>): <c>ChangeLog.Parse</c> is LF-line-based,
    /// so those two are the characters that would make a later read back a
    /// forged <c>## date</c> heading or <c>* entry</c> bullet as genuine
    /// audit-trail history (§9), and a caller who sent one needs to learn the
    /// write did not happen. U+000C, U+0085, U+2028 and U+2029 cannot do that —
    /// the §9 parser does not split on them — but they DO split a downstream
    /// markdown or JavaScript renderer of the same file, and they were
    /// persisted verbatim into the user's repository, where every other
    /// consumer inherits them. Refusing them too would fail a call over a
    /// character most callers cannot see, so they are folded and the call
    /// succeeds. Same rule as the read side (<c>OkfBundleTools.OneLine</c>): nothing
    /// downstream can start a new line.</para>
    ///
    /// <para><see cref="string.ReplaceLineEndings(string)"/> rather than a
    /// hand-rolled set: it is the same helper the rendering side uses, and by
    /// the time this runs <see cref="GuardLogField"/> has already refused the
    /// only two terminators it folds that we do not want folded silently.</para>
    /// </summary>
    /// <param name="value">The guarded argument's value.</param>
    internal static string FoldLogField(string value) => value.Trim().ReplaceLineEndings(" ");
```

- [ ] **Step 4: The tool delegates** — in `src/OKF4net.Agents/OkfBundleTools.cs`:

(a) the late-guard hook needs no field of its own any more — replace

```csharp
    internal Action? BeforeLateReparseCheckForTest
    {
        get => _beforeLateReparseCheckForTest;
        set
        {
            _beforeLateReparseCheckForTest = value; // still consulted by AppendLog's own late re-check
            _writer.BeforeLateReparseCheckForTest = value;
        }
    }

    private Action? _beforeLateReparseCheckForTest;
```

with

```csharp
    internal Action? BeforeLateReparseCheckForTest
    {
        get => _writer.BeforeLateReparseCheckForTest;
        set => _writer.BeforeLateReparseCheckForTest = value;
    }
```

(b) delete the four log field members and their docs, which Step 3 moved: everything from the `    /// <summary>` line above `    /// Validates one <see cref="AppendLog"/> argument, returning the rejection` through the line `    private static string FoldLogField(string value) => value.Trim().ReplaceLineEndings(" ");` and the blank line after it.

(c) `AppendLog`'s summary and body — replace

```csharp
    /// but entries within a day stay chronological). The read-modify-write is
    /// serialized under the bundle write lock (shared with
    /// <see cref="WriteConcept"/> and <see cref="RegenerateIndexes"/>) so
    /// concurrent calls can't lose an update to each other. The existing file,
    /// if any, is read with the same strict-UTF-8 decoding <see cref="ChangesSince"/>
    /// uses, then re-rendered through <see cref="ChangeLog.ToMarkdown"/> — the
    /// strict §9 model — so any non-conforming prose or comments in a
    /// hand-authored <c>log.md</c> are not preserved. Never throws for
    /// expected errors (a <paramref name="kind"/> or <paramref name="text"/>
    /// that <see cref="GuardLogField"/> refuses — empty, or carrying a line
    /// break, a control character or a bidirectional control character — or a
    /// <c>log.md</c> that fails strict UTF-8 decoding) — those are reported as
    /// a plain-text message instead.
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    [Description("Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §9 model, so non-conforming prose or comments in a hand-authored log.md are not preserved.")]
    public string AppendLog(
        [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
        [Description("The entry text.")] string text)
    {
        if (GuardLogField(kind, "kind") is { } kindError)
        {
            return kindError;
        }

        if (GuardLogField(text, "text") is { } textError)
        {
            return textError;
        }

        return RunTool(() =>
        {
            var logPath = Path.Combine(BundleRoot, LogFilename);

            // Reject log.md itself being a reparse point (symlink/junction),
            // e.g. a planted file symlink at bundleRoot/log.md pointing at an
            // external file: File.Exists/ReadAllBytes/WriteAllText below all
            // follow it, so without this check AppendLog would silently
            // overwrite whatever external file it points at. log.md always
            // lives directly at BundleRoot, so its only directory ancestor is
            // BundleRoot itself -- the ancestor walk stops there
            // immediately without checking anything, which is why the file
            // node itself (not its ancestor chain) is the check that matters
            // here; both are included for the same defense-in-depth shape as
            // WriteConcept's guard. Both strict: a log.md whose link status
            // cannot be read is refused like a link (a guard fails closed --
            // see ReparsePoints.IsReparsePointOrUninspectable).
            if (ReparsePoints.IsReparsePointOrUninspectable(logPath) || ReparsePoints.HasReparsePointOrUninspectableAncestor(BundleRoot, BundleRoot))
            {
                return "Error: log.md is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to write through it.";
            }

            var today = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var foldedKind = FoldLogField(kind);
            var entry = new LogEntry(foldedKind, FoldLogField(text));

            // Serialized under the bundle write lock (shared with WriteConcept and
            // RegenerateIndexes): without it, two concurrent AppendLog calls
            // could both read the same "before" text, each append their own
            // entry to it, and the second write would silently clobber the
            // first (a lost update). Locking the whole read-modify-write
            // makes it atomic.
            using (_writer.EnterWriteLock())
            {
                // Strict UTF-8, matching ChangesSince's AppendLogFileChanges:
                // a non-UTF-8 log.md throws DecoderFallbackException (caught
                // by RunTool below) instead of being silently re-decoded with
                // U+FFFD replacement characters and then rewritten that way.
                var existingText = File.Exists(logPath)
                    ? OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath))
                    : string.Empty;

                // ChangeLog.Parse is permissive (never throws); used here only to
                // locate today's day (if any) among the existing entries.
                var changeLog = ChangeLog.Parse(existingText);

                var days = changeLog.Days.ToList();
                var dayIndex = days.FindIndex(d => string.Equals(d.Date, today, StringComparison.Ordinal));
                if (dayIndex >= 0)
                {
                    var day = days[dayIndex];
                    days[dayIndex] = day with { Entries = [.. day.Entries, entry] };
                }
                else
                {
                    // New LogDay at the head: days are newest-first (§9).
                    days.Insert(0, new LogDay(today, [entry]));
                }

                BeforeLateReparseCheckForTest?.Invoke();

                // Late, best-effort re-check -- same TOCTOU gap as
                // BundleConceptWriter's own late re-check (see its remarks);
                // AppendLog has the identical validate-then-write shape
                // between the early check above (run before acquiring
                // the write lock) and the write below, just without an
                // intervening Directory.CreateDirectory call. log.md always
                // lives directly at BundleRoot, so the ancestor-walk half of
                // this re-check is a no-op here, same as the early check's
                // ancestor call above -- the check that matters is log.md
                // itself having been replaced with a reparse point in this
                // narrow window.
                var logParentDir = Path.GetDirectoryName(logPath);
                if ((!string.IsNullOrEmpty(logParentDir) && ReparsePoints.HasReparsePointOrUninspectableAncestor(BundleRoot, logParentDir))
                    || ReparsePoints.IsReparsePointOrUninspectable(logPath))
                {
                    return "Error: log.md resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.";
                }

                File.WriteAllText(logPath, new ChangeLog(changeLog.Title, days).ToMarkdown(), OkfEncodings.NoBom);
                _bundle = null;
            }

            // The FOLDED kind, not the raw argument: this message is itself a
            // line-structured tool result, and echoing the argument verbatim
            // would put back the separator the write just removed.
            return $"Appended a '{foldedKind}' entry under {today} in log.md.";
        });
    }
```

with

```csharp
    /// but entries within a day stay chronological). A thin delegate onto
    /// <see cref="BundleConceptWriter.AppendLog"/>, the single write path of
    /// <c>log.md</c>: the read-modify-write is serialized under the bundle
    /// write lock (shared with <see cref="WriteConcept"/> and
    /// <see cref="RegenerateIndexes"/>) so concurrent calls can't lose an
    /// update to each other, and is written through the provider conditioned
    /// on the version read, retried on a conflict. The existing file,
    /// if any, is read with the same strict-UTF-8 decoding <see cref="ChangesSince"/>
    /// uses, then re-rendered through <see cref="ChangeLog.ToMarkdown"/> — the
    /// strict §9 model — so any non-conforming prose or comments in a
    /// hand-authored <c>log.md</c> are not preserved. Never throws for
    /// expected errors (a <paramref name="kind"/> or <paramref name="text"/>
    /// that <see cref="BundleConceptWriter.GuardLogField"/> refuses — empty, or carrying a line
    /// break, a control character or a bidirectional control character — or a
    /// <c>log.md</c> that fails strict UTF-8 decoding) — those are reported as
    /// a plain-text message instead.
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    [Description("Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §9 model, so non-conforming prose or comments in a hand-authored log.md are not preserved.")]
    public string AppendLog(
        [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
        [Description("The entry text.")] string text) =>
        _writer.AppendLog(kind, text);
```

(d) the read-side escaper reads the one bidi list where it now lives — replace

```csharp
        c != '\t' && (char.IsControl(c) || IsBidiControl(c));
```

with

```csharp
        c != '\t' && (char.IsControl(c) || BundleConceptWriter.IsBidiControl(c));
```

(e) and every remaining `<see cref="GuardLogField"/>` (four are left in the file) becomes `<see cref="BundleConceptWriter.GuardLogField"/>`, every remaining `<see cref="IsBidiControl"/>` (one) becomes `<see cref="BundleConceptWriter.IsBidiControl"/>` (Edit with replace-all on each).

- [ ] **Step 5: Run the new tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterLogTests|FullyQualifiedName~BundleConceptWriterProviderTests"`
Expected: `Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47` (12 log tests, the four rows of the notification theory of B46 and B48 and the cancellation test included; the matrix now 7 × 2; Task 4a's tests).

- [ ] **Step 6: Run the whole suite — one Windows test fails, for the reason of Deviation 2**

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`
Expected on Windows: `Failed: 1` — `OkfBundleToolsTests.AppendLog_refuses_a_log_file_that_is_an_uninspectable_junction`, `Expected: "Error: log.md is a reparse point (symlink"···` / `Actual: "Error: log.md resolves through a reparse "···`. Every other `AppendLog` test of `OkfWriteToolsTests` and `OkfBundleToolsTests` passes unchanged, the late-guard one (`AppendLog_late_guard_refuses_a_log_file_swapped_for_an_uninspectable_junction`) included: its hook now fires inside the provider's write (`ParentsResolvedForTest`, part A's Task 2b) and the message is the one it asserts.

In `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs`, replace

```csharp
        // early check on log.md itself refuses it with this message. (Not an
        // escape: without it the late strict check, or the OS refusing to write
        // a file over a directory, still refuses.)
        using var tmp = new TempDir();
        using var external = new TempDir();
        var tools = new OkfBundleTools(tmp.Path);
        using var junction = tmp.TryCreateUninspectableJunction("log.md", external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var result = tools.AppendLog("Update", "entry");

        Assert.Equal("Error: log.md is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to write through it.", result);
```

with

```csharp
        // early check on log.md itself refuses it. (Not an escape: without it
        // the late strict check, or the OS refusing to write a file over a
        // directory, still refuses.) Since log.md is written by
        // BundleConceptWriter.AppendLog through the provider (lot B), that early
        // check is the provider's STRICT check of the whole path, and every
        // refused path is one BundleAccessRefusedException, translated into the
        // writer's one reparse-point message (decision B15): the former "is a
        // reparse point ... refusing to write through it" wording is gone.
        using var tmp = new TempDir();
        using var external = new TempDir();
        var tools = new OkfBundleTools(tmp.Path);
        using var junction = tmp.TryCreateUninspectableJunction("log.md", external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var result = tools.AppendLog("Update", "entry");

        Assert.Equal("Error: log.md resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.", result);
```

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` — `Failed: 0` (3 375 tests in the replay, 28 skipped).

- [ ] **Step 7: CHANGELOG and README**

In `CHANGELOG.md`, under `### Added`, after Task 4c's bullet (it ends `  written: …`.`), add:

```markdown
- **`BundleConceptWriter.AppendLog(kind, text, date?)`**: appends one entry
  to the bundle root's `log.md` (spec §9), with the field guards and result
  strings of `OkfBundleTools.AppendLog`, which now delegates to it.
```

and under `### Changed`, after Task 4b's bullet (it ends `` `OkfContextProvider`, `FileMemoryStore`) already is. ``), add:

```markdown
- **`log.md` is written by `BundleConceptWriter`** (spec §3): `okf_append_log`
  (`OkfBundleTools.AppendLog`) delegates to `BundleConceptWriter.AppendLog`,
  so the log is written through the provider, atomically, conditioned on the
  version read and retried on a conflict like every other read-modify-write.
  Its messages are unchanged, except that a `log.md` which is itself a link
  or cannot be inspected is refused with the message for a path through one
  (`Error: log.md resolves through a reparse point (symlink/junction), or an
  entry that could not be inspected, inside the bundle, which is not
  allowed.`).
```

In `README.md`, after the sentence Task 4b added (it ends `the version it read itself, and is never retried.`), continue the paragraph with:

```markdown
`AppendLog(kind, text)` appends an entry to the root `log.md` the same way.
```

- [ ] **Step 8: Verify and commit**

```bash
for f in src/OKF4net/BundleConceptWriter.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/BundleConceptWriterLogTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Failed: 0
```

Then the Linux container command with the filter `FullyQualifiedName~BundleConceptWriter|FullyQualifiedName~RecordVerification|FullyQualifiedName~VerificationReport|FullyQualifiedName~OKF4net.Tests.Providers|FullyQualifiedName~OkfWriteTools|FullyQualifiedName~OkfVerifyTool|FullyQualifiedName~OkfBundleTools|FullyQualifiedName~FileMemoryStore|FullyQualifiedName~OkfContextProvider|FullyQualifiedName~CliTests|FullyQualifiedName~GoldenParity|FullyQualifiedName~IndexTests` for both passes — `Failed: 0` (in the replay: 1 074 tests, 42 and 38 skipped) — and once the whole suite as root (`Category!=ContainerIntegration` as the filter): `Failed: 0` (3 375 tests, 64 skipped). Then `dotnet test producers/OkfProducer.sln` — `Failed: 0` (826 passed, 15 skipped in the replay).

```bash
git add src/OKF4net/BundleConceptWriter.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/BundleConceptWriterLogTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs CHANGELOG.md README.md
git commit -F - <<'MSG'
feat(writer): log.md is appended by BundleConceptWriter through the provider

BundleConceptWriter.AppendLog appends one entry to the root log.md: the field
guards and result strings of OkfBundleTools.AppendLog, moved verbatim, then a
read through the provider, a conditioned write and the bounded retries of
every read-modify-write, then the write notification, once. OkfBundleTools.AppendLog delegates to it and still
invalidates its cache through onWriteCommitted; its read-side escaper reads
BundleConceptWriter.IsBidiControl.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 6: `IndexGenerator` on a provider

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`Load`'s listing loop moves into `internal static ListNames`)
- Modify: `src/OKF4net/IndexGenerator.cs` (a `using`, and everything from the summary of `RegenerateIndexes(string)` to the end of the class; `Synthesize`, `BuildIndexText`, `DefaultSynthesize` and `IndexEntry` are unchanged)
- Modify: `src/OKF4net/Internal/ReparsePoints.cs`, `src/OKF4net/Internal/PathOrdering.cs`, `src/OKF4net.Catalog/CatalogPathResolver.cs` (doc comments naming the generator's former walks)
- Modify: `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` (the read-only matrix covers the two provider overloads)
- Test: `tests/OKF4net.Tests/IndexGeneratorProviderTests.cs`
- Unchanged, must stay green: `tests/OKF4net.Tests/IndexTests.cs` (26 tests, the four late-reparse seam tests of B41 included), `GoldenParityTests` (the index golden counts exactly 8 files in its bundle)
- Modify: `CHANGELOG.md`, `CLAUDE.md` (the lenient-site count)

**Interfaces:**
- Consumes: lot A — `Bundle.ReadText(IBundleProvider, BundleKey, BundleLimits, byte[], out BundleObjectVersion)` and `Bundle.ReadChunkBytes` (`internal` since Task 4a), `BundleKeyValidator(native, policy, extra)` and its `ValidateTree(rawNames, unadmitted).Accepted`, `BundleLoadOptions` (`Limits`, `KeyPolicy`, `ExtraKeyValidators`, `Default`), `BundleDisplayPath.Compose(location, key)`, `ProviderScopes.Begin(provider)`, `new FileSystemBundleProvider(root)` (`Location` is the root as given), `ReparsePoints.CanonicalizeRoot`, `PathOrdering.CompareComponentWise`, `MarkdownPaths.HasMarkdownExtension`, `BundleKey` (`Parse`, `Segments`, `Name`, `IsMarkdown`); Task 1 — `BundleWriteReport(written, deleted, failed)` (`Written`, `Deleted`, `Failed`, `Succeeded`), `BundleWriteFailure(key, reason)`, `BundleWriteConflictException(key)` and `(key, reason)`, `BundleWriteConflictReason`, `InMemoryBundleProvider` (`canWrite`, `native`, `Add`, `Calls`, `FailWrite`, `BeforeNextWrite`, `FailReadOf`, `Exists`); Tasks 2a–2c — `FileSystemBundleProvider.Write` (whole-path and own-entry STRICT checks, `NameUnavailable` for a directory under the key); Task 3 — `BundleTreeCheck.CheckCreation(provider, validator, key, limits)`; Task 4b — `BundleConceptWriter.MaxAttempts`, `BundleConceptWriter.RetryDelay(int)`; Task 5 — the seven-row `WriteCalls` matrix of `BundleConceptWriterProviderTests`.
- Produces:
  - `public static BundleWriteReport IndexGenerator.RegenerateIndexes(IBundleProvider provider, BundleLoadOptions? options = null)` and `public static BundleWriteReport IndexGenerator.RegenerateIndexesWith(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions? options = null)`: `ArgumentNullException` for a null `provider`, then `BundleReadOnlyException` before any call to it, then `ArgumentNullException` for a null `synthesize`; B36–B39, B47 (an `index.md` key the key policy of `options` refuses is a `Failed` entry with the reason `index.md cannot be written under the bundle's key policy: {reason}`, never read nor written).
  - `internal static BundleWriteReport IndexGenerator.Regenerate(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions options, bool legacy)` — `legacy: true` is the `string` overloads' contract (B38), callable by tests on any provider.
  - `internal static (List<string> TreeInput, List<string> OverLength) Bundle.ListNames(IBundleProvider provider, BundleLimits limits)` (B40).
  - Unchanged: `RegenerateIndexes(string)` and `RegenerateIndexesWith(string, Synthesize)` (same list of display paths, same order, `relativeDir` with the OS separator, an empty list for a root that is not a directory), `BuildIndexText`, `DefaultSynthesize`, `Synthesize`, `IndexEntry`, `internal static Action<string>? BeforeLateReparseCheckForTest` (B41).

- [ ] **Step 1: Write the failing tests**

`tests/OKF4net.Tests/IndexGeneratorProviderTests.cs` (then `unix2dos -q` it). `(char)92` is a backslash: the file-writing tool would decode a `\u005c` escape (Global Constraints).

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="IndexGenerator"/> on a provider (spec §8, §9, §11; decisions B5, B13, B14): the
/// provider overloads return a <see cref="BundleWriteReport"/>, every <c>index.md</c> is written
/// conditioned on what was read and retried on a conflict, a directory with no file is no longer
/// referenced (D2), and the <c>string</c> overloads keep their return values, their
/// <c>relativeDir</c> and their exception behaviour.
/// </summary>
public class IndexGeneratorProviderTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Doc(string type, string title, string description) =>
        $"---\ntype: {type}\ntitle: {title}\ndescription: {description}\n---\n\n# {title}\n";

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static InMemoryBundleProvider Storage(params (string Key, string Text)[] objects)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        foreach (var (key, text) in objects)
        {
            storage.Add(key, text);
        }

        return storage;
    }

    private static readonly (string Key, string Text)[] Sample =
    [
        ("datasets/ga4.md", Doc("BigQuery Dataset", "GA4 Dataset", "GA4 sample.")),
        ("tables/events.md", Doc("BigQuery Table", "events", "Daily event tables.")),
        ("tables/users.md", Doc("BigQuery Table", "users", "Per-user dimension.")),
        ("log.md", "# Log\n\n## 2026-10-19\n* **Update**: x.\n"),
    ];

    [Fact]
    public void The_provider_overload_writes_what_the_string_overload_writes_and_reports_the_keys_in_order()
    {
        using var tmp = new TempDir();
        foreach (var (key, text) in Sample)
        {
            tmp.Write(key, text);
        }

        var paths = IndexGenerator.RegenerateIndexes(tmp.Path);
        var storage = Storage(Sample);

        var report = IndexGenerator.RegenerateIndexes(storage);

        Assert.True(report.Succeeded);
        Assert.Empty(report.Deleted);
        Assert.Equal([K("datasets/index.md"), K("tables/index.md"), K("index.md")], report.Written);
        Assert.Equal([.. report.Written.Select(k => BundleDisplayPath.Compose(tmp.Path, k))], paths);
        foreach (var key in report.Written)
        {
            Assert.Equal(File.ReadAllText(BundleDisplayPath.Compose(tmp.Path, key)), Text(storage, key.ToString()));
        }
    }

    [Fact]
    public void Each_index_is_written_conditioned_on_the_version_read()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")), ("a/index.md", "# stale\n"));

        IndexGenerator.RegenerateIndexes(storage);

        var writes = storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["write a/index.md IfMatch", "write index.md MustNotExist"], writes);
    }

    [Fact]
    public void A_root_index_written_meanwhile_is_read_again_and_its_marker_carried_forward()
    {
        var storage = Storage(("a.md", Doc("Note", "A", "a.")));
        storage.BeforeNextWrite(p => p.Add("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Knowledge\n"));

        var report = IndexGenerator.RegenerateIndexes(storage);

        Assert.Equal([K("index.md")], report.Written);
        Assert.Equal(
            ["read index.md", "write index.md MustNotExist", "read index.md", "write index.md IfMatch"],
            storage.Calls.Where(c => c.EndsWith("index.md", StringComparison.Ordinal) || c.Contains("index.md ", StringComparison.Ordinal)));
        var doc = OkfDocument.Parse(Text(storage, "index.md"));
        Assert.Equal("0.2", doc.Frontmatter.Get("okf_version")?.AsDisplayString());
        Assert.Contains("* [A](a.md) - a.\n", Text(storage, "index.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_that_keeps_conflicting_is_a_failure_and_the_other_directories_are_written()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        for (var n = 1; n <= 3; n++)
        {
            storage.FailWrite(n, new BundleWriteConflictException(K("a/index.md")));
        }

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal("index.md kept changing while it was being written; the write was refused after 3 attempts", failure.Reason);
        Assert.Equal([K("index.md")], report.Written);
        Assert.False(storage.Exists(K("a/index.md")));
        // The parent lists the directory, without the description of an index that was not written.
        Assert.Equal("# Subdirectories\n\n* [a](a/index.md)\n", Text(storage, "index.md"));
    }

    [Fact]
    public void A_provider_refusal_is_a_failure_in_the_report_and_a_silent_skip_for_the_string_overloads()
    {
        var refused = new BundleAccessRefusedException(K("a/index.md"), "secret-token=123 refused by the storage");
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        storage.FailWrite(1, refused);

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal("access to 'a/index.md' was refused by the provider", failure.Reason);
        Assert.Equal([K("index.md")], report.Written);

        var legacy = Storage(("a/x.md", Doc("Note", "X", "x.")));
        legacy.FailWrite(1, refused);
        var skipped = IndexGenerator.Regenerate(legacy, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true);
        Assert.Empty(skipped.Failed);
        Assert.Equal([K("index.md")], skipped.Written);
    }

    [Fact]
    public void Any_other_failure_is_a_failure_in_the_report_and_ends_a_string_overload_run_as_before()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        storage.FailWrite(1, new IOException("disk full at C:/secret/path"));
        var report = IndexGenerator.RegenerateIndexes(storage);
        Assert.Equal("I/O error while writing the index (IOException)", Assert.Single(report.Failed).Reason);
        Assert.Equal([K("index.md")], report.Written);

        var io = Storage(("a/x.md", Doc("Note", "X", "x.")));
        io.FailWrite(1, new IOException("disk full"));
        Assert.Equal("disk full", Assert.Throws<IOException>(() => IndexGenerator.Regenerate(io, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true)).Message);
        Assert.False(io.Exists(K("index.md")));

        var conflicting = Storage(("a/x.md", Doc("Note", "X", "x.")));
        for (var n = 1; n <= 3; n++)
        {
            conflicting.FailWrite(n, new BundleWriteConflictException(K("a/index.md")));
        }

        var conflict = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.Regenerate(conflicting, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, conflict.Reason);
        Assert.False(conflicting.Exists(K("index.md")));
    }

    [Fact]
    public void An_unreadable_root_index_is_left_as_it_is_and_reported()
    {
        const string Malformed = "---\nokf_version: \"0.2\"\n# unterminated frontmatter block, no closing ---\n";
        var storage = Storage(("index.md", Malformed), ("a/x.md", Doc("Note", "X", "x.")));

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("index.md"), failure.Key);
        Assert.Equal("the existing index.md could not be read or parsed, so it was left as it is", failure.Reason);
        Assert.Equal([K("a/index.md")], report.Written);
        Assert.Equal(Malformed, Text(storage, "index.md"));

        var locked = Storage(("a.md", Doc("Note", "A", "a.")), ("index.md", "# Knowledge\n")).FailReadOf("index.md", new IOException("locked"));
        Assert.Equal("the existing index.md could not be read or parsed, so it was left as it is", Assert.Single(IndexGenerator.RegenerateIndexes(locked).Failed).Reason);
        var legacy = IndexGenerator.Regenerate(Storage(("index.md", Malformed), ("a.md", Doc("Note", "A", "a."))), IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true);
        Assert.Empty(legacy.Written);
        Assert.Empty(legacy.Failed);
    }

    /// <summary>
    /// A new <c>index.md</c> is a creation like any other: the tree check of spec §2 runs before
    /// it, so an <c>index.md</c> that would also be the prefix of other keys is not written. The
    /// string overloads throw what the filesystem provider throws for a directory there.
    /// </summary>
    [Fact]
    public void A_new_index_that_would_make_the_tree_invalid_is_refused_before_it_is_written()
    {
        static InMemoryBundleProvider Conflicting() =>
            new InMemoryBundleProvider(BundleKeyConstraints.FileDirectoryConflict, canWrite: true)
                .Add("a/y.md", "---\ntype: Note\ntitle: Y\ndescription: y.\n---\n")
                .Add("a/index.md/x.md", "---\ntype: Note\ntitle: X\ndescription: x.\n---\n");
        var storage = Conflicting();

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.StartsWith("\"a/index.md\" cannot be created: objects exist under it", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("write a/index.md MustNotExist", storage.Calls);
        Assert.Equal([K("a/index.md/index.md"), K("index.md")], report.Written);

        var thrown = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.Regenerate(Conflicting(), IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true));
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, thrown.Reason);
    }

    /// <summary>
    /// The one exception the string overloads throw for a cause they met before: a directory
    /// named <c>index.md</c> with files under it, where the index of its parent must go. The write
    /// over a directory threw <c>UnauthorizedAccessException</c>; the creation is now refused before
    /// any write, as the name cannot hold an object. The indexes written before it stay written.
    /// </summary>
    [Fact]
    public void On_the_filesystem_a_directory_named_index_md_ends_the_run_with_a_conflict()
    {
        using var tmp = new TempDir();
        tmp.Write("x/a.md", Doc("Note", "A", "a."));
        tmp.Write("x/index.md/b.md", Doc("Note", "B", "b."));

        var thrown = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.RegenerateIndexes(tmp.Path));

        Assert.Equal(BundleWriteConflictReason.NameUnavailable, thrown.Reason);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "x", "index.md", "index.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// Spec §11, "Dossiers vides" (D2): a directory with no file under it, at any depth, no longer
    /// appears in its parent's index; one that holds only non-<c>.md</c> files still does.
    /// </summary>
    [Fact]
    public void An_empty_directory_is_no_longer_referenced_but_one_holding_only_non_markdown_files_still_is()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", Doc("Note", "A", "a."));
        tmp.Write("assets/logo.png", "png");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "nested", "deeper"));

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        Assert.Equal([Path.Combine(tmp.Path, "notes", "index.md"), Path.Combine(tmp.Path, "index.md")], written);
        Assert.Equal(
            "# Subdirectories\n\n* [assets](assets/index.md)\n* [notes](notes/index.md) - a.\n",
            File.ReadAllText(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// No listing follows a link (the provider's walk skips it), so a directory whose only entries
    /// are symlinks or junctions holds no object and is not referenced either. Before the provider
    /// it was, being a real directory.
    /// </summary>
    [SkippableFact]
    public void A_directory_holding_only_links_is_not_referenced()
    {
        using var external = new TempDir();
        external.Write("a.md", Doc("Note", "A", "a."));
        using var tmp = new TempDir();
        tmp.Write("notes/n.md", Doc("Note", "N", "n."));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("onlylinks", "j"), external.Path), "neither a junction nor a directory symlink can be created here");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        Assert.Equal("# Subdirectories\n\n* [notes](notes/index.md) - n.\n", File.ReadAllText(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// Spec §11, "<c>Synthesize</c>": the string overload passes the directory relative to the
    /// root with the OS separator, as <c>Path.GetRelativePath</c> did (<c>a\b</c> on Windows); the
    /// provider overload passes the key prefix, <c>a/b</c>, on every OS.
    /// </summary>
    [Fact]
    public void Synthesize_gets_the_os_separator_from_the_string_overload_and_a_slash_from_the_provider_overload()
    {
        var files = new[] { ("a/b/x.md", Doc("Note", "X", "x.")), ("a/b/y.md", Doc("Note", "Y", "y.")) };
        using var tmp = new TempDir();
        foreach (var (key, text) in files)
        {
            tmp.Write(key, text);
        }

        var fromString = new List<string>();
        IndexGenerator.RegenerateIndexesWith(tmp.Path, (dir, children) =>
        {
            fromString.Add(dir);
            return "s";
        });
        var fromProvider = new List<string>();
        IndexGenerator.RegenerateIndexesWith(Storage(files), (dir, children) =>
        {
            fromProvider.Add(dir);
            return "s";
        });

        Assert.Equal([Path.Combine("a", "b")], fromString);
        Assert.Equal(OperatingSystem.IsWindows() ? "a" + (char)92 + "b" : "a/b", fromString[0]); // 92: a backslash
        Assert.Equal(["a/b"], fromProvider);
    }

    /// <summary>
    /// The run's key policy covers the index keys it produces (external review, F1): a host
    /// validator refusing <c>a/index.md</c> makes that index a failure in the report of both
    /// provider overloads, and a silent skip under the <c>string</c> overloads' contract. It is
    /// never read nor written, whether it would be created or replaced; the others are written.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_index_the_key_policy_refuses_is_neither_read_nor_written(bool exists)
    {
        const string Old = "# Note\n\n* [old](x.md)\n";
        const string Reason = "index.md cannot be written under the bundle's key policy: index excluded by host";
        var options = new BundleLoadOptions { ExtraKeyValidators = [k => k.ToString() == "a/index.md" ? "index excluded by host" : null] };
        InMemoryBundleProvider Make() => exists
            ? Storage(("a/x.md", Doc("Note", "X", "x.")), ("a/index.md", Old))
            : Storage(("a/x.md", Doc("Note", "X", "x.")));
        static bool Touches(string call) => call.Contains("a/index.md", StringComparison.Ordinal);

        var storage = Make();
        var report = IndexGenerator.RegenerateIndexes(storage, options);
        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal(Reason, failure.Reason);
        Assert.Equal([K("index.md")], report.Written);
        Assert.DoesNotContain(storage.Calls, Touches);

        var synthesized = Make();
        var withSynthesize = IndexGenerator.RegenerateIndexesWith(synthesized, IndexGenerator.DefaultSynthesize, options);
        Assert.Equal(Reason, Assert.Single(withSynthesize.Failed).Reason);
        Assert.DoesNotContain(synthesized.Calls, Touches);

        var legacy = Make();
        var skipped = IndexGenerator.Regenerate(legacy, IndexGenerator.DefaultSynthesize, options, legacy: true);
        Assert.Empty(skipped.Failed);
        Assert.Equal([K("index.md")], skipped.Written);
        Assert.DoesNotContain(legacy.Calls, Touches);
        if (exists)
        {
            Assert.Equal(Old, Text(legacy, "a/index.md"));
        }
        else
        {
            Assert.False(legacy.Exists(K("a/index.md")));
        }
    }
}
```

In `tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs` (with `Edit`), extend the read-only matrix rather than adding a second one. (a) Replace

```csharp
    /// <summary>Every write entry point of the writer, by name (B3).</summary>
    private static readonly Dictionary<string, Action<BundleConceptWriter>> WriteCalls = new()
    {
        ["WriteConcept(yaml)"] = w => w.WriteConcept("metrics/dau", Fm, "body\n"),
        ["WriteConcept(frontmatter)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n"),
        ["AppendToConceptAtomic"] = w => w.AppendToConceptAtomic("metrics/dau", Fm, b => (b ?? string.Empty) + "more\n"),
        ["RecordVerifications"] = w => w.RecordVerifications(["metrics/dau"], "human:ada"),
        ["WriteConcept(yaml, condition)"] = w => w.WriteConcept("metrics/dau", Fm, "body\n", BundleWriteCondition.MustNotExist),
        ["WriteConcept(frontmatter, condition)"] = w => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
        ["AppendLog"] = w => w.AppendLog("Update", "entry"),
    };
```

with

```csharp
    /// <summary>Every write entry point of the core, by name (B3): the writer's methods, and the index generator's provider overloads, called on the writer's provider.</summary>
    private static readonly Dictionary<string, Action<BundleConceptWriter, IBundleProvider>> WriteCalls = new()
    {
        ["WriteConcept(yaml)"] = (w, _) => w.WriteConcept("metrics/dau", Fm, "body\n"),
        ["WriteConcept(frontmatter)"] = (w, _) => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n"),
        ["AppendToConceptAtomic"] = (w, _) => w.AppendToConceptAtomic("metrics/dau", Fm, b => (b ?? string.Empty) + "more\n"),
        ["RecordVerifications"] = (w, _) => w.RecordVerifications(["metrics/dau"], "human:ada"),
        ["WriteConcept(yaml, condition)"] = (w, _) => w.WriteConcept("metrics/dau", Fm, "body\n", BundleWriteCondition.MustNotExist),
        ["WriteConcept(frontmatter, condition)"] = (w, _) => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
        ["AppendLog"] = (w, _) => w.AppendLog("Update", "entry"),
        ["IndexGenerator.RegenerateIndexes(provider)"] = (_, p) => IndexGenerator.RegenerateIndexes(p),
        ["IndexGenerator.RegenerateIndexesWith(provider)"] = (_, p) => IndexGenerator.RegenerateIndexesWith(p, IndexGenerator.DefaultSynthesize),
    };
```

(b) In `Every_write_entry_point_on_a_read_only_filesystem_view_throws_and_changes_nothing`, replace

```csharp
        var writer = new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path).AsReadOnly());

        var ex = Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer));
```

with

```csharp
        var provider = new FileSystemBundleProvider(tmp.Path).AsReadOnly();
        var writer = new BundleConceptWriter(provider);

        var ex = Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer, provider));
```

(c) In `Every_write_entry_point_on_a_read_only_provider_throws_before_any_call`, the line

```csharp
        Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer));
```

becomes

```csharp
        Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer, storage));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OKF4net.Tests -v q -nologo 2>&1 | grep -E " error " | sed -E 's/.*(error [A-Z0-9]+: [^[]*).*/\1/' | sort | uniq -c`
Expected: build errors only, each naming what this task adds — `CS0117` (`IndexGenerator` has no `Regenerate`), `CS1503` (an `IBundleProvider` or `InMemoryBundleProvider` where `RegenerateIndexes(string)` wants a `string`), `CS1061` (`Written`, `Failed`, `Deleted`, `Succeeded` on the `IReadOnlyList<string>` the old overloads return).

- [ ] **Step 3: Move the listing loop into `Bundle.ListNames`, then rewrite the generator**

In `src/OKF4net/Bundle.cs` (with `Edit`), (a) in `Load`, replace the listing loop

```csharp
        // The listing is consumed under the core's own bounds, whatever the provider does:
        // entries while consuming; depth and key length on every name that parses, before the
        // tree validation, so a too-deep key rejected for another reason still aborts.
        var treeInput = new List<string>();
        // Names refused for their length are still physically there: the tree validation counts
        // their ancestors as prefixes, so `x.md` beside an over-long `x.md/…` is a conflict.
        var overLength = new List<string>();
        var parseProblems = new List<BundleParseError>();
        var rejectedKeys = new HashSet<BundleKey>();
        try
        {
            var count = 0;
            foreach (var listing in provider.Enumerate(null, limits))
            {
                if (++count > limits.MaxEntries)
                {
                    throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
                }

                // Depth and key length are measured on the RAW name, before any parsing: a
                // name that also breaks a base rule must not bypass a global bound (the third
                // review slipped `bad\name/a/b.md` past MaxDepth = 1 this way).
                var raw = listing.RawName;
                if (raw.Split('/').Length > limits.MaxDepth)
                {
                    throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
                }

                if (System.Text.Encoding.UTF8.GetByteCount(raw) > limits.MaxKeyBytes)
                {
                    BundleKey.TryParse(raw, out var longKey, out _);
                    if (longKey is not null)
                    {
                        rejectedKeys.Add(longKey);
                    }

                    if (MarkdownPaths.HasMarkdownExtension(raw))
                    {
                        parseProblems.Add(new BundleParseError(raw, longKey, $"key is longer than the {limits.MaxKeyBytes}-byte bound"));
                    }

                    overLength.Add(raw);
                    continue;
                }

                treeInput.Add(raw);
            }
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BundleLoadException($"I/O error while listing the bundle ({e.GetType().Name})", e);
        }
```

with

```csharp
        List<string> treeInput;
        List<string> overLength;
        try
        {
            (treeInput, overLength) = ListNames(provider, limits);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BundleLoadException($"I/O error while listing the bundle ({e.GetType().Name})", e);
        }

        var parseProblems = new List<BundleParseError>();
        var rejectedKeys = new HashSet<BundleKey>();
        foreach (var raw in overLength)
        {
            BundleKey.TryParse(raw, out var longKey, out _);
            if (longKey is not null)
            {
                rejectedKeys.Add(longKey);
            }

            if (MarkdownPaths.HasMarkdownExtension(raw))
            {
                parseProblems.Add(new BundleParseError(raw, longKey, $"key is longer than the {limits.MaxKeyBytes}-byte bound"));
            }
        }
```

The over-length names are handled after the loop instead of inside it, in the same order; `parseProblems` holds nothing else at that point and is sorted at the end of `Load`, and `rejectedKeys` is a set, so `Load`'s result does not change (lot A's loader tests are the check).

(b) Right before the line `    /// <summary>The size of the copy buffer <see cref="ReadText"/> reads through.</summary>`, insert the following; it ends with a blank line, which separates it from that summary:

```csharp
    /// <summary>
    /// Consumes <paramref name="provider"/>'s listing of the whole bundle under the core's own
    /// bounds, whatever the provider does (spec §6): entries while consuming; depth and key
    /// length on every RAW name, before any parsing, so a name that also breaks a base rule
    /// cannot bypass a global bound (the third review slipped <c>bad\name/a/b.md</c> past
    /// <c>MaxDepth = 1</c> that way). Returns the names to validate as a tree and, apart, the
    /// names refused for their length — still physically there: the tree validation counts their
    /// ancestors as prefixes, so <c>x.md</c> beside an over-long <c>x.md/…</c> is a conflict.
    /// Provider exceptions and the bounds' <see cref="BundleLimitExceededException"/> propagate.
    /// The one listing consumer of the core: <see cref="Load(IBundleProvider, BundleLoadOptions?)"/>
    /// and <see cref="IndexGenerator"/> both list through it.
    /// </summary>
    internal static (List<string> TreeInput, List<string> OverLength) ListNames(IBundleProvider provider, BundleLimits limits)
    {
        var treeInput = new List<string>();
        var overLength = new List<string>();
        var count = 0;
        foreach (var listing in provider.Enumerate(null, limits))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = listing.RawName;
            if (raw.Split('/').Length > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            if (System.Text.Encoding.UTF8.GetByteCount(raw) > limits.MaxKeyBytes)
            {
                overLength.Add(raw);
                continue;
            }

            treeInput.Add(raw);
        }

        return (treeInput, overLength);
    }

```

In `src/OKF4net/IndexGenerator.cs`, (c) after `using OKF4net.Internal;` add the line

```csharp
using OKF4net.Providers;
```

and (d) replace everything from the `    /// <summary>` line above `    /// Regenerates every <c>index.md</c> in the bundle using` down to and including the line `}` that closes the class (the first line that is `}` alone; the `IndexEntry` record follows it) with the block below. What it does: the `string` overloads return an empty list for a root that is not a directory, else run the core over `new FileSystemBundleProvider(trimmed root)` in `legacy` mode and map each written key to `BundleDisplayPath.Compose(root, key)`; the provider overloads check `CanWrite` first. The core lists through `Bundle.ListNames` and `ValidateTree` and reads every `.md` child through `Bundle.ReadText` inside one provider scope (B39, B40), then, outside it, walks the directories deepest-first (ties: `PathOrdering.CompareComponentWise`, as before), builds each directory's entries from its children (a `.md` object, or a prefix with an object under it, B36), fires the hook (B41), and writes the `index.md`: check its key under the run's key policy first — a key the policy refuses, a host's validator included, is neither read nor written, a `Failed` entry or a silent skip in `legacy` mode (B47) — then read its current version (the root's document too, to carry `okf_version`), refuse a creation the tree check refuses (B37), write `IfMatch(version)` or `MustNotExist`, retry a `ConditionFailed` conflict at most three times; each failure is reported or thrown per B38. A directory's description for its parent is computed only once its `index.md` is written, as before.

```csharp
    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle using
    /// <see cref="DefaultSynthesize"/>.
    /// </summary>
    public static IReadOnlyList<string> RegenerateIndexes(string bundleRoot) =>
        RegenerateIndexesWith(bundleRoot, DefaultSynthesize);

    /// <summary>
    /// Test-only hook invoked once per directory about to get its
    /// <c>index.md</c>, after its entries are known and before that
    /// <c>index.md</c> is read and written through the provider -- where the
    /// provider's late guards run (see <see cref="RegenerateIndexesWith(string, Synthesize)"/>'s
    /// remarks). Its argument is the directory's display path: the bundle root, or
    /// the root followed by the directory's segments joined with the OS separator.
    /// Used by <c>IndexTests</c> to deterministically substitute a directory that
    /// was listed as a real one with a junction/symlink, in the exact narrow window
    /// such a race would need to land in. <c>internal</c> rather than
    /// test-conditional compilation, consistent with this assembly's
    /// <c>InternalsVisibleTo</c> grant to <c>OKF4net.Tests</c>. No-op (null) in
    /// production.
    /// </summary>
    internal static Action<string>? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle, deriving each
    /// subdirectory's description with the supplied synthesizer.
    ///
    /// Directories are processed deepest-first so a parent index can reuse
    /// the descriptions computed for its children. Empty directories are
    /// skipped. Returns the paths of the index files written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs through a <see cref="FileSystemBundleProvider"/> over
    /// <paramref name="bundleRoot"/> (trailing separators trimmed), exactly as
    /// <see cref="RegenerateIndexesWith(IBundleProvider, Synthesize, BundleLoadOptions?)"/>
    /// does with the default <see cref="BundleLoadOptions"/>; each returned path is
    /// that root followed by the <c>index.md</c>'s key segments joined with the OS
    /// separator, in the order the files were written. <paramref name="synthesize"/>
    /// receives the directory's path relative to the root with the OS separator
    /// (<c>a\b</c> on Windows). A <paramref name="bundleRoot"/> that is not an
    /// existing directory gives an empty list.
    /// </para>
    /// <para>
    /// <b>Reparse-point safety.</b> The bundle is listed by the provider's walk,
    /// which skips every symlink and junction under the root (never follows one)
    /// and refuses to enter a directory it cannot inspect; so a linked
    /// directory is neither indexed nor referenced. Every <c>index.md</c> is then
    /// read and written by the provider, whose guards are STRICT
    /// (<see cref="ReparsePoints.IsReparsePointOrUninspectable"/>) and run at the
    /// moment of the access: the whole path from the root, the <c>index.md</c>
    /// entry itself, again under the commit lock right before the commit. A
    /// directory replaced by a link, or an <c>index.md</c> planted as one, after
    /// the listing is refused there, and that <c>index.md</c> is SKIPPED (not in
    /// the returned list); regeneration continues with the remaining directories.
    /// The bundle root itself is never inspected: a symlinked or mounted root is a
    /// legitimate setup and is indexed like any other.
    /// </para>
    /// <para>
    /// <b>Atomic, conditional writes.</b> Each <c>index.md</c> is committed
    /// atomically, conditioned on the version just read (or on its absence), and a
    /// conflict with another writer is retried at most three times, reading again
    /// each time. A regeneration takes no lock of its own (its callers lock, or
    /// not): a concurrent write surfaces as a conflict and a retry, never as a lost
    /// update.
    /// </para>
    /// <para>
    /// <b>Failures.</b> A root <c>index.md</c> that cannot be read or parsed is left
    /// untouched and skipped. Any other failure — an I/O error, a name the storage
    /// cannot hold, a write that kept conflicting — propagates and ends the run, as
    /// an I/O error did before the provider: the <c>index.md</c> files written
    /// until then stay written.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RegenerateIndexesWith(string bundleRoot, Synthesize synthesize)
    {
        if (!Directory.Exists(bundleRoot))
        {
            return [];
        }

        // §8: a trailing separator is trimmed (ReparsePoints.CanonicalizeRoot), so the
        // display paths below are the same for "dir" and "dir\".
        var root = ReparsePoints.CanonicalizeRoot(bundleRoot);
        var report = Regenerate(new FileSystemBundleProvider(root), synthesize, BundleLoadOptions.Default, legacy: true);
        return [.. report.Written.Select(key => BundleDisplayPath.Compose(root, key))];
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> of the bundle held by <paramref name="provider"/>
    /// with <see cref="DefaultSynthesize"/>; see
    /// <see cref="RegenerateIndexesWith(IBundleProvider, Synthesize, BundleLoadOptions?)"/>.
    /// </summary>
    /// <param name="provider">Where the bundle is read and written.</param>
    /// <param name="options">The key policy and bounds the bundle is loaded with; <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <returns>The keys written, in order, and the <c>index.md</c> keys that could not be written, with why.</returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public static BundleWriteReport RegenerateIndexes(IBundleProvider provider, BundleLoadOptions? options = null) =>
        RegenerateIndexesWith(provider, DefaultSynthesize, options);

    /// <summary>
    /// Regenerates every <c>index.md</c> of the bundle held by <paramref name="provider"/>
    /// (spec §8, §9): the bundle is listed and validated as a load under
    /// <paramref name="options"/> does, each directory of a <c>.md</c> key gets the
    /// index of its <c>.md</c> children and of its subdirectories — prefixes with at
    /// least one object under them, so a directory with no file is not referenced —
    /// deepest-first, and each <c>index.md</c> is written through the provider,
    /// conditioned on the version read (<see cref="BundleWriteCondition.IfMatch"/>, or
    /// <see cref="BundleWriteCondition.MustNotExist"/> for a new one), a creation first
    /// passing the tree check every creation of the core passes. A conflict is retried
    /// at most three times, reading again each time.
    /// </summary>
    /// <param name="provider">Where the bundle is read and written.</param>
    /// <param name="synthesize">The subdirectory-description synthesizer; it receives the directory's key prefix with <c>/</c> as separator (<c>a/b</c>).</param>
    /// <param name="options">The key policy and bounds the bundle is loaded with; <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <returns>
    /// The keys written, in order, and one failure per <c>index.md</c> that was not:
    /// refused by the key policy of <paramref name="options"/> (a host's own validator
    /// included; that <c>index.md</c> is neither read nor written), refused by the provider (a
    /// link on its path), a name the storage cannot hold or a creation the tree check refuses, a
    /// write that kept conflicting, an I/O error, or a root <c>index.md</c> that could not be
    /// read or parsed (left untouched). The reasons are library-authored, but for the key
    /// policy's own reason. A failure ends nothing: the other directories are written.
    /// </returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    /// <exception cref="BundleProviderException">Listing the bundle failed or exceeded a bound of <paramref name="options"/>.</exception>
    /// <exception cref="IOException">Listing the bundle failed.</exception>
    public static BundleWriteReport RegenerateIndexesWith(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.Capabilities.CanWrite)
        {
            throw new BundleReadOnlyException(provider.Id);
        }

        ArgumentNullException.ThrowIfNull(synthesize);
        return Regenerate(provider, synthesize, options ?? BundleLoadOptions.Default, legacy: false);
    }

    /// <summary>
    /// The regeneration behind both families of overloads. <paramref name="legacy"/> is the
    /// <c>string</c> overloads' contract: an <c>index.md</c> key the policy refuses, a provider
    /// refusal (<see cref="BundleAccessRefusedException"/>, a STRICT guard) and an unreadable root
    /// <c>index.md</c> skip that directory silently,
    /// and every other failure propagates, ending the run; <c>relativeDir</c> uses the OS
    /// separator. Otherwise every per-directory failure becomes a <see cref="BundleWriteFailure"/>
    /// and the run goes on; <c>relativeDir</c> uses <c>/</c>.
    /// </summary>
    internal static BundleWriteReport Regenerate(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions options, bool legacy)
    {
        var limits = options.Limits;
        var validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);
        var tree = new Dictionary<string, Dictionary<string, Child>>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var documents = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        // Listing and reading, inside one provider scope (each directory listed once): the
        // keys a load would accept, then every .md child's entry. Nothing is written yet.
        using (ProviderScopes.Begin(provider))
        {
            var (treeInput, overLength) = Bundle.ListNames(provider, limits);
            var chunk = new byte[Bundle.ReadChunkBytes];
            foreach (var key in validator.ValidateTree(treeInput, overLength).Accepted)
            {
                var segments = key.Segments;
                for (var depth = 0; depth < segments.Count; depth++)
                {
                    var parent = string.Join('/', segments.Take(depth));
                    if (!tree.TryGetValue(parent, out var children))
                    {
                        children = new Dictionary<string, Child>(StringComparer.Ordinal);
                        tree[parent] = children;
                    }

                    var leaf = depth == segments.Count - 1;
                    children.TryGetValue(segments[depth], out var child);
                    children[segments[depth]] = leaf ? child with { IsObject = true } : child with { IsPrefix = true };
                    if (key.IsMarkdown)
                    {
                        directories.Add(parent);
                    }
                }

                if (key.IsMarkdown && key.Name != IndexFile && ReadEntry(provider, key, limits, chunk) is { } entry)
                {
                    documents[key.ToString()] = entry;
                }
            }
        }

        // Deepest-first; ties broken by path for determinism.
        var order = directories.ToList();
        order.Sort((a, b) =>
        {
            var cmp = Depth(b).CompareTo(Depth(a));
            return cmp != 0 ? cmp : PathOrdering.CompareComponentWise(a, b);
        });

        var written = new List<BundleKey>();
        var failed = new List<BundleWriteFailure>();
        var dirDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in order)
        {
            var entries = new List<IndexEntry>();
            var names = tree[directory].Keys.ToList();
            names.Sort(PathOrdering.CompareComponentWise);
            foreach (var name in names)
            {
                if (name == IndexFile)
                {
                    continue;
                }

                var child = tree[directory][name];
                var path = directory.Length == 0 ? name : directory + "/" + name;
                if (child.IsObject && MarkdownPaths.HasMarkdownExtension(name))
                {
                    if (documents.TryGetValue(path, out var document))
                    {
                        entries.Add(document);
                    }
                }
                else if (child.IsPrefix)
                {
                    var description = dirDescriptions.GetValueOrDefault(path, string.Empty);
                    entries.Add(new IndexEntry("Subdirectories", name, $"{name}/{IndexFile}", description));
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            BeforeLateReparseCheckForTest?.Invoke(directory.Length == 0 ? provider.Location : BundleDisplayPath.Compose(provider.Location, BundleKey.Parse(directory)));

            var indexKey = BundleKey.Parse(directory.Length == 0 ? IndexFile : directory + "/" + IndexFile);
            var failure = WriteIndex(provider, validator, limits, indexKey, isRoot: directory.Length == 0, BuildIndexText(entries), legacy);
            if (failure is not null)
            {
                if (failure.Length > 0)
                {
                    failed.Add(new BundleWriteFailure(indexKey, failure));
                }

                continue;
            }

            written.Add(indexKey);
            if (directory.Length == 0)
            {
                continue;
            }

            var pairs = entries.Select(e => (e.Title, e.Description)).ToList();
            string desc;
            if (pairs.Count == 1 && pairs[0].Description.Length != 0)
            {
                desc = pairs[0].Description;
            }
            else
            {
                var rel = legacy ? directory.Replace('/', Path.DirectorySeparatorChar) : directory;
                desc = synthesize(rel, pairs);
            }

            dirDescriptions[directory] = desc;
        }

        return new BundleWriteReport(written, [], failed);
    }

    /// <summary>
    /// Reads and writes one <c>index.md</c>, retrying a conflict (spec §4, decision B5):
    /// <see langword="null"/> once written; otherwise the reason it was not, or an empty
    /// string for a silent skip (<paramref name="legacy"/>: a key the policy refuses, a provider
    /// refusal, an unreadable root). In <paramref name="legacy"/> mode every other failure is thrown.
    /// </summary>
    private static string? WriteIndex(IBundleProvider provider, BundleKeyValidator validator, BundleLimits limits, BundleKey key, bool isRoot, string body, bool legacy)
    {
        // The run's key policy covers the index keys it produces, as it covers every concept the
        // writer writes (spec §2, decision B2): an index.md the validator refuses — a host's own
        // rule included — is neither read nor written, whether it exists or not.
        if (validator.CheckKey(key) is { } refused)
        {
            return legacy ? string.Empty : $"index.md cannot be written under the bundle's key policy: {refused}";
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (current, unreadable) = ReadCurrentIndex(provider, key, isRoot, limits);
                if (unreadable)
                {
                    // A pre-existing root index.md that fails to read or parse (locked by
                    // another process, malformed YAML, bad encoding) is left untouched rather
                    // than silently overwritten frontmatter-free, so a transient read failure
                    // can never drop the okf_version marker okf-mcp's discovery relies on.
                    return legacy ? string.Empty : "the existing index.md could not be read or parsed, so it was left as it is";
                }

                var content = body;

                // The bundle-root index.md is the only place frontmatter is spec-sanctioned
                // at all (§12: the okf_version marker, see Bundle.OkfVersion), and even there
                // ONLY the okf_version key is sanctioned (§8) -- every other index.md must
                // have none. So only the root's existing okf_version value (never the rest
                // of its frontmatter) is carried forward; a non-root index.md is body only.
                // Both self-heal every §8 violation regeneration can reach.
                if (current?.Document?.Frontmatter.Get("okf_version") is { } okfVersion)
                {
                    var rootFrontmatter = new Frontmatter();
                    rootFrontmatter.Set("okf_version", okfVersion);
                    content = new OkfDocument(rootFrontmatter, body).Serialize();
                }

                if (current is null && BundleTreeCheck.CheckCreation(provider, validator, key, limits) is { } conflict)
                {
                    return legacy ? throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable) : conflict;
                }

                provider.Write(key, OkfEncodings.NoBom.GetBytes(content), current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist);
                return null;
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && attempt < BundleConceptWriter.MaxAttempts)
            {
                Thread.Sleep(BundleConceptWriter.RetryDelay(attempt));
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && !legacy)
            {
                return $"index.md kept changing while it was being written; the write was refused after {BundleConceptWriter.MaxAttempts} attempts";
            }
            catch (BundleAccessRefusedException e)
            {
                return legacy ? string.Empty : e.Message;
            }
            catch (Exception e) when (!legacy && e is not BundleReadOnlyException && (e is BundleProviderException or IOException or UnauthorizedAccessException))
            {
                return e is BundleProviderException ? e.Message : $"I/O error while writing the index ({e.GetType().Name})";
            }
        }
    }

    /// <summary>
    /// The <c>index.md</c> under <paramref name="key"/> as it is now: <see langword="null"/>
    /// when there is none; otherwise its version and, for the root, its parsed document
    /// (strict UTF-8, the core's one bounded reader). <c>Unreadable</c> is set for a root
    /// <c>index.md</c> that exists but cannot be read, decoded or parsed.
    /// </summary>
    private static ((BundleObjectVersion Version, OkfDocument? Document)? Current, bool Unreadable) ReadCurrentIndex(IBundleProvider provider, BundleKey key, bool isRoot, BundleLimits limits)
    {
        try
        {
            if (!isRoot)
            {
                using var obj = provider.Read(key);
                return ((obj.Version, null), false);
            }

            var text = Bundle.ReadText(provider, key, limits, new byte[Bundle.ReadChunkBytes], out var version);
            return ((version, OkfDocument.Parse(text)), false);
        }
        catch (BundleObjectNotFoundException)
        {
            return (null, false);
        }
        catch (Exception e) when (isRoot && e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or DocumentParseException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// The index entry of one <c>.md</c> child, or <see langword="null"/> when it cannot be read,
    /// decoded or parsed — such a document is left out of its directory's index, as before. The
    /// title is the frontmatter's, or the file stem; the link is the file name.
    /// </summary>
    private static IndexEntry? ReadEntry(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk)
    {
        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(Bundle.ReadText(provider, key, limits, chunk, out _));
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or DocumentParseException)
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(key.Name);
        return new IndexEntry(doc.Frontmatter.Type ?? string.Empty, doc.Frontmatter.Title ?? stem, key.Name, doc.Frontmatter.Description ?? string.Empty);
    }

    /// <summary>The number of segments of a key prefix (0 for the root).</summary>
    private static int Depth(string prefix) => prefix.Length == 0 ? 0 : prefix.Count(c => c == '/') + 1;

    /// <summary>What a name stands for under one prefix: an object, the prefix of other keys, or both (a tree the policy lets through).</summary>
    private readonly record struct Child(bool IsObject, bool IsPrefix);
}
```

`unix2dos -q src/OKF4net/IndexGenerator.cs` if your editor wrote LF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~IndexGeneratorProviderTests"`
Expected: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14` (the two rows of B47's theory included; on Windows the link test uses a junction, which needs no privilege).

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~IndexTests|FullyQualifiedName~BundleConceptWriterProviderTests|FullyQualifiedName~GoldenParityTests|FullyQualifiedName~CliTests|FullyQualifiedName~OkfBundleToolsTests|FullyQualifiedName~OKF4net.Tests.Providers"`
Expected: `Passed!  - Failed:     0, Passed:   711, Skipped:    16, Total:   727` — `IndexTests` unchanged (on Windows without Developer Mode its two file/directory-symlink tests skip; the two uninspectable-junction tests run), the matrix with its two new rows, the index golden with its 8 files.

- [ ] **Step 5: Comments that named the removed walks, CHANGELOG, CLAUDE.md**

The generator's two lenient walks (`CollectMarkdown`, the per-child `IsReparsePoint` skip) and its private `HasReparsePointOrUninspectableAncestor` wrapper are gone; three doc comments named them. With `Edit`:

(a) `src/OKF4net/Internal/ReparsePoints.cs`, in `IsReparsePoint`'s remarks,

```csharp
    /// the links among them (<c>Bundle</c>'s and <c>IndexGenerator</c>'s
    /// <c>CollectMarkdown</c>, <c>IndexGenerator</c>'s per-child skip) or
```

becomes

```csharp
    /// the links among them (<c>FileSystemBundleProvider</c>'s walk, through
    /// which a load and an index regeneration both list the bundle) or
```

and, in `HasReparsePointAncestor`'s `rootComparison` parameter doc,

```csharp
    /// the 2-arg overloads, <c>IndexGenerator</c>'s private wrapper,
```

becomes

```csharp
    /// the 2-arg overloads, <c>FileSystemBundleProvider</c>'s guards,
```

(b) `src/OKF4net/Internal/PathOrdering.cs`, the summary's first line

```csharp
/// Compares two absolute file paths component-by-component (splitting on
```

becomes

```csharp
/// Compares two absolute file paths, or two bundle keys, component-by-component (splitting on
```

(c) `src/OKF4net.Catalog/CatalogPathResolver.cs`, in `HasReparsePointInPath`'s summary,

```csharp
    /// same reasoning that keeps <c>IndexGenerator</c>'s own <c>HasReparsePointOrUninspectableAncestor</c>
    /// wrapper, and the shared <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>
```

becomes

```csharp
    /// same reasoning that keeps <c>FileSystemBundleProvider</c>'s per-key guards (behind which
    /// <c>IndexGenerator</c> writes), and the shared <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>
```

(d) `CHANGELOG.md`, under `### Added`, after the line ``  strings of `OkfBundleTools.AppendLog`, which now delegates to it. `` (the end of Task 5's bullet), add:

```markdown
- **`IndexGenerator` over a provider.** `IndexGenerator.RegenerateIndexes(provider,
  options)` and `RegenerateIndexesWith(provider, synthesize, options)`
  regenerate the `index.md` files of any writable `IBundleProvider` and return
  a `BundleWriteReport`: the `index.md` keys written, in order, and those that
  were not, with why (a key the run's key policy refuses — such an `index.md`
  is neither read nor written — a path the provider refuses, a creation the
  tree check refuses, a write that kept conflicting, an I/O error, a root
  `index.md` that cannot be read). `synthesize` receives the directory's key prefix (`a/b`).
  The `string` overloads keep their return values, their order and
  `relativeDir` with the OS separator.
```

and under `### Changed`, after the line `` allowed.`). `` that ends Task 5's bullet (`` - **`log.md` is written by `BundleConceptWriter`** ``), add:

```markdown
- **`okf index` and `okf_regenerate_indexes` read and write through the
  provider** (spec §8, §9): the bundle is listed and validated as a load lists
  and validates it, and each `index.md` is written atomically, conditioned on
  the version read (or on its absence), a conflict retried at most 3 times.
  An `index.md` the provider refuses (a symlink or junction on its path, an
  entry that cannot be inspected) is still skipped silently. The output does
  not change, except that **a directory with no file under it, at any depth,
  is no longer referenced by its parent's index** (spec §2, D2); a directory
  holding only non-`.md` files still is. As in a load, a name that is no key
  (a backslash, a control character, over 1 024 bytes) is not listed, a
  directory whose only entries are symlinks or junctions is not referenced
  (no listing follows a link), and the load's bounds apply. A directory
  named `index.md` with files under it now ends the run with
  `BundleWriteConflictException` where the write threw
  `UnauthorizedAccessException`.
```

(e) `CLAUDE.md`, the lenient-site count of the `Internal/ReparsePoints.cs` bullet. Lot A left it at four sites (the provider's walk, the generator's two walks, and `TryResolveResource`'s classification), with line numbers that had already drifted on `origin/dev` (`Bundle.cs:673-674`, `:732-734`). This task removes the generator's two walks and moves `Bundle.cs`; the sentence

```markdown
and that is now exactly four sites: three walks (`Providers/FileSystemBundleProvider.Walk`, the load walk that replaced `Bundle.CollectMarkdown`, and the two `IndexGenerator` walks) plus `Bundle.cs:673-674`, `TryResolveResource`'s §6.2 status classification, which is not a walk at all — it is lenient because the actual read gate downstream, `Bundle.ReadResourceText(string)` (guard at `:732-734`), uses the STRICT pair and throws. Do not add a fifth, and never add a lenient use in a guard.
```

becomes

```markdown
and that is now exactly two sites: one walk (`Providers/FileSystemBundleProvider.Walk`, through which every listing goes — a load's, an index regeneration's, `GetVersion`'s; `IndexGenerator`'s own two walks went when it moved onto the provider) plus `Bundle.cs:802-803`, `TryResolveResource`'s §6.2 status classification, which is not a walk at all — it is lenient because the actual read gate downstream, `Bundle.ReadResourceText(string)` (guard at `:862-863`), uses the STRICT pair and throws. Do not add a third, and never add a lenient use in a guard.
```

Check the count and the line numbers against the code, not against this text:

```bash
grep -rn 'ReparsePoints.IsReparsePoint(\|ReparsePoints.HasReparsePointAncestor(' src --include=*.cs | grep -v '///'
grep -n 'IsReparsePointOrUninspectable(fullPath)\|HasReparsePointOrUninspectableAncestor(fullRoot, fullPath' src/OKF4net/Bundle.cs
```

Expected: `src/OKF4net/Bundle.cs:802` and `:803` (the classification) and `src/OKF4net/Providers/FileSystemBundleProvider.cs:1494` (the walk; 1368 before r3 grew the write side) — no `IndexGenerator` line; then `862` and `863`.

- [ ] **Step 6: Verify and commit**

```bash
for f in src/OKF4net/Bundle.cs src/OKF4net/IndexGenerator.cs src/OKF4net/Internal/ReparsePoints.cs src/OKF4net/Internal/PathOrdering.cs src/OKF4net.Catalog/CatalogPathResolver.cs tests/OKF4net.Tests/IndexGeneratorProviderTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Passed: 3365, Skipped: 28, Total: 3393
```

Then the Linux container command of the Global Constraints with the filter `FullyQualifiedName~IndexTests|FullyQualifiedName~IndexGeneratorProviderTests|FullyQualifiedName~BundleConceptWriterProviderTests|FullyQualifiedName~GoldenParityTests|FullyQualifiedName~OKF4net.Tests.Providers|FullyQualifiedName~CliTests` in place of the `Providers` one, both passes. Expected: as root `Passed: 690, Skipped: 23, Total: 713`; as `tester` `Passed: 693, Skipped: 20, Total: 713` (the skips are the Windows-only rows and, as root, the read-only-target and partial-verify tests that root cannot provoke); the two symlink tests of `IndexTests` run there and pass.

```bash
git add src/OKF4net/Bundle.cs src/OKF4net/IndexGenerator.cs src/OKF4net/Internal/ReparsePoints.cs src/OKF4net/Internal/PathOrdering.cs src/OKF4net.Catalog/CatalogPathResolver.cs tests/OKF4net.Tests/IndexGeneratorProviderTests.cs tests/OKF4net.Tests/BundleConceptWriterProviderTests.cs CHANGELOG.md CLAUDE.md
git commit -F - <<'MSG'
feat(index): IndexGenerator lists, reads and writes through a provider

New RegenerateIndexes(IBundleProvider, BundleLoadOptions?) and
RegenerateIndexesWith(IBundleProvider, Synthesize, BundleLoadOptions?) return a
BundleWriteReport. The bundle is listed through Bundle.ListNames (the load's
bounded listing, now shared) and validated as a load validates it; each .md
child is read through Bundle.ReadText inside one provider scope; each index.md
is written through the provider conditioned on the version read (MustNotExist
for a new one, after the tree check), a conflict retried at most three times.
A directory is referenced when an object lies under it, so an empty directory
is no longer listed. The string overloads delegate through a
FileSystemBundleProvider: same paths in the same order, relativeDir with the OS
separator, a provider refusal or an unreadable root index skipped silently,
every other failure thrown. The read-only matrix covers both new overloads.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 7: Callers and consumers keep their bytes

**Files:**
- Test: `tests/OKF4net.Tests/CliTests.cs` (one test: D2 at the CLI surface)
- Modify: `producers/src/OkfProducer.Core/Generation/BundleWriter.cs` (two comments, no code)
- Unchanged, verified here: `src/OKF4net.Cli/OkfCli.cs` (`CmdIndex`, `CmdVerify`), `src/OKF4net.Agents/OkfBundleTools.cs` (`RegenerateIndexes`, and the `WriteConcept`, `AppendLog`, `Verify` tools), `src/OKF4net.Agents/OkfContextProvider.cs` (memory capture), `src/OKF4net.Catalog/FileMemoryStore.cs` (the writer's string constructor; its scope deletion is lot D's), `samples/*`
- Scratch, outside the repository (any directory; below `$S`): `differential-c.sh`, `normalize.js`, `tools-differential.sh`, `toolsdiff/` — never committed

**Interfaces:**
- Consumes: Task 6 (`IndexGenerator.RegenerateIndexes(string)` through the provider, D2); Tasks 4a–5 (the writer behind `CmdVerify`, `OkfBundleTools`, `OkfContextProvider`, `FileMemoryStore`).
- Produces: no API. The evidence that every caller's output is byte-identical to `origin/dev` except the inputs B36 and B38 name.

None of these callers changes. `CmdIndex` calls `IndexGenerator.RegenerateIndexes(path)` after `RequireBundleRoot` and catches `IOException`/`UnauthorizedAccessException` as before; a provider exception the string overload now throws (B38: a bound, the directory named `index.md`) is an `OkfException`, which `Run`'s safety net turns into `error: …` and exit 1, the shape of every other failure. `CmdVerify` and the agent tools go through `BundleConceptWriter` (Tasks 4a–5). `OkfBundleTools.RegenerateIndexes` keeps the string overload under the writer's lock — lot C moves the tools onto handles; its `RunTool` already converts an `OkfException`. `OkfContextProvider`'s capture calls `AppendToConceptAtomic` with a pure `buildBody` and then `AppendLog`; `FileMemoryStore` builds its writer from a string. `producers/`'s `BundleWriter` uses `new BundleConceptWriter(string)`, `WriteConcept(string, Frontmatter, string)` (matching the `"Error:"` prefix) and `IndexGenerator.RegenerateIndexes(string)`, none of whose signatures changed in lots A–B: nothing forced a code change there. Two of its comments, however, describe the generator's former walks and late checks by line number, and Task 6 made them false.

- [ ] **Step 1: Pin D2 at the CLI surface**

In `tests/OKF4net.Tests/CliTests.cs`, right after the closing `    }` of `Index_with_trailing_separator_still_writes_the_root_index`, insert a blank line and:

```csharp
    /// <summary>
    /// Spec §2 and §10 (D2) at the CLI surface: for a bundle holding an empty directory,
    /// <c>okf index</c> prints the lines it printed before (the empty directory never had an index
    /// of its own), but the root index no longer references it. A directory holding only a
    /// non-<c>.md</c> file still is. On the code before the provider the root index listed
    /// <c>empty</c>, and this test failed.
    /// </summary>
    [Fact]
    public void Index_no_longer_references_an_empty_directory()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\n\nbody\n");
        tmp.Write("assets/logo.png", "png");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));

        var r = Run("index", tmp.Path);

        Assert.Equal(0, r.Code);
        Assert.Equal(
            $"wrote {Path.Combine(tmp.Path, "notes", "index.md")}\nwrote {Path.Combine(tmp.Path, "index.md")}\n\n2 index file(s) regenerated.\n",
            r.Out);
        Assert.Equal(
            "# Subdirectories\n\n* [assets](assets/index.md)\n* [notes](notes/index.md) - d\n",
            File.ReadAllText(Path.Combine(tmp.Path, "index.md")));
    }
```

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Index_no_longer_references_an_empty_directory"`
Expected: `Passed: 1` — Task 6 did the work. The same test on a worktree of `origin/dev` fails (executed while drafting):

```text
Assert.Equal() Failure: Strings differ
                                   ↓ (pos 49)
Expected: ···"assets/index.md)\n* [notes](notes/index.md"···
Actual:   ···"assets/index.md)\n* [empty](empty/index.md"···
```

- [ ] **Step 2: The unchanged callers' own tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests|FullyQualifiedName~GoldenParityTests|FullyQualifiedName~OkfBundleToolsTests|FullyQualifiedName~OkfWriteToolsTests|FullyQualifiedName~OkfVerifyToolTests|FullyQualifiedName~OkfContextProviderMemoryTests|FullyQualifiedName~FileMemoryStoreTests"`
Expected: `Passed!  - Failed:     0, Passed:   349, Skipped:     4, Total:   353`.

- [ ] **Step 3: `okf index` and `okf verify` against `origin/dev`, byte for byte**

Build both sides: a detached worktree of `origin/dev` (`git worktree add --detach ../lotb-base origin/dev`, then `dotnet build OKF4net.sln` in it) and this branch (built). Save as `$S/normalize.js`:

```javascript
// usage: node normalize.js <file> <dir>  -- replaces every spelling of <dir> (as given, with / or \) by SIDE
const fs = require('fs');
const [file, dir] = process.argv.slice(2);
const bs = String.fromCharCode(92);
let t = fs.readFileSync(file, 'utf8');
for (const form of [dir, dir.split('/').join(bs), dir.split(bs).join('/')]) t = t.split(form).join('SIDE');
fs.writeFileSync(file, t);
```

and as `$S/differential-c.sh` (it copies every input, never touches either worktree):

```bash
#!/bin/sh
# usage: differential-c.sh <base worktree> <branch worktree> <scratch dir>
# For each input bundle, runs one okf index/verify sequence on two fresh copies, one per
# build, then compares the transcripts (stdout, stderr, exit codes) and every byte of the
# two resulting trees. Prints one line per bundle.
BASE="$1"; BRANCH="$2"; OUT="$3"
rm -rf "${OUT:?}"; mkdir -p "$OUT/base" "$OUT/branch"
# Two edge bundles the lot changes on purpose (CHANGELOG): an empty directory, and a
# directory named index.md with a file under it.
mkdir -p "$OUT/inputs/edge_empty/notes" "$OUT/inputs/edge_empty/empty" "$OUT/inputs/edge_empty/assets" "$OUT/inputs/edge_indexdir/x/index.md"
printf -- '---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n' > "$OUT/inputs/edge_empty/notes/a.md"
printf 'png' > "$OUT/inputs/edge_empty/assets/logo.png"
printf -- '---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n' > "$OUT/inputs/edge_indexdir/x/a.md"
printf -- '---\ntype: Note\ntitle: B\ndescription: b.\n---\n\nB.\n' > "$OUT/inputs/edge_indexdir/x/index.md/b.md"
for src in "$BRANCH/bundles/acme_retail" "$BRANCH/bundles/ga4" "$BRANCH/bundles/meridian_transit" "$BRANCH/bundles/attestation_containers_demo" \
           "$BRANCH/tests/fixtures/appendix_a" "$BRANCH/tests/fixtures/okf_v02" "$BRANCH/tests/fixtures/okf_v02_computation" "$BRANCH/tests/fixtures/okf_v02_reserved" \
           "$OUT/inputs/edge_empty" "$OUT/inputs/edge_indexdir"; do
  name=$(basename "$src")
  ids=$(cd "$src" && find . -name '*.md' ! -name index.md ! -name log.md | sed 's#^\./##; s#\.md$##' | sort | head -3 | tr '\n' ' ')
  first=$(echo $ids | cut -d' ' -f1)
  for side in base branch; do
    if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
    OKF="dotnet $WT/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll"
    cp -r "$src" "$OUT/$side/$name"
    (
      cd "$OUT/$side"
      $OKF index $name; echo "exit=$?"
      $OKF index $name; echo "exit=$?"
      $OKF verify $name $ids --by human:ada --at 2026-10-19T00:00:00Z; echo "exit=$?"
      $OKF verify $name $first --by human:ada --at 2026-10-19T01:00:00Z; echo "exit=$?"
      $OKF verify $name $first nope/missing --by human:ada --at 2026-10-19T02:00:00Z; echo "exit=$?"
      $OKF verify $name $first $first --by human:ada; echo "exit=$?"
      printf '%s\n' $ids | $OKF verify $name - --by process:nightly --at 2026-10-19T03:00:00Z; echo "exit=$?"
      $OKF index $name; echo "exit=$?"
    ) > "$OUT/$name.$side.out" 2>&1
    node "$(dirname "$0")/normalize.js" "$OUT/$name.$side.out" "$(cd "$OUT/$side" && pwd -W)"
  done
  if cmp -s "$OUT/$name.base.out" "$OUT/$name.branch.out"; then o="transcripts identical ($(wc -l < "$OUT/$name.base.out") lines)"; else o="TRANSCRIPTS DIFFER"; fi
  if diff -r "$OUT/base/$name" "$OUT/branch/$name" > /dev/null; then t="trees identical ($(find "$OUT/base/$name" -type f | wc -l) files)"; else t="TREES DIFFER"; fi
  echo "$name: $o; $t"
done
```

Run, in Git Bash: `sh $S/differential-c.sh <origin/dev worktree> <this worktree> $S/diff`. Expected (executed):

```text
acme_retail: transcripts identical (41 lines); trees identical (18 files)
ga4: transcripts identical (38 lines); trees identical (14 files)
meridian_transit: transcripts identical (41 lines); trees identical (19 files)
attestation_containers_demo: transcripts identical (29 lines); trees identical (8 files)
appendix_a: transcripts identical (32 lines); trees identical (8 files)
okf_v02: transcripts identical (27 lines); trees identical (4 files)
okf_v02_computation: transcripts identical (41 lines); trees identical (16 files)
okf_v02_reserved: transcripts identical (25 lines); trees identical (6 files)
edge_empty: transcripts identical (25 lines); TREES DIFFER
edge_indexdir: TRANSCRIPTS DIFFER; trees identical (3 files)
```

The last two are the inputs B36 and B38 change on purpose; check that the difference is exactly that: `diff -r $S/diff/base/edge_empty $S/diff/branch/edge_empty` shows only `< * [empty](empty/index.md)` in `index.md`, and `diff $S/diff/edge_indexdir.base.out $S/diff/edge_indexdir.branch.out` shows only the three `okf index` error lines, `error: Access to the path 'SIDE\edge_indexdir\x\index.md' is denied.` against `error: 'x/index.md' cannot be stored under that name: the storage holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name` (same `exit=1`, same files written before).

- [ ] **Step 4: The agent write tools against `origin/dev`, byte for byte**

`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes` and `okf_verify`, through `OkfBundleTools`, on three bundles. The frontmatter carries its own `generated` block so that no clock enters the bytes; `okf_append_log` dates its entry with today's UTC date on both sides. Save `$S/toolsdiff/toolsdiff.csproj`:

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
// Runs the agent write tools (okf_write_concept, okf_append_log, okf_regenerate_indexes, okf_verify)
// on one bundle copy and prints every result, the root replaced by BUNDLE.
using OKF4net.Agents;

var root = args[0];
var tools = new OkfBundleTools(root);
void Print(string label, string result) => Console.Write($"== {label}\n{result.Replace(root, "BUNDLE")}\n");
const string Fm = "type: Note\ntitle: Diff\ndescription: Differential run.\ngenerated:\n  by: human:ada\n  at: 2026-10-19T00:00:00Z\n";
Print("write new", tools.WriteConcept("notes/diff-new", Fm, "Body.\n"));
Print("write update", tools.WriteConcept("notes/diff-new", Fm, "Body two.\n"));
Print("write missing keys", tools.WriteConcept("notes/bad", "type: Note\n", "Body.\n"));
Print("write reserved", tools.WriteConcept("log", Fm, "Body.\n"));
Print("write invalid id", tools.WriteConcept("../escape", Fm, "Body.\n"));
Print("append log", tools.AppendLog("Update", "Differential run."));
Print("append log again", tools.AppendLog("Creation", "Second entry."));
Print("append log refused", tools.AppendLog("Up\ndate", "x"));
Print("regenerate", tools.RegenerateIndexes());
Print("regenerate again", tools.RegenerateIndexes());
Print("verify", tools.Verify("notes/diff-new", "human:ada", "2026-10-19T00:00:00Z"));
Print("verify missing", tools.Verify("notes/diff-new, notes/nope", "human:ada", "2026-10-19T01:00:00Z"));
```

and `$S/tools-differential.sh` (it builds one copy of `toolsdiff` per side, each referencing that side's `OKF4net.Agents`):

```bash
#!/bin/sh
# usage: tools-differential.sh <base worktree> <branch worktree> <scratch dir>
# Builds toolsdiff against each worktree's OKF4net.Agents, runs it on fresh copies of each
# bundle, and compares the outputs and the resulting trees byte for byte.
BASE="$1"; BRANCH="$2"; OUT="$3"; HERE="$(cd "$(dirname "$0")" && pwd)"
rm -rf "${OUT:?}"; mkdir -p "$OUT"
for side in base branch; do
  if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
  cp -r "$HERE/toolsdiff" "$OUT/toolsdiff-$side"
  dotnet build "$OUT/toolsdiff-$side/toolsdiff.csproj" -nologo -v q -p:OkfRoot="$(cd "$WT" && pwd -W)" > "$OUT/build-$side.txt" 2>&1 || { echo "build $side failed"; cat "$OUT/build-$side.txt"; exit 1; }
done
for b in bundles/acme_retail bundles/ga4 tests/fixtures/okf_v02; do
  name=$(basename $b)
  for side in base branch; do
    if [ $side = base ]; then WT="$BASE"; else WT="$BRANCH"; fi
    mkdir -p "$OUT/$side"; cp -r "$BRANCH/$b" "$OUT/$side/$name"
    dotnet "$OUT/toolsdiff-$side/bin/Debug/net10.0/toolsdiff.dll" "$(cd "$OUT/$side/$name" && pwd -W)" > "$OUT/$name.$side.out" 2>&1; echo "exit=$?" >> "$OUT/$name.$side.out"
  done
  if cmp -s "$OUT/$name.base.out" "$OUT/$name.branch.out"; then o="outputs identical ($(wc -l < "$OUT/$name.base.out") lines)"; else o="OUTPUTS DIFFER"; fi
  if diff -r "$OUT/base/$name" "$OUT/branch/$name" > /dev/null; then t="trees identical ($(find "$OUT/base/$name" -type f | wc -l) files)"; else t="TREES DIFFER"; fi
  echo "$name: $o; $t"
done
```

Run: `sh $S/tools-differential.sh <origin/dev worktree> <this worktree> $S/tools`. Expected (executed):

```text
acme_retail: outputs identical (42 lines); trees identical (20 files)
ga4: outputs identical (40 lines); trees identical (17 files)
okf_v02: outputs identical (34 lines); trees identical (7 files)
```

- [ ] **Step 5: `producers/` — two comments, then its suite**

In `producers/src/OkfProducer.Core/Generation/BundleWriter.cs` (with `Edit`), in `Write`, beside the `IndexGenerator.RegenerateIndexes(outPath)` call, replace

```csharp
        // inside the bundle and writes outside it" -- and that claim was false in every clause. Read
        // src/OKF4net/IndexGenerator.cs before restoring any version of it:
        //
        //   * the traversal is CollectMarkdown over Directory.GetFileSystemEntries (:437), not
        //     Directory.EnumerateDirectories, and it tests ReparsePoints.IsReparsePoint BEFORE it
        //     recurses (:447), so nothing under a linked directory is ever collected;
        //   * the per-directory child listing applies the same skip (:226), so a linked subdirectory
        //     contributes no index entry either;
        //   * immediately before each write there is an ancestor re-check (:266) AND a check on the
        //     index.md file node itself (:290), the latter added -- per its own comment -- to close
        //     exactly this class; both late checks use the strict IsReparsePointOrUninspectable, so
        //     an entry whose link status cannot be read is skipped too;
```

with

```csharp
        // inside the bundle and writes outside it" -- and that claim was false in every clause. Read
        // src/OKF4net/IndexGenerator.cs before restoring any version of it. It lists, reads and writes
        // through a FileSystemBundleProvider over this same outPath:
        //
        //   * the listing is the provider's walk (FileSystemBundleProvider.Walk), which tests
        //     ReparsePoints.IsReparsePoint on every entry and never descends into or lists a link, and
        //     refuses to enter a directory it cannot inspect, so nothing under a linked directory is
        //     ever indexed, and a linked subdirectory contributes no index entry either;
        //   * every index.md is read and written by the provider, whose STRICT guards
        //     (IsReparsePointOrUninspectable) check the whole path AND the index.md entry itself
        //     before the temporary is created, and again under the commit lock right before the
        //     commit; an index.md they refuse is skipped, never written through;
```

and in the summary of `CommitStaging` (the paragraph `<b>What runs after this, and gates itself.</b>`) replace

```csharp
    /// an <c>index.md</c> through a junction; that was wrong. It collects with
    /// <c>Directory.GetFileSystemEntries</c> and skips reparse points before recursing, skips them
    /// again when listing a directory's children, and re-checks both the ancestor chain and the
    /// <c>index.md</c> node itself immediately before writing. The line references are in
    /// <see cref="Write"/>, beside the call.</para>
```

with

```csharp
    /// an <c>index.md</c> through a junction; that was wrong. It lists through the filesystem
    /// provider's walk, which skips reparse points and never follows one, and writes each
    /// <c>index.md</c> through that provider, whose STRICT guards check the path and the
    /// <c>index.md</c> entry itself before the write and again right before the commit. The details
    /// are in <see cref="Write"/>, beside the call.</para>
```

Run: `dotnet test producers/OkfProducer.sln`
Expected: `Passed!  - Failed:     0, Passed:   826, Skipped:    15, Total:   841` — the same counts as on `origin/dev` (executed on both).

- [ ] **Step 6: The samples**

Run, for each solution: `for s in samples/*/*.sln; do echo "== $s"; dotnet build "$s" -v q -nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"; done`
Expected: `agents-quickstart`, `attestation-containers-demo` and `catalog-explorer` — `0 Warning(s)`, `0 Error(s)`. `acme-retail-agent` fails with `NU1605 … Detected package downgrade: Microsoft.Agents.AI from 1.24.0 to 1.20.0`, **exactly as on `origin/dev`** (Deviation 4: the sample pins 1.20.0, `OKF4net.Agents` was bumped to 1.24.0). To check that its code compiles against this branch, copy `samples/acme-retail-agent/src/AcmeRetailAgent/{*.cs,AcmeRetailAgent.csproj}` and the root `Directory.Build.props` to a scratch directory, raise the pin to `1.24.0` and point both `ProjectReference`s at the worktree's `src/OKF4net/OKF4net.csproj` and `src/OKF4net.Agents/OKF4net.Agents.csproj`, and build: `0 Error(s)`, on this branch and on `origin/dev` alike. Do not commit that change in this lot.

- [ ] **Step 7: Verify and commit**

```bash
for f in tests/OKF4net.Tests/CliTests.cs producers/src/OkfProducer.Core/Generation/BundleWriter.cs; do echo "$(tr -cd '\r' < $f | wc -c) $(wc -l < $f) $f"; done
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"   # Passed: 3366, Skipped: 28, Total: 3394
git add tests/OKF4net.Tests/CliTests.cs producers/src/OkfProducer.Core/Generation/BundleWriter.cs
git commit -F - <<'MSG'
test(cli): okf index no longer references an empty directory; producer comments describe the provider gates

CliTests pins D2 at the CLI surface: the printed lines are unchanged and the
root index no longer lists an empty directory. The two comments of the
producer's BundleWriter that described IndexGenerator's former walks and late
checks now describe the provider's walk and guards that replaced them.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

### Task 8: Lot verification and docs

**Files:**
- Modify: `README.md` ("Writing through a provider", which Task 2c created and Tasks 4a–5 extended)
- Modify: `CLAUDE.md` (the `Providers/` bullet: the write side)
- Modify: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (four dated notes; nothing rewritten)
- Modify: `CHANGELOG.md` (one bullet, the measured cost of `okf index`; every other entry the lot needs is already in it, written by the task that made the change)
- Scratch: `$S/aot-smoke.sh`, `$S/bench.js`

**Interfaces:**
- Consumes: everything above.
- Produces: the docs, and the executed evidence the PR description carries.

- [ ] **Step 1: README**

In `README.md`, after the line `` `AppendLog(kind, text)` appends an entry to the root `log.md` the same way. `` (Task 5's), insert a blank line and:

```markdown
`IndexGenerator.RegenerateIndexes(provider, options)` regenerates every
`index.md` through a provider: it lists and validates the bundle as a load
does, writes each `index.md` conditioned on the version it read, retries a
conflict like the writer, and returns a `BundleWriteReport` — the `index.md`
keys written, in order, and those it could not write, with why. A directory is
referenced by its parent's index only when a file lies under it: an empty
directory is not, a directory holding only non-`.md` files is.
`IndexGenerator.RegenerateIndexes(path)` and `okf index` go through a
`FileSystemBundleProvider` and print and return what they did before.
```

- [ ] **Step 2: CLAUDE.md, the `Providers/` bullet**

With `Edit`, (a) `` `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lot A). `` becomes `` `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lots A and B). ``:

```markdown
`docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lot A).
```

```markdown
`docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lots A and B).
```

(b) In the same bullet,

```markdown
`FileSystemBundleProvider` is the default and read-only until lot B; with a confinement boundary
```

becomes

```markdown
`FileSystemBundleProvider` is the default and writable: `Write` puts the bytes in a temporary `.okf-write-<32 hex>.tmp` beside the target, then, under a striped in-process commit lock, re-checks the boundary, the path STRICTly, the exact stored name and the condition (the SHA-256 of the stored bytes) and commits with `File.Replace` or `File.Move` without overwrite; a failed commit is classified from the disk, and an unknown outcome keeps the temporary (`BundleWriteOutcomeUnknownException`). That is the only way `src/OKF4net` writes a bundle (`FileMemoryStore`'s scope deletion moves onto it in lot D): `BundleConceptWriter` (concepts, verifications, `log.md`) and `IndexGenerator` write through a provider, conditioned on what they read, a conflict retried at most three times, and a creation first passes `BundleTreeCheck` — do not add a write around them. `Bundle.ListNames` (the bounded listing) and `Bundle.ReadText` (the bounded strict-UTF-8 reader) are the core's single listing consumer and single reader; do not fork either. With a confinement boundary
```

Check the claim before committing it: `grep -rn 'File\.\(WriteAll\|Delete\|Move\|Replace\)\|Directory\.\(Delete\|CreateDirectory\)\|new FileStream' src/OKF4net --include=*.cs | grep -v '///'` lists only `Providers/FileSystemBundleProvider.cs`.

- [ ] **Step 3: The design's dated notes**

The spec is French and keeps its history: each note is added beside the text it qualifies, dated, and nothing is rewritten. With `Edit` on `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`:

(a) In §4, after the line `    Cet échec est rendu comme un conflit.`, a blank line and:

```markdown
  > **Note du 2026-10-19 (lot B, plan d'implémentation, B17 à B22) :** chaque
  > primitive a été mesurée avant d'être employée (sondes du plan). Sous Unix,
  > `File.Move` sans écrasement est un `lstat` puis un `rename` : un autre
  > **processus** qui crée le nom entre les deux est remplacé ; dans un processus,
  > le verrou de commit ferme la fenêtre (B20, #178). Sous Windows,
  > `File.Replace` est atomique pour l'état stocké, pas pour les lecteurs :
  > pendant le remplacement, un lecteur concurrent peut ne trouver aucun objet
  > sous le nom ou rencontrer une violation de partage, jamais un objet tronqué
  > (B17). Le temporaire d'un remplacement est créé en 0600 et prend le mode de
  > la cible par son handle ; celui d'une création garde le mode par défaut,
  > faute d'API pour lire l'umask (B18). Un nom que le volume confondrait avec
  > une entrée stockée est refusé d'après le volume lui-même, pas d'après un
  > repli de casse (B19) ; une cible que le processus ne peut pas écrire est
  > refusée comme avant (B22) ; un échec du commit est classé d'après le disque,
  > et une issue inconnue garde le temporaire (B21).

  > **Note du 2026-10-20 (lot B, revues externes exécutées, B42 à B48) :**
  > `ReplaceFile` peut aussi échouer **sans effet** (erreur 1175, mesurée deux
  > fois en 1 500 commits, lecteurs ouverts) : le commit est refait, au plus
  > trois fois, depuis la re-vérification de la condition sur les octets stockés,
  > puis échoue avec `BundleWriteNotCommittedException`, la cible intacte (B43).
  > Une lecture réessaie la fenêtre de `ReplaceFile` (violation de partage, ou
  > entrée listée un instant plus tôt puis introuvable), sous Windows seulement,
  > ce qui la rétrécit sans la fermer (B45). Le classement d'un commit échoué
  > renouvelle la frontière et les gardes STRICT avant toute lecture (B44). Une
  > écriture inconditionnelle n'exige que le droit d'écrire la cible, comme
  > `File.WriteAllText` ; seul `IfMatch` la lit (B42). Une notification
  > `onWriteCommitted` qui échoue après le commit ne fait pas passer l'écriture
  > pour un échec (B46) : elle s'exécute une seule fois, l'écriture devenue
  > définitive, hors de toute nouvelle tentative, et rien de ce qu'elle lève
  > n'est pris pour un refus de l'écriture (B48). La politique de clés couvre
  > aussi les clés `index.md` que produit `IndexGenerator` (B47).
```

(b) In §4, after the line `  l'écriture est inconditionnelle, comme aujourd'hui.`, a blank line and:

```markdown
  > **Note du 2026-10-19 (lot B, B29) :** la version attendue est une
  > `BundleWriteCondition` (`IfMatch(v)`, `MustNotExist` ou `None`), pas une
  > `BundleObjectVersion?` : l'observation « absent » (`MustNotExist`) doit
  > pouvoir atteindre le writer. Les surcharges à trois arguments restent
  > inconditionnelles.
```

(c) In §9, right before the line ``**`BundleRoot` d'`OkfBundleTools` et de `BundleConceptWriter`.** Ces propriétés``, this note followed by a blank line:

```markdown
> **Note du 2026-10-19 (lot B, plan d'implémentation) :** `BundleConceptWriter`
> rend tout refus d'un chemin par le provider avec le message de son ancienne
> garde de point de reparse : ses deux messages (le chemin, l'entrée cible)
> n'en font plus qu'un, puisque le provider ne les distingue pas (B32), et ses
> essais lire-modifier-écrire tournent sous son verrou (B30). `IndexGenerator`
> référence un sous-dossier dès qu'un objet, de n'importe quelle extension, se
> trouve sous lui (§11) ; la création d'un `index.md` passe le contrôle
> d'arborescence comme toute création du cœur ; la surcharge `string` saute en
> silence un `index.md` que le provider refuse et un `index.md` racine
> illisible, et lève toute autre erreur, comme avant.
```

(d) In §10, under "Inchangé", after the line `    index régénéré ;`, a blank line, this note, and a blank line:

```markdown
    > **Note du 2026-10-19 (lot B) :** et hors deux cas voisins, consignés au
    > CHANGELOG : un dossier dont les seules entrées sont des liens symboliques
    > ou des jonctions n'est plus référencé non plus (aucun listing ne suit un
    > lien), et un dossier nommé `index.md` qui contient des fichiers fait
    > échouer `okf index` avec `BundleWriteConflictException` au lieu de
    > `UnauthorizedAccessException` (message d'erreur différent, même code de
    > sortie).
```

- [ ] **Step 4: The CHANGELOG — the cost of `okf index`, then check it**

Under `### Changed`, after Task 6's bullet (its last line is `` `UnauthorizedAccessException`. ``), add the cost the lot measured (R2, and r5's wording: the range over every series measured so far, not a band; Step 5 (g) measures it again, and your series go into the PR):

```markdown
- **`okf index` is slower on a large bundle.** Regenerating the indexes now
  lists, validates and reads the bundle through the provider, as a load does,
  and commits every `index.md` atomically. On 3 000 flat concepts (medians of
  7 runs, the two builds alternating), fifteen series measured on one machine
  for this change and its three reviews put it at +17.7 % to +37.5 % longer
  than before, typically about +24 %; `okf validate` varied between −1.3 %
  and +9.5 % over the same series, with overlapping ranges, so no stable
  change is established. Measurements on one machine are not a general
  guarantee. Bringing `okf index` back down is tracked in #208.
```

Every other behaviour lot B changes has its entry under `## [Unreleased]`, written by the task that made the change. Check each one exists (one `grep -c` per item, each must print at least `1`):

```bash
for p in 'FileSystemBundleProvider` is writable' 'checked again under a per-object lock' 'A failed commit says what happened.**' 'Reads no longer block writes (B8)' 'FileShare.ReadWrite | FileShare.Delete' 'concurrent reader can for a moment' 'Durability of a filesystem write, stated' 'is now invoked once per attempt' '`log.md` is written by `BundleConceptWriter`' 'no longer referenced by its parent' 'with the same message as a path through one' 'Error: log.md resolves through a reparse point' 'Write side of the provider contract' 'Conditional concept writes' 'VerificationOutcome.Report' '`IndexGenerator` over a provider' 'BundleWriteNotCommittedException`**' 'A write notification that fails no longer hides the write' '`okf index` is slower on a large bundle'; do printf '%s: ' "$p"; awk '/^## \[Unreleased\]/{f=1} /^## \[0/{if(f) exit} f' CHANGELOG.md | grep -cF -- "$p"; done
```

Expected: nineteen lines, each ending `: 1` — atomic conditional writes and `CanWrite` (Task 2a), the commit's classification and its retry of a replacement refused without effect, the Windows reader window (B17) and `FileShare` on read (Task 2b), the durability statement (Task 2c), a failing write notification (Task 4a), `buildBody` once per attempt (Task 4b), `log.md` through the writer (Task 5), empty directories (Task 6), the cost of `okf index` (this step), the two refusal texts that changed (Tasks 4a, 5), and the five `### Added` entries. No breaking API change is listed: none was made.

- [ ] **Step 5: The lot's executed verification**

Run each and keep the output for the PR description.

(a) Windows, three identical full runs:

```bash
dotnet build OKF4net.sln -v q -nologo && dotnet format OKF4net.sln --verify-no-changes
for i in 1 2 3; do dotnet test OKF4net.sln --no-build --filter "Category!=ContainerIntegration" 2>&1 | grep -E "Passed!|Failed!"; done
```

Expected: `0 Warning(s)`, `0 Error(s)`, format exit 0, then three times `Passed!  - Failed:     0, Passed:  3366, Skipped:    28, Total:  3394`.

(b) Linux, the whole test project, as root and as an unprivileged user — the Global Constraints command with `--filter "Category!=ContainerIntegration"` in both passes. Expected: root `Passed!  - Failed:     0, Passed:  3330, Skipped:    64, Total:  3394`; `tester` `Passed!  - Failed:     0, Passed:  3334, Skipped:    60, Total:  3394` (root skips four more tests, those that need an unprivileged user — the read-only and the write-only targets among them).

(c) `dotnet test producers/OkfProducer.sln` — `Passed: 826, Skipped: 15, Total: 841`.

(d) The two differentials of Task 7 (Steps 3 and 4), on the final tree: the same ten and three lines.

(e) Native AOT. The ILCompiler's link step calls `vswhere.exe`, which the Visual Studio Build Tools install outside `PATH`; in PowerShell:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish src/OKF4net.Cli -c Release
dotnet publish src/OKF4net.Render -c Release
```

Expected: both succeed (`okf.exe` 4 817 920 bytes and `okf-render.exe` 4 567 040 bytes in r4's replay; 4 817 408 and 4 567 040 in r3's; 4 811 776 and 4 563 968 in r1). Then the published `okf` against the Debug build on two copies of `bundles/acme_retail` — save as `$S/aot-smoke.sh`:

```bash
#!/bin/sh
# usage: aot-smoke.sh <branch worktree> <scratch dir>
# Runs okf index / verify from the Native AOT binary and from the Debug build on two copies of
# bundles/acme_retail and compares transcripts and trees.
WT="$1"; OUT="$2"
rm -rf "${OUT:?}"; mkdir -p "$OUT/aot" "$OUT/jit"
for side in aot jit; do
  if [ $side = aot ]; then OKF="$WT/src/OKF4net.Cli/bin/Release/net10.0/win-x64/publish/okf.exe"; else OKF="dotnet $WT/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll"; fi
  cp -r "$WT/bundles/acme_retail" "$OUT/$side/acme_retail"
  (
    cd "$OUT/$side"
    $OKF --version; echo "exit=$?"
    $OKF index acme_retail; echo "exit=$?"
    $OKF verify acme_retail metrics/revenue metrics/gross-margin --by human:ada --at 2026-10-19T00:00:00Z; echo "exit=$?"
    $OKF verify acme_retail metrics/revenue nope --by human:ada --at 2026-10-19T01:00:00Z; echo "exit=$?"
    $OKF index acme_retail; echo "exit=$?"
    $OKF validate acme_retail; echo "exit=$?"
  ) > "$OUT/$side.out" 2>&1
  node "$(dirname "$0")/normalize.js" "$OUT/$side.out" "$(cd "$OUT/$side" && pwd -W)"
done
cmp -s "$OUT/aot.out" "$OUT/jit.out" && echo "transcripts identical ($(wc -l < "$OUT/aot.out") lines)" || echo "TRANSCRIPTS DIFFER"
diff -r "$OUT/aot/acme_retail" "$OUT/jit/acme_retail" > /dev/null && echo "trees identical ($(find "$OUT/aot/acme_retail" -type f | wc -l) files)" || echo "TREES DIFFER"
grep -E "^(wrote|recorded|error|exit=)" "$OUT/aot.out" | head -20
```

Run: `sh $S/aot-smoke.sh <this worktree> $S/aot`. Expected: `transcripts identical (51 lines)`, `trees identical (18 files)`, then the `okf index` lines (`wrote SIDE\acme_retail\computations\index.md` … `wrote SIDE\acme_retail\index.md`), `recorded metrics/revenue  human:ada  2026-10-19T00:00:00Z`, `error: unknown concept "nope"` with `exit=1`.

(f) Nothing under `tests/fixtures/` or `bundles/` moved: `git diff --stat origin/dev..HEAD -- tests/fixtures bundles` prints nothing.

(g) The cost of `okf index` (R2), against `origin/dev`, with the Debug builds of both worktrees (`dotnet build src/OKF4net.Cli` in each) — save as `$S/bench.js`:

```javascript
// okf validate / okf index on 3 000 flat concepts, origin/dev against the branch: 8 alternating
// iterations per verb and side, the first discarded, the median of the other 7 reported.
// usage: node bench.js <base okf.dll> <branch okf.dll> <scratch dir>
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const [baseDll, branchDll, scratch] = process.argv.slice(2);
fs.rmSync(scratch, { recursive: true, force: true });
const seed = path.join(scratch, 'seed');
fs.mkdirSync(seed, { recursive: true });
fs.writeFileSync(path.join(seed, 'index.md'), '---\nokf_version: "0.2"\n---\n\n# Bundle\n');
for (let i = 0; i < 3000; i++) {
  const n = String(i).padStart(4, '0');
  fs.writeFileSync(path.join(seed, `c${n}.md`), `---\ntype: Note\ntitle: Concept ${i}\ndescription: Concept ${i} description.\n---\n\nBody ${i}.\n`);
}
const run = (dll, args) => spawnSync('dotnet', [dll, ...args], { encoding: 'utf8', maxBuffer: 1 << 28 });
run(baseDll, ['index', seed]);
for (const side of ['base', 'branch']) fs.cpSync(seed, path.join(scratch, side), { recursive: true });
const rows = [];
for (const verb of ['validate', 'index']) {
  for (let it = 0; it < 8; it++) {
    for (const side of it % 2 === 0 ? ['base', 'branch'] : ['branch', 'base']) {
      const t0 = process.hrtime.bigint();
      const p = run(side === 'base' ? baseDll : branchDll, [verb, path.join(scratch, side)]);
      const s = Number(process.hrtime.bigint() - t0) / 1e9;
      rows.push({ verb, side, it, s, exit: p.status, out: p.stdout + p.stderr });
    }
  }
}
const median = a => { const b = [...a].sort((x, y) => x - y); return b[Math.floor(b.length / 2)]; };
for (const verb of ['validate', 'index']) {
  const m = {};
  for (const side of ['base', 'branch']) m[side] = median(rows.filter(r => r.verb === verb && r.side === side && r.it > 0).map(r => r.s));
  const exits = [...new Set(rows.filter(r => r.verb === verb).map(r => r.exit))];
  console.log(`${verb}: origin/dev ${m.base.toFixed(3)} s, branch ${m.branch.toFixed(3)} s, ${((m.branch / m.base - 1) * 100).toFixed(1)} % (median of 7; exit codes ${exits.join(',')})`);
}
const outs = v => [...new Set(rows.filter(r => r.verb === v).map(r => r.out.split(path.join(scratch, 'base')).join('<b>').split(path.join(scratch, 'branch')).join('<b>')))];
console.log('distinct outputs: validate ' + outs('validate').length + ', index ' + outs('index').length);
```

Run: `node $S/bench.js <origin/dev worktree>/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll <this worktree>/src/OKF4net.Cli/bin/Debug/net10.0/okf.dll $S/bench`, three times. Expected: `distinct outputs: validate 1, index 1` in every series (the same bytes from both builds). The times are not a pass/fail check: they vary from series to series and from machine to machine, so put yours in the PR beside these. Over the fifteen series measured for this plan, all on one machine — eight by its own revisions (r1–r3 drafting +17.7 %, +19.0 %, +19.5 %, +22.8 %, +23.9 %; r4 +18.2 %, +20.6 %, +20.7 %, `origin/dev` 0.86–0.88 s, this branch 1.01–1.06 s) and seven by its three executed external reviews (r2 +24.9 %; r3 +37.5 %, 0.409 s against 0.562 s, then +24.0 % and +24.0 %; r4 +36.0 %, 0.401 s against 0.545 s, then +26.8 %, 0.394 s against 0.499 s, and +24.4 %, 0.393 s against 0.488 s) — `okf index` was +17.7 % to +37.5 % slower, typically about +24 % (the median of the fifteen is +23.9 %). `okf validate` varied between −1.3 % and +9.5 % over the same series (r4 +2.5 %, −1.3 %, +2.2 %; the r3 review +9.5 %, +2.5 %, −0.7 %; the r4 review −0.1 %, +3.2 %, +2.2 %), with overlapping ranges: no stable change is established. Three series on one machine are not a general guarantee, and neither are fifteen. The cost is accepted for lot B and tracked by #208.

`dotnet test tests/OKF4net.Tests --filter Category=ContainerIntegration` is not part of this lot's checks: lot B does not touch the attestation code, which moves onto the seam in lot C (spec §11's "0 test ignoré" item is lot C's).

- [ ] **Step 6: Verify and commit**

```bash
dotnet build OKF4net.sln -v q -nologo          # 0 Warning(s), 0 Error(s)
dotnet format OKF4net.sln --verify-no-changes  # exit code 0
git add README.md CLAUDE.md CHANGELOG.md docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md
git commit -F - <<'MSG'
docs: writing through a provider covers IndexGenerator; CLAUDE.md, the seam design and the CHANGELOG record lot B

The README's "Writing through a provider" describes the index regeneration
over a provider and the empty-directory rule. CLAUDE.md's Providers bullet
states the filesystem write protocol and the single write, listing and reading
paths. The seam design gains dated notes where lot B's implementation decided
differently from its text; its history is untouched. The CHANGELOG states the
measured cost of okf index on a large bundle.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
MSG
```

- [ ] **Step 7: The PR**

Push `feat/bundle-provider-b` (`git push -u origin feat/bundle-provider-b`) only once the lot is reviewed, and open the PR against `dev`. Its description carries, as checked boxes with the outputs pasted:

- [ ] Windows: build 0 warnings, format, three full runs `3366 / 28 / 3394` (Step 5a).
- [ ] Linux container, root and unprivileged, whole test project (Step 5b).
- [ ] `dotnet test producers/OkfProducer.sln` green, counts equal to `origin/dev` (Step 5c).
- [ ] The cost of `okf index` against `origin/dev`, three series, both outputs identical, the medians pasted (Step 5g).
- [ ] `okf index` / `okf verify` differential: eight bundles identical, the two edge bundles differ exactly as the CHANGELOG says (Task 7 Step 3).
- [ ] Agent write tools differential: three bundles identical (Task 7 Step 4).
- [ ] AOT publish of `okf` and `okf-render`; the published `okf` matches the JIT build (Step 5e).
- [ ] `git diff --stat origin/dev..HEAD -- tests/fixtures bundles` empty (Step 5f).
- [ ] The decisions to confirm: B17–B48 (parts A, B and C, and revisions r3 and r4), the deviations of each part, and the `acme-retail-agent` package pin (Deviation 4 of part C, not fixed here).
- [ ] `graphify update .` run after the last code change (repository rule).

## Appendix: pre-flight consistency pass

**Revision r4.** F6 was fixed on a spike rebuilt from `spike/lotb-d` (`spike/lotb-e`): Tasks 1–3 kept as they were; Tasks 4a, 4b, 4c and 5 cherry-picked one by one, each commit amended with its part of the fix (the structural change in 4a; in 4b the cherry-pick's conflicts resolved by hand — `WriteConditionedLocked` and `WithRetries` run inside `WriteThenNotify`; 4c's `written.Add` beside the record, before `NotifyCommitted`; 5's `AppendLog`) and its regression tests; Tasks 6 and 7 cherry-picked unchanged; Task 8's commit amended with the CHANGELOG's measured cost and the seam design's dated note (B48). Every intermediate task was built (0 warnings), formatted and run in full on Windows, and the affected classes run in the Linux container as root and as `tester` after 4a, 4b, 4c and 5. Each new conflict test was run against r3's writer of its own task and failed: in 4b all twelve conflict cases then present (4a's three included), in 4c three, in 5 two. Every code block of r3 that the rebuilt spike changed was regenerated from it by mapping its line range through the diff between `spike/lotb-d` and `spike/lotb-e` (`regen.js`, which also reports a block that an insertion merely touches — it found Task 4a's `### Changed` bullets, extended at their end, and Task 4c's two one-line `records.Add` edits, which are right as they are); every csharp/markdown block of r4 is then found verbatim in `spike/lotb-e` at its task's commit or the one before (`checkblocks.js`; the one exception, as in r3, is Task 7's scratch `toolsdiff/Program.cs`, which is not in the repository). Code-block integrity against r3 (`integrity.js`): 200 of r3's 232 code blocks are byte-identical in r4; the 32 others are Task 4a's test file, its blocks (b), (g), (j), (k), (l), the prepare-and-write loops of (m), (q) and its `### Changed` bullets; Task 4b's test file, the old and new blocks of (a) and (b) in Step 3, the new block of (d), the old and new blocks of (a) and (b) in Step 4, and its `### Changed` bullet; Task 4c's test file; Task 5's test file and its block (b); Task 8's seam-design note (a) and CHANGELOG bullet; and seven command blocks whose commented counts or commit messages changed (Task 3 Step 5, Task 4a Step 9, Task 4b Step 7, Task 4c Step 6, Task 5 Step 8, Task 6 Step 6, Task 7 Step 7). No block was added or removed. Then, in a fresh worktree of `origin/dev` (`fa983e4`), every step of r4 was applied literally, as for r3, with the Linux container after Tasks 2a, 2b, 2c, 4a, 4b, 4c, 5, 6 and 8: every step applied without a missing or ambiguous anchor, every red step failed for the reason its task gives, Tasks 1–3 gave r3's counts exactly, every count of Tasks 4a–8 in the bodies and in the table below is that replay's, and the final tree is identical to `spike/lotb-e` (`git diff --quiet`). On that tree: the two differentials of Task 7 against `origin/dev` (the same ten and three lines), `dotnet test producers/OkfProducer.sln` 826/15/841, the Native AOT publish and smoke of Task 8 (transcripts identical, 51 lines; trees identical, 18 files), and the review's five F6 modes (its own `extra` program, built against the replay's `OKF4net.dll`): `notify-append-once` and `notify-append` `notifications=1; buildBody=1; appended=1`, `notify-log` `notifications=1; appended=1`, `notify-conditional` `notifications=1; diskNew=True`, `notify-verify` `notifications=1; recorded=True; written=1; failed=0` — each string method returning the notification's own text, `Error: 'x.md' changed since it was read; the write was refused`, never the writer's refusal of its condition nor `kept changing`. Two counts r3 had left stale are corrected with them: Task 3's full run (3 299, not the drafting run's 3 284) and Task 6's Linux pass (713 tests: r2's 686 predated r3's provider tests).

**Revision r3.** The fixes of the external review were made on a spike rebuilt one commit per task (`spike/lotb-d`, from `spike/lotb-c-replay`, whose tree is `spike/lotb-c`'s), each as a change to the commit of the task that owns it, every intermediate task built (0 warnings), formatted and run in full on Windows, and the affected classes run in the Linux container as root and as `tester`; each new test was also run against the unfixed code and failed (F1, F3, F4, F5; F2 is the review's own repro). Every code block of the plan that changed was regenerated from that spike by its task and position (`setblock.js`, which regenerates every unchanged block of r2 byte for byte from `spike/lotb-c-replay` too); 186 of r2's 226 code blocks are byte-identical in r3 (`integrity.js`, which looks each one up among r3's blocks), the 40 others are exactly the blocks of the edits listed in the revision history and two commands whose commented counts moved, and r3 adds the blocks of Task 2b's new edits, of the CHANGELOG bullets of Tasks 2b and 8, of Task 8's benchmark and of probes A4 and A5. Then, in a fresh worktree of `origin/dev` (`fa983e4`), every step of r3 was applied literally — every block taken from the plan file itself, by its task and its position, each checked against the sentence that introduces it — with the red, green, CRLF, format and full-suite checks after each task, the Linux container (root and `tester`) after Tasks 2a, 2b, 2c, 4a and 8, and a commit per task: every step applied without a missing or ambiguous anchor, every red step failed for the reason its task gives, every count below is that replay's, and the final tree is identical to `spike/lotb-d` (`git diff --quiet`). Two text-only changes made after that replay (the wording of `Read`'s remarks and of one test summary, and the measured range of the CHANGELOG's `okf index` bullet) were replayed again in full the same way, with the same counts everywhere but one full run of Task 7, where `CliContainerEngineRunTests.A_hung_engine_kill_does_not_hang_the_timed_out_run` failed once (an 11 s timing test lot B does not touch; three runs of its class passed right after — see the Global Constraints). In r2's table below, "red: build errors only" was wrong for Task 2c: its tests compile, and exactly two assertions fail (M1).


`plan-assembled.md` is `part-a-amended.md`, `part-b-amended.md` and `part-c.md`, concatenated. Part A is amended in two ways: part B's three "Changes required in part A" are folded into Tasks 2a and 2b (the list below), and two sentences that Task 6 made untrue are corrected (rows 19 and 20 of the first table). Part B is reduced to what remains: its section "Changes required in part A" becomes a paragraph pointing at part A, its Deviation 5, `B32` and Task 4a's `Consumes` and hook heading no longer cite `PA-*`; every code block of part B is regenerated from the same spike commits by B's own `gen-plan.js` (the original part B regenerates byte for byte from them, which shows the tooling reproduces it), so no code block of part B changed.

**The replay.** In a fresh worktree of `origin/dev` (`fa983e4`), every step of the assembled plan was applied literally — part A's code blocks taken by their position in `part-a-amended.md`, each checked against the sentence that introduces it, parts B and C's by their tags — with the plan's own red, green, CRLF, format and full-suite checks after each task, and a commit per task. Result: every step applied without a missing or ambiguous anchor; every red step failed for the reason its task gives (build errors naming what the task adds; Task 2b's `Concurrent_reads_never_block_a_replacement_and_never_see_a_torn_object` before B8; Task 4a's and Task 5's one expected string each); every green step reached the counts the tasks state. After Task 2c, `FileSystemBundleProvider.cs` and `FileSystemBundleProviderWriteTests.cs` are identical to `spike/lotb-b`'s (part A plus PA-1..PA-3) and the whole tree differs from part A's own spike only by those 80 lines; after Task 5 the tree is identical to `spike/lotb-b`; after Task 8 it is identical to `spike/lotb-c` (`git diff --quiet`, both). The replay found two defects, both fixed in the text: the fold first dropped the blank line between the `ParentsResolvedForTest` property and the next hook (a slip of the fold, not of the plan), and Task 2a's (c) block ended with a blank line that a literal replacement turns into a second blank line before `CheckBoundary`'s summary (part A's own `replace.ps1` trimmed it, which is why part A's replay did not see it) — the block now ends at `HashForRead`'s brace. The replay then ran the lot's checks on its final tree: the `okf` and agent-tool differentials of Task 7 (same ten and three lines), and the Linux pass of Task 6 on the replay's Task 6 commit (root 664/22/686, `tester` 666/20/686).

### PA-1..PA-3: where they are folded

| Change | Folded into | Text of part A that changed |
|---|---|---|
| PA-1 — every segment storable, then the whole path STRICT-checked before anything is listed | Task 2a, Step 3 (c) code block, `ResolveParentForWrite` (summary and opening) | the prose "What it does, in order", Interfaces → Produces (the guards Task 4a relies on), decision B6 (1), the commit message |
| PA-2 — the late check covers the object's own entry | Task 2a, Step 3 (c) code block, end of `ResolveParentForWrite` (`GuardStrict(key, Path.Join(current, key.Name))` and its comment) | B6 (1), the prose, the commit message |
| PA-2 — the `ParentsResolvedForTest` hook | Task 2b, Step 3 (a) (the property opens the block of hooks, in the position the spike has it) and a new edit (f) — (h) before r3 — (the call, right before PA-2's comment) | Task 2b Interfaces → Produces; the commit message; the note under (h) that Task 4a wires it |
| PA-3 — a second listing before a name is called folded | Task 2a, Step 3 (c) code block, `FindExactForWrite` | B19 (b), the prose, the commit message |
| PA-3's test, `Writers_creating_one_new_directory_at_the_same_moment_all_succeed` | Task 2a, Step 1, end of `FileSystemBundleProviderWriteTests` | Task 2a Step 4's count: 36 passed, 2 skipped, 38 (34/2/36 before PA-3's test, 35/2/37 before F5's) |
| (history) | — | revision history r2 |

Without them part B's Task 4a and part C's Task 6 consume members part A did not produce: executed on the spike, removing PA-1 and PA-2 makes `IndexTests.Index_write_is_skipped_for_an_index_file_swapped_for_a_junction_whose_link_status_cannot_be_inspected` and `…_for_a_directory_swapped_for_a_junction_whose_link_status_cannot_be_inspected` fail with a raw `UnauthorizedAccessException : Access to the path '…\d' is denied.` (and part B recorded the writer's equivalents).

### Pairs of tasks that share a file or an interface

| # | Pair | Shared | Produced by the first | Consumed by the second | Found |
|---|---|---|---|---|---|
| 1 | 1 → 2a | `WritableProviderContractTests`, `BundleWriteConflictReason`, `BundleWriteOutcomeUnknownException` | the 15 contract tests, the two exception shapes | `FileSystemBundleProviderContractTests` inherits the tests; the provider throws both shapes | consistent (replay: 2a's 36 tests include the 15) |
| 2 | 1 → 3, 4a–5, 6 | `InMemoryBundleProvider` | `canWrite`, `native`, `Calls`, `ClearCalls`, `ConflictOnNextWrite`, `BeforeNextWrite`, `FailWrite`, and lot A's `Add`, `FailReadOf`, `RefuseExistsOf`, `FailEnumerateWith`, `ObeyLimits` | every member the later tests call | consistent — each later test file compiled against Task 1's rewrite in the replay |
| 3 | 1 → 4c, 6 | `BundleWriteReport`, `BundleWriteFailure` | null lists and an empty reason refused | 4c's `Report`; 6's report (an empty string is only Task 6's internal "skip" marker, never passed to `BundleWriteFailure`) | consistent |
| 4 | 2a → 2b → 2c | `FileSystemBundleProvider.cs` | 2a the write side; 2b hooks, classification, read sharing; 2c `ForgetListings` | each task's edits anchor on text the previous one wrote (2b (a) after `TemporaryCreatedForTest`, (f) on PA-2's comment; 2c (c) on `Write`'s body, (d) on 2b's `BeforeCommitForTest` in `Delete`) | consistent after the fold; every anchor unique in the replay |
| 5 | 2a/2b → 4a | the guards and the hook the writer relies on | PA-1, PA-2, `ParentsResolvedForTest` | `BeforeLateReparseCheckForTest` forwards to the hook; `WriteConcept_refuses_a_path_crossing_a_junction_whose_link_status_cannot_be_inspected` | **inconsistent before the fold** (part B's own "Changes required in part A"); fixed by the fold |
| 6 | 2a → 4b | `FindExactForWrite` under two writers | PA-3 | `Two_writers_without_a_shared_lock_lose_no_append_over_fifty_rounds` | inconsistent before the fold; fixed by it; 20/20 in the replay |
| 7 | 2a/2b → 6 | the provider's late guards | whole-path and own-entry STRICT checks | the four `IndexTests` seam tests (B41's table) | consistent after the fold; two of them fail without PA-1/PA-2 (above) |
| 8 | 2c → 6 | provider scopes (B28) | writes resolve outside the memo and drop it on their path | Task 6 lists and reads inside one scope and writes only after closing it (B39) | consistent |
| 9 | 3 → 4a, 5, 6 | `BundleTreeCheck.CheckCreation` | a sentence without final period, or `null` | the writer appends a period after `Error: `; Task 6 uses the sentence as the failure's reason, or throws `NameUnavailable` in the string overloads | consistent |
| 10 | 4a → 6 | `src/OKF4net/Bundle.cs` | `ReadText`, `ReadChunkBytes` made `internal` | Task 6 calls `ReadText`; its own edit (the listing loop of `Load`, `ListNames`) touches another region | consistent; anchors disjoint in the replay |
| 11 | 4b → 6 | the retry policy | `BundleConceptWriter.MaxAttempts`, `RetryDelay(int)` (`internal static`) | `IndexGenerator.WriteIndex` | consistent: one policy |
| 12 | 4a, 4b, 5 → 6 | `BundleConceptWriterProviderTests.WriteCalls` | the seven-row matrix (`Action<BundleConceptWriter>`) | Task 6 changes its type to `Action<BundleConceptWriter, IBundleProvider>` and adds two rows; its "old" block is the dictionary exactly as Task 5 leaves it | consistent; the matrix is extended, not duplicated |
| 13 | 4a–5 → 7 | `okf verify`, the agent write tools | the writer through the provider | Task 7's two differentials | consistent: byte-identical to `origin/dev` |
| 14 | 6 → 7 | `okf index` | the string overload through the provider, D2 | the CLI test; the producer's two comments | consistent |
| 15 | 2c, 4a, 4b, 5 → 8 | `README.md` "Writing through a provider" | the section (2c) and its writer paragraphs | Task 8 inserts after Task 5's `AppendLog` line | consistent |
| 16 | 1–6 → 8 | `CHANGELOG.md` | each task's own entries | Task 8's audit: nineteen items, each present once (r3: the phrase for Task 2b's bullet is `A failed commit says what happened.**`, since its new `### Added` bullet quotes the title) | consistent |
| 17 | 6 → 8 | `CLAUDE.md` | the `ReparsePoints` bullet's lenient count | Task 8 edits the `Providers/` bullet (another line) | consistent |
| 18 | 6 → 7 → 8 | the differential scripts | Task 7 Steps 3–4 | Task 8 Step 5d reruns them on the final tree | consistent |
| 19 | Global Constraints ↔ 6 | the lenient-site count | "the count of four lenient sites in `CLAUDE.md` stays true" | Task 6 removes the generator's two walks | **inconsistent**; part A's sentence amended: the lot removes two, Task 6 brings the count to two |
| 20 | B13 ↔ 6 | what an index references | "derived from the provider's listing of `.md` keys" | Task 6 follows spec §11 (B36) | **inconsistent** with the spec and with the fixtures; part A's B13 amended to the spec's rule, the reason given |
| 21 (r3) | 2a → 2b | `Holds(condition, exists, version)`, `EnsureWritable`, `HashForWrite` only under `IfMatch` (B42) | a replacement's version known only under `IfMatch` | 2b's `CommitTemporary` (same checks, inside the attempt loop) and `ClassifyCommitFailure` (`existed` without `before`: no read of the target) | consistent; 2b's (c) "old" block is 2a's `CommitTemporary` exactly |
| 22 (r3) | 2b → 4a, 6 | `BundleWriteNotCommittedException` (B43) | a `BundleProviderException` with a library-authored message | the writer's `Translate` (`Error: {message}`), `IndexGenerator`'s report (the message) and `string` overloads (propagates) | consistent; no new arm needed |
| 23 (r3) | 4a → 4b → 4c | `RecordVerifications`' write loop (B46) | 4a: `committed`, `notificationFailure` and, since r4, `NotifyCommitted` after the `catch` (B48) | 4b wraps the write in `WithRetries`, 4c adds `written` and `failure` — each "old" block is the loop as the previous task leaves it | consistent; every anchor unique in the replay |
| 24 (r3) | 2b → 2c | `Write`'s remarks | 2b's reader paragraph ends `… retries that window a bounded number of times.</para>` | 2c inserts its four paragraphs after that line | consistent (the anchor moved from r2's `replacement fail.</para>`) |
| 25 (r3) | 2b → 2c, 4a, 4b, 5, 6, 8 | `CHANGELOG.md` | 2b's `### Added` bullet after Task 1's; its `### Changed` bullets now end `  A key that does not exist is answered at once.` | Tasks 4a–6 insert after Task 1's bullet and the earlier ones (before 2b's); 2c inserts after 2b's new last line; 4a's F2 bullet follows its own, and 4b–6 insert before it | consistent; the order is the replay's and the spike's |

### Each task on its own

| Task | Tests against code (r3 replay) | Files created, and later touched | Found |
|---|---|---|---|
| 1 | red: build errors only (CS0103, CS0246, CS1061, CS1729, CS1739); then 27/0/27, `Providers` 342/12/354, full 3183/26/3209 | creates `BundleWriteReport.cs` and three test files; rewrites `InMemoryBundleProvider.cs` — none edited later (`BundleProviderExceptions.cs` is edited by 2b, r3) | consistent |
| 2a | red: build errors only (CS0117, CS1061); then 36/2/38, `Providers` 377/14/391, PA-3's test 10/10, Linux `Providers` root 372/19/391 and `tester` `FileSystemBundleProvider` 80/16/96, full 3218/28/3246 | creates `FileSystemBundleProviderWriteTests.cs` (PA-3's test, then F5's, at its end) and `…ContractTests.cs`, never edited later; `FileSystemBundleProvider.cs` edited by 2b, 2c | consistent |
| 2b | red: build errors only (CS0117, CS0246, CS1061); then 8 failed/23 passed (B8's and B45's reasons), after Step 5 31/0/31 five times, `Providers` 408/14/422, Linux root 403/19/422 and `tester` 111/16/127, full 3249/28/3277 | creates `FileSystemBundleProviderCommitTests.cs`; adds `BundleWriteNotCommittedException` to Task 1's `BundleProviderExceptions.cs` | consistent |
| 2c | red: 2 failed, 2 passed (the reasons of Step 2; r2 said "build errors only", wrongly); then 19/0/19, full 3253/28/3281, Linux whole suite root 3218/63/3281 and `tester` 3222/59/3281 | creates `FileSystemBundleProviderWriteScopeTests.cs`; creates README's "Writing through a provider", extended by 4a, 4b, 5, 8 | consistent |
| 3 | red: build errors only (CS0103); then 18/0/18, full 3271/28/3299 | creates `BundleTreeCheck.cs` and its tests, never edited later | consistent |
| 4a | red: build errors only (CS1503, CS1739); then 29/0/29; full 1 failed (the expected string of Deviation 2), after the fix 3300/28/3328; Linux root 500/31/531, `tester` 190/28/218 | creates `BundleConceptWriterProviderTests.cs`, edited by 4b, 5, 6 | consistent |
| 4b | red: build errors only (CS0117, CS1501); then 52/0/52, the two concurrency tests 20/20, full 3323/28/3351; Linux (4a's filter, this revision's own check) root 523/31/554, `tester` 213/28/241 | creates `BundleConceptWriterRetryTests.cs` | consistent |
| 4c | red: build errors only (CS1061); then 87/0/87, full 3333/28/3361; Linux root 585/31/616, `tester` 587/29/616 | creates `VerificationReportTests.cs` | consistent |
| 5 | red: build errors only (CS1061); then 47/0/47; full 1 failed (the expected string of Deviation 2), after the fix 3347/28/3375; Linux root 1032/42/1074, `tester` 1036/38/1074, whole suite root 3311/64/3375 and `tester` 3315/60/3375 | creates `BundleConceptWriterLogTests.cs` | consistent |
| 6 | red: build errors only (CS0117, CS1061, CS1501, CS1503); then 14/0/14, the neighbouring classes 711/16/727, the lenient grep 802, 803, 1494 then 862, 863; full 3365/28/3393; Linux root 690/23/713, `tester` 693/20/713 | creates `IndexGeneratorProviderTests.cs`, never edited later; edits `Bundle.cs` after 4a; `CLAUDE.md`, later edited by 8 | consistent |
| 7 | the CLI test 1/0/1 (fails on `origin/dev`), the callers 349/4/353, producers 826/15/841, full 3366/28/3394 | edits `CliTests.cs` and the producer's `BundleWriter.cs`; creates nothing in the repository (its scripts live in a scratch directory) | consistent |
| 8 | the CHANGELOG audit nineteen times `1`; the write grep lists only `FileSystemBundleProvider.cs` (11 lines); three full runs 3366/28/3394; Linux whole suite root 3330/64/3394, `tester` 3334/60/3394; `tests/fixtures`/`bundles` diff empty; tree identical to `spike/lotb-e` | edits README, `CLAUDE.md`, the spec | consistent |

# Bundle provider, lot A: contract, keys and the filesystem provider (read side) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the `IBundleProvider` seam under `Bundle` and load a bundle through it, with the filesystem as the first — read-only, in this lot — provider, so that every later lot (writer, handles/observations, catalog) builds on a contract that already carries a bundle end to end with the golden suite untouched.

**Architecture:** New types live under `src/OKF4net/Providers/` (keys and their three-layer validation, the contract and its value types, the filesystem provider, the §6.2 resolver on keys). `Bundle.Load(IBundleProvider, BundleLoadOptions?)` becomes the real loader; `Bundle.Load(string)` is a thin overload over `FileSystemBundleProvider`. Every existing public member is **kept** in this lot (`Concept.Path`, `Bundle.Root`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)`, the three-parameter `Concept` constructor and its `Deconstruct`): new members are added beside them (`Concept.Key`, `Bundle.Provider`/`Location`/`Version`/`ParseProblems`, `TryResolveResourceKey`, `ReadResourceText(BundleKey)`), and the historical ones are computed from the new ones by the display rule, so the CLI's bytes do not move. The breaking removals are lot D.

**Tech Stack:** C# 14 / .NET 10, BCL only in `src/OKF4net` (zero `PackageReference`), xunit + `Xunit.SkippableFact` in `tests/OKF4net.Tests`.

**Spec:** `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (revision 6, commit `39ea06c`). Sections implemented here: §1 (contract, read side), §2 (keys, three layers, tree validation, §6.2 resolution, enumeration scope), §3 (the provider-level guard only), §6 (bounds), §7 (no URL dereference; errors translated at the provider boundary), §8 (filesystem provider, display rule, confinement boundary, weak `GetVersion`), §9 rows `Bundle`, `ConceptId`, `BundleValidator`, §10 behaviour changes that this lot introduces, §11 items that this lot can verify.

## Decomposition of the spec into lots

The spec is one design but four sequential deliveries; each later lot assumes the previous one is merged into `dev`. Only lot A is planned here.

| Lot | Scope | Spec sections | Status |
|---|---|---|---|
| **A** (this plan) | `BundleKey` + three-layer validation; contract types; read-only `FileSystemBundleProvider`; `Bundle.Load(provider)`; §6.2 on keys; bounds; validator through the provider; display rule + Windows test | §1, §2, §3 (guard), §6, §7 (partial), §8, §9 (`Bundle`, `ConceptId`, `BundleValidator`), §10/§11 (its part) | planned |
| **B** | Write side: provider `Write`/`Delete` (`CanWrite` flips to true), SHA-256 object versions on write, `File.Replace`/`File.Move` atomic commit **starting from issue #157's recorded pitfalls** (`ReplaceFile` 1176, temp permissions before content, Unix `File.Move` race, durability statement, orphaned temporaries), `BundleConceptWriter`/`IndexGenerator` on the provider, optional expected version, retries, `log.md` through the writer, `BundleWriteReport`, tree check before creation, empty directories (D2) | §3 (rest), §4, §9 (`BundleConceptWriter`, `IndexGenerator`) | next |
| **C** | `BundleHandle`, refresh policies, reload obligation, observations registry and purposes (D1, D5), `okf_run_computation` capture and pinning, executable-resource survey states, D6, `OkfBundleTools`/`OkfContextProvider` on handles | §5, §9 (agents, attestation) | after B |
| **D** | Catalog (snapshot owns handles, provider factories, `MemoryDeleteResult` replaced), Viewer, `okf-mcp` discovery bound, `BundleRoot` obsolete, breaking removals (`Concept.Path`, `Bundle.Root`, `Diagnostic.Path`, `AuditFinding.Path`, …), spec-conformance entry for D6, CHANGELOG consolidation, README/CLAUDE.md (#143) | §9 (catalog, viewer, CLI/MCP), §10 (breaking list) | after C |

Lot A is additive on the public API and changes behaviour only where §10 of the spec already announces it: finite default bounds, `Strict` key policy (tree included), exact-case matching, `\` in a §6.2 path → `Unsafe`, provider I/O errors translated. It does **not** touch the write path: `BundleConceptWriter` and `IndexGenerator` keep their direct `File.*` code until lot B — "read-only provider" never meant "read-only library".

## Revision history

- **r1 (2026-10-12)** — first version, written from spec revision 6, commit `cffa636`.
- **r2 (2026-10-12)** — after an external review that **executed** r1's tasks 1–6 on Windows and left seven tests red plus four exploitable gaps (evidence: the review's `okf-plan-evidence-cffa636` directory). Everything it found is fixed here:
  - **Blocking.** `Enumerate` bypassed the guards `Read` applied: a junction prefix was walkable, and because the walk was lazy, the confinement check ran at call time, not at access time (Task 4: prefix guarded STRICTly, boundary re-checked at every directory listing). The core did not enforce the entry/depth/key bounds itself — `Load` did `.ToList()` and trusted the provider (Task 6: the listing is streamed and bounded in the core; a deliberately disobedient fake provider pins it). Objects the tree validation excluded stayed reachable through `TryResolveResourceKey` and `ComputeOkfVersion` (Task 6: rejected keys are kept and honoured). The `nativeConstraints` constructor parameter could weaken the native floor and switch exact-case matching off (Task 4: it can only add, and exact-case follows the OS).
  - **Important.** A fourth positional `Concept` parameter removed the three-parameter CLR constructor and `Deconstruct` (Task 6: `Key` is a non-positional `init` property). Provider I/O messages reached callers raw, host paths included (Task 6: library-authored messages, raw detail as `InnerException`). The walk yielded files in reverse order; the `"."` resolver row was wrong; a test fixture was 79 bytes against a 64-byte bound; four existing tests needed migration (Tasks 4, 5, 6, 7). Two XML `cref`s did not compile (Tasks 1, 6).
  - **NFC.** The review proved that `string.Normalize` is a silent no-op under `InvariantGlobalization` *and* that r1's fail-closed rule (refuse every non-ASCII segment) broke a real use: an ASCII concept referencing `café.sql`, already NFC, was refused by the AOT `okf`. Maintainer ruling (2026-10-12): **a table-free NFC quick check** — two tiny range tables (`NFC_QC=No`, derived `Maybe`), always used, never `Normalize`, with an exhaustive ICU cross-check test on hosts where normalization works. The residual (two non-composing marks in the wrong canonical order, canonically equivalent to the ordered form) is accepted and documented; closing it would reject common Thai/Lao/Tibetan names and push hosts off `Strict`. `BundleKeyValidator.NormalizationAvailable` is gone. (Task 2.)
  - Layer 1's control-character rule now matches the spec's stated ranges exactly (U+0000–U+001F, U+007F–U+009F) with its own predicate; r1 routed through `LineSafeText.ContainsControlCharacter`, which also counts U+2028/U+2029, and that silently dropped two existing tests' `sub\u2028dir/log.md` fixtures (Task 1).
- **r3 (2026-10-14)** — after a second **executed** review of r2 (`62860c0`): eleven r2 fixes confirmed, two tests still red, and the confinement, tree-exclusion, bound and error-translation fixes shown to be partial. Fixed here:
  - **Blocking.** A deferred enumeration was still bypassable (prefix replaced before consumption; root replaced between two `MoveNext`): `Enumerate` is now eager and every directory is STRICT-guarded before its listing (Task 4, decision 12). `ReadResourceText(BundleKey)` read rejected and policy-refused keys directly, and a subtree whose prefix was condemned could be re-entered by an object created later: one shared guard with rejected prefixes (Task 6, decision 11).
  - **Important.** Depth was checked only on accepted keys and never on late resolutions: depth and key length are checked on the raw names during consumption and again by the shared guard (Task 6). The case fold missed the `AΣ`/`aσ` NTFS alias (Task 2, decision 13). The NFC generator died on U+FFFE before printing anything (Task 2, decision 14). Typed provider exceptions were relayed by `e.Message`, and the read-only message carried the host path: messages are now built from `(category, key, bound)` only (Tasks 3, 6). `ComputeOkfVersion` swallowed a global bound failure (Task 6). The tree-exclusion test expected four diagnostics where the enumeration scope yields two (Task 6).
  - **Minor.** A literal U+2028 in a test row (the write tool had decoded the escape; it is the six-character escape text again); one more `cref` to qualify; decision 7 wrongly listed U+000C as kept; `audit --json` added to the display test (Task 7); exact-case matching can be requested by a folding flag on Linux (decision 15).
- **r4 (2026-10-14)** — after a third **executed** review of r3 (`6194958`): tasks 1–6 green, 11 goldens green, every earlier scenario closed; one blocker and six seams left. Fixed here:
  - **Blocking.** Rejected prefixes were compared ordinally although their rejection came from a case equivalence, so a third spelling (`AB` after `Ab`/`aB`) re-entered a condemned subtree: prefixes are now remembered and compared in their **folded** form (Task 6, decision 11).
  - **Important.** The walk parsed a directory's relative name into a key just to label a guard, so a directory with an invalid name (U+0085, > 1 024 bytes) aborted the whole load instead of being skipped with a diagnostic (Task 4). Duplicates were dropped before conflict detection, so `x`, `x`, `x/y.sql` hid the object-is-a-prefix conflict (Task 2). The depth bound sat inside the `TryParse` success branch, so a name violating the base rules bypassed it (Task 6). `BundleLimitExceededException.Bound` was a free string copied into the public message: it is a closed enum now (Tasks 4, 6, decision 16). The `ToUpperInvariant` fold merges `σ`/`ς`, which NTFS keeps distinct — maintainer ruling (a): kept as a documented conservative over-approximation under `Strict` **and** `Native` (decision 13). The `audit --json` display assertion was red by construction (no stale concept in the fixture): it filters with `--type Table` (Task 7). The table generator cannot compile before the validator exists: Task 2's steps are reordered.
  - **Editorial.** Decision 9 contradicted decision 15 on exact-case flags; Task 8 still spoke of an optional fourth `Concept` parameter.
- **r5 (2026-10-14)** — after a fourth **executed** review of r4 (`64d4fa6`): the plan now runs with no manual correction, three identical full runs at 2 629 green, every earlier scenario closed; one blocker and three seams left. Fixed here:
  - **Blocking.** `RejectedPrefixes` was filled only while dooming surviving candidates, so when every offender was a duplicate (`Ab/x.sql` ×2, `aB/y.sql` ×2; `x` ×2, `x/y.sql` ×2) the collision was detected but no prefix recorded, and a late `AB/new.sql` or `x/new.sql` resolved: prefixes are now taken directly from the structural collisions and conflicts (Task 2), with loader tests through a provider that lists a name twice (Task 6).
  - **Important.** `ResolveExact` enumerated each traversed directory before the STRICT guard ran, so under a junction an absent name could be told from a present one (`NotFound` vs refused): every traversed directory is now guarded before it is listed, and `Exists` answers `false` on a refusal (Task 4). The duplicate fix used a `List.Contains` per duplicate — 3.3 s for 64 000 names in the review's measurement: a `HashSet` now (Task 2).
  - **Minor.** A junction fixture passed `"src/p"` to `mklink`, which the helper does not normalise, so the deferred-replacement test skipped for the wrong reason: `Path.Combine` (Task 4).
- **r6 (2026-10-14)** — after a fifth **executed** review of r5 (`93f3bac`): verdict "ready after two targeted corrections", every r4 reproduction closed, one blocker and one fixture failure left. Fixed here:
  - **Blocking.** The file/directory conflict check still excluded case-doomed keys (`keys = structural.Where(k => !doomed.Contains(k))`), so for `x.md`, `x.md/A.sql`, `x.md/a.sql` the children were rejected but `x.md` — an object that is also a prefix — was loaded, and a late `x.md/new.sql` resolved: conflicts are computed on **all** structural keys (Task 2), with the mixed case added to the validator and loader tests (Tasks 2, 6).
  - **Important.** The deferred-replacement test now ran, and failed in its own cleanup: `Directory.Delete(root, recursive: true)` refuses to traverse the junction it had planted; the junction is deleted first, non-recursively (Task 4).
- **r7 (2026-10-14)** — after a sixth **executed** review of r6 (`a092d16`): both r6 corrections confirmed (91 targeted tests green, an independent oracle agreeing on 26 244 duplicate/collision/conflict combinations), one blocker left. `structural` was built from the admitted candidates, so a key refused by `CheckKey` — a host validator refusing `x.md`, or `Strict` refusing `x.md/con.sql` — lost its physical presence and its child or parent came back in. Every name that parses now enters `structural` **before** `CheckKey` and before duplicate handling; admission decides what is accepted and diagnosed, never what exists (Task 2, with validator and loader regressions in Tasks 2 and 6).
- **r8 (2026-10-14)** — after a seventh **executed** review of r7 (`0089f95`): the r7 fix confirmed (80 targeted tests green, an oracle agreeing on 1 259 712 ASCII combinations), one blocker and one performance seam left. Fixed here:
  - **Blocking.** Sibling names were grouped by the case fold alone, so canonically equivalent names — `café` beside `cafe` + U+0301, `Å` beside U+212B — were never recognised as one object: under `Strict` the NFD name was refused but its twin was accepted with no prefix condemned, and under `UnicodeFormCollisions` alone both were accepted. Maintainer ruling (2026-10-14, option b: tables derived from ICU rather than a closed per-level rule): `CanonicalForm`, a table-driven canonical decomposition **with** canonical ordering (the NFD; Hangul by arithmetic), derived and guarded by the same generator test as the quick-check sets; `FoldSegment` decomposes under `UnicodeFormCollisions` and upcases under `CaseInsensitiveCollisions`, and both the grouping and `FoldKeyText` use it (Task 2, decision 17; loader regressions in Task 6). The plan had announced a composition-only fold; it was replaced before writing because adjacent-pair composition misses the reordering case (`é` + cedilla against `ȩ` + acute), which needs the combining classes anyway — and with them the ordering half of the quick check closes for free, so the "reorder residual" of decision 2 is gone.
  - **Important.** Each candidate scanned the whole list of colliding and conflicting keys (`FirstOrDefault` with `StartsWith`): 26 s for 64 000 names in colliding sibling pairs, 13 s in file/prefix conflicts. Both lists are sets now and each candidate looks up its own prefixes, outermost first — one lookup per segment (Task 2, with a 65 536-name regression whose duration Task 8 records; no timing assertion).
- **r9 (2026-10-14)** — after an eighth **executed** review of r8 (`fdb70b2`): verdict "ready after one minor correction", nothing blocking or important reproduced — the Unicode collisions closed on 36 extra checks, `Decompose` agreeing with the host's NFD on every scalar plus 120 000 sequences, 922 non-zero-class code points all Mn/Mc, the oracle at 1 259 712 comparisons, three full runs at 2 994 green, four bundles byte-identical, the tables costing 34 816 bytes in `okf`. The one correction: r8's performance regression put 65 536 names under **two** colliding roots, so r7's scan had two keys to check per candidate and took 0.4 s — the 26 s the comment attributed to that shape came from the review's *distributed* collisions. The fixture now grows its condemned prefixes with the listing (16 384 colliding pairs and 16 384 conflicts), the comments cite the shapes the figures came from, and Task 8's filter follows the new name (Task 2, Task 8). Lesson recorded: a figure quoted in a test comment must come from the input that test builds.

## Global Constraints

- **Branch and worktree.** Execute in a dedicated worktree on branch `feat/bundle-provider-a`, created from `origin/dev` **after** the spec + plan docs PR (`docs/bundle-provider-spec`) is merged into `dev`, or rebased on it. Never work in `E:\Sources\okf` (shared checkout; `dev` is checked out there). To start from `dev`: `git worktree add .claude/worktrees/bundle-provider-a -b feat/bundle-provider-a origin/dev`. Never `git checkout dev`.
- **Zero third-party runtime dependencies in `src/OKF4net`** — BCL only. No P/Invoke. No new `PackageReference` anywhere in `src/`. `InvariantGlobalization` stays on in `OKF4net.Cli` and `OKF4net.Render` (turning it off would make the Native AOT `okf` require libicu at run time on Linux).
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on every public member (the build treats missing docs as errors), nullable on, `TreatWarningsAsErrors`, LangVersion 14. **An XML `cref` may only name a type that exists at that task** (r1 broke the build twice this way); use `<c>…</c>` for a type a later task adds.
- **Source files are CRLF in the working tree.** `Write` produces LF; after every `Write`/`Edit` of a `.cs` file, normalise with `unix2dos` or `sed -i 's/$/\r/'` on a **fresh LF file only** — never on a file that already has CR. Before each commit: `tr -cd '\r' < f | wc -c` equals `wc -l < f` for every touched `.cs` file. Markdown files are LF.
- **`tests/fixtures/` is never edited and no snapshot is regenerated.** If a golden comparison turns red, STOP and report: lot A promises byte-identical CLI output.
- **Path-safety guards use the STRICT predicates** (`ReparsePoints.IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor`); enumeration walks use the lenient pair, and this lot adds exactly **one** lenient site (the provider's walk), which replaces `Bundle.CollectMarkdown` — update the count in `CLAUDE.md` in Task 8. A guard runs **at the moment of the access**, never only when an enumerable is created.
- **Keys are never percent-decoded** and a URL in a §6.2 field is **never** dereferenced.
- **Bounds, policy and constraints are enforced in the core** (`Bundle.Load`) while the listing is consumed — never after a `.ToList()` — and the core reads provider streams with byte counting; the provider enforces the same bounds on its own walks (`GetVersion`, `Enumerate`), counting every entry it inspects, skipped reparse points included, before sorting.
- **NFC is checked by the quick-check tables and canonical equivalence is decided by the decomposition and combining-class tables, never by `string.Normalize`**, so the library and the AOT binaries give one answer. All four tables are generated from ICU on the test host (Task 2 ships the generator, `UnicodeTableTests`) and guarded by an exhaustive cross-check test that runs wherever normalization works.
- **Provider errors are translated at the core boundary** (§7): a `BundleLoadException` or `IOException` leaving `Bundle` carries a library-authored message naming the key and the exception type; the raw exception is the `InnerException`, which is the host-log channel.
- `Bundle.Load` stays permissive (§11): a per-resource or per-concept problem goes to `ParseProblems`; only the global failures of §6 (entry bound, depth, provider I/O error, access refused) throw `BundleLoadException`.
- Each task adds its own `CHANGELOG.md` entry under `## [Unreleased]` (`### Added` for new API, `### Changed` for behaviour changes; nothing in this lot is **Breaking**), describing behaviour, not commits.
- Commit messages say only what the code actually does. End each with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Never `git add -A`. Do not push.
- Run tests with `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`; `ContainerIntegration` tests need Docker and are out of CI by decision. Run the `Providers` tests at least once as an account that can create symlinks and junctions (Developer Mode on Windows) and confirm **0 skipped**: the review's junction probes found what the skipped symlink tests could not.

## Decisions taken by this plan (not in the spec — for the maintainer to confirm)

1. **Read-time version mismatch has its own exception.** `BundleVersionMismatchException` for a `Read(key, expected)` whose version moved; `BundleWriteConflictException` stays for writes (lot B). Both derive from `BundleProviderException : OkfException`.
2. **NFC by quick-check tables** (maintainer ruling 2026-10-12, see Revision history): `NfcQuickCheck.No` and `NfcQuickCheck.Maybe` as `(int Start, int End)` ranges; a segment containing a `No` character is refused ("never in NFC"), one containing a `Maybe` character is refused ("cannot be decided without composition tables") — the derived `Maybe` set is the set of marks that actually recompose with some base, tighter than UCD's — and, since r8, one whose marks are out of canonical order is refused too (`CanonicalForm.IsCanonicallyOrdered`, decision 17): the reorder residual r2 accepted is closed. The one over-rejection left: a standalone composing mark on a base it does not compose with (`q` + U+0323); rare in names, documented.
3. **The filesystem provider is read-only in this lot** (`CanWrite = false`): a declared capability per §3, not a placeholder; lot B flips it with #157's commit protocol.
4. **`Concept.Path` is computed by the display rule** (`BundleDisplayPath.Compose(Location, key)` = `Path.Combine(root, …)`), byte-identical to today for the filesystem; Task 7 pins it unnormalised on Windows.
5. **`Concept.Key` is a non-positional `init` property**, so the three-parameter constructor and `Deconstruct` are unchanged (r1's optional fourth positional parameter removed both at the CLR level).
6. **`ParseErrors` stays `IReadOnlyList<(string Path, string Error)>`**, projected from `ParseProblems`; a rejected raw name projects as `Location` + separator + `DebugQuote.Quote(raw)`.
7. **Layer-1 control characters** are exactly U+0000–U+001F and U+007F–U+009F, per UTF-16 code unit, with the validator's own predicate; U+2028/U+2029 are *not* rejected (they are not in the spec's ranges, and existing tests legitimately put U+2028 in a directory name), while U+000C is, being inside the first range.
8. **`BundleLimits.MaxDepth` counts key segments** (`a/b/c.md` has depth 3).
9. **`additionalConstraints`, not `nativeConstraints`**: the filesystem provider's constructor can only add flags to the OS-derived set, never replace it; exact-case matching is on by the OS (Windows, macOS) or by a folding flag the host adds (decision 15), and nothing can switch it off.
10. **`"."` and `"./"` resolve to the concept's own prefix** (`Candidate`, then `Missing` since no object has that key) rather than `Unsafe`: lexically they name a prefix, and nothing about them escapes the bundle. `"../"` from a top-level concept, `"/"` and `""` resolve to the root and stay `Unsafe`.
11. **Tree-rejected keys and prefixes are remembered by the bundle** (`RejectedKeys`, `RejectedPrefixes`) and one shared guard — policy, rejected keys, rejected prefixes, depth and key-length bounds — fronts both `TryResolveResourceKey` and `ReadResourceText(BundleKey)`, so a rejected object cannot be read directly and an object created later under a rejected prefix cannot be rehabilitated. A rejected prefix is matched **in its folded form** (each segment through `FoldSegment`: canonical decomposition under `UnicodeFormCollisions`, the upper-case fold under `CaseInsensitiveCollisions`, identity under neither): a prefix condemned for a collision condemns every spelling of it, not just the ones that were listed (r3 compared ordinally and let `AB` in after `Ab`/`aB`; r7 folded case only and would have let `café` in after `cafe` + U+0301). A key under no rejected prefix that appeared after load is checked live, as `TryResolveResource` does today.
12. **`Enumerate` is eager**: the whole walk runs inside the call, under the guards, and returns a materialised list (still typed `IEnumerable<BundleListing>`). A lazily consumed enumeration cannot be made safe against a root or prefix replaced between two `MoveNext` calls (r2's per-listing re-check was shown to be bypassable); with no deferred access there is nothing to defend. A replacement racing the walk itself is the accepted inter-process window (#178).
13. **Case folding for collisions is `Rune.ToUpperInvariant` per code point**, context-free — not `UnicodeCaseFold.ToLowercase`, whose final-sigma rule turns `AΣ` into `aς` and misses the real `AΣ`/`aσ` alias the review measured on Windows. It is a **conservative over-approximation** of NTFS's `$UpCase` table, not an equivalence: it also merges pairs NTFS keeps distinct (`σ.sql` and `ς.sql` are two files on an NTFS volume, measured by the third review). Maintainer ruling (2026-10-14, option a): the same fold applies under `Strict` **and** `Native`, so `Native` on Windows can over-reject such siblings — a guard fails closed, `$UpCase` cannot be shipped, and the pairs concerned are confusable to a reader anyway. The over-rejection is documented in the CHANGELOG and the validator's doc comment. `ToUpperInvariant` performs full simple case mapping under invariant globalization since .NET 8.
14. **A code point the host normalizer refuses** (`ArgumentException` from `Normalize`, e.g. the noncharacter U+FFFE) is classified `No` by the table generator: fail closed, never silently `Yes`.
15. **Exact-case matching is enabled by the OS *or* by a folding flag in `additionalConstraints`**, so a Linux host on a case-insensitive mount can ask for it; nothing can switch it off.
16. **A bound is a closed category** (`enum BundleBound { Entries, Depth, KeyBytes }`), never a free string: the public message of a `BundleLimitExceededException` is built from the enum, so a provider cannot inject text into it (r3 copied a string field into the message).
17. **Canonical equivalence by table-driven NFD** (maintainer ruling 2026-10-14, option b): `CanonicalForm` ships every canonical decomposition (≈ 2 060 records; Hangul syllables by arithmetic) and the canonical combining classes as ranks, both derived from ICU by `UnicodeTableTests` and guarded by it — `Decompose` is compared with the host's NFD on every scalar and on random mark sequences. Under `UnicodeFormCollisions`, sibling names are grouped, and rejected prefixes matched, in canonical decomposition, so `café` and `cafe` + U+0301, `Å` and U+212B, a Hangul syllable and its conjoining jamo, `ȩ` + acute and `é` + cedilla are one name, as on APFS; this holds for every structural name, including one `RequireNfc` refuses. Under a native Windows set (no `UnicodeFormCollisions`) the two forms stay two files, as on NTFS. The same classes let the NFC check verify canonical ordering, closing decision 2's residual. Cost: ≈ 40 KB of tables in the AOT binaries, zero dependencies, no `Normalize`. The alternative — treating a quick-check-undecidable name as condemning its whole sibling level — was closed but punished every neighbour of one decomposed file.

## Review Focus

Inputs the spec implies but no task's tests exercise directly, each pinned to the owning task:

1. **A `.md` whose stem is a reserved name only once lower-cased** (`Con.md`, `lPt¹.md`): Windows reserves them case-insensitively — Task 2 tests `Con.md` and `lpt¹.md`.
2. **A directory named `index.md` or `log.md`**: through keys it is a prefix, and `tables/index.md/x.md` is a concept `tables/index.md/x`, as today — Task 6 pins it.
3. **A key exactly at 1 024 UTF-8 bytes vs 1 025** — Task 1 tests both edges; Task 6 tests a lower `limits.MaxKeyBytes` enforced by the core.
4. **A root that is itself a symlink**: loads without a boundary, refused with one — Task 4.
5. **A provider that enumerates the same raw name twice**, or that ignores every bound — Task 2 (duplicates) and Task 6 (the disobedient provider).

---
### Task 1: `BundleKey` — layer-1 (base) validation

**Files:**
- Create: `src/OKF4net/Providers/BundleKey.cs`
- Test: `tests/OKF4net.Tests/Providers/BundleKeyTests.cs`

**Interfaces:**
- Consumes: `OKF4net.Internal.MarkdownPaths.HasMarkdownExtension(string)`. (Not `LineSafeText.ContainsControlCharacter`: it also counts U+2028/U+2029, which the spec's layer 1 does not reject — decision 7.)
- Produces: `public sealed class BundleKey : IEquatable<BundleKey>, IComparable<BundleKey>` with `IReadOnlyList<string> Segments`, `string Name`, `BundleKey? Parent`, `bool IsMarkdown`, `string ToString()` (segments joined by `/`), `static bool TryParse(string raw, out BundleKey? key, out string? reason)`, `static BundleKey Parse(string raw)` (throws `BundleKeyException`), `BundleKey Append(string segment)`, `bool StartsWith(BundleKey prefix)` (segment-bounded), `static BundleKey FromSegments(IReadOnlyList<string>)`, `const int MaxUtf8Bytes = 1024`, `int Utf8ByteCount`. Also `public sealed class BundleKeyException : OkfException`. Ordinal, case-sensitive equality and ordering.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyTests
{
    [Theory]
    [InlineData("a.md", new[] { "a.md" })]
    [InlineData("tables/orders.md", new[] { "tables", "orders.md" })]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("%2e%2e/x.md", new[] { "%2e%2e", "x.md" })]           // never percent-decoded
    [InlineData("tables/index.md/x.md", new[] { "tables", "index.md", "x.md" })]
    [InlineData("sub\u2028dir/log.md", new[] { "sub\u2028dir", "log.md" })] // U+2028 is a separator, not a control: layer 1 keeps it (decision 7)
    [InlineData("café.md", new[] { "café.md" })]
    public void Parses_valid_keys_into_segments(string raw, string[] segments)
    {
        Assert.True(BundleKey.TryParse(raw, out var key, out var reason), reason);
        Assert.Equal(segments, key!.Segments);
        Assert.Equal(raw, key.ToString());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/a.md", "leading")]
    [InlineData("a.md/", "trailing")]
    [InlineData("a//b.md", "empty segment")]
    [InlineData("./a.md", "'.' segment")]
    [InlineData("a/../b.md", "'..' segment")]
    [InlineData("a\\b.md", "backslash")]
    [InlineData("a\0.md", "control")]
    [InlineData("a\u001f.md", "control")]
    [InlineData("a\u007f.md", "control")]
    [InlineData("a\u0085.md", "control")]
    [InlineData("a\u009f.md", "control")]
    public void Rejects_base_rule_violations(string raw, string reasonFragment)
    {
        Assert.False(BundleKey.TryParse(raw, out var key, out var reason));
        Assert.Null(key);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Length_bound_is_1024_utf8_bytes_inclusive()
    {
        var ascii1024 = new string('a', 1021) + ".md"; // 1024 bytes
        Assert.True(BundleKey.TryParse(ascii1024, out var key, out _));
        Assert.Equal(1024, key!.Utf8ByteCount);
        var ascii1025 = new string('a', 1022) + ".md";
        Assert.False(BundleKey.TryParse(ascii1025, out _, out var reason));
        Assert.Contains("1024", reason);
        // 'é' is 2 bytes in UTF-8: 512 of them = 1024 bytes, 513 = 1026.
        Assert.True(BundleKey.TryParse(new string('é', 512), out _, out _));
        Assert.False(BundleKey.TryParse(new string('é', 513), out _, out _));
    }

    [Fact]
    public void Equality_and_ordering_are_ordinal_and_case_sensitive()
    {
        var a = BundleKey.Parse("A/x.md");
        var b = BundleKey.Parse("a/x.md");
        Assert.NotEqual(a, b);
        Assert.True(a.CompareTo(b) < 0); // 'A' (0x41) < 'a' (0x61)
        Assert.Equal(BundleKey.Parse("a/x.md"), b);
        Assert.Equal(BundleKey.Parse("a/x.md").GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Name_parent_markdown_and_prefix_are_segment_bounded()
    {
        var key = BundleKey.Parse("tables/orders.md");
        Assert.Equal("orders.md", key.Name);
        Assert.Equal(BundleKey.Parse("tables"), key.Parent);
        Assert.Null(BundleKey.Parse("a.md").Parent);
        Assert.True(key.IsMarkdown);
        Assert.False(BundleKey.Parse("tables/orders.MD").IsMarkdown); // ordinal, like MarkdownPaths
        Assert.True(key.StartsWith(BundleKey.Parse("tables")));
        Assert.False(BundleKey.Parse("tablesx/orders.md").StartsWith(BundleKey.Parse("tables")));
        Assert.Equal(BundleKey.Parse("tables/orders.md/extra"), key.Append("extra"));
        Assert.Throws<BundleKeyException>(() => key.Append("a/b"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyTests"`
Expected: build error — `OKF4net.Providers.BundleKey` does not exist.

- [ ] **Step 3: Implement `BundleKey`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>Thrown when a string is not a valid <see cref="BundleKey"/> (base rules, spec §2 layer 1).</summary>
public sealed class BundleKeyException(string message) : OkfException(message)
{
}

/// <summary>
/// A validated, provider-independent object key inside a bundle: <c>/</c>-separated
/// segments, compared ordinally and case-sensitively, never percent-decoded.
/// This type carries only the base rules (spec §2, layer 1); the storage-specific
/// constraints and the portability policy live in <c>BundleKeyValidator</c>.
/// </summary>
public sealed class BundleKey : IEquatable<BundleKey>, IComparable<BundleKey>
{
    /// <summary>Upper bound on a key's UTF-8 length, inclusive (spec §2, §6).</summary>
    public const int MaxUtf8Bytes = 1024;

    private readonly string _text;

    private BundleKey(List<string> segments, int utf8ByteCount)
    {
        Segments = new ReadOnlyCollection<string>(segments);
        _text = string.Join('/', segments);
        Utf8ByteCount = utf8ByteCount;
    }

    /// <summary>The key's segments, in order; never empty.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The key's length in UTF-8 bytes (the unit of <see cref="MaxUtf8Bytes"/> and of a host's <c>BundleLimits.MaxKeyBytes</c>).</summary>
    public int Utf8ByteCount { get; }

    /// <summary>The last segment.</summary>
    public string Name => Segments[^1];

    /// <summary>The key without its last segment, or <see langword="null"/> for a top-level key.</summary>
    public BundleKey? Parent => Segments.Count <= 1 ? null : Parse(string.Join('/', Segments.Take(Segments.Count - 1)));

    /// <summary><see langword="true"/> when the last segment ends with <c>.md</c> (ordinal, like <c>MarkdownPaths</c>).</summary>
    public bool IsMarkdown => MarkdownPaths.HasMarkdownExtension(Name);

    /// <summary>Builds a key from segments; the joined text is checked by the base rules.</summary>
    public static BundleKey FromSegments(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Parse(string.Join('/', segments));
    }

    /// <summary>Parses a raw name, throwing <see cref="BundleKeyException"/> when it breaks a base rule.</summary>
    public static BundleKey Parse(string raw) =>
        TryParse(raw, out var key, out var reason) ? key : throw new BundleKeyException(reason);

    /// <summary>
    /// Parses a raw name against the base rules: non-empty, no leading or trailing
    /// <c>/</c>, no empty, <c>.</c> or <c>..</c> segment, no backslash, no control
    /// character (U+0000–U+001F, U+007F–U+009F — exactly those; U+2028 and friends are
    /// separators and stay), at most <see cref="MaxUtf8Bytes"/> bytes in UTF-8. The text is
    /// taken literally: <c>%2e%2e</c> is a name, not <c>..</c>.
    /// </summary>
    public static bool TryParse(string raw, [NotNullWhen(true)] out BundleKey? key, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(raw);
        key = null;

        if (raw.Length == 0)
        {
            reason = "key is empty";
            return false;
        }

        if (raw[0] == '/')
        {
            reason = "key has a leading '/'";
            return false;
        }

        if (raw[^1] == '/')
        {
            reason = "key has a trailing '/'";
            return false;
        }

        if (raw.Contains('\\'))
        {
            reason = "key contains a backslash";
            return false;
        }

        foreach (var c in raw)
        {
            if (c <= '\u001f' || (c >= '\u007f' && c <= '\u009f'))
            {
                reason = $"key contains the control character U+{(int)c:X4}";
                return false;
            }
        }

        var bytes = Encoding.UTF8.GetByteCount(raw);
        if (bytes > MaxUtf8Bytes)
        {
            reason = $"key is longer than {MaxUtf8Bytes} UTF-8 bytes";
            return false;
        }

        var segments = raw.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                reason = "key has an empty segment";
                return false;
            }

            if (segment == ".")
            {
                reason = "key has a '.' segment";
                return false;
            }

            if (segment == "..")
            {
                reason = "key has a '..' segment";
                return false;
            }
        }

        key = new BundleKey(segments.ToList(), bytes);
        reason = null;
        return true;
    }

    /// <summary>Returns this key with one more segment; the segment must pass the base rules and contain no <c>/</c>.</summary>
    public BundleKey Append(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Contains('/'))
        {
            throw new BundleKeyException("a segment must not contain '/'");
        }

        return Parse(_text + "/" + segment);
    }

    /// <summary><see langword="true"/> when this key equals <paramref name="prefix"/> or is under it at a segment boundary (<c>ab/x</c> is not under <c>a</c>).</summary>
    public bool StartsWith(BundleKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Segments.Count > Segments.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], prefix.Segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Equals(BundleKey? other) => other is not null && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as BundleKey);

    /// <inheritdoc/>
    public override int GetHashCode() => string.GetHashCode(_text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public int CompareTo(BundleKey? other) => other is null ? 1 : string.CompareOrdinal(_text, other._text);

    /// <inheritdoc/>
    public override string ToString() => _text;
}
```

Create `src/OKF4net/Providers/` and `tests/OKF4net.Tests/Providers/`; convert both new files to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyTests"`
Expected: 5 test methods, 22 cases, all green; the build has no warning (the only `cref`s name `BundleKey` and `BundleKeyException`).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/BundleKey.cs tests/OKF4net.Tests/Providers/BundleKeyTests.cs
git commit -F - <<'MSG'
feat(providers): BundleKey with the base (layer-1) key rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 2: `BundleKeyConstraints`, `BundleKeyPolicy`, the NFC quick check and `BundleKeyValidator` — layers 2 and 3, tree validation

**Files:**
- Create: `src/OKF4net/Providers/BundleKeyConstraints.cs`
- Create: `src/OKF4net/Providers/NfcQuickCheck.cs`
- Create: `src/OKF4net/Providers/CanonicalForm.cs`
- Create: `src/OKF4net/Providers/BundleKeyValidator.cs`
- Test: `tests/OKF4net.Tests/Providers/UnicodeTableTests.cs` (the generator **and** the guard of the four tables)
- Test: `tests/OKF4net.Tests/Providers/BundleKeyValidatorTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1); `System.Text.Rune` (`ToUpperInvariant`, `EnumerateRunes`, `GetUnicodeCategory` — the last only in the generator test). Not `UnicodeCaseFold`: its final-sigma rule is linguistic, not a storage alias rule (decision 13).
- Produces:
  - `[Flags] public enum BundleKeyConstraints` with `None`, `TrailingDotOrSpace`, `WindowsIllegalCharacters`, `WindowsReservedNames`, `CaseInsensitiveCollisions`, `RequireNfc`, `UnicodeFormCollisions`, `FileDirectoryConflict`; `public static class BundleKeyConstraintSets` with `Windows`, `MacOS`, `Linux`, `Strict`, `ForCurrentOperatingSystem()`.
  - `public enum BundleKeyPolicy { Strict, Native }`.
  - `public enum NfcStatus { Yes, No, Maybe }`; `internal static class NfcQuickCheck` with `(int Start, int End)[] No`, `(int Start, int End)[] Maybe`, `NfcStatus Of(Rune)`. Never calls `string.Normalize`.
  - `internal static class CanonicalForm` with `int[] DecompositionData` (flat records `code, count, code points…`, every non-Hangul canonical decomposition, fully decomposed), `int[] CombiningClassData` (flat triples `start, end, rank`), `string Decompose(string)` (canonical decomposition **and** canonical ordering: the NFD of its input, Hangul by arithmetic), `int ClassOf(Rune)` (the combining-class rank, 0 for a starter) and `bool IsCanonicallyOrdered(string)`. Never calls `string.Normalize` (decision 17).
  - `public sealed record BundleKeyRejection(string RawName, string Reason)`; `public sealed record BundleKeyTreeResult(IReadOnlyList<BundleKey> Accepted, IReadOnlyList<BundleKeyRejection> Rejected, IReadOnlyList<BundleKey> RejectedPrefixes)` — `RejectedPrefixes` are the colliding or conflicting keys themselves (`A`, `a`, `a.md`), under which nothing may ever be admitted (decision 11).
  - `public sealed class BundleKeyValidator` with ctor `(BundleKeyConstraints native, BundleKeyPolicy policy, IReadOnlyList<Func<BundleKey, string?>>? extraValidators = null)`, `BundleKeyConstraints Effective`, `string? CheckKey(BundleKey)`, `BundleKeyTreeResult ValidateTree(IEnumerable<string> rawNames)`, `static string FoldForCollision(string segment)` (per-code-point `Rune.ToUpperInvariant`), `string FoldSegment(string segment)` (`CanonicalForm.Decompose` under `UnicodeFormCollisions`, then `FoldForCollision` under `CaseInsensitiveCollisions`, identity under neither) and `string FoldKeyText(BundleKey key)` (each segment through `FoldSegment` — the form in which sibling names are grouped and in which `Bundle` remembers rejected prefixes, decision 11).

**Why a quick check and not `Normalize` (decision 2):** under `InvariantGlobalization` (`okf`, `okf-render`) `string.Normalize(FormC)` returns its input unchanged and `IsNormalized` answers `true` — measured on .NET 10. UAX #15's quick check needs two sets: characters never in NFC (`NFC_QC=No`) and marks that can compose with a preceding base. A segment with neither, whose marks are in canonical order (`CanonicalForm.IsCanonicallyOrdered`, decision 17), is NFC. The sets are derived from ICU on the test host by the test in Step 1, which writes the C# literals to paste on first run and then guards the pasted tables forever. A code point the host normalizer refuses (U+FFFE and the other noncharacters raise `ArgumentException`) is classified `No` — fail closed (decision 14).

**Why canonical decomposition tables for collisions (decision 17):** `UnicodeFormCollisions` asks whether two sibling names are *canonically equivalent* — one file on APFS. r7 answered it with the case fold alone, on the argument that no composing sequence survives `RequireNfc`; but since r7 every parsed name is structurally present, refused or not, so the NFD spelling `cafe` + U+0301 that `RequireNfc` refuses still sits beside its NFC twin `café`, and under `UnicodeFormCollisions` alone (no `RequireNfc`) both are admissible on their own — the seventh review accepted both. Canonical equivalence is decided by comparing canonical decompositions, and the exact decomposition needs two more tables: every canonical decomposition (≈ 2 060 entries; Hangul syllables are arithmetic) and the canonical combining classes, so that `é` + cedilla and `ȩ` + acute — equivalent only after the marks are reordered — decompose to one form. Composition-only approaches (adjacent pairs) miss exactly that reordering case, so the fold is decomposition + ordering, which is NFD, and leaves **no** residual. The combining classes are derived as *ranks*, not UCD numbers: NFD reorders two adjacent marks exactly when the second has the lower class, so the test orders every mark against representatives with `Normalize` alone and the shipped table only needs the order. All four tables ship as source, are derived by the one test in Step 1 and are guarded by it on every host whose ICU works.

**The test rows below are written with `\uXXXX` escape text on purpose** — an NFD string pasted as literal characters is indistinguishable from its NFC form on screen. Keep them as C# `\u` escapes when you copy them.

- [ ] **Step 1: Write the table test, which is also the generator**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// Derives four Unicode tables from the host's ICU and compares them with the ones
/// shipped in <see cref="NfcQuickCheck"/> (the NFC quick-check sets) and
/// <see cref="CanonicalForm"/> (canonical decompositions and combining-class ranks).
/// Runs only where normalization works (never under invariant globalization). On a
/// mismatch — first run with empty tables, or a host ICU with a newer Unicode than
/// the tables — the failure names a file holding the exact C# literals to paste.
/// </summary>
public class UnicodeTableTests
{
    private static bool NormalizationWorks => "é".Normalize(NormalizationForm.FormC).Length == 1;

    private static string Nfc(string s) => s.Normalize(NormalizationForm.FormC);

    private static string Nfd(string s) => s.Normalize(NormalizationForm.FormD);

    private static bool IsHangulSyllable(int cp) => cp is >= 0xAC00 and <= 0xD7A3;

    /// <summary>
    /// No = { c : NFC(c) != c } ∪ { c : the normalizer refuses c } (decision 14).
    /// Maybe = the last character of every direct canonical pair that recomposes,
    /// walked from each primary composite's full decomposition (head = NFC(all but
    /// last); while the head is one character, the last is a composing second
    /// element; then recurse on the head).
    /// Decompositions = { c → NFD(c) : NFD(c) != c, c not a Hangul syllable } (Hangul is
    /// arithmetic, <see cref="Hangul_decomposition_is_arithmetic"/> pins that).
    /// </summary>
    internal static (SortedSet<int> No, SortedSet<int> Maybe, SortedDictionary<int, string> Decompositions, int Refused) Derive()
    {
        var no = new SortedSet<int>();
        var maybe = new SortedSet<int>();
        var decompositions = new SortedDictionary<int, string>();
        var refused = 0;
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var s = new Rune(cp).ToString();
            string nfc;
            string nfd;
            try
            {
                nfc = Nfc(s);
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                // The host normalizer refuses this scalar (noncharacters such as U+FFFE): fail closed.
                no.Add(cp);
                refused++;
                continue;
            }

            if (!string.Equals(nfc, s, StringComparison.Ordinal))
            {
                no.Add(cp);
            }

            if (!string.Equals(nfd, s, StringComparison.Ordinal) && !IsHangulSyllable(cp))
            {
                decompositions[cp] = nfd;
            }

            var seq = nfd.EnumerateRunes().ToList();
            if (seq.Count < 2 || !string.Equals(Nfc(string.Concat(seq)), s, StringComparison.Ordinal))
            {
                continue;
            }

            while (seq.Count >= 2)
            {
                var head = Nfc(string.Concat(seq.Take(seq.Count - 1))).EnumerateRunes().ToList();
                if (head.Count != 1)
                {
                    break;
                }

                maybe.Add(seq[^1].Value);
                seq = Nfd(head[0].ToString()).EnumerateRunes().ToList();
            }
        }

        return (no, maybe, decompositions, refused);
    }

    /// <summary>
    /// The canonical combining classes, as RANKS (1 = the lowest non-zero class), derived
    /// without the UCD numbers: NFD reorders two adjacent non-starters exactly when the
    /// second has the lower class, so comparing NFD(r + x) with x + r orders x against a
    /// representative r, and equal classes reorder in neither direction. A non-starter is
    /// a mark (General_Category Mn or Mc — every ccc != 0 code point is one) that is its own
    /// NFD and that NFD moves against at least one of three probes of classes 1, 230 and
    /// 240 (U+0334, U+0301, U+0345): a class strictly between 1 and 240 moves against one
    /// of the two extremes, class 230 moves against 240, 1 against 230, 240 against 1.
    /// </summary>
    internal static SortedDictionary<int, int> DeriveCombiningClassRanks()
    {
        int[] probes = [0x0334, 0x0301, 0x0345];
        var nonStarters = new List<int>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var rune = new Rune(cp);
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            {
                continue;
            }

            var s = rune.ToString();
            string nfd;
            try
            {
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!string.Equals(nfd, s, StringComparison.Ordinal))
            {
                continue; // decomposes: its constituents get a class, it does not
            }

            foreach (var p in probes)
            {
                var probe = new Rune(p).ToString();
                if (!string.Equals(Nfd(probe + s), probe + s, StringComparison.Ordinal)
                    || !string.Equals(Nfd(s + probe), s + probe, StringComparison.Ordinal))
                {
                    nonStarters.Add(cp);
                    break;
                }
            }
        }

        // Representatives of the classes met so far, in increasing class order.
        var representatives = new List<int>();
        var classIndexOf = new Dictionary<int, int>();
        foreach (var cp in nonStarters)
        {
            var x = new Rune(cp).ToString();
            var lo = 0;
            var hi = representatives.Count - 1;
            var found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var r = new Rune(representatives[mid]).ToString();
                var rx = Nfd(r + x);
                var xr = Nfd(x + r);
                if (string.Equals(rx, x + r, StringComparison.Ordinal))
                {
                    hi = mid - 1; // x moved before r: class(x) < class(r)
                }
                else if (string.Equals(xr, r + x, StringComparison.Ordinal))
                {
                    lo = mid + 1; // r moved before x: class(x) > class(r)
                }
                else
                {
                    found = mid;
                    break;
                }
            }

            if (found < 0)
            {
                representatives.Insert(lo, cp);
                found = lo;
            }

            classIndexOf[cp] = representatives[found];
        }

        var rankOf = representatives.Select((rep, i) => (rep, rank: i + 1)).ToDictionary(t => t.rep, t => t.rank);
        return new SortedDictionary<int, int>(classIndexOf.ToDictionary(kv => kv.Key, kv => rankOf[kv.Value]));
    }

    internal static List<(int Start, int End)> ToRanges(SortedSet<int> set)
    {
        var ranges = new List<(int, int)>();
        int? start = null;
        var prev = -2;
        foreach (var cp in set)
        {
            if (start is null)
            {
                start = cp;
            }
            else if (cp != prev + 1)
            {
                ranges.Add((start.Value, prev));
                start = cp;
            }

            prev = cp;
        }

        if (start is not null)
        {
            ranges.Add((start.Value, prev));
        }

        return ranges;
    }

    /// <summary>Flat triples (start, end, rank) over consecutive code points of one rank — the layout of <see cref="CanonicalForm.CombiningClassData"/>.</summary>
    internal static List<int> ToClassTriples(SortedDictionary<int, int> ranks)
    {
        var data = new List<int>();
        int? start = null;
        var prev = -2;
        var rank = 0;
        foreach (var (cp, r) in ranks)
        {
            if (start is not null && (cp != prev + 1 || r != rank))
            {
                data.AddRange([start.Value, prev, rank]);
                start = null;
            }

            start ??= cp;
            rank = r;
            prev = cp;
        }

        if (start is not null)
        {
            data.AddRange([start.Value, prev, rank]);
        }

        return data;
    }

    /// <summary>Flat records (code, count, code points…) — the layout of <see cref="CanonicalForm.DecompositionData"/>.</summary>
    internal static List<int> ToDecompositionData(SortedDictionary<int, string> decompositions)
    {
        var data = new List<int>();
        foreach (var (cp, nfd) in decompositions)
        {
            var runes = nfd.EnumerateRunes().Select(r => r.Value).ToList();
            data.Add(cp);
            data.Add(runes.Count);
            data.AddRange(runes);
        }

        return data;
    }

    private static string RangeLiteral(string name, List<(int Start, int End)> ranges) =>
        $"    internal static readonly (int Start, int End)[] {name} =\n    [\n"
        + string.Join("\n", ranges.Select(r => $"        (0x{r.Start:X4}, 0x{r.End:X4}),"))
        + "\n    ];\n";

    private static string FlatLiteral(string name, List<int> data, Func<List<int>, int, int> recordLength)
    {
        var sb = new StringBuilder();
        sb.Append($"    internal static readonly int[] {name} =\n    [\n");
        for (var i = 0; i < data.Count;)
        {
            var len = recordLength(data, i);
            sb.Append("        ").Append(string.Join(", ", data.Skip(i).Take(len).Select(v => $"0x{v:X4}"))).Append(",\n");
            i += len;
        }

        return sb.Append("    ];\n").ToString();
    }

    [SkippableFact]
    public void Tables_match_the_host_normalizer()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to derive from");
        var (no, maybe, decompositions, refused) = Derive();
        var ranks = DeriveCombiningClassRanks();
        var expectedNo = ToRanges(no);
        var expectedMaybe = ToRanges(maybe);
        var expectedDecompositions = ToDecompositionData(decompositions);
        var expectedClasses = ToClassTriples(ranks);

        var literal = "// NfcQuickCheck.cs\n\n" + RangeLiteral("No", expectedNo) + "\n" + RangeLiteral("Maybe", expectedMaybe)
            + "\n// CanonicalForm.cs\n\n" + FlatLiteral("DecompositionData", expectedDecompositions, (d, i) => 2 + d[i + 1])
            + "\n" + FlatLiteral("CombiningClassData", expectedClasses, (_, _) => 3);
        var file = Path.Combine(AppContext.BaseDirectory, "unicode-tables.generated.txt");
        File.WriteAllText(file, literal);
        var message = $"The shipped Unicode tables differ from this host's ICU. The C# literals to paste into "
            + $"src/OKF4net/Providers/NfcQuickCheck.cs and CanonicalForm.cs are in {file}.\n"
            + $"(No: {no.Count} code points in {expectedNo.Count} ranges, {refused} of them refused by the normalizer; "
            + $"Maybe: {maybe.Count} in {expectedMaybe.Count}; Decompositions: {decompositions.Count} entries, {expectedDecompositions.Count} ints; "
            + $"combining classes: {ranks.Count} code points in {ranks.Values.Distinct().Count()} ranks, {expectedClasses.Count / 3} triples.)";
        Assert.True(expectedNo.SequenceEqual(NfcQuickCheck.No), message);
        Assert.True(expectedMaybe.SequenceEqual(NfcQuickCheck.Maybe), message);
        Assert.True(expectedDecompositions.SequenceEqual(CanonicalForm.DecompositionData), message);
        Assert.True(expectedClasses.SequenceEqual(CanonicalForm.CombiningClassData), message);
    }

    [SkippableFact]
    public void Hangul_decomposition_is_arithmetic()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to compare with");
        for (var cp = 0xAC00; cp <= 0xD7A3; cp++)
        {
            var s = new Rune(cp).ToString();
            Assert.Equal(Nfd(s), CanonicalForm.Decompose(s));
        }
    }

    [SkippableFact]
    public void Decompose_agrees_with_the_host_normalizer_on_every_scalar_and_on_mark_sequences()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to compare with");
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var s = new Rune(cp).ToString();
            string nfd;
            try
            {
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                continue; // a noncharacter: refused by the quick check before any fold
            }

            Assert.Equal(nfd, CanonicalForm.Decompose(s));
        }

        // Reordering: every pair of non-starters after a base, both orders.
        var marks = DeriveCombiningClassRanks().Keys.ToList();
        var rng = new Random(20261014);
        for (var i = 0; i < 20_000; i++)
        {
            var a = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var b = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var c = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var s = "a" + a + b + c;
            Assert.Equal(Nfd(s), CanonicalForm.Decompose(s));
        }
    }

    [Theory]
    [InlineData(0x00E9, NfcStatus.Yes)]   // é precomposed
    [InlineData(0x0301, NfcStatus.Maybe)] // combining acute
    [InlineData(0x212B, NfcStatus.No)]    // ANGSTROM SIGN → Å
    [InlineData(0x2126, NfcStatus.No)]    // OHM SIGN → Ω
    [InlineData(0xF900, NfcStatus.No)]    // CJK compatibility ideograph
    [InlineData(0xFFFE, NfcStatus.No)]    // noncharacter the normalizer refuses: closed
    [InlineData(0x1161, NfcStatus.Maybe)] // Hangul jungseong A
    [InlineData(0xAC00, NfcStatus.Yes)]   // precomposed syllable
    [InlineData(0x0E48, NfcStatus.Yes)]   // Thai tone mark: composes with nothing
    [InlineData(0x093F, NfcStatus.Yes)]   // Devanagari vowel sign I
    [InlineData(0x0041, NfcStatus.Yes)]
    public void Spot_checks(int codePoint, NfcStatus expected)
    {
        Assert.Equal(expected, NfcQuickCheck.Of(new Rune(codePoint)));
    }

    [Theory]
    [InlineData("caf\u00e9", "cafe\u0301")]                     // é → e + acute
    [InlineData("\u212b", "A\u030a")]                            // ANGSTROM SIGN → A + ring (singleton to a composite, fully decomposed)
    [InlineData("\u00c5", "A\u030a")]                            // Å → the same
    [InlineData("\uac01", "\u1100\u1161\u11a8")]                // Hangul LVT syllable: arithmetic
    [InlineData("\u0229\u0301", "e\u0327\u0301")]               // ȩ + acute: already in canonical order (202 < 230)
    [InlineData("\u00e9\u0327", "e\u0327\u0301")]               // é + cedilla: REORDERED to the same form (the two names are one on APFS)
    [InlineData("a\u0305\u0335", "a\u0335\u0305")]              // overline (230) then stroke overlay (1): reordered
    [InlineData("\u0e17\u0e35\u0e48", "\u0e17\u0e35\u0e48")]  // Thai: unchanged
    [InlineData("plain", "plain")]
    public void Decompose_spot_checks(string input, string expected)
    {
        Assert.Equal(expected, CanonicalForm.Decompose(input));
    }

    [Theory]
    [InlineData("a\u0335\u0305", true)]   // stroke overlay (class 1) then overline (230)
    [InlineData("a\u0305\u0335", false)]  // the reverse: not canonically ordered
    [InlineData("e\u0327\u0301", true)]
    [InlineData("e\u0301\u0327", false)]
    [InlineData("a\u0301b\u0327", true)]  // a starter between them: two separate runs
    [InlineData("caf\u00e9", true)]
    public void IsCanonicallyOrdered_spot_checks(string input, bool expected)
    {
        Assert.Equal(expected, CanonicalForm.IsCanonicallyOrdered(input));
    }

    [Fact]
    public void Combining_class_ranks_order_like_the_ucd_classes()
    {
        Assert.Equal(0, CanonicalForm.ClassOf(new Rune('a')));
        Assert.Equal(0, CanonicalForm.ClassOf(new Rune(0x00E9)));
        var overlay = CanonicalForm.ClassOf(new Rune(0x0335));  // ccc 1
        var cedilla = CanonicalForm.ClassOf(new Rune(0x0327));  // ccc 202
        var below = CanonicalForm.ClassOf(new Rune(0x0323));    // ccc 220
        var above = CanonicalForm.ClassOf(new Rune(0x0301));    // ccc 230
        var iota = CanonicalForm.ClassOf(new Rune(0x0345));     // ccc 240
        Assert.True(0 < overlay && overlay < cedilla && cedilla < below && below < above && above < iota);
        Assert.Equal(above, CanonicalForm.ClassOf(new Rune(0x0300))); // grave: also 230
    }
}
```

- [ ] **Step 2: Write the validator tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyValidatorTests
{
    private static BundleKeyValidator Strict() => new(BundleKeyConstraints.None, BundleKeyPolicy.Strict);
    private static BundleKeyValidator NativeLinux() => new(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native);
    private static BundleKeyValidator NativeWindows() => new(BundleKeyConstraintSets.Windows, BundleKeyPolicy.Native);
    private static BundleKeyValidator NativeMacOS() => new(BundleKeyConstraintSets.MacOS, BundleKeyPolicy.Native);

    [Fact]
    public void Strict_is_the_union_of_every_known_native_set()
    {
        var v = Strict();
        Assert.Equal(BundleKeyConstraintSets.Strict, v.Effective);
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.RequireNfc));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict));
    }

    [Fact]
    public void Native_never_drops_below_the_providers_own_constraints()
    {
        var v = NativeWindows();
        Assert.Equal(BundleKeyConstraintSets.Windows, v.Effective);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("con.md")));
    }

    [Theory]
    [InlineData("a./b.md")]      // trailing dot in a directory segment: Windows strips it → alias of a/b.md
    [InlineData("a /b.md")]      // trailing space
    [InlineData("con.md")]
    [InlineData("Con.md")]       // reserved names are case-insensitive
    [InlineData("COM1.md")]
    [InlineData("com\u00b9.md")] // COM¹
    [InlineData("lpt\u00b3/x.md")]
    [InlineData("lPt\u00b9.md")]
    [InlineData("nul.txt.md")]   // reserved base before the first '.'
    [InlineData("a:b.md")]
    [InlineData("a|b.md")]
    [InlineData("a\"b.md")]
    public void Strict_rejects_windows_aliases_and_reserved_names(string raw)
    {
        var key = BundleKey.Parse(raw);
        Assert.NotNull(Strict().CheckKey(key));
        Assert.Null(NativeLinux().CheckKey(key)); // legal on a POSIX filesystem
    }

    [Theory]
    [InlineData("tables/orders.md")]
    [InlineData("a.b.c.md")]
    [InlineData("console.md")]   // 'console' is not 'con'
    [InlineData("com10.md")]     // only COM1..COM9 and the superscripts
    [InlineData("_con.md")]
    [InlineData("foo..md")]      // a trailing dot in the STEM is not an alias; only a whole segment ending with '.' is (a./b.md above)
    public void Strict_accepts_ordinary_names(string raw)
    {
        Assert.Null(Strict().CheckKey(BundleKey.Parse(raw)));
    }

    [Theory]
    [InlineData("caf\u00e9.md")]                 // NFC
    [InlineData("ni\u00f1o/\u00fcber.md")]
    [InlineData("\uAC00\uB098.md")]              // precomposed Hangul
    [InlineData("\u0e17\u0e35\u0e48.md")]        // Thai with stacked marks: none composes
    [InlineData("\u0915\u093f.md")]              // Devanagari KA + vowel sign I
    [InlineData("\u65e5\u672c\u8a9e.md")]
    [InlineData("a\u0335\u0305.md")]             // two non-composing marks in canonical order (class 1, then 230)
    public void Strict_accepts_nfc_names_without_normalizing(string raw)
    {
        Assert.Null(Strict().CheckKey(BundleKey.Parse(raw)));
    }

    [Theory]
    [InlineData("cafe\u0301.md", "U+0301")]   // NFD: composing acute
    [InlineData("\u212b.md", "U+212B")]       // never NFC
    [InlineData("\u1100\u1161.md", "U+1161")] // conjoining jamo
    [InlineData("q\u0323.md", "U+0323")]      // over-rejection, documented: the mark composes with other bases
    [InlineData("x\ufffe.md", "U+FFFE")]      // refused by the normalizer: closed (decision 14)
    [InlineData("a\u0305\u0335.md", "canonical order")] // overline (230) before stroke overlay (1): NFC would reorder them (decision 17)
    public void Strict_refuses_what_the_quick_check_cannot_call_nfc(string raw, string expectedInReason)
    {
        var key = BundleKey.Parse(raw);
        var reason = Strict().CheckKey(key);
        Assert.NotNull(reason);
        Assert.Contains(expectedInReason, reason);
        Assert.Null(NativeLinux().CheckKey(key));
    }

    [Fact]
    public void Tree_rejects_per_level_case_collisions_with_their_subtrees_and_reports_the_prefixes()
    {
        var r = Strict().ValidateTree(["A/x.md", "a/y.md", "b/z.md"]);
        Assert.Equal([BundleKey.Parse("b/z.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("collides", rej.Reason));
        Assert.Contains(r.Rejected, rej => rej.RawName == "A/x.md");
        Assert.Contains(r.Rejected, rej => rej.RawName == "a/y.md");
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("a")], r.RejectedPrefixes.OrderBy(k => k).ToList());
    }

    [Theory]
    [InlineData("A\u03a3/x.md", "a\u03c3/y.md")] // capital sigma vs small sigma: one name on NTFS (review-measured)
    [InlineData("a\u03c2/x.md", "a\u03c3/y.md")] // final sigma vs small sigma: NTFS upcases both to Σ
    [InlineData("STRASSE/x.md", "strasse/y.md")]
    public void Tree_folds_like_a_filesystem_upcase_table_not_like_a_linguistic_lowercase(string first, string second)
    {
        var r = Strict().ValidateTree([first, second]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
    }

    // Canonically equivalent siblings: one name on APFS. The NFD spelling is refused by
    // RequireNfc, but it is still THERE, so its NFC twin collides with it and both prefixes
    // are condemned (seventh review: r7 grouped by case fold only and accepted `caf\u00e9/x.md`).
    [Theory]
    [InlineData("caf\u00e9/x.md", "cafe\u0301/y.md")]       // é vs e + acute
    [InlineData("\u00c5/x.md", "\u212b/y.md")]              // Å vs ANGSTROM SIGN
    [InlineData("\uac00/x.md", "\u1100\u1161/y.md")]        // Hangul syllable vs conjoining jamo
    public void Tree_rejects_canonically_equivalent_siblings_under_strict(string nfc, string other)
    {
        var r = Strict().ValidateTree([nfc, other]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.Contains(r.Rejected, rej => rej.RawName == nfc && rej.Reason.Contains("collides"));
        Assert.Contains(r.Rejected, rej => rej.RawName == other); // refused by RequireNfc, structurally present all the same
        Assert.Equal(
            new[] { nfc, other }.Select(n => BundleKey.Parse(n).Parent!).OrderBy(k => k).ToList(),
            r.RejectedPrefixes);
    }

    [Theory]
    [InlineData("caf\u00e9/x.md", "cafe\u0301/y.md")]
    [InlineData("\u00c5/x.md", "\u212b/y.md")]
    [InlineData("\u0229\u0301/x.md", "\u00e9\u0327/y.md")] // ȩ + acute vs é + cedilla: equivalent only after reordering (classes 202 < 230)
    public void UnicodeFormCollisions_alone_rejects_both_forms(string nfc, string other)
    {
        // No RequireNfc: both names are admissible on their own (the third row's acute is a
        // `Maybe` mark that Strict would refuse); together they are one object on the volume.
        var v = new BundleKeyValidator(BundleKeyConstraints.UnicodeFormCollisions, BundleKeyPolicy.Native);
        var r = v.ValidateTree([nfc, other]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("collides", rej.Reason));
        Assert.Equal(2, r.RejectedPrefixes.Count);
    }

    [Fact]
    public void A_native_windows_set_keeps_both_unicode_forms_but_folds_case()
    {
        // NTFS compares code points, not canonical forms: é and e + acute are two files.
        var r = NativeWindows().ValidateTree(["caf\u00e9/x.md", "cafe\u0301/y.md", "CAF\u00c9/z.md"]);
        Assert.Equal([BundleKey.Parse("cafe\u0301/y.md")], r.Accepted);
        Assert.Equal([BundleKey.Parse("CAF\u00c9"), BundleKey.Parse("caf\u00e9")], r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejects_deep_case_collisions_too()
    {
        var r = Strict().ValidateTree(["p/q/R/x.md", "p/q/r/y.md", "p/q/s/z.md"]);
        Assert.Equal([BundleKey.Parse("p/q/s/z.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.Contains(BundleKey.Parse("p/q/R"), r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejects_file_directory_conflicts_in_both_directions()
    {
        var r = Strict().ValidateTree(["a.md", "a.md/b.md", "ok.md"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("both an object and a prefix", rej.Reason));
        Assert.Equal([BundleKey.Parse("a.md")], r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_keeps_distinct_case_on_a_native_case_sensitive_set()
    {
        var r = NativeLinux().ValidateTree(["A/x.md", "a/y.md"]);
        Assert.Equal(2, r.Accepted.Count);
        Assert.Empty(r.Rejected);
        Assert.Empty(r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_reports_base_rule_failures_and_duplicates_with_the_raw_name()
    {
        var r = Strict().ValidateTree(["bad\\name.md", "dup.md", "dup.md", "fine.md"]);
        Assert.Equal([BundleKey.Parse("fine.md")], r.Accepted);
        Assert.Contains(r.Rejected, rej => rej.RawName == "bad\\name.md" && rej.Reason.Contains("backslash"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "dup.md" && rej.Reason.Contains("twice"));
    }

    [Fact]
    public void A_duplicated_object_still_counts_as_a_prefix_conflict()
    {
        var r = Strict().ValidateTree(["x", "x", "x/y.sql"]);
        Assert.Empty(r.Accepted);
        Assert.Equal([BundleKey.Parse("x")], r.RejectedPrefixes);
        Assert.Contains(r.Rejected, rej => rej.RawName == "x" && rej.Reason.Contains("twice"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "x/y.sql" && rej.Reason.Contains("both an object and a prefix"));
    }

    [Theory]
    [InlineData(new[] { "Ab/x.sql", "Ab/x.sql", "aB/y.sql", "aB/y.sql" }, new[] { "Ab", "aB" })]
    [InlineData(new[] { "x", "x", "x/y.sql", "x/y.sql" }, new[] { "x" })]
    [InlineData(new[] { "x.md", "x.md/A.sql", "x.md/a.sql" }, new[] { "x.md", "x.md/A.sql", "x.md/a.sql" })] // children case-doomed, parent still an object-and-prefix
    public void Prefixes_are_condemned_even_when_every_offender_is_a_duplicate_or_case_doomed(string[] listing, string[] prefixes)
    {
        var r = Strict().ValidateTree(listing);
        Assert.Empty(r.Accepted);
        Assert.Equal(prefixes.Select(BundleKey.Parse).OrderBy(k => k), r.RejectedPrefixes);
    }

    [Fact]
    public void A_restricting_validator_cannot_erase_an_objects_structural_presence()
    {
        // The host refuses the parent: the child must not come back in through the gap.
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native, [k => k.ToString() == "x.md" ? "refused by the host" : null]);
        var r = v.ValidateTree(["x.md", "x.md/ok.sql"]);
        Assert.Empty(r.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], r.RejectedPrefixes);

        // Same with a Strict rejection of the child: the parent is still an object-and-prefix.
        var strict = Strict().ValidateTree(["x.md", "x.md/con.sql"]);
        Assert.Empty(strict.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], strict.RejectedPrefixes);
    }

    [Fact]
    public void The_outermost_condemned_prefix_is_the_one_named_in_the_reason()
    {
        var r = Strict().ValidateTree(["A/x.md", "a/B/y.md", "a/b/z.md"]);
        Assert.Empty(r.Accepted);
        var deep = Assert.Single(r.Rejected, rej => rej.RawName == "a/b/z.md");
        Assert.Contains("'a'", deep.Reason); // not 'a/b': the reader is told the first prefix that fails
    }

    [Fact]
    public void Large_listings_whose_condemned_prefixes_grow_with_them_validate_in_one_pass()
    {
        // 65 536 names whose condemned prefixes grow WITH the listing: 16 384 case-colliding
        // pairs (`p{i}/A` beside `p{i}/a`) and 16 384 file/prefix conflicts (`q{i}` beside
        // `q{i}/child.sql`). That is the shape on which the seventh review measured r7's
        // per-candidate scan of the condemned list: 26 s (collisions) and 13 s (conflicts) for
        // 64 000 names; with one set lookup per ancestor it is under a second. A fixture with
        // two colliding roots — r8's — never exposed that scan (two keys to scan per candidate,
        // 0.4 s either way: eighth review). The assertions are on the outcome — a timing
        // assertion is a flake, not a proof — so a regression to the scan shows up as this
        // test's duration in the run, which Task 8 records.
        var names = new List<string>(65_536);
        for (var i = 0; i < 16_384; i++)
        {
            names.Add($"p{i}/A/x.sql");
            names.Add($"p{i}/a/y.sql");
            names.Add($"q{i}");
            names.Add($"q{i}/child.sql");
        }

        var r = Strict().ValidateTree(names);
        Assert.Empty(r.Accepted);
        Assert.Equal(65_536, r.Rejected.Count);
        Assert.Equal(3 * 16_384, r.RejectedPrefixes.Count); // p{i}/A, p{i}/a and q{i}
        Assert.Contains(BundleKey.Parse("p16383/a"), r.RejectedPrefixes);
        Assert.Contains(BundleKey.Parse("q0"), r.RejectedPrefixes);
    }

    [Theory]
    [InlineData("Ab/x.md", "AB/X.MD", "AB/X.MD", "Ab/x.md")]
    [InlineData("caf\u00e9/x.md", "CAFE\u0301/X.MD", "CAF\u00c9/X.MD", "caf\u00e9/x.md")]   // Strict decomposes then upcases; Windows upcases only; Linux: identity
    [InlineData("\u212b/x.md", "A\u030a/X.MD", "\u212b/X.MD", "\u212b/x.md")]               // ANGSTROM SIGN has no case mapping
    public void FoldKeyText_folds_the_way_the_effective_constraints_compare_siblings(string raw, string strict, string windows, string linux)
    {
        var key = BundleKey.Parse(raw);
        Assert.Equal(strict, Strict().FoldKeyText(key));
        Assert.Equal(windows, NativeWindows().FoldKeyText(key));
        Assert.Equal(linux, NativeLinux().FoldKeyText(key));
        Assert.Equal(strict, NativeMacOS().FoldKeyText(key)); // macOS folds both
    }

    [Fact]
    public void Extra_validators_only_restrict()
    {
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native,
            [k => k.Segments.Any(s => s.Any(char.IsUpper)) ? "upper-case segment" : null]);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("Tables/x.md")));
        Assert.Null(v.CheckKey(BundleKey.Parse("tables/x.md")));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~NfcQuickCheck|FullyQualifiedName~BundleKeyValidatorTests"`
Expected: build error — `NfcQuickCheck`, `BundleKeyValidator`, `BundleKeyConstraints` do not exist.

- [ ] **Step 4: Implement the constraints** (`src/OKF4net/Providers/BundleKeyConstraints.cs`)

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// Storage-specific aliasing rules a provider declares (spec §2, layer 2). Each flag
/// names a way two distinct keys could designate the same object on that storage.
/// </summary>
[Flags]
public enum BundleKeyConstraints
{
    /// <summary>No constraint beyond the base rules.</summary>
    None = 0,

    /// <summary>A segment must not end with <c>.</c> or a space (Windows strips them).</summary>
    TrailingDotOrSpace = 1 << 0,

    /// <summary>A segment must not contain <c>&lt; &gt; : " | ? *</c> (Windows; <c>:</c> also opens an NTFS stream).</summary>
    WindowsIllegalCharacters = 1 << 1,

    /// <summary>A segment's base (before the first <c>.</c>) must not be a reserved Windows device name.</summary>
    WindowsReservedNames = 1 << 2,

    /// <summary>Two siblings must not be equal once folded the way a filesystem upcase table folds.</summary>
    CaseInsensitiveCollisions = 1 << 3,

    /// <summary>Every segment must pass the NFC quick check (no <c>NFC_QC=No</c> character, no composing mark).</summary>
    RequireNfc = 1 << 4,

    /// <summary>
    /// Two siblings must not be canonically equivalent (APFS compares normalized names):
    /// sibling names are compared in canonical decomposition (<c>CanonicalForm.Decompose</c>),
    /// independently of <see cref="RequireNfc"/> — a decomposed name that the quick check
    /// refuses is still a file beside its precomposed twin.
    /// </summary>
    UnicodeFormCollisions = 1 << 5,

    /// <summary>An object key must not also be a prefix of other keys (a file cannot be a directory).</summary>
    FileDirectoryConflict = 1 << 6,
}

/// <summary>The constraint sets of the storages this library knows (spec §2, table of native constraints).</summary>
public static class BundleKeyConstraintSets
{
    /// <summary>NTFS/FAT as Windows exposes them.</summary>
    public static BundleKeyConstraints Windows =>
        BundleKeyConstraints.TrailingDotOrSpace
        | BundleKeyConstraints.WindowsIllegalCharacters
        | BundleKeyConstraints.WindowsReservedNames
        | BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>APFS/HFS+ as macOS exposes them by default.</summary>
    public static BundleKeyConstraints MacOS =>
        BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.RequireNfc
        | BundleKeyConstraints.UnicodeFormCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>A POSIX filesystem: only the file/directory conflict beyond the base rules.</summary>
    public static BundleKeyConstraints Linux => BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>The union of every known set: a key valid here is valid on every provider (<see cref="BundleKeyPolicy.Strict"/>).</summary>
    public static BundleKeyConstraints Strict => Windows | MacOS | Linux;

    /// <summary>The set for the operating system this process runs on. The real boundary is the volume, not the OS (spec §2); hosts on mixed volumes keep <see cref="BundleKeyPolicy.Strict"/>.</summary>
    public static BundleKeyConstraints ForCurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : Linux;
}

/// <summary>Which constraints apply on top of a provider's own (spec §2, layer 3). Chosen by the host, never by content.</summary>
public enum BundleKeyPolicy
{
    /// <summary>Every known native set (the default): a bundle valid on one provider is valid on all.</summary>
    Strict,

    /// <summary>Only the current provider's native set.</summary>
    Native,
}
```

- [ ] **Step 5: Implement `NfcQuickCheck` and `CanonicalForm` with empty tables, then let the test fill them**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Providers;

/// <summary>Result of the NFC quick check for one code point (UAX #15 §9).</summary>
public enum NfcStatus
{
    /// <summary>Never changes under NFC on its own.</summary>
    Yes,

    /// <summary>Never in NFC (a singleton or non-starter decomposition), or refused by the reference normalizer.</summary>
    No,

    /// <summary>A mark that recomposes with some preceding base; undecidable without composition tables.</summary>
    Maybe,
}

/// <summary>
/// UAX #15's NFC quick check, table-driven and independent of <see cref="string.Normalize(NormalizationForm)"/>,
/// which is a silent no-op under invariant globalization (the Native AOT <c>okf</c>).
/// <see cref="No"/> is <c>NFC_QC=No</c> plus the code points the reference normalizer refuses;
/// <see cref="Maybe"/> is the set of second elements of every direct canonical pair that
/// recomposes — tighter than UCD's <c>NFC_QC=Maybe</c>, and exact for the question "can this
/// mark compose with something before it". The canonical ordering of the marks is checked
/// separately by <see cref="CanonicalForm.IsCanonicallyOrdered"/>. Both tables are derived
/// from ICU by <c>tests/OKF4net.Tests/Providers/UnicodeTableTests.cs</c>, which fails with the
/// literal to paste whenever a host normalizer disagrees with them. Generated on
/// <c>[OS, .NET version and ICU/Unicode version, filled in when pasted]</c>.
/// </summary>
internal static class NfcQuickCheck
{
    internal static readonly (int Start, int End)[] No =
    [
        // paste from UnicodeTableTests.Tables_match_the_host_normalizer
    ];

    internal static readonly (int Start, int End)[] Maybe =
    [
        // paste from UnicodeTableTests.Tables_match_the_host_normalizer
    ];

    /// <summary>The quick-check status of one scalar value.</summary>
    internal static NfcStatus Of(Rune rune) =>
        Contains(No, rune.Value) ? NfcStatus.No
        : Contains(Maybe, rune.Value) ? NfcStatus.Maybe
        : NfcStatus.Yes;

    private static bool Contains((int Start, int End)[] ranges, int cp)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var (start, end) = ranges[mid];
            if (cp < start)
            {
                hi = mid - 1;
            }
            else if (cp > end)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
```

Then `src/OKF4net/Providers/CanonicalForm.cs` — the canonical decomposition, independent of `Normalize` for the same reason:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Providers;

/// <summary>
/// Canonical decomposition and canonical ordering (the NFD of a string), table-driven and
/// independent of <see cref="string.Normalize(NormalizationForm)"/>, which is a silent no-op
/// under invariant globalization. <see cref="DecompositionData"/> holds every canonical
/// decomposition outside the Hangul syllables (which are arithmetic) as flat records
/// <c>code, count, code points…</c>, each already fully decomposed; <see cref="CombiningClassData"/>
/// holds the canonical combining classes as flat triples <c>start, end, rank</c> — ranks in
/// class order, not the UCD numbers, since only the order matters to reordering. Both are
/// derived from ICU by <c>tests/OKF4net.Tests/Providers/UnicodeTableTests.cs</c>, which fails
/// with the literals to paste whenever a host normalizer disagrees, and which compares
/// <see cref="Decompose"/> with the host's NFD on every scalar and on random mark sequences.
/// Generated on <c>[OS, .NET version and ICU/Unicode version, filled in when pasted]</c>.
/// </summary>
internal static class CanonicalForm
{
    private const int HangulBase = 0xAC00;
    private const int HangulEnd = 0xD7A3;
    private const int LBase = 0x1100;
    private const int VBase = 0x1161;
    private const int TBase = 0x11A7;
    private const int VCount = 21;
    private const int TCount = 28;

    internal static readonly int[] DecompositionData =
    [
        // paste from UnicodeTableTests.Tables_match_the_host_normalizer
    ];

    internal static readonly int[] CombiningClassData =
    [
        // paste from UnicodeTableTests.Tables_match_the_host_normalizer
    ];

    // Built once from the flat data: code point → its full canonical decomposition.
    private static readonly Dictionary<int, string> Decompositions = BuildDecompositions();

    private static Dictionary<int, string> BuildDecompositions()
    {
        var map = new Dictionary<int, string>();
        for (var i = 0; i < DecompositionData.Length;)
        {
            var code = DecompositionData[i];
            var count = DecompositionData[i + 1];
            var sb = new StringBuilder(count * 2);
            for (var j = 0; j < count; j++)
            {
                sb.Append(new Rune(DecompositionData[i + 2 + j]).ToString());
            }

            map[code] = sb.ToString();
            i += 2 + count;
        }

        return map;
    }

    /// <summary>The combining-class rank of one scalar: 0 for a starter, otherwise its position in class order.</summary>
    internal static int ClassOf(Rune rune)
    {
        var lo = 0;
        var hi = CombiningClassData.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var start = CombiningClassData[mid * 3];
            var end = CombiningClassData[mid * 3 + 1];
            if (rune.Value < start)
            {
                hi = mid - 1;
            }
            else if (rune.Value > end)
            {
                lo = mid + 1;
            }
            else
            {
                return CombiningClassData[mid * 3 + 2];
            }
        }

        return 0;
    }

    /// <summary>
    /// The canonical decomposition of <paramref name="text"/> with its marks in canonical
    /// order: the NFD. Two canonically equivalent strings decompose to the same string, which
    /// is what makes two sibling names comparable under
    /// <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>.
    /// </summary>
    internal static string Decompose(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runes = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp is >= HangulBase and <= HangulEnd)
            {
                var s = cp - HangulBase;
                runes.Add(new Rune(LBase + s / (VCount * TCount)));
                runes.Add(new Rune(VBase + s % (VCount * TCount) / TCount));
                var t = s % TCount;
                if (t != 0)
                {
                    runes.Add(new Rune(TBase + t));
                }
            }
            else if (Decompositions.TryGetValue(cp, out var decomposed))
            {
                runes.AddRange(decomposed.EnumerateRunes());
            }
            else
            {
                runes.Add(rune);
            }
        }

        // Canonical ordering: within each run of non-starters, a stable sort by class.
        for (var i = 1; i < runes.Count; i++)
        {
            var c = ClassOf(runes[i]);
            if (c == 0)
            {
                continue;
            }

            var j = i;
            while (j > 0 && ClassOf(runes[j - 1]) > c)
            {
                (runes[j - 1], runes[j]) = (runes[j], runes[j - 1]);
                j--;
            }
        }

        var sb = new StringBuilder(text.Length);
        foreach (var rune in runes)
        {
            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    /// <summary>Whether no two adjacent non-starters of <paramref name="text"/> are out of class order (the second part of UAX #15's quick check).</summary>
    internal static bool IsCanonicallyOrdered(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var previous = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var c = ClassOf(rune);
            if (c != 0 && previous > c)
            {
                return false;
            }

            previous = c;
        }

        return true;
    }
}
```

The insertion sort only moves a mark past marks of a strictly higher class, so equal classes keep their order (stable) — the same rule NFD applies. Runs of non-starters are a few marks long in any real name; the bound on a segment is the key's own 1 024 bytes.

Do not run the generator yet: the test project also holds `BundleKeyValidatorTests`, which cannot compile until Step 6 (the third review hit CS0246 here).

- [ ] **Step 6: Implement the validator** (`src/OKF4net/Providers/BundleKeyValidator.cs`)

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>A raw name the tree validation refused, with the reason (spec §2).</summary>
public sealed record BundleKeyRejection(string RawName, string Reason);

/// <summary>
/// The outcome of <see cref="BundleKeyValidator.ValidateTree"/>: accepted keys in input order,
/// every rejected raw name, and the colliding or conflicting keys themselves
/// (<see cref="RejectedPrefixes"/>), under which nothing may later be admitted.
/// </summary>
public sealed record BundleKeyTreeResult(IReadOnlyList<BundleKey> Accepted, IReadOnlyList<BundleKeyRejection> Rejected, IReadOnlyList<BundleKey> RejectedPrefixes);

/// <summary>
/// Applies spec §2's layers 2 and 3 to keys: the provider's native constraints,
/// the host's portability policy, and optional extra validators that can only
/// restrict. <see cref="ValidateTree"/> checks a whole listing as a tree —
/// per-level collisions and file/directory conflicts — not key by key.
/// NFC is decided by <see cref="NfcQuickCheck"/> and <see cref="CanonicalForm.IsCanonicallyOrdered"/>;
/// sibling names are compared through <see cref="FoldSegment"/>: canonical decomposition
/// (<see cref="CanonicalForm.Decompose"/>) and the per-code-point upper-case fold
/// <see cref="FoldForCollision"/>, never a linguistic case mapping, never <c>Normalize</c>.
/// </summary>
public sealed class BundleKeyValidator
{
    private static readonly string[] ReservedBases =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    private static readonly char[] WindowsIllegal = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly IReadOnlyList<Func<BundleKey, string?>> _extra;

    /// <summary>Creates a validator for a provider declaring <paramref name="native"/>, under <paramref name="policy"/>.</summary>
    public BundleKeyValidator(BundleKeyConstraints native, BundleKeyPolicy policy, IReadOnlyList<Func<BundleKey, string?>>? extraValidators = null)
    {
        Effective = policy == BundleKeyPolicy.Strict ? BundleKeyConstraintSets.Strict | native : native;
        _extra = extraValidators ?? [];
    }

    /// <summary>The constraints actually enforced: never below the provider's native set.</summary>
    public BundleKeyConstraints Effective { get; }

    /// <summary>
    /// Folds one segment for collision detection: per code point,
    /// <see cref="Rune.ToUpperInvariant(Rune)"/>, no context. This is a <b>conservative
    /// over-approximation</b> of a filesystem upcase table, not an equivalence: it merges
    /// <c>Σ</c>, <c>σ</c> and <c>ς</c>, whereas NTFS keeps <c>σ</c> and <c>ς</c> distinct — so
    /// under <see cref="BundleKeyPolicy.Native"/> on Windows two such siblings are rejected
    /// although the volume would hold both (maintainer ruling, plan decision 13: a guard
    /// fails closed, and the exact table cannot be shipped). Works under invariant
    /// globalization (full simple case mapping since .NET 8).
    /// </summary>
    public static string FoldForCollision(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var sb = new StringBuilder(segment.Length);
        foreach (var rune in segment.EnumerateRunes())
        {
            sb.Append(Rune.ToUpperInvariant(rune).ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Folds one segment the way the effective constraints say two sibling names are
    /// compared: canonical decomposition (<see cref="CanonicalForm.Decompose"/>) under
    /// <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, then <see cref="FoldForCollision"/>
    /// under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/>; identity under
    /// neither. It runs on every structural name, refused ones included: a decomposed name
    /// that <see cref="BundleKeyConstraints.RequireNfc"/> refuses is still a file beside its
    /// precomposed twin (seventh review, decision 17).
    /// </summary>
    public string FoldSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var folded = Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions) ? CanonicalForm.Decompose(segment) : segment;
        return Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) ? FoldForCollision(folded) : folded;
    }

    /// <summary>
    /// The text under which a key is remembered as a rejected prefix (decision 11): every
    /// segment through <see cref="FoldSegment"/>. Two spellings that would collide fold to the
    /// same text, so a condemned prefix condemns every spelling of itself.
    /// </summary>
    public string FoldKeyText(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) && !Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions))
        {
            return key.ToString();
        }

        return string.Join('/', key.Segments.Select(FoldSegment));
    }

    /// <summary>Checks one key's segments against the effective constraints and the extra validators; <see langword="null"/> when accepted.</summary>
    public string? CheckKey(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var segment in key.Segments)
        {
            var reason = CheckSegment(segment);
            if (reason is not null)
            {
                return $"segment {DebugQuote.Quote(segment)} {reason}";
            }
        }

        foreach (var validator in _extra)
        {
            var reason = validator(key);
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a listing as a tree. Every raw name goes through the base rules and
    /// <see cref="CheckKey"/>; then, under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/>
    /// or <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, siblings at any level that fold
    /// to the same name are all rejected with their subtrees; under
    /// <see cref="BundleKeyConstraints.FileDirectoryConflict"/>, an object that is also a prefix of
    /// other keys is rejected with everything under it. A raw name listed twice is rejected.
    /// The colliding and conflicting keys are returned as <see cref="BundleKeyTreeResult.RejectedPrefixes"/>.
    /// </summary>
    public BundleKeyTreeResult ValidateTree(IEnumerable<string> rawNames)
    {
        ArgumentNullException.ThrowIfNull(rawNames);
        var rejected = new List<BundleKeyRejection>();
        var candidates = new List<(string Raw, BundleKey Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        // Structural presence: every name that is a key at all, recorded BEFORE CheckKey and
        // before duplicate handling. Admission (policy, extra validators) decides what is
        // accepted and diagnosed; it never erases the fact that an object is THERE, which is
        // what the collision and conflict checks reason about (sixth review: a host validator
        // refusing `x.md` must not rehabilitate `x.md/ok.sql`).
        var structural = new HashSet<BundleKey>();

        foreach (var raw in rawNames)
        {
            if (!seen.Add(raw))
            {
                if (duplicates.Add(raw))
                {
                    rejected.Add(new BundleKeyRejection(raw, "listed twice by the provider"));
                }

                continue;
            }

            if (!BundleKey.TryParse(raw, out var key, out var reason))
            {
                rejected.Add(new BundleKeyRejection(raw, reason));
                continue;
            }

            structural.Add(key);
            var keyReason = CheckKey(key);
            if (keyReason is not null)
            {
                rejected.Add(new BundleKeyRejection(raw, keyReason));
                continue;
            }

            candidates.Add((raw, key));
        }

        // A duplicated name is rejected, but the object it names is still THERE: its first
        // occurrence entered `structural` above, so a duplicated `x` still conflicts with
        // `x/y.sql` (r3 dropped it first and let `x/y.sql` through).
        candidates.RemoveAll(c => duplicates.Contains(c.Raw));

        var doomed = new HashSet<BundleKey>();
        var rejectedPrefixes = new HashSet<BundleKey>();
        if (Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) || Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions))
        {
            var groups = new Dictionary<(string Parent, string Folded), HashSet<string>>();
            foreach (var key in structural)
            {
                for (var depth = 1; depth <= key.Segments.Count; depth++)
                {
                    var parent = string.Join('/', key.Segments.Take(depth - 1));
                    var name = key.Segments[depth - 1];
                    // Grouped by the SAME fold Bundle later matches rejected prefixes with
                    // (FoldKeyText): canonical decomposition and/or case, per the effective
                    // constraints. r7 grouped by case alone and let `café` stand beside the
                    // refused `cafe` + U+0301 (seventh review).
                    var g = (parent, FoldSegment(name));
                    if (!groups.TryGetValue(g, out var names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        groups[g] = names;
                    }

                    names.Add(name);
                }
            }

            var colliding = groups.Where(kv => kv.Value.Count > 1)
                .SelectMany(kv => kv.Value.Select(name => kv.Key.Parent.Length == 0 ? name : kv.Key.Parent + "/" + name))
                .Select(BundleKey.Parse)
                .ToHashSet();

            // The condemned prefixes come from the STRUCTURAL collision, not from the candidates
            // it dooms: when every offender is a duplicate there is no candidate left to doom,
            // yet the prefix is condemned all the same (fourth review).
            rejectedPrefixes.UnionWith(colliding);

            foreach (var (raw, key) in candidates)
            {
                var hit = OutermostCondemned(key, colliding);
                if (hit is not null && doomed.Add(key))
                {
                    rejected.Add(new BundleKeyRejection(raw, $"'{hit}' collides with a sibling that differs only by case or Unicode form"));
                }
            }
        }

        if (Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict))
        {
            // On ALL structural keys, case-doomed ones included: a child condemned for a case
            // collision is still physically under its parent, so the parent is still an object
            // that is also a prefix (fifth review: `x.md`, `x.md/A.sql`, `x.md/a.sql`).
            var keys = structural;
            var prefixes = new HashSet<BundleKey>();
            foreach (var key in keys)
            {
                for (var parent = key.Parent; parent is not null; parent = parent.Parent)
                {
                    prefixes.Add(parent);
                }
            }

            var conflicts = keys.Where(prefixes.Contains).ToHashSet();
            rejectedPrefixes.UnionWith(conflicts); // likewise: structural, not candidate-driven
            foreach (var (raw, key) in candidates)
            {
                if (doomed.Contains(key))
                {
                    continue;
                }

                var conflict = OutermostCondemned(key, conflicts);
                if (conflict is not null)
                {
                    doomed.Add(key);
                    rejected.Add(new BundleKeyRejection(raw, $"'{conflict}' is both an object and a prefix of other keys"));
                }
            }
        }

        var accepted = candidates.Where(c => !doomed.Contains(c.Key)).Select(c => c.Key).ToList();
        return new BundleKeyTreeResult(accepted, rejected, rejectedPrefixes.OrderBy(k => k).ToList());
    }

    /// <summary>
    /// The outermost prefix of <paramref name="key"/> (the key itself included) that is in
    /// <paramref name="condemned"/>, or <see langword="null"/>: one set lookup per segment, so
    /// the whole pass is linear in the listing times its depth. r7 scanned the condemned list
    /// once per candidate — quadratic when that list grows with the listing: 26 s for 64 000
    /// names in colliding sibling pairs, 13 s in file/prefix conflicts (seventh review).
    /// </summary>
    private static BundleKey? OutermostCondemned(BundleKey key, HashSet<BundleKey> condemned)
    {
        for (var depth = 1; depth <= key.Segments.Count; depth++)
        {
            var prefix = depth == key.Segments.Count ? key : BundleKey.FromSegments(key.Segments.Take(depth).ToList());
            if (condemned.Contains(prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private string? CheckSegment(string segment)
    {
        if (Effective.HasFlag(BundleKeyConstraints.TrailingDotOrSpace) && (segment[^1] == '.' || segment[^1] == ' '))
        {
            return "ends with '.' or a space";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsIllegalCharacters) && segment.IndexOfAny(WindowsIllegal) >= 0)
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
            foreach (var rune in segment.EnumerateRunes())
            {
                switch (NfcQuickCheck.Of(rune))
                {
                    case NfcStatus.No:
                        return $"contains U+{rune.Value:X4}, which is never in Unicode NFC";
                    case NfcStatus.Maybe:
                        return $"contains the combining character U+{rune.Value:X4}, whose NFC form cannot be decided without composition tables; use the precomposed form";
                }
            }

            // Every rune is a starter or a non-composing mark here; NFC would still reorder
            // marks of decreasing class (decision 17 closed this residual).
            if (!CanonicalForm.IsCanonicallyOrdered(segment))
            {
                return "has combining marks out of canonical order, so it is not in Unicode NFC";
            }
        }

        return null;
    }
}
```

Convert every new source file and test file to CRLF.

- [ ] **Step 7: Generate and paste the tables**

Now everything compiles. Run `dotnet test OKF4net.sln --filter "FullyQualifiedName~UnicodeTableTests.Tables_match"`: it **fails by design** (the derivation catches the normalizer's `ArgumentException`, so it reaches the assertion) and names the file holding the four literals (`unicode-tables.generated.txt` beside the test assembly). Paste `No` and `Maybe` into `NfcQuickCheck.cs`, `DecompositionData` and `CombiningClassData` into `CanonicalForm.cs`, fill both "Generated on" notes (OS, .NET version, and the Unicode version of the host's ICU — on Windows the OS-shipped ICU, e.g. `Windows 11 24H2 / .NET 10.0.x / ICU 72, Unicode 15.0`), rerun: green. Sanity: `No` ≈ 1 120 code points plus the refused noncharacters (≈ 66: U+FDD0–U+FDEF and U+nFFFE/U+nFFFF of every plane), `Maybe` ≈ 110, decompositions ≈ 2 060 entries (≈ 8 500 ints, ≈ 34 KB in the binary), combining classes ≈ 900–950 code points in ≈ 55 ranks (UCD has 56 distinct non-zero classes in Unicode 15). If the counts are wildly off, the host's `Normalize` is not working — do not paste. The derivation takes a few seconds (two `Normalize` calls per scalar, six more per mark); `Decompose_agrees_with_the_host_normalizer…` then compares every scalar and 20 000 random three-mark sequences with the host's NFD.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~UnicodeTableTests|FullyQualifiedName~BundleKeyValidatorTests"`
Expected: all green, the three `SkippableFact`s **not skipped** on the dev box (the test host has ICU). Note the duration of `Large_listings_whose_condemned_prefixes_grow_with_them_validate_in_one_pass` (under a second; r7's scan took 26 s and 13 s on the two halves of this shape).

- [ ] **Step 9: Commit**

```bash
git add src/OKF4net/Providers/BundleKeyConstraints.cs src/OKF4net/Providers/NfcQuickCheck.cs src/OKF4net/Providers/CanonicalForm.cs src/OKF4net/Providers/BundleKeyValidator.cs tests/OKF4net.Tests/Providers/UnicodeTableTests.cs tests/OKF4net.Tests/Providers/BundleKeyValidatorTests.cs
git commit -F - <<'MSG'
feat(providers): native constraints, Strict/Native policy, NFC quick check and tree validation

NFC is decided by UAX #15 quick-check tables plus a canonical-ordering check,
all derived from ICU and guarded by an exhaustive cross-check test
(noncharacters the normalizer refuses are classified No), never by
string.Normalize, which is a no-op under invariant globalization. Sibling
names are compared in canonical decomposition (table-driven NFD, Hangul by
arithmetic) where the storage folds Unicode forms, and per code point with
ToUpperInvariant, the way an NTFS upcase table does, where it folds case.
Per-level collisions, file/directory conflicts and the Windows reserved
names are checked on the whole listing in one pass, and the condemned
prefixes are reported.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 3: The contract — `IBundleProvider`, its value types, exceptions, and the read-only decorator

**Files:**
- Create: `src/OKF4net/Providers/IBundleProvider.cs`
- Create: `src/OKF4net/Providers/BundleObject.cs`
- Create: `src/OKF4net/Providers/BundleProviderExceptions.cs`
- Create: `src/OKF4net/Providers/BundleParseError.cs`
- Create: `src/OKF4net/Providers/ReadOnlyBundleProvider.cs`
- Test: `tests/OKF4net.Tests/Providers/ReadOnlyBundleProviderTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1), `BundleKeyConstraints` (Task 2).
- Produces (exact shapes below): `IBundleProvider`, `BundleProviderCapabilities`, `BundleLimits`, `BundleListing`, `BundleObject`, `BundleObjectVersion`, `BundleVersion`, `BundleWriteCondition`/`BundleWriteConditionKind`, `BundleProviderException` and its five subclasses, `BundleObjectTooLargeException : IOException`, `BundleParseError`, `ReadOnlyBundleProvider`, `BundleProviderExtensions.AsReadOnly`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ReadOnlyBundleProviderTests
{
    /// <summary>A writable in-memory provider that records every write call; the decorator must never reach it.</summary>
    private sealed class RecordingProvider : IBundleProvider
    {
        public List<string> Calls { get; } = [];
        public string Id => "recording";
        public string Location => "memory://recording";
        public BundleProviderCapabilities Capabilities => new(CanWrite: true);
        public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.FileDirectoryConflict;
        public BundleVersion GetVersion(BundleLimits limits) => new("v1");
        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => [new BundleListing("a.md", 3, null)];
        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) =>
            new(new MemoryStream(Encoding.UTF8.GetBytes("abc")), new BundleObjectVersion("sha"), 3);
        public bool Exists(BundleKey key) => key.ToString() == "a.md";
        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
        {
            Calls.Add($"write {key}");
            return new BundleObjectVersion("sha2");
        }
        public void Delete(BundleKey key, BundleWriteCondition condition) => Calls.Add($"delete {key}");
    }

    [Fact]
    public void Decorator_declares_no_write_and_refuses_before_touching_the_inner_provider()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(BundleKey.Parse("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(BundleKey.Parse("a.md"), BundleWriteCondition.None));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Decorator_delegates_every_read_member()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.Equal("recording", ro.Id);
        Assert.Equal("memory://recording", ro.Location);
        Assert.Equal(BundleKeyConstraints.FileDirectoryConflict, ro.NativeConstraints);
        Assert.Equal(new BundleVersion("v1"), ro.GetVersion(BundleLimits.Default));
        Assert.Single(ro.Enumerate(null, BundleLimits.Default));
        Assert.True(ro.Exists(BundleKey.Parse("a.md")));
        using var obj = ro.Read(BundleKey.Parse("a.md"));
        Assert.Equal(new BundleObjectVersion("sha"), obj.Version);
    }

    [Fact]
    public void AsReadOnly_is_idempotent()
    {
        var ro = new RecordingProvider().AsReadOnly();
        Assert.Same(ro, ro.AsReadOnly());
    }

    [Fact]
    public void Default_limits_match_the_spec()
    {
        var l = BundleLimits.Default;
        Assert.Equal(16L * 1024 * 1024, l.MaxObjectBytes);
        Assert.Equal(1_000_000, l.MaxEntries);
        Assert.Equal(64, l.MaxDepth);
        Assert.Equal(1024, l.MaxKeyBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), l.RequestTimeout);
        Assert.Equal(16, l.MaxConcurrentRequests);
    }

    [Fact]
    public void Write_conditions_and_versions_are_value_types_compared_ordinally()
    {
        var v = new BundleObjectVersion("abc");
        Assert.Equal(BundleWriteConditionKind.IfMatch, BundleWriteCondition.IfMatch(v).Kind);
        Assert.Equal(v, BundleWriteCondition.IfMatch(v).Expected);
        Assert.Null(BundleWriteCondition.None.Expected);
        Assert.Equal(BundleWriteConditionKind.MustNotExist, BundleWriteCondition.MustNotExist.Kind);
        Assert.NotEqual(new BundleObjectVersion("ABC"), v);
    }

    [Fact]
    public void Too_large_is_an_IOException_so_existing_handlers_treat_it_as_unreadable()
    {
        var e = new BundleObjectTooLargeException(BundleKey.Parse("a.md"), 17, 16);
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Contains("a.md", e.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReadOnlyBundleProviderTests"`
Expected: build error — none of the contract types exist.

- [ ] **Step 3: Implement the value types**

`src/OKF4net/Providers/BundleObject.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>An opaque version token for one object; only the provider knows how it is built. Compared ordinally.</summary>
public readonly record struct BundleObjectVersion(string Token);

/// <summary>An opaque version token for a whole bundle, cheap to obtain; drives refresh decisions. Compared ordinally.</summary>
public readonly record struct BundleVersion(string Token);

/// <summary>One entry of a provider listing: the raw name (relative to the root, <c>/</c>-separated, not yet validated), its length and version when the backend gives them for free.</summary>
public sealed record BundleListing(string RawName, long? Length, BundleObjectVersion? Version);

/// <summary>An object read from a provider: its content stream (owned by this instance) and its version. The core reads the stream counting bytes (spec §6).</summary>
public sealed class BundleObject : IDisposable
{
    /// <summary>Creates an object over <paramref name="content"/>; disposing the object disposes the stream.</summary>
    public BundleObject(Stream content, BundleObjectVersion version, long? length)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Version = version;
        Length = length;
    }

    /// <summary>The content, positioned at its start.</summary>
    public Stream Content { get; }

    /// <summary>The object's version at the time of the read.</summary>
    public BundleObjectVersion Version { get; }

    /// <summary>The length in bytes when the provider knows it before reading.</summary>
    public long? Length { get; }

    /// <inheritdoc/>
    public void Dispose() => Content.Dispose();
}

/// <summary>How a write or delete is conditioned (spec §4).</summary>
public enum BundleWriteConditionKind
{
    /// <summary>Unconditional.</summary>
    None,

    /// <summary>Succeeds only if no object exists under the key (a create).</summary>
    MustNotExist,

    /// <summary>Succeeds only if the current version equals <see cref="BundleWriteCondition.Expected"/>.</summary>
    IfMatch,
}

/// <summary>The condition a provider checks atomically with a write or delete (spec §4).</summary>
public readonly record struct BundleWriteCondition(BundleWriteConditionKind Kind, BundleObjectVersion? Expected)
{
    /// <summary>Unconditional.</summary>
    public static BundleWriteCondition None => new(BundleWriteConditionKind.None, null);

    /// <summary>Create only.</summary>
    public static BundleWriteCondition MustNotExist => new(BundleWriteConditionKind.MustNotExist, null);

    /// <summary>Replace only if the current version is <paramref name="expected"/>.</summary>
    public static BundleWriteCondition IfMatch(BundleObjectVersion expected) => new(BundleWriteConditionKind.IfMatch, expected);
}

/// <summary>What a provider can do beyond reading (spec §3): reading is never optional.</summary>
public sealed record BundleProviderCapabilities(bool CanWrite);

/// <summary>The bounds of spec §6. The core enforces sizes, counts and depth on what it receives; a provider enforces the same on its own walks, and the network bounds on its requests.</summary>
public sealed record BundleLimits
{
    /// <summary>The spec's defaults.</summary>
    public static BundleLimits Default { get; } = new();

    /// <summary>Largest object the core reads, in bytes (16 MiB).</summary>
    public long MaxObjectBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Largest listing, every entry counted whatever its name (1 000 000).</summary>
    public int MaxEntries { get; init; } = 1_000_000;

    /// <summary>Deepest key, in segments (64).</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Longest key, in UTF-8 bytes (1 024; also <see cref="BundleKey.MaxUtf8Bytes"/>).</summary>
    public int MaxKeyBytes { get; init; } = BundleKey.MaxUtf8Bytes;

    /// <summary>Per-request timeout for network providers (30 s); unused by the filesystem.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent requests for network providers (16); unused by the filesystem.</summary>
    public int MaxConcurrentRequests { get; init; } = 16;
}
```

`src/OKF4net/Providers/IBundleProvider.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A storage behind a bundle (spec §1): a key space and the bytes under each key.
/// A provider knows nothing of concepts, links or frontmatter; it lists raw names,
/// reads and writes bytes under keys the core has already validated, and declares
/// what it can do. There are no directories: a prefix exists only while an object
/// lives under it. Reading is mandatory; writing is a declared capability.
/// </summary>
public interface IBundleProvider
{
    /// <summary>A stable, non-secret identity: diagnostics, catalog de-duplication, lock key for non-filesystem providers.</summary>
    string Id { get; }

    /// <summary>A displayable, non-secret location (the root as given, for the filesystem).</summary>
    string Location { get; }

    /// <summary>What this provider can do beyond reading.</summary>
    BundleProviderCapabilities Capabilities { get; }

    /// <summary>The aliasing rules of this storage (spec §2, layer 2); never disabled by any policy.</summary>
    BundleKeyConstraints NativeConstraints { get; }

    /// <summary>A cheap token for the whole bundle; its internal walk respects <paramref name="limits"/>.</summary>
    BundleVersion GetVersion(BundleLimits limits);

    /// <summary>Lists raw names under the root, or under <paramref name="prefix"/> at a segment boundary (<c>a</c> covers <c>a/…</c>, never <c>ab/…</c>). The core validates them; the provider does not.</summary>
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits);

    /// <summary>Reads one object. With <paramref name="expected"/>, succeeds only if the current version matches, else throws <see cref="BundleVersionMismatchException"/>.</summary>
    /// <exception cref="BundleObjectNotFoundException">No object under the key.</exception>
    BundleObject Read(BundleKey key, BundleObjectVersion? expected = null);

    /// <summary>Whether an object exists under the key, matching it exactly (case and Unicode form).</summary>
    bool Exists(BundleKey key);

    /// <summary>Writes an object atomically if <paramref name="condition"/> holds; returns the new version.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>Deletes an object if <paramref name="condition"/> holds.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
}
```

`src/OKF4net/Providers/BundleProviderExceptions.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Base of the provider contract's exceptions (spec §1). Messages are library-authored and name keys, never host paths or credentials.</summary>
public abstract class BundleProviderException(string message) : OkfException(message)
{
}

/// <summary>A write or delete was attempted on a provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> (spec §3). Nothing was attempted. The message never carries the provider id (for the filesystem it is a host path, §7); it is on <see cref="ProviderId"/>.</summary>
public sealed class BundleReadOnlyException(string providerId)
    : BundleProviderException("the provider is read-only; the write was refused before being attempted")
{
    /// <summary>The refusing provider's <see cref="IBundleProvider.Id"/>, for the host's log.</summary>
    public string ProviderId { get; } = providerId;
}

/// <summary>A write or delete condition did not hold (spec §4).</summary>
public sealed class BundleWriteConflictException(BundleKey key)
    : BundleProviderException($"'{key}' changed since it was read; the write was refused")
{
    /// <summary>The key whose condition failed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>A read with an expected version found a different one (pinning, spec §5.4).</summary>
public sealed class BundleVersionMismatchException(BundleKey key)
    : BundleProviderException($"'{key}' is no longer the version that was observed")
{
    /// <summary>The key whose version changed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>No object under the key.</summary>
public sealed class BundleObjectNotFoundException(BundleKey key)
    : BundleProviderException($"no object under '{key}'")
{
    /// <summary>The missing key.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>
/// An object exceeds <see cref="BundleLimits.MaxObjectBytes"/> (spec §6). Derives from
/// <see cref="IOException"/> so every existing handler that treats an I/O failure as
/// "unreadable" classifies it correctly without change (spec, point to validate 6).
/// </summary>
public sealed class BundleObjectTooLargeException(BundleKey key, long observedBytes, long maxBytes)
    : IOException($"'{key}' is larger than the {maxBytes}-byte bound ({observedBytes} bytes read before stopping)")
{
    /// <summary>The offending key.</summary>
    public BundleKey Key { get; } = key;
}
```

`src/OKF4net/Providers/BundleParseError.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// One problem met while loading a bundle (spec §2): the raw name as the provider
/// listed it, the validated key when the name passed validation (<see langword="null"/>
/// when it is precisely the name that was rejected), and the reason.
/// </summary>
public sealed record BundleParseError(string RawName, BundleKey? Key, string Error);
```

`src/OKF4net/Providers/ReadOnlyBundleProvider.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Downgrades a provider to read-only (spec §3): every read delegates, every write throws <see cref="BundleReadOnlyException"/> before reaching the inner provider.</summary>
public sealed class ReadOnlyBundleProvider : IBundleProvider
{
    private readonly IBundleProvider _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public ReadOnlyBundleProvider(IBundleProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public string Id => _inner.Id;

    /// <inheritdoc/>
    public string Location => _inner.Location;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => _inner.NativeConstraints;

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits) => _inner.GetVersion(limits);

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => _inner.Enumerate(prefix, limits);

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => _inner.Read(key, expected);

    /// <inheritdoc/>
    public bool Exists(BundleKey key) => _inner.Exists(key);

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);
}

/// <summary>Extension methods on <see cref="IBundleProvider"/>.</summary>
public static class BundleProviderExtensions
{
    /// <summary>Returns a read-only view of <paramref name="provider"/>; a provider that already is one is returned as is. A host can downgrade, never upgrade.</summary>
    public static IBundleProvider AsReadOnly(this IBundleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider is ReadOnlyBundleProvider ? provider : new ReadOnlyBundleProvider(provider);
    }
}
```

Convert the five new source files and the test file to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReadOnlyBundleProviderTests"`
Expected: 6 tests green. Then `dotnet build OKF4net.sln`: no warning (every public member documented).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/IBundleProvider.cs src/OKF4net/Providers/BundleObject.cs src/OKF4net/Providers/BundleProviderExceptions.cs src/OKF4net/Providers/BundleParseError.cs src/OKF4net/Providers/ReadOnlyBundleProvider.cs tests/OKF4net.Tests/Providers/ReadOnlyBundleProviderTests.cs
git commit -F - <<'MSG'
feat(providers): the IBundleProvider contract, its value types and the read-only decorator

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 4: `FileSystemBundleProvider` — the read side, with its guards

**Files:**
- Create: `src/OKF4net/Providers/FileSystemBundleProvider.cs`
- Create: `src/OKF4net/Providers/BundleDisplayPath.cs`
- Modify: `src/OKF4net/Providers/BundleProviderExceptions.cs` (two more exceptions)
- Test: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs`

**Interfaces:**
- Consumes: the contract (Task 3); `OKF4net.Internal.ReparsePoints` (`CanonicalizeRoot`, `IsWithin(root, path, comparison)`, `IsReparsePoint`, `IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor(root, path, comparison)`); `TempDir` (`Path`, `Write`, `TryCreateFileSymlink`, `TryCreateDirectorySymlink`, `TryCreateJunctionToExternalDir`).
- Produces:
  - `public sealed class FileSystemBundleProvider : IBundleProvider` with ctor `(string root, string? confinementBoundary = null, BundleKeyConstraints additionalConstraints = BundleKeyConstraints.None, BundleLimits? limits = null)`, properties `string Root` (as given), `string? ConfinementBoundary` (canonical), `bool MatchesCaseExactly`, plus the interface. `Capabilities.CanWrite` is **false** in this lot. `NativeConstraints` = the OS set **OR** `additionalConstraints` (decision 9). Exact-case matching is on when the OS folds case **or** `NativeConstraints` carries `CaseInsensitiveCollisions`/`UnicodeFormCollisions` (decision 15).
  - `public static class BundleDisplayPath` with `string Compose(string location, BundleKey key)` = `Path.Combine([location, ..key.Segments])`.
  - Exceptions added: `BundleAccessRefusedException : BundleProviderException` with `BundleKey? Key` and `string Detail`; `public enum BundleBound { Entries, Depth, KeyBytes }` and `BundleLimitExceededException : BundleProviderException` with `BundleBound Bound`, `long Limit` and `static string Describe(BundleBound)` (decision 16).
- **`Enumerate` is eager** (decision 12): the walk runs to completion inside the call and the result is a materialised `List<BundleListing>`; there is no deferred storage access for a caller's interleaved filesystem change to exploit. Every directory — the root, a prefix, every subdirectory — passes the STRICT guard before it is listed; the walk is lenient only in that a reparse-point *entry* is skipped rather than followed (the one lenient site this lot adds); every inspected entry is counted before the skip and before sorting; the confinement boundary is re-checked before every storage access.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class FileSystemBundleProviderTests
{
    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static BundleKey K(string s) => BundleKey.Parse(s);

    [Fact]
    public void Enumerate_lists_every_file_relative_with_slashes_in_ordinal_order_depth_first_and_is_eager()
    {
        using var tmp = new TempDir();
        tmp.Write("b.md", "b");
        tmp.Write("a/z.txt", "z");
        tmp.Write("a/B.md", "B");
        tmp.Write("a/a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        var listing = p.Enumerate(null, BundleLimits.Default);
        Assert.IsAssignableFrom<IReadOnlyList<BundleListing>>(listing); // materialised, not deferred
        Assert.Equal(["a/B.md", "a/a.md", "a/z.txt", "b.md"], listing.Select(l => l.RawName)); // 'B' < 'a' ordinally; non-.md files are listed too
        Assert.All(listing, l => Assert.Null(l.Version)); // the filesystem gives no cheap version
        Assert.Equal(1, listing.Single(l => l.RawName == "b.md").Length);
    }

    [Fact]
    public void Enumerate_prefix_is_segment_bounded()
    {
        using var tmp = new TempDir();
        tmp.Write("a/x.md", "x");
        tmp.Write("ab/y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["a/x.md"], p.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName));
        Assert.Empty(p.Enumerate(K("nope"), BundleLimits.Default));
    }

    [Fact]
    public void Enumerate_counts_every_entry_against_the_bound_and_enforces_depth()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        tmp.Write("y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        // Entries inspected: d (directory), d/x.md, y.md = 3.
        var ex = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));
        Assert.Equal(BundleBound.Entries, ex.Bound);
        Assert.Equal(2, p.Enumerate(null, new BundleLimits { MaxEntries = 3 }).Count()); // 2 files listed, 3 entries counted
        tmp.Write("l1/l2/l3/deep.md", "d");
        var depth = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxDepth = 3 })); // l1/l2/l3/deep.md has 4 segments
        Assert.Equal(BundleBound.Depth, depth.Bound);
    }

    [Fact]
    public void A_directory_whose_name_is_no_valid_key_is_walked_not_fatal()
    {
        using var tmp = new TempDir();
        tmp.Write("bad\u0085dir/x.md", "x"); // U+0085 is a layer-1 control character: the KEY is invalid, the DIRECTORY exists
        tmp.Write("ok.md", "o");
        var p = new FileSystemBundleProvider(tmp.Path);
        var names = p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName).ToList();
        Assert.Equal(["bad\u0085dir/x.md", "ok.md"], names); // raw names, not keys: the loader rejects the first with a diagnostic (Task 6)
    }

    [SkippableFact]
    public void Enumerate_counts_skipped_reparse_points_too()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateFileSymlink("l1.md", "real.md") && tmp.TryCreateFileSymlink("l2.md", "real.md"), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));
    }

    [SkippableFact]
    public void Enumerate_skips_a_symlinked_entry_but_read_refuses_it_strictly()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateFileSymlink("link.md", "real.md"), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("link.md")));
        Assert.False(p.Exists(K("link.md")));
    }

    [SkippableFact]
    public void A_junction_prefix_is_refused_and_a_file_under_it_is_unreadable()
    {
        using var external = new TempDir();
        external.Write("secret.md", "s");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("jump", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("jump"), BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("jump/secret.md")));
        // Present and absent names under the junction are indistinguishable: both refused
        // before the junction is ever listed (fourth review).
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("jump/absent.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("jump/absent"), BundleLimits.Default));
        Assert.False(p.Exists(K("jump/secret.md")));
        Assert.False(p.Exists(K("jump/absent.md")));
        Assert.Empty(p.Enumerate(null, BundleLimits.Default)); // the walk skips the junction entry and lists nothing under it
    }

    [SkippableFact]
    public void An_enumeration_taken_before_a_prefix_or_root_is_replaced_never_sees_the_replacement()
    {
        using var external = new TempDir();
        external.Write("secret.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("src/p/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);

        var byPrefix = p.Enumerate(K("p"), BundleLimits.Default); // eager: already complete
        Directory.Delete(Path.Combine(root, "p"), recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("src", "p"), external.Path), "junctions unavailable"); // the helper hands the path to mklink as is: native separator
        Assert.Equal(["p/a.md"], byPrefix.Select(l => l.RawName)); // what was there, not what is there now
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("p"), BundleLimits.Default)); // a fresh call sees the junction and refuses

        var whole = p.Enumerate(null, BundleLimits.Default);
        Directory.Delete(Path.Combine(root, "p")); // the junction first, non-recursively: a recursive delete refuses to traverse it
        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.DoesNotContain(whole, l => l.RawName.Contains("secret"));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("secret.md")));
    }

    [Fact]
    public void Read_returns_the_bytes_and_a_sha256_version_and_honours_expected()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "hello");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var obj = p.Read(K("a.md"));
        Assert.Equal(new BundleObjectVersion(Sha256Hex("hello")), obj.Version);
        Assert.Equal(5, obj.Length);
        using var reader = new StreamReader(obj.Content, Encoding.UTF8);
        Assert.Equal("hello", reader.ReadToEnd());
        Assert.Throws<BundleVersionMismatchException>(() => p.Read(K("a.md"), new BundleObjectVersion("other")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("missing.md")));
        using var pinned = p.Read(K("a.md"), new BundleObjectVersion(Sha256Hex("hello")));
        Assert.Equal(5, pinned.Length);
    }

    [Fact]
    public void Read_stops_at_the_object_bound_before_hashing()
    {
        using var tmp = new TempDir();
        tmp.Write("big.md", new string('x', 11));
        var p = new FileSystemBundleProvider(tmp.Path, limits: new BundleLimits { MaxObjectBytes = 10 });
        var ex = Assert.Throws<BundleObjectTooLargeException>(() => p.Read(K("big.md")));
        Assert.IsAssignableFrom<IOException>(ex);
        tmp.Write("ok.md", new string('x', 10));
        using var ok = p.Read(K("ok.md"));
        Assert.Equal(10, ok.Length);
    }

    [SkippableFact]
    public void Keys_match_exactly_in_case_on_a_case_insensitive_volume()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "needs a case-insensitive default volume");
        using var tmp = new TempDir();
        tmp.Write("References/A.sql", "select 1");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.MatchesCaseExactly);
        Assert.True(p.Exists(K("References/A.sql")));
        Assert.False(p.Exists(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("References/a.sql")));
        using var exact = p.Read(K("References/A.sql"));
        Assert.Equal(8, exact.Length);
        Assert.Empty(p.Enumerate(K("references"), BundleLimits.Default));
    }

    [Fact]
    public void Constraints_can_be_added_never_removed_and_a_folding_flag_turns_exact_case_on_anywhere()
    {
        using var tmp = new TempDir();
        tmp.Write("a/UP.sql", "x");
        var p = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.None);
        Assert.Equal(BundleKeyConstraintSets.ForCurrentOperatingSystem(), p.NativeConstraints);
        var folding = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.CaseInsensitiveCollisions);
        Assert.True(folding.MatchesCaseExactly); // on Linux too (a case-insensitive mount, decision 15)
        Assert.True(folding.NativeConstraints.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions));
        Assert.True(folding.NativeConstraints.HasFlag(BundleKeyConstraintSets.ForCurrentOperatingSystem()));
        Assert.True(folding.Exists(K("a/UP.sql")));
        Assert.False(folding.Exists(K("a/up.sql")));
    }

    [Fact]
    public void GetVersion_changes_with_content_and_is_stable_otherwise()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.md", "one");
        var p = new FileSystemBundleProvider(tmp.Path);
        var v1 = p.GetVersion(BundleLimits.Default);
        Assert.Equal(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "two!"); // different length
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "one");
        File.SetLastWriteTimeUtc(file, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default)); // same bytes, different mtime: a listing digest, not a content hash
        Assert.Throws<BundleLimitExceededException>(() => p.GetVersion(new BundleLimits { MaxEntries = 0 }));
    }

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

    [Fact]
    public void Id_is_the_canonical_root_and_location_is_the_root_as_given()
    {
        using var tmp = new TempDir();
        var withSlash = tmp.Path + Path.DirectorySeparatorChar;
        var a = new FileSystemBundleProvider(tmp.Path);
        var b = new FileSystemBundleProvider(withSlash);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(withSlash, b.Location);
        Assert.Equal(Path.Combine(tmp.Path, "tables", "x.md"), BundleDisplayPath.Compose(tmp.Path, K("tables/x.md")));
    }

    [Fact]
    public void A_boundary_outside_the_root_refuses_every_access()
    {
        using var tmp = new TempDir();
        tmp.Write("inside/a.md", "a");
        tmp.Write("elsewhere/b.md", "b");
        var p = new FileSystemBundleProvider(Path.Combine(tmp.Path, "elsewhere"), confinementBoundary: Path.Combine(tmp.Path, "inside"));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("b.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("b.md")));
    }

    [SkippableFact]
    public void A_root_turned_into_a_junction_after_construction_is_refused_on_the_next_access_when_a_boundary_is_given()
    {
        using var external = new TempDir();
        external.Write("a.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("src/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);
        using (var before = p.Read(K("a.md")))
        {
            Assert.Equal(6, before.Length);
        }

        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a.md")));
    }

    [SkippableFact]
    public void A_symlinked_root_loads_without_a_boundary_but_not_with_one()
    {
        using var tmp = new TempDir();
        tmp.Write("target/a.md", "a");
        Skip.IfNot(tmp.TryCreateDirectorySymlink("link", "target"), "directory symlinks unavailable");
        var link = Path.Combine(tmp.Path, "link");
        var free = new FileSystemBundleProvider(link);
        Assert.Equal(["a.md"], free.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        using var obj = free.Read(K("a.md"));
        Assert.Equal(1, obj.Length);
        var bounded = new FileSystemBundleProvider(link, confinementBoundary: tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => bounded.Read(K("a.md")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderTests"`
Expected: build error — `FileSystemBundleProvider` does not exist.

- [ ] **Step 3: Add the two exceptions** to `src/OKF4net/Providers/BundleProviderExceptions.cs`

Messages are fixed text plus the key: a provider's `Detail` is for the host's log, never for the message (§7).

```csharp
/// <summary>A STRICT path guard or the confinement boundary refused an access (spec §8). The message names the key only; <see cref="Detail"/> is the provider's own explanation, for the host's log.</summary>
public sealed class BundleAccessRefusedException(BundleKey? key, string detail)
    : BundleProviderException(key is null ? "access to the bundle was refused by its provider" : $"access to '{key}' was refused by the provider")
{
    /// <summary>The key, when the refusal concerned one.</summary>
    public BundleKey? Key { get; } = key;

    /// <summary>Why, in the provider's words — may name storage internals; log it, never show it to a model.</summary>
    public string Detail { get; } = detail;
}

/// <summary>The bounds of spec §6 a walk or a load can exceed — a closed set, so a message built from it carries nothing a provider wrote (decision 16).</summary>
public enum BundleBound
{
    /// <summary><see cref="BundleLimits.MaxEntries"/>.</summary>
    Entries,

    /// <summary><see cref="BundleLimits.MaxDepth"/>.</summary>
    Depth,

    /// <summary><see cref="BundleLimits.MaxKeyBytes"/>.</summary>
    KeyBytes,
}

/// <summary>A walk or a load exceeded one of its <see cref="BundleLimits"/> (spec §6). The message names the bound by its category only.</summary>
public sealed class BundleLimitExceededException(BundleBound bound, long limit)
    : BundleProviderException($"the bundle exceeds the {Describe(bound)} bound ({limit})")
{
    /// <summary>Which bound was exceeded.</summary>
    public BundleBound Bound { get; } = bound;

    /// <summary>The bound's value.</summary>
    public long Limit { get; } = limit;

    /// <summary>The fixed word for a bound in messages: <c>entries</c>, <c>depth</c>, <c>key-length</c>.</summary>
    public static string Describe(BundleBound bound) => bound switch
    {
        BundleBound.Entries => "entries",
        BundleBound.Depth => "depth",
        BundleBound.KeyBytes => "key-length",
        _ => "unknown",
    };
}
```

- [ ] **Step 4: Implement `BundleDisplayPath`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// The display rule of spec §8: a provider's <see cref="IBundleProvider.Location"/>
/// followed by the key's segments joined with the OS separator — exactly what
/// <c>Path.Combine(root, …)</c> produced before the seam, so the CLI's bytes do not move.
/// </summary>
public static class BundleDisplayPath
{
    /// <summary>Composes the displayable path of <paramref name="key"/> under <paramref name="location"/>.</summary>
    public static string Compose(string location, BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(key);
        var parts = new string[key.Segments.Count + 1];
        parts[0] = location;
        for (var i = 0; i < key.Segments.Count; i++)
        {
            parts[i + 1] = key.Segments[i];
        }

        return Path.Combine(parts);
    }
}
```

- [ ] **Step 5: Implement the provider**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The default provider (spec §8): a bundle as a directory tree. Read-only in this
/// lot (<see cref="Capabilities"/> says so; lot B adds the atomic write).
/// <see cref="Enumerate"/> is eager — the walk completes inside the call — so no
/// deferred storage access exists; every directory passes the STRICT guard before it
/// is listed, a reparse-point entry is skipped (never followed), every inspected entry
/// is counted before the skip and before sorting, and with a confinement boundary the
/// chain boundary→root is re-checked before every storage access. Versions are SHA-256
/// of content; the bundle token is a digest of the listing (name, length, mtime),
/// deliberately weak and never used for pinning.
/// </summary>
public sealed class FileSystemBundleProvider : IBundleProvider
{
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    private readonly string _fullRoot;
    private readonly BundleLimits _limits;

    /// <summary>
    /// Creates a provider over <paramref name="root"/>. <paramref name="confinementBoundary"/>
    /// (a catalog root) makes the root itself subject to the STRICT guards on every access;
    /// without it the root is trusted host configuration, as today.
    /// <paramref name="additionalConstraints"/> is OR-ed into the OS's native set and can only
    /// add rules (a folding flag also turns exact-case matching on, for a case-insensitive
    /// mount on Linux); <paramref name="limits"/> bounds <see cref="Read"/>, which the
    /// interface gives no per-call limits.
    /// </summary>
    public FileSystemBundleProvider(string root, string? confinementBoundary = null, BundleKeyConstraints additionalConstraints = BundleKeyConstraints.None, BundleLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        _fullRoot = ReparsePoints.CanonicalizeRoot(root);
        ConfinementBoundary = confinementBoundary is null ? null : ReparsePoints.CanonicalizeRoot(confinementBoundary);
        NativeConstraints = BundleKeyConstraintSets.ForCurrentOperatingSystem() | additionalConstraints;
        _limits = limits ?? BundleLimits.Default;
        MatchesCaseExactly = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || NativeConstraints.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions)
            || NativeConstraints.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);
    }

    /// <summary>The root as given (also <see cref="Location"/>).</summary>
    public string Root { get; }

    /// <summary>The canonical boundary every access is re-checked against, or <see langword="null"/>.</summary>
    public string? ConfinementBoundary { get; }

    /// <summary>Whether keys are matched segment by segment against the real entry names (spec §2's correspondence rule). On by the OS or by a folding flag; never off by a caller.</summary>
    public bool MatchesCaseExactly { get; }

    /// <inheritdoc/>
    public string Id => _fullRoot;

    /// <inheritdoc/>
    public string Location => Root;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints { get; }

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var sb = new StringBuilder();
        var counter = new Counter();
        Walk(_fullRoot, string.Empty, 0, limits, counter, entry =>
        {
            if (entry.IsDirectory)
            {
                return;
            }

            long length;
            long mtime;
            try
            {
                var info = new FileInfo(entry.FullPath);
                length = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = -1;
                mtime = -1;
            }

            sb.Append(entry.RawName).Append('\0').Append(length).Append('\0').Append(mtime).Append('\n');
        });

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var start = _fullRoot;
        if (prefix is not null)
        {
            var resolved = ResolveExact(prefix);
            if (resolved is null)
            {
                return Array.Empty<BundleListing>();
            }

            GuardStrict(prefix, resolved); // a junction prefix is refused, not walked
            if (!Directory.Exists(resolved))
            {
                return Array.Empty<BundleListing>();
            }

            start = resolved;
        }

        var result = new List<BundleListing>();
        Walk(start, string.Empty, prefix?.Segments.Count ?? 0, limits, new Counter(), entry =>
        {
            if (entry.IsDirectory)
            {
                return;
            }

            long? length;
            try
            {
                length = new FileInfo(entry.FullPath).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = null;
            }

            var raw = prefix is null ? entry.RawName : prefix + "/" + entry.RawName;
            result.Add(new BundleListing(raw, length, null));
        });

        return result;
    }

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
        GuardStrict(key, path);
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

    /// <inheritdoc/>
    public bool Exists(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        string? path;
        try
        {
            path = ResolveExact(key);
        }
        catch (BundleAccessRefusedException)
        {
            return false; // a question, not an access: a refused traversal reads as "no such object"
        }

        if (path is null || !File.Exists(path))
        {
            return false;
        }

        return !ReparsePoints.IsReparsePointOrUninspectable(path)
            && !ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison);
    }

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <summary>
    /// With a boundary: the root must be under it, and neither the root nor any entry
    /// between the boundary and the root may be a reparse point or uninspectable — STRICT,
    /// at every storage access, because the ancestor walk stops <i>before</i> the root.
    /// </summary>
    private void CheckBoundary()
    {
        if (ConfinementBoundary is null)
        {
            return;
        }

        if (!ReparsePoints.IsWithin(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root is outside its confinement boundary");
        }

        if (ReparsePoints.IsReparsePointOrUninspectable(_fullRoot)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root, or a directory between the boundary and it, is a reparse point or cannot be inspected");
        }
    }

    /// <summary>STRICT: the path must be under the root and neither it nor any directory between the root and it may be a reparse point or uninspectable.</summary>
    private void GuardStrict(BundleKey? key, string path)
    {
        if (!ReparsePoints.IsWithin(_fullRoot, path, PathComparison)
            || (!string.Equals(path, _fullRoot, PathComparison) && ReparsePoints.IsReparsePointOrUninspectable(path))
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison))
        {
            throw new BundleAccessRefusedException(key, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>
    /// Maps a key to its on-disk path. With <see cref="MatchesCaseExactly"/>, each segment must
    /// match an existing entry's name <b>exactly</b> (ordinal) — spec §2's correspondence rule —
    /// else <see langword="null"/>. Otherwise the OS compares bytes already and the joined path
    /// is returned as is.
    /// </summary>
    private string? ResolveExact(BundleKey key)
    {
        var parts = new string[key.Segments.Count + 1];
        parts[0] = _fullRoot;
        for (var i = 0; i < key.Segments.Count; i++)
        {
            parts[i + 1] = key.Segments[i];
        }

        var joined = Path.Combine(parts);
        if (!MatchesCaseExactly)
        {
            return joined;
        }

        var current = _fullRoot;
        foreach (var segment in key.Segments)
        {
            // STRICT before listing a traversed directory (the root excepted: a symlinked root
            // without a boundary stays loadable, Review Focus 4). Otherwise an absent name under
            // a junction could be told apart from a present one before the read gate ever ran
            // (fourth review: NotFound vs refused).
            if (!string.Equals(current, _fullRoot, PathComparison)
                && (ReparsePoints.IsReparsePointOrUninspectable(current)
                    || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, current, PathComparison)))
            {
                throw new BundleAccessRefusedException(key, "it resolves through a reparse point or an entry that could not be inspected");
            }

            string? match = null;
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos(segment, SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                    {
                        match = entry.FullName;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    private sealed class Counter
    {
        public int Entries;
    }

    private readonly record struct WalkEntry(string FullPath, string RawName, bool IsDirectory);

    /// <summary>
    /// Depth-first, ordinal per directory, recursive (depth bounded by
    /// <see cref="BundleLimits.MaxDepth"/>), <b>not</b> an iterator: it runs to completion and
    /// hands each entry to <paramref name="visit"/>. Before listing any directory it re-checks
    /// the boundary and the directory itself STRICTly (a directory replaced by a junction
    /// since the parent was listed is refused, not entered). Every entry a listing returns is
    /// counted before the reparse-point skip and before sorting.
    /// </summary>
    private void Walk(string dir, string rel, int depth, BundleLimits limits, Counter counter, Action<WalkEntry> visit)
    {
        CheckBoundary();
        // The path alone: a directory whose relative name is no valid key (U+0085, > 1 024
        // bytes) is still a directory to guard; its entries are rejected BY NAME later, by the
        // loader, never by an exception here (the third review hit BundleKeyException).
        GuardStrict(null, dir);
        var names = new List<string>();
        foreach (var full in Directory.EnumerateFileSystemEntries(dir))
        {
            if (++counter.Entries > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            names.Add(Path.GetFileName(full));
        }

        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var full = Path.Combine(dir, name);
            if (ReparsePoints.IsReparsePoint(full))
            {
                continue; // lenient: a walk skips, the read gate refuses
            }

            if (depth + 1 > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            var raw = rel.Length == 0 ? name : rel + "/" + name;
            if (Directory.Exists(full))
            {
                visit(new WalkEntry(full, raw, true));
                Walk(full, raw, depth + 1, limits, counter, visit);
            }
            else if (File.Exists(full))
            {
                visit(new WalkEntry(full, raw, false));
            }
        }
    }

    private static byte[] ReadBounded(Stream stream, long max, BundleKey key)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new BundleObjectTooLargeException(key, total, max);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
```

Two notes for the implementer: `GuardStrict` skips the "is itself a reparse point" test for the root (a symlinked root without a boundary stays loadable, Review Focus 4; with a boundary, `CheckBoundary` refuses it); in `Walk`, `GuardStrict` receives no key — a directory's relative name may be no valid key at all (U+0085, more than 1 024 bytes) and must still be guarded as a path and walked, its entries being rejected by name by the loader; never parse it. `ReparsePoints.IsWithin`, `CanonicalizeRoot` etc. are `internal` in the same assembly. Convert every new/modified source file and the test file to CRLF.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderTests"`
Expected: green. Run it once as an account that can create symlinks and junctions (Developer Mode on Windows): **0 skipped** — the two executed reviews' junction probes are now these tests, and they only count once they run.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net/Providers/FileSystemBundleProvider.cs src/OKF4net/Providers/BundleDisplayPath.cs src/OKF4net/Providers/BundleProviderExceptions.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs
git commit -F - <<'MSG'
feat(providers): read-only filesystem provider with STRICT guards and an eager, bounded walk

Enumerate completes inside the call, so there is no deferred storage access;
every directory, prefix included, is STRICT-guarded before it is listed; every
inspected entry is counted before the reparse skip and before sorting; the
confinement boundary is re-checked before every access; exact-case matching
follows the OS or a folding flag and cannot be switched off; refusal messages
name the key only. Writes are refused and declared so; lot B adds them.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 5: `ResourceKeyResolver` — §6.2 path-valued fields resolved lexically on keys

**Files:**
- Create: `src/OKF4net/Providers/ResourceKeyResolver.cs`
- Test: `tests/OKF4net.Tests/Providers/ResourceKeyResolverTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1); `OKF4net.FrontmatterResourceClassifier.KindOf(string)` (`internal`, same assembly; returns `Url` / `BundleRelative` / `ConceptRelative`).
- Produces: `public enum ResourceKeyOutcome { Url, Unsafe, Candidate }` and `public static class ResourceKeyResolver` with `ResourceKeyOutcome Resolve(BundleKey conceptKey, string rawPath, out BundleKey? key)`. A `Candidate` is a syntactically valid key under the root whose existence has **not** been checked; Task 6 runs it through the validator and the provider.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ResourceKeyResolverTests
{
    private static readonly BundleKey Concept = BundleKey.Parse("computations/revenue.md");
    private static readonly BundleKey TopLevel = BundleKey.Parse("revenue.md");

    [Theory]
    [InlineData("https://example.test/q.sql")]
    [InlineData("s3://bucket/q.sql")]
    public void A_url_is_never_dereferenced(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Url, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("/references/a.sql", "references/a.sql")]   // bundle-relative
    [InlineData("references/a.sql", "references/a.sql")]    // bare: from the root (S6.2-1)
    [InlineData("./a.sql", "computations/a.sql")]           // concept-relative
    [InlineData("../references/a.sql", "references/a.sql")]
    [InlineData("a//./b.sql", "a/b.sql")]                   // empty and '.' segments ignored
    [InlineData("%2e%2e/x.sql", "%2e%2e/x.sql")]            // never percent-decoded
    [InlineData("/./a.sql", "a.sql")]
    public void Resolves_the_spec_table_rows_to_keys(string raw, string expected)
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse(expected), key);
    }

    [Fact]
    public void Concept_relative_from_a_top_level_concept_resolves_from_the_root()
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(TopLevel, "./a.sql", out var key));
        Assert.Equal(BundleKey.Parse("a.sql"), key);
    }

    [Theory]
    [InlineData("../../q.sql")]       // climbs above the root
    [InlineData("/../q.sql")]
    [InlineData("../")]               // resolves to the root itself
    [InlineData("/")]
    [InlineData("")]
    [InlineData("C:\\x.sql")]         // drive forms
    [InlineData("e:q.sql")]
    [InlineData("/e:q.sql")]
    [InlineData("C:/x.sql")]
    [InlineData("references\\a.sql")] // backslash → Unsafe (point to validate 7)
    [InlineData("a\0b.sql")]          // control character
    public void Unsafe_rows(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void A_bare_dot_names_the_concepts_own_prefix_not_an_escape(string raw)
    {
        // Decision 10: lexically this is the prefix `computations`, nothing escapes; the
        // caller then finds no object under it and reports Missing. From a top-level
        // concept the prefix is the root, which IS Unsafe (covered by "../" above).
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse("computations"), key);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(TopLevel, raw, out _));
    }

    [Fact]
    public void A_resolved_key_longer_than_the_bound_is_unsafe()
    {
        var raw = "/" + new string('a', 1030);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out _));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ResourceKeyResolverTests"`
Expected: build error — `ResourceKeyResolver` does not exist.

- [ ] **Step 3: Implement the resolver**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What <see cref="ResourceKeyResolver.Resolve"/> made of a §6.2 value.</summary>
public enum ResourceKeyOutcome
{
    /// <summary>An absolute URL; never dereferenced (spec §7).</summary>
    Url,

    /// <summary>The value climbs above the root, resolves to the root itself, uses a drive form or a backslash, or breaks a base key rule.</summary>
    Unsafe,

    /// <summary>A syntactically valid key under the root; existence and policy are checked by the caller.</summary>
    Candidate,
}

/// <summary>
/// Resolves a §6.2 path-valued field lexically on keys (spec §2): no OS path semantics,
/// no percent-decoding, no filesystem access. <c>/x</c> and a bare <c>x</c> resolve from
/// the root (this implementation's reading of §6.2, docs/spec-conformance S6.2-1);
/// <c>./x</c> and <c>../x</c> from the concept's own prefix.
/// </summary>
public static class ResourceKeyResolver
{
    /// <summary>Resolves <paramref name="rawPath"/> relative to <paramref name="conceptKey"/>'s prefix or the root.</summary>
    public static ResourceKeyOutcome Resolve(BundleKey conceptKey, string rawPath, out BundleKey? key)
    {
        ArgumentNullException.ThrowIfNull(conceptKey);
        ArgumentNullException.ThrowIfNull(rawPath);
        key = null;

        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
        {
            return ResourceKeyOutcome.Url;
        }

        if (rawPath.Contains('\\'))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        var relative = rawPath.TrimStart('/');
        if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            // A drive-relative or drive-absolute form (e:q.sql, C:/x): never a bundle path.
            return ResourceKeyOutcome.Unsafe;
        }

        var segments = new List<string>();
        if (kind == FrontmatterResourceKind.ConceptRelative && conceptKey.Parent is { } parent)
        {
            segments.AddRange(parent.Segments);
        }

        foreach (var segment in relative.Split('/'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        return ResourceKeyOutcome.Unsafe;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        if (segments.Count == 0)
        {
            return ResourceKeyOutcome.Unsafe;
        }

        if (!BundleKey.TryParse(string.Join('/', segments), out var parsed, out _))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        key = parsed;
        return ResourceKeyOutcome.Candidate;
    }
}
```

Convert both files to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ResourceKeyResolverTests"`
Expected: green (every table row).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/ResourceKeyResolver.cs tests/OKF4net.Tests/Providers/ResourceKeyResolverTests.cs
git commit -F - <<'MSG'
feat(providers): resolve §6.2 path-valued fields lexically on keys

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 6: `Bundle.Load(IBundleProvider, BundleLoadOptions?)` — the loader on the seam, and the validator through it

**Files:**
- Create: `src/OKF4net/Providers/BundleLoadOptions.cs`
- Modify: `src/OKF4net/OkfException.cs` (add a `(message, inner)` constructor) and `src/OKF4net/Bundle.cs`'s `BundleLoadException` (same)
- Modify: `src/OKF4net/ConceptId.cs` (add `ToKey()` / `FromKey(BundleKey)` next to `ToPath`/`FromPath`, which stay)
- Modify: `src/OKF4net/Bundle.cs` (`Concept` gains a non-positional `Key`; new `Load(provider)`; `Load(string)` delegates; new members; one shared key guard; `CollectMarkdown` removed)
- Modify: `src/OKF4net/Validate.cs` (`ValidateReserved` reads through the provider; `ConceptRelativeHint` and the resource existence check use `TryResolveResourceKey`)
- Modify: `tests/OKF4net.Tests/ValidateTests.cs`, `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs` (two migrations, Step 8)
- Create: `tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs` (test helper)
- Test: `tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5; `OKF4net.Internal.PathOrdering.CompareComponentWise` (splits on `/` and `\`, ordinal per segment, shorter first — applied to key texts it yields today's concept order); `OKF4net.Internal.OkfEncodings.Strict`; `DebugQuote.Quote`.
- Produces:
  - `public sealed record BundleLoadOptions { BundleLimits Limits; BundleKeyPolicy KeyPolicy = Strict; IReadOnlyList<Func<BundleKey,string?>>? ExtraKeyValidators; static Default }`.
  - `ConceptId.ToKey()` → `BundleKey`; `static ConceptId FromKey(BundleKey)` (throws `ConceptIdException` for a non-`.md` key).
  - `Concept(ConceptId Id, string Path, OkfDocument Document)` **unchanged** (constructor and `Deconstruct` intact) plus `public BundleKey? Key { get; init; }`, always set by `Bundle.Load`.
  - `OkfException(string message, Exception inner)` (protected) and `BundleLoadException(string message, Exception inner)`.
  - On `Bundle`: `static Bundle Load(IBundleProvider provider, BundleLoadOptions? options = null)`; `IBundleProvider Provider`; `string Location`; `BundleVersion Version`; `BundleLoadOptions Options`; `IReadOnlyList<BundleParseError> ParseProblems`; `IReadOnlyList<BundleKey> IndexKeys`, `LogKeys`; `IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions`; `IReadOnlySet<BundleKey> RejectedKeys`; `IReadOnlyList<BundleKey> RejectedPrefixes`; `bool IsKeyAllowed(BundleKey key, out string? reason)` — **the one guard** (policy, rejected keys, rejected prefixes, depth and key-length bounds) in front of `TryResolveResourceKey` and `ReadResourceText(BundleKey)` (decision 11); `bool TryResolveResourceKey(Concept, string, out BundleKey?, out ResourceResolutionStatus)`; `string ReadResourceText(BundleKey)`. `Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)` keep their current behaviour.
- **Error translation (§7):** every exception that leaves `Bundle` for a provider failure is built by `Translate` from the exception's **type and fields** (category, key, bound) — never from `e.Message`, which a provider may have filled with storage internals; the raw exception is the `InnerException`.

- [ ] **Step 1: Write the in-memory provider test helper**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A read-only provider over a dictionary, so loader tests can use names the host
/// OS could not create (reserved names, case-colliding siblings) and never touch the
/// disk. Native constraints default to <see cref="BundleKeyConstraints.None"/>.
/// <see cref="ObeyLimits"/> is on by default; off, it ignores every bound — the core
/// must still enforce them (spec §6). Objects can be added after a load to model a
/// change behind the bundle's back.
/// </summary>
public sealed class InMemoryBundleProvider : IBundleProvider
{
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    public InMemoryBundleProvider(BundleKeyConstraints native = BundleKeyConstraints.None, string id = "memory")
    {
        NativeConstraints = native;
        Id = id;
    }

    public bool ObeyLimits { get; init; } = true;

    private readonly HashSet<string> _listedTwice = new(StringComparer.Ordinal);

    /// <summary>Makes <see cref="Enumerate"/> list <paramref name="rawName"/> twice — a buggy backend (Review Focus 5).</summary>
    public InMemoryBundleProvider ListTwice(string rawName)
    {
        _listedTwice.Add(rawName);
        return this;
    }

    /// <summary>Thrown by every <see cref="Read"/> when set.</summary>
    public Exception? FailReadsWith { get; set; }

    public InMemoryBundleProvider Add(string rawName, string text)
    {
        _objects[rawName] = Encoding.UTF8.GetBytes(text);
        return this;
    }

    /// <summary>Makes <see cref="Read"/> of one object throw <paramref name="failure"/> instead.</summary>
    public InMemoryBundleProvider FailReadOf(string rawName, Exception failure)
    {
        _failures[rawName] = failure;
        return this;
    }

    public string Id { get; }
    public string Location => "memory://" + Id;
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);
    public BundleKeyConstraints NativeConstraints { get; }

    public BundleVersion GetVersion(BundleLimits limits)
    {
        var sb = new StringBuilder();
        foreach (var (name, bytes) in _objects)
        {
            sb.Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        var count = 0;
        foreach (var (name, bytes) in _objects)
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
            if (_listedTwice.Contains(name))
            {
                yield return new BundleListing(name, bytes.Length, null);
            }
        }
    }

    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        if (FailReadsWith is { } failure)
        {
            throw failure;
        }

        if (_failures.TryGetValue(key.ToString(), out var specific))
        {
            throw specific;
        }

        if (!_objects.TryGetValue(key.ToString(), out var bytes))
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

    public bool Exists(BundleKey key) => _objects.ContainsKey(key.ToString());

    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);

    public void Delete(BundleKey key, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleProviderLoadTests
{
    // 79 UTF-8 bytes; the "big" fixtures below are 115 and 227 — the bound used with them is 100.
    private const string Orders = "---\ntype: Table\ntitle: Orders\ndescription: One row per order.\n---\n\nSee [customers](/tables/customers.md).\n";
    private const string Customers = "---\ntype: Table\ntitle: Customers\ndescription: One row per customer.\n---\n\nBody.\n";
    private const string Broken = "---\ntype: Note\nk: *a\n---\nbody\n"; // a YAML alias: the subset rejects it → ParseErrors

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Loading_through_the_filesystem_provider_equals_loading_by_path()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n");
        tmp.Write("tables/orders.md", Orders);
        tmp.Write("tables/customers.md", Customers);
        tmp.Write("tables/log.md", "# Log\n\n## 2026-01-01\n\n- x\n");
        tmp.Write("tables/broken.md", Broken);
        var byPath = Bundle.Load(tmp.Path);
        var provider = new FileSystemBundleProvider(tmp.Path);
        var byProvider = Bundle.Load(provider);

        Assert.Equal(byPath.Concepts.Select(c => c.Id), byProvider.Concepts.Select(c => c.Id));
        Assert.Equal(byPath.Concepts.Select(c => c.Path), byProvider.Concepts.Select(c => c.Path));
        Assert.Equal(byPath.Concepts.Select(c => c.Document.Body), byProvider.Concepts.Select(c => c.Document.Body));
        Assert.Equal(byPath.IndexFiles, byProvider.IndexFiles);
        Assert.Equal(byPath.LogFiles, byProvider.LogFiles);
        Assert.Equal(byPath.ParseErrors, byProvider.ParseErrors);
        Assert.Single(byPath.ParseErrors);
        Assert.Equal(byPath.OkfVersion, byProvider.OkfVersion);
        Assert.Equal(byPath.Root, byProvider.Root);
        var orders = ConceptId.Parse("tables/orders");
        Assert.Equal(byPath.LinksFrom(orders), byProvider.LinksFrom(orders));
        Assert.Equal(byPath.Backlinks(ConceptId.Parse("tables/customers")), byProvider.Backlinks(ConceptId.Parse("tables/customers")));

        Assert.Same(provider, byProvider.Provider);
        Assert.Equal(tmp.Path, byProvider.Location);
        Assert.Equal(provider.GetVersion(BundleLimits.Default), byProvider.Version);
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byProvider.Get(orders)!.Key);
        Assert.Equal(new BundleObjectVersion(Sha(Orders)), byProvider.ObjectVersions[BundleKey.Parse("tables/orders.md")]);
        Assert.Equal([BundleKey.Parse("index.md")], byProvider.IndexKeys);
        Assert.Equal([BundleKey.Parse("tables/log.md")], byProvider.LogKeys);
        Assert.NotNull(byPath.Provider); // Load(string) sets the new members too
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byPath.Get(orders)!.Key);
    }

    [Fact]
    public void Concept_keeps_its_three_positional_parameters()
    {
        var doc = OkfDocument.Parse(Customers);
        var concept = new Concept(ConceptId.Parse("a"), "a.md", doc);
        var (id, path, document) = concept; // Deconstruct(3) still exists
        Assert.Equal(ConceptId.Parse("a"), id);
        Assert.Equal("a.md", path);
        Assert.Same(doc, document);
        Assert.Null(concept.Key);
        Assert.Equal(BundleKey.Parse("a.md"), (concept with { Key = BundleKey.Parse("a.md") }).Key);
    }

    [Fact]
    public void Strict_rejections_land_in_ParseProblems_with_the_raw_name_and_no_key()
    {
        var p = new InMemoryBundleProvider()
            .Add("con.md", Customers)
            .Add("ok.md", Customers)
            .Add("a:b.txt", "not a concept, silently skipped");
        var bundle = Bundle.Load(p);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("con.md", problem.RawName);
        Assert.Null(problem.Key);
        Assert.Contains("reserved", problem.Error);
        var projected = Assert.Single(bundle.ParseErrors);
        Assert.Contains("con.md", projected.Path);
        Assert.Equal(problem.Error, projected.Error);
        Assert.Contains(BundleKey.Parse("a:b.txt"), bundle.RejectedKeys); // remembered even though not diagnosed
    }

    [Fact]
    public void Native_policy_keeps_what_strict_rejects()
    {
        var p = new InMemoryBundleProvider().Add("A/x.md", Customers).Add("a/y.md", Customers);
        var strict = Bundle.Load(p);
        Assert.Empty(strict.Concepts);
        Assert.Equal(2, strict.ParseProblems.Count);
        Assert.Contains(BundleKey.Parse("A/x.md"), strict.RejectedKeys);
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("a")], strict.RejectedPrefixes);
        var native = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal(2, native.Concepts.Count);
        Assert.Empty(native.ParseProblems);
        Assert.Empty(native.RejectedKeys);
        Assert.Empty(native.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejected_objects_stay_rejected_for_resolution_direct_reads_the_root_index_and_later_descendants()
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("A/x.sql", "outside tree x")
            .Add("a/y.sql", "y")
            .Add("con.sql", "reserved")
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n")
            .Add("INDEX.md", "# shadow\n");
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;

        // Diagnostics follow the enumeration scope (§2): only the two .md names.
        Assert.Equal(2, bundle.ParseProblems.Count);
        Assert.Equal(5, bundle.RejectedKeys.Count); // A/x.sql, a/y.sql, con.sql, index.md, INDEX.md
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("INDEX.md"), BundleKey.Parse("a"), BundleKey.Parse("index.md")], bundle.RejectedPrefixes);

        bundle.TryResolveResourceKey(concept, "/A/x.sql", out var key, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status); // Exists would say yes; the tree said no
        Assert.Null(key);

        // Direct reads go through the same guard.
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("A/x.sql")));
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("con.sql")));

        // The root index was rejected: it declares nothing.
        Assert.Empty(bundle.IndexKeys);
        Assert.Null(bundle.OkfVersion);

        // An object created later under a condemned prefix is not rehabilitated.
        p.Add("A/new.sql", "late");
        bundle.TryResolveResourceKey(concept, "/A/new.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("A/new.sql")));

        // One created later under a clean prefix is checked live, as today.
        p.Add("c/late.sql", "fine");
        bundle.TryResolveResourceKey(concept, "./late.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
    }

    [Fact]
    public void A_third_spelling_of_a_condemned_prefix_is_still_condemned()
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("Ab/x.sql", "x")
            .Add("aB/y.sql", "y");
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.Equal([BundleKey.Parse("Ab"), BundleKey.Parse("aB")], bundle.RejectedPrefixes);

        p.Add("AB/new.sql", "third spelling");
        bundle.TryResolveResourceKey(concept, "/AB/new.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("AB/new.sql")));
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("ab/z.sql"), out var reason));
        Assert.Contains("rejected when the bundle was loaded", reason);
    }

    [Theory]
    [InlineData("Ab/x.sql", "aB/y.sql", "AB/new.sql", true)]        // every offender duplicated: the collision is structural only
    [InlineData("x", "x/y.sql", "x/new.sql", true)]                 // same for the file/directory conflict
    [InlineData("x.md/A.sql", "x.md/a.sql", "x.md/new.sql", false)] // case-doomed children: the parent `x.md` stays an object-and-prefix
    [InlineData("caf\u00e9/x.sql", "cafe\u0301/y.sql", "caf\u00e9/new.sql", false)] // canonically equivalent prefixes: the NFC spelling is condemned with the refused NFD one (decision 17)
    [InlineData("\u212b/x.sql", "\u00c5/y.sql", "\u00c5/new.sql", false)]            // ANGSTROM SIGN beside Å
    public void Prefixes_condemned_only_structurally_still_guard_late_objects(string first, string second, string late, bool duplicated)
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("x.md", "---\ntype: T\n---\n")
            .Add(first, "1")
            .Add(second, "2");
        if (duplicated)
        {
            p.ListTwice(first).ListTwice(second);
        }
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.NotEmpty(bundle.RejectedPrefixes);

        p.Add(late, "late");
        bundle.TryResolveResourceKey(concept, "/" + late, out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse(late)));
    }

    [Fact]
    public void A_host_validator_that_refuses_a_parent_does_not_rehabilitate_its_children()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux) // native: FileDirectoryConflict
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("x.md", "---\ntype: T\n---\n")
            .Add("x.md/ok.sql", "ok");
        var options = new BundleLoadOptions
        {
            KeyPolicy = BundleKeyPolicy.Native,
            ExtraKeyValidators = [k => k.ToString() == "x.md" ? "refused by the host" : null],
        };
        var bundle = Bundle.Load(p, options);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.DoesNotContain(bundle.Concepts, c => c.Id == ConceptId.Parse("x"));
        Assert.Equal([BundleKey.Parse("x.md")], bundle.RejectedPrefixes);

        bundle.TryResolveResourceKey(concept, "/x.md/ok.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("x.md/ok.sql")));

        p.Add("x.md/new.sql", "late");
        bundle.TryResolveResourceKey(concept, "/x.md/new.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
    }

    [Fact]
    public void A_directory_whose_name_is_no_valid_key_is_a_diagnostic_not_a_failure()
    {
        var p = new InMemoryBundleProvider()
            .Add("bad\u0085dir/x.md", Customers) // U+0085: layer-1 control character
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("bad\u0085dir/x.md", problem.RawName);
        Assert.Null(problem.Key);
        Assert.Contains("control", problem.Error);
    }

    [Fact]
    public void A_concept_over_the_object_bound_becomes_a_parse_problem_and_the_load_goes_on()
    {
        var big = "---\ntype: T\n---\n" + new string('x', 100); // 115 bytes
        var p = new InMemoryBundleProvider().Add("big.md", big).Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 100 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(BundleKey.Parse("big.md"), problem.Key);
        Assert.Contains("100", problem.Error);
    }

    [Fact]
    public void A_root_index_over_the_bound_gives_no_declared_version_but_a_global_failure_reading_it_aborts()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n" + new string('#', 200)) // 227 bytes
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 100 } });
        Assert.Null(bundle.OkfVersion);
        Assert.Single(bundle.IndexKeys);

        var failing = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n")
            .Add("ok.md", Customers)
            .FailReadOf("index.md", new BundleLimitExceededException(BundleBound.Entries, 1));
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(failing));
        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public void The_core_enforces_entries_depth_and_key_bytes_even_when_the_provider_does_not()
    {
        var entries = new InMemoryBundleProvider { ObeyLimits = false }.Add("a.md", Customers).Add("b.md", Customers);
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(entries, new BundleLoadOptions { Limits = new BundleLimits { MaxEntries = 1 } }));
        Assert.Contains("entries", ex.Message);

        var depth = new InMemoryBundleProvider { ObeyLimits = false }.Add("a/b/c.md", Customers);
        var dex = Assert.Throws<BundleLoadException>(() => Bundle.Load(depth, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2 } }));
        Assert.Contains("depth", dex.Message);

        // Depth is measured on the raw name, before any other rejection: a too-deep name that
        // Strict would also reject (reserved name), or that is no valid key at all (backslash,
        // over 1 024 bytes), still aborts the load.
        var deepReserved = new InMemoryBundleProvider { ObeyLimits = false }.Add("a/b/con.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepReserved, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2 } })).Message);
        var deepInvalid = new InMemoryBundleProvider { ObeyLimits = false }.Add("bad\\name/a/b.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepInvalid, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 1 } })).Message);
        var deepLong = new InMemoryBundleProvider { ObeyLimits = false }.Add(new string('x', 1030) + "/a/b.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepLong, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 1 } })).Message);

        var longKey = new InMemoryBundleProvider { ObeyLimits = false }.Add("longname.md", Customers).Add("ok.md", Customers);
        var bundle = Bundle.Load(longKey, new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 6 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("longname.md", problem.RawName);
        Assert.Contains("6", problem.Error);
        Assert.Contains(BundleKey.Parse("longname.md"), bundle.RejectedKeys);
    }

    [Fact]
    public void Bounds_also_apply_to_keys_that_appear_after_the_load()
    {
        var p = new InMemoryBundleProvider().Add("c/rev.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2, MaxKeyBytes = 12 } });
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        p.Add("long/path/q.sql", "late and too deep");
        bundle.TryResolveResourceKey(concept, "/long/path/q.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("long/path/q.sql")));
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("long/path/q.sql"), out var reason));
        Assert.Contains("depth", reason);
    }

    [Fact]
    public void Provider_failures_are_translated_from_their_type_never_from_their_message()
    {
        var raw = new InMemoryBundleProvider().Add("a.md", Customers);
        raw.FailReadsWith = new IOException("secret-token=123 host=C:/private signed-url=https://private/?sig=secret");
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(raw));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Contains("'a.md'", ex.Message);
        Assert.Contains(nameof(IOException), ex.Message);
        Assert.Contains("secret-token", ex.InnerException!.Message);

        var typed = new InMemoryBundleProvider().Add("c.md", Customers);
        typed.FailReadsWith = new BundleAccessRefusedException(BundleKey.Parse("c.md"), "secret-token=123");
        var tex = Assert.Throws<BundleLoadException>(() => Bundle.Load(typed));
        Assert.DoesNotContain("secret", tex.Message);
        Assert.Contains("'c.md'", tex.Message);
        Assert.Contains("secret-token", ((BundleAccessRefusedException)tex.InnerException!).Detail);

        var readOnly = Assert.Throws<BundleReadOnlyException>(() => raw.Write(BundleKey.Parse("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.DoesNotContain(raw.Id, readOnly.Message);
    }

    [Fact]
    public void Reserved_filenames_are_classified_by_the_last_segment_only()
    {
        var p = new InMemoryBundleProvider()
            .Add("tables/index.md/x.md", Customers)
            .Add("tables/index.md", "# Tables\n")
            .Add("log.md", "# Log\n");
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal([ConceptId.Parse("tables/index.md/x")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal([BundleKey.Parse("tables/index.md")], bundle.IndexKeys);
        Assert.Equal([BundleKey.Parse("log.md")], bundle.LogKeys);
    }

    [Fact]
    public void Resource_keys_resolve_through_the_provider_and_the_policy()
    {
        var p = new InMemoryBundleProvider()
            .Add("computations/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("references/q.sql", "select 1")
            .Add("computations/sibling.sql", "select 2");
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("computations/rev"))!;

        Assert.True(bundle.TryResolveResourceKey(concept, "/references/q.sql", out var key, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("references/q.sql"), key);

        bundle.TryResolveResourceKey(concept, "./sibling.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("computations/sibling.sql"), key);

        bundle.TryResolveResourceKey(concept, "references/missing.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Missing, status);
        Assert.Equal(BundleKey.Parse("references/missing.sql"), key);

        bundle.TryResolveResourceKey(concept, ".", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Missing, status); // the prefix `computations` is no object

        bundle.TryResolveResourceKey(concept, "../../q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(key);

        bundle.TryResolveResourceKey(concept, "references\\q.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "/con.sql", out _, out status); // rejected by Strict
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "https://x/q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Url, status);
        Assert.Null(key);

        Assert.Equal("select 1", bundle.ReadResourceText(BundleKey.Parse("references/q.sql")));
        Assert.Throws<FileNotFoundException>(() => bundle.ReadResourceText(BundleKey.Parse("references/missing.sql")));
    }

    [Fact]
    public void ConceptId_round_trips_through_keys()
    {
        var id = ConceptId.Parse("tables/orders");
        Assert.Equal(BundleKey.Parse("tables/orders.md"), id.ToKey());
        Assert.Equal(id, ConceptId.FromKey(BundleKey.Parse("tables/orders.md")));
        Assert.Throws<ConceptIdException>(() => ConceptId.FromKey(BundleKey.Parse("tables/orders.txt")));
    }

    [Fact]
    public void The_validator_reads_reserved_files_and_resources_through_the_provider()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n\n- [Orders](tables/orders.md) — One row per order.\n")
            .Add("tables/index.md", "---\nstray: frontmatter\n---\n# Tables\n")
            .Add("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: q.sql\n---\n")
            .Add("tables/q.sql", "select 1")
            .Add("log.md", "# Log\n\n## not-a-date\n\n- x\n");
        var bundle = Bundle.Load(p);
        var report = BundleValidator.Validate(bundle);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.IndexHasFrontmatter);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid);
        var missing = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.Contains("a file exists at tables/q.sql", missing.Message); // the concept-relative hint, now through the provider
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleProviderLoadTests"`
Expected: build error — `BundleLoadOptions`, `Bundle.Load(IBundleProvider, …)`, `Concept.Key` do not exist.

- [ ] **Step 4: Add `BundleLoadOptions`, and the inner-exception constructors**

`src/OKF4net/Providers/BundleLoadOptions.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What a host controls when loading a bundle through a provider (spec §2, §6).</summary>
public sealed record BundleLoadOptions
{
    /// <summary>The spec's defaults: default bounds, <see cref="BundleKeyPolicy.Strict"/>, no extra validator.</summary>
    public static BundleLoadOptions Default { get; } = new();

    /// <summary>The bounds enforced by the core and handed to the provider for its own walks.</summary>
    public BundleLimits Limits { get; init; } = BundleLimits.Default;

    /// <summary>The portability policy applied on top of the provider's native constraints.</summary>
    public BundleKeyPolicy KeyPolicy { get; init; } = BundleKeyPolicy.Strict;

    /// <summary>Host validators that may only restrict further; each returns a reason or <see langword="null"/>.</summary>
    public IReadOnlyList<Func<BundleKey, string?>>? ExtraKeyValidators { get; init; }
}
```

In `src/OKF4net/OkfException.cs`, add beside the existing constructor:

```csharp
    /// <summary>Creates an exception with a library-authored message and the raw cause as <see cref="Exception.InnerException"/>.</summary>
    protected OkfException(string message, Exception inner)
        : base(message, inner)
    {
    }
```

In `src/OKF4net/Bundle.cs`, `BundleLoadException` gets the matching constructor:

```csharp
    /// <summary>Creates the exception with the raw provider failure as <see cref="Exception.InnerException"/> (the host-log channel, spec §7).</summary>
    public BundleLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
```

- [ ] **Step 5: Add the key bridges to `ConceptId`** (after `ToPath`)

```csharp
    /// <summary>The key of this concept's document: its parents, then <c>Name.md</c> (spec §2).</summary>
    public OKF4net.Providers.BundleKey ToKey()
    {
        var segments = new List<string>(Segments.Count);
        segments.AddRange(Segments.Take(Segments.Count - 1));
        segments.Add(Name + ".md");
        return OKF4net.Providers.BundleKey.FromSegments(segments);
    }

    /// <summary>The concept id of a <c>.md</c> key: every segment validated, the last one stripped of <c>.md</c>.</summary>
    /// <exception cref="ConceptIdException">The key does not end with <c>.md</c>, or a segment is not a valid id segment.</exception>
    public static ConceptId FromKey(OKF4net.Providers.BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.IsMarkdown)
        {
            throw new ConceptIdException($"{DebugQuote.Quote(key.ToString())} is not a markdown key");
        }

        var segments = key.Segments.ToList();
        segments[^1] = segments[^1].Substring(0, segments[^1].Length - 3);
        return New(segments);
    }
```

- [ ] **Step 6: Rework `Bundle.cs`**

Replace the `Concept` record (positional parameters unchanged):

```csharp
/// <summary>A loaded concept: its id, its display path (spec §8's rule), and its document.</summary>
public sealed record Concept(ConceptId Id, string Path, OkfDocument Document)
{
    /// <summary>The concept's key in its provider; set by <see cref="Bundle.Load(OKF4net.Providers.IBundleProvider, OKF4net.Providers.BundleLoadOptions?)"/>, <see langword="null"/> for a hand-built instance.</summary>
    public OKF4net.Providers.BundleKey? Key { get; init; }
}
```

Add `using OKF4net.Providers;` at the top of `Bundle.cs`. **Every existing `<see cref="Load"/>` and `<see cref="Bundle.Load"/>` in `src/` becomes `…Load(string)`** — the overload makes the bare name ambiguous (CS0419); `grep -rn 'cref="Load"\|cref="Bundle.Load"' src/` lists them (there are several in `Bundle.cs` and `OkfCli.cs`). Replace the private constructor and `Load(string)` with:

```csharp
    private Bundle(
        IBundleProvider provider,
        BundleLoadOptions options,
        BundleKeyValidator validator,
        BundleVersion version,
        string? okfVersion,
        List<Concept> concepts,
        Dictionary<ConceptId, int> index,
        List<BundleKey> indexKeys,
        List<BundleKey> logKeys,
        List<BundleParseError> parseProblems,
        HashSet<BundleKey> rejectedKeys,
        List<BundleKey> rejectedPrefixes,
        Dictionary<BundleKey, BundleObjectVersion> objectVersions,
        Dictionary<ConceptId, List<ResolvedLink>> outbound,
        Dictionary<ConceptId, List<ConceptId>> backlinks)
    {
        Provider = provider;
        Options = options;
        _validator = validator;
        Version = version;
        _okfVersion = okfVersion;
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
        ObjectVersions = objectVersions;
        _outbound = outbound;
        _backlinks = backlinks;
    }

    private static string DisplayPathOf(string location, BundleParseError problem) =>
        problem.Key is { } key
            ? BundleDisplayPath.Compose(location, key)
            : System.IO.Path.Combine(location, DebugQuote.Quote(problem.RawName).Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// Loads a bundle from a directory tree through the default
    /// <see cref="FileSystemBundleProvider"/> (spec §8).
    ///
    /// Throws only for I/O failures or a non-directory root. Per-file parse
    /// failures are recorded in <see cref="ParseErrors"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, or an I/O error occurred.</exception>
    public static Bundle Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new BundleLoadException($"bundle root is not a directory: {root}");
        }

        return Load(new FileSystemBundleProvider(root), null);
    }

    /// <summary>
    /// Loads a bundle through <paramref name="provider"/> (spec §1, §2, §6): consumes the
    /// listing under the core's own entry, depth and key-length bounds, validates the names
    /// as a tree under <paramref name="options"/>' policy, reads every <c>.md</c> object within
    /// the byte bound, parses the concepts and builds the link graph. Permissive (§11): a
    /// rejected name, an oversized or unparseable concept goes to <see cref="ParseProblems"/>;
    /// only a provider failure or a global bound (entries, depth) throws.
    /// </summary>
    /// <exception cref="BundleLoadException">The provider failed, refused the access, or a global bound was exceeded. The raw cause is the inner exception.</exception>
    public static Bundle Load(IBundleProvider provider, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= BundleLoadOptions.Default;
        var limits = options.Limits;
        var validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);

        // The listing is consumed under the core's own bounds, whatever the provider does:
        // entries while consuming; depth and key length on every name that parses, before the
        // tree validation, so a too-deep key rejected for another reason still aborts.
        var treeInput = new List<string>();
        var parseProblems = new List<BundleParseError>();
        var rejectedKeys = new HashSet<BundleKey>();
        BundleVersion version;
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

                    continue;
                }

                treeInput.Add(raw);
            }

            version = provider.GetVersion(limits);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BundleLoadException($"I/O error while listing the bundle ({e.GetType().Name})", e);
        }

        var tree = validator.ValidateTree(treeInput);
        foreach (var rejection in tree.Rejected)
        {
            if (BundleKey.TryParse(rejection.RawName, out var rejectedKey, out _))
            {
                rejectedKeys.Add(rejectedKey);
            }

            // Only a .md name is diagnosed (spec §2, enumeration scope); other names were never examined.
            if (MarkdownPaths.HasMarkdownExtension(rejection.RawName))
            {
                parseProblems.Add(new BundleParseError(rejection.RawName, null, rejection.Reason));
            }
        }

        var mdKeys = tree.Accepted.Where(k => k.IsMarkdown).ToList();
        mdKeys.Sort((a, b) => PathOrdering.CompareComponentWise(a.ToString(), b.ToString()));

        var concepts = new List<Concept>();
        var indexKeys = new List<BundleKey>();
        var logKeys = new List<BundleKey>();
        var objectVersions = new Dictionary<BundleKey, BundleObjectVersion>();

        foreach (var key in mdKeys)
        {
            switch (key.Name)
            {
                case IndexFilename:
                    indexKeys.Add(key);
                    break;
                case LogFilename:
                    logKeys.Add(key);
                    break;
                default:
                    {
                        string text;
                        try
                        {
                            text = ReadText(provider, key, limits, out var objectVersion);
                            objectVersions[key] = objectVersion;
                        }
                        catch (BundleObjectTooLargeException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }
                        catch (BundleProviderException e)
                        {
                            throw Translate(e, key);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            throw new BundleLoadException($"I/O error reading '{key}' ({e.GetType().Name})", e);
                        }
                        catch (System.Text.DecoderFallbackException)
                        {
                            throw new BundleLoadException("I/O error: stream did not contain valid UTF-8");
                        }

                        OkfDocument document;
                        try
                        {
                            document = OkfDocument.Parse(text);
                        }
                        catch (DocumentParseException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }

                        try
                        {
                            var id = ConceptId.FromKey(key);
                            concepts.Add(new Concept(id, BundleDisplayPath.Compose(provider.Location, key), document) { Key = key });
                        }
                        catch (ConceptIdException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, $"Missing required frontmatter keys: {e.Message}"));
                        }

                        break;
                    }
            }
        }

        var index = new Dictionary<ConceptId, int>();
        for (var i = 0; i < concepts.Count; i++)
        {
            index[concepts[i].Id] = i;
        }

        var (outbound, backlinks) = BuildGraph(concepts, index);
        var okfVersion = ComputeOkfVersion(provider, limits, indexKeys);

        return new Bundle(provider, options, validator, version, okfVersion, concepts, index, indexKeys, logKeys, parseProblems, rejectedKeys, tree.RejectedPrefixes.ToList(), objectVersions, outbound, backlinks);
    }

    /// <summary>
    /// Builds the public message of a provider failure from the exception's type and fields
    /// only (spec §7): a provider may have put storage internals in its message, which stays
    /// on the inner exception for the host's log.
    /// </summary>
    private static BundleLoadException Translate(BundleProviderException e, BundleKey? key) => e switch
    {
        BundleLimitExceededException l => new BundleLoadException($"the bundle exceeds the {BundleLimitExceededException.Describe(l.Bound)} bound ({l.Limit})", e),
        BundleAccessRefusedException r => new BundleLoadException(
            (r.Key ?? key) is { } k ? $"access to '{k}' was refused by the provider" : "access to the bundle was refused by its provider", e),
        BundleObjectNotFoundException n => new BundleLoadException($"no object under '{n.Key}'", e),
        BundleVersionMismatchException m => new BundleLoadException($"'{m.Key}' changed while it was being read", e),
        BundleReadOnlyException => new BundleLoadException("the provider refused a write", e),
        _ => new BundleLoadException(key is null ? $"provider failure ({e.GetType().Name})" : $"provider failure reading '{key}' ({e.GetType().Name})", e),
    };

    /// <summary>Reads one object as strict UTF-8, counting bytes against the bound (spec §6) whatever the provider says.</summary>
    private static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, out BundleObjectVersion version)
    {
        using var obj = provider.Read(key);
        version = obj.Version;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = obj.Content.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limits.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, total, limits.MaxObjectBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return OkfEncodings.Strict.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
```

Replace the instance `ComputeOkfVersion` with a static one that reads the root index **only if the tree accepted it** (decision 11) and treats only the **local** failures as "no declared version" (too large, missing, unreadable, invalid UTF-8, unparseable); a global failure — a bound, a refusal — propagates as a load failure:

```csharp
    private static string? ComputeOkfVersion(IBundleProvider provider, BundleLimits limits, List<BundleKey> indexKeys)
    {
        var key = BundleKey.Parse(IndexFilename);
        if (!indexKeys.Contains(key))
        {
            return null;
        }

        string text;
        try
        {
            text = ReadText(provider, key, limits, out _);
        }
        catch (Exception e) when (e is BundleObjectNotFoundException or BundleObjectTooLargeException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, key);
        }

        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(text);
        }
        catch (DocumentParseException)
        {
            return null;
        }

        return doc.Frontmatter.Get("okf_version")?.AsDisplayString();
    }
```

(`BundleObjectTooLargeException` derives from `IOException`, so the first filter already covers it; it is named for the reader.) Delete `CollectMarkdown`. Add the new members next to `Root`:

```csharp
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

    /// <summary>The colliding or conflicting keys the tree validation condemned (spec §2), as listed. <see cref="IsKeyAllowed"/> matches them in their <b>folded</b> form, so every spelling of a condemned prefix is refused, even for an object created after the load.</summary>
    public IReadOnlyList<BundleKey> RejectedPrefixes { get; }

    /// <summary>The version of every concept object read at load time (spec §5.1).</summary>
    public IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions { get; }

    private readonly BundleKeyValidator _validator;
    private readonly HashSet<string> _rejectedPrefixFolds;

    /// <summary>
    /// The one guard in front of every late access to a key (spec §2, decision 11): the key
    /// policy, the keys and prefixes the tree validation rejected, and the depth and
    /// key-length bounds this bundle was loaded with. <paramref name="reason"/> says which.
    /// </summary>
    public bool IsKeyAllowed(BundleKey key, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Segments.Count > Options.Limits.MaxDepth)
        {
            reason = $"'{key}' exceeds the depth bound ({Options.Limits.MaxDepth})";
            return false;
        }

        if (key.Utf8ByteCount > Options.Limits.MaxKeyBytes)
        {
            reason = $"'{key}' exceeds the key-length bound ({Options.Limits.MaxKeyBytes} bytes)";
            return false;
        }

        if (_validator.CheckKey(key) is { } policyReason)
        {
            reason = policyReason;
            return false;
        }

        if (RejectedKeys.Contains(key))
        {
            reason = $"'{key}' was rejected when the bundle was loaded";
            return false;
        }

        // Prefixes are compared in their folded form (decision 11): a prefix condemned for a
        // case collision condemns every spelling of itself, not only the ones that were listed
        // (the third review re-entered `Ab`/`aB` through `AB`).
        for (var depth = 1; depth <= key.Segments.Count; depth++)
        {
            var prefix = BundleKey.FromSegments(key.Segments.Take(depth).ToList());
            if (_rejectedPrefixFolds.Contains(_validator.FoldKeyText(prefix)))
            {
                reason = $"'{key}' is under '{prefix}', which was rejected when the bundle was loaded";
                return false;
            }
        }

        reason = null;
        return true;
    }
```

Change `Root` to `public string Root => Provider.Location;`. Keep `IndexFiles`, `LogFiles`, `ParseErrors`, `Concepts`, `Count`, `IsEmpty`, `Get`, `Contains`, `LinksFrom`, `Backlinks`, `BrokenLinks`, `OkfVersion`, `TryResolveResource` and `ReadResourceText(string)` **exactly as they are** (the last two still read `Root`, the filesystem root; lot D retires them). Add, after `ReadResourceText(string)`:

```csharp
    /// <summary>
    /// Resolves a §6.2 path-valued field to a key (spec §2): lexically, through
    /// <see cref="ResourceKeyResolver"/>, then <see cref="IsKeyAllowed"/>, then the provider's
    /// <see cref="IBundleProvider.Exists"/>. Never throws (§11, permissive): a provider refusal
    /// reads as <see cref="ResourceResolutionStatus.Unsafe"/>. Always returns <see langword="true"/>,
    /// like <see cref="TryResolveResource"/>.
    /// </summary>
    public bool TryResolveResourceKey(Concept concept, string rawPath, out BundleKey? key, out ResourceResolutionStatus status)
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentNullException.ThrowIfNull(rawPath);
        var conceptKey = concept.Key ?? concept.Id.ToKey();
        switch (ResourceKeyResolver.Resolve(conceptKey, rawPath, out var candidate))
        {
            case ResourceKeyOutcome.Url:
                key = null;
                status = ResourceResolutionStatus.Url;
                return true;
            case ResourceKeyOutcome.Unsafe:
                key = null;
                status = ResourceResolutionStatus.Unsafe;
                return true;
        }

        if (!IsKeyAllowed(candidate!, out _))
        {
            key = null;
            status = ResourceResolutionStatus.Unsafe;
            return true;
        }

        key = candidate;
        try
        {
            status = Provider.Exists(candidate!) ? ResourceResolutionStatus.Resolved : ResourceResolutionStatus.Missing;
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            key = null;
            status = ResourceResolutionStatus.Unsafe;
        }

        return true;
    }

    /// <summary>
    /// Reads a resolved resource as strict UTF-8 through the provider, after
    /// <see cref="IsKeyAllowed"/> (spec §2) and the provider's own STRICT guards and
    /// confinement boundary (spec §8). Messages are library-authored (§7); the raw cause is
    /// the inner exception. A refusal — by this bundle's guard or by the provider — surfaces
    /// as <see cref="UnauthorizedAccessException"/> and a missing object as
    /// <see cref="FileNotFoundException"/>, so existing callers classify both as before.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The key is not allowed, or the provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public string ReadResourceText(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsKeyAllowed(key, out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }

        try
        {
            return ReadText(Provider, key, Options.Limits, out _);
        }
        catch (BundleAccessRefusedException e)
        {
            throw new UnauthorizedAccessException($"access to '{key}' was refused by the provider", e);
        }
        catch (BundleObjectNotFoundException e)
        {
            throw new FileNotFoundException($"no object under '{key}'", key.ToString(), e);
        }
        catch (BundleObjectTooLargeException)
        {
            throw;
        }
        catch (BundleProviderException e)
        {
            throw new IOException($"provider failure reading '{key}' ({e.GetType().Name})", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new UnauthorizedAccessException($"access to '{key}' was denied ({e.GetType().Name})", e);
        }
        catch (IOException e)
        {
            throw new IOException($"I/O error reading '{key}' ({e.GetType().Name})", e);
        }
    }
```

- [ ] **Step 7: Route `Validate.cs` through the provider**

In `ValidateReserved`, the index loop becomes:

```csharp
        for (var i = 0; i < bundle.IndexKeys.Count; i++)
        {
            var key = bundle.IndexKeys[i];
            var path = bundle.IndexFiles[i];
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"index.md could not be read: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            // … unchanged parse and checks …
            var isRoot = key.Segments.Count == 1;
```

and the `rootIndex` local goes. The log loop likewise iterates `bundle.LogKeys` with `bundle.LogFiles[i]` as the display `path` and reads `bundle.ReadResourceText(key)`. In the resource existence check, replace `bundle.TryResolveResource(concept, resource.RawPath, out _, out var resolutionStatus);` with `bundle.TryResolveResourceKey(concept, resource.RawPath, out _, out var resolutionStatus);`. Replace `ConceptRelativeHint`'s body:

```csharp
    internal static string? ConceptRelativeHint(Bundle bundle, Concept concept, FrontmatterResource resource)
    {
        if (resource.Kind != FrontmatterResourceKind.BundleRelative
            || resource.RawPath.StartsWith('/') || resource.RawPath.StartsWith('\\'))
        {
            return null;
        }

        // Would the same bare path exist next to the concept? Ask the resolver for its
        // concept-relative form, under the same policy and through the same provider.
        bundle.TryResolveResourceKey(concept, "./" + resource.RawPath, out var siblingKey, out var siblingStatus);
        if (siblingStatus != ResourceResolutionStatus.Resolved || siblingKey is null)
        {
            return null;
        }

        return $" — a file exists at {siblingKey}; a bare path resolves from the bundle root (this implementation's reading of §6.2) — write ./{resource.RawPath} for the concept-relative form";
    }
```

After this, `grep -n "File\.\|ReparsePoints\." src/OKF4net/Validate.cs` prints nothing.

- [ ] **Step 8: Migrate the two existing tests the new path rules change**

The first executed review found exactly these; their security purpose is kept, only the outcome of the input changes.

`tests/OKF4net.Tests/ValidateTests.cs`, `A_rooted_bare_path_gets_no_concept_relative_hint_even_if_a_sibling_file_exists`: a backslash is now `Unsafe` on every OS (spec §2, point 7), so the theory splits —

```csharp
    [Fact]
    public void A_rooted_bare_path_gets_no_concept_relative_hint_even_if_a_sibling_file_exists()
    {
        using var tmp = new TempDir();
        tmp.Write("computations/rev.md", "---\ntype: Attested Computation\ntitle: R\ndescription: D\nruntime: python\ncomputation: /query.sql\n---\n");
        tmp.Write("computations/query.sql", "SELECT 1");
        tmp.Write("index.md", "---\ntype: Index\ntitle: I\ndescription: D\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        var d = Assert.Single(report.Diagnostics, x => x.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.DoesNotContain("a file exists at", d.Message);
    }

    [Fact]
    public void A_backslash_rooted_path_is_unsafe_on_every_os_and_gets_no_hint()
    {
        using var tmp = new TempDir();
        tmp.Write("computations/rev.md", "---\ntype: Attested Computation\ntitle: R\ndescription: D\nruntime: python\ncomputation: \\query.sql\n---\n");
        tmp.Write("computations/query.sql", "SELECT 1");
        tmp.Write("index.md", "---\ntype: Index\ntitle: I\ndescription: D\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        var d = Assert.Single(report.Diagnostics, x => x.Code == DiagnosticCode.FrontmatterPathUnsafe);
        Assert.DoesNotContain("a file exists at", d.Message);
        Assert.DoesNotContain(report.Diagnostics, x => x.Code == DiagnosticCode.FrontmatterPathMissing);
    }
```

`tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`, `A_frontmatter_value_quoted_by_a_diagnostic_cannot_forge_a_report_line`: the resource value carries a `\n`, which layer 1 now rejects, so the diagnostic is "escapes the bundle" instead of "not found" — the forged `[error]` line still never starts a report line, which is the point. Replace the last assertion:

```csharp
        Assert.Contains("'s.md [error] forged.md: made up ' escapes the bundle", rendered, StringComparison.Ordinal);
```

The two `sub` + U+2028 + `dir/log.md` tests in the same file need **no** change: U+2028 is not a layer-1 control character (decision 7), so the directory still loads.

Convert every touched `.cs` file to CRLF (new files) or verify it still is (modified files).

- [ ] **Step 9: Run the new tests, then the whole suite**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleProviderLoadTests"` → green.
Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` → green, **0 golden diff**, no skipped test that was not skipped before. If any golden comparison fails, STOP: do not regenerate, report the diff.

- [ ] **Step 10: Commit**

```bash
git add src/OKF4net/Providers/BundleLoadOptions.cs src/OKF4net/OkfException.cs src/OKF4net/ConceptId.cs src/OKF4net/Bundle.cs src/OKF4net/Validate.cs tests/OKF4net.Tests/ValidateTests.cs tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs
git commit -F - <<'MSG'
feat(core): load a bundle through an IBundleProvider; the validator reads through it

Bundle.Load(string) now delegates to the filesystem provider. The core
consumes the listing under its own entry, depth and key-length bounds,
validates names as a tree under the host's policy, reads objects within
the byte bound, and keeps the rejected keys and prefixes behind one guard
that fronts every later resolution and direct read. Provider failures are
translated from their type and fields, never from their message. Concept
keeps its three positional parameters and gains an init-only Key.
Validate.cs no longer touches the disk.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 7: Pin the display rule where the goldens cannot, document the lot

**Files:**
- Test: `tests/OKF4net.Tests/CliDisplayPathTests.cs`
- Modify: `README.md` (one new subsection under the library's API section)
- Modify: `CHANGELOG.md` (`## [Unreleased]`)

**Interfaces:**
- Consumes: `OkfCli.Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)`; `TempDir`.
- Produces: nothing new in code.

- [ ] **Step 1: Write the non-normalised display test**

`GoldenParityTests` compares after `r.Out.Replace('\\', '/')`, so it cannot see a separator change (spec §8, §11). This test feeds the CLI an absolute Windows path and asserts the exact bytes of a concept path in `validate` and `info` output, with no normalisation. It is meaningful on Windows only; elsewhere it still runs (separators are `/` on both sides).

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Spec §8's display rule, pinned without the path-separator normalisation the
/// golden tests apply: on Windows a concept path must still be printed with
/// backslashes, byte for byte what <c>Path.Combine(root, …)</c> produced before
/// the provider seam.
/// </summary>
public class CliDisplayPathTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = OkfCli.Run(args, TextReader.Null, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Validate_and_info_print_concept_paths_with_the_os_separator_unnormalised()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: missing.sql\n---\n");
        tmp.Write("tables/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n"); // a YAML alias: the subset rejects it, so it lands in ParseErrors
        var expectedOrders = Path.Combine(tmp.Path, "tables", "orders.md");
        var expectedBroken = Path.Combine(tmp.Path, "tables", "broken.md");

        var validate = Run("validate", tmp.Path);
        Assert.Contains(expectedOrders + ": frontmatter path 'resource'", validate.Out);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains('\\', expectedOrders);
            Assert.DoesNotContain(tmp.Path.Replace('\\', '/') + "/tables/orders.md", validate.Out);
        }

        var info = Run("info", tmp.Path);
        Assert.Contains("unparseable files:\n  " + expectedBroken + ": ", info.Out);

        // `okf audit` prints ids, not paths (the .out golden has no path); `audit --json`
        // carries `"path"`, which the golden compares after `\\\\` → `/` normalisation.
        // Without a filter, audit lists only stale concepts and this fixture has none
        // (`"findings":[]`), so filter by type to get a finding with a path. The
        // JSON-escaped display path must appear verbatim: on Windows that is the
        // backslash doubled.
        var audit = Run("audit", tmp.Path, "--json", "--type", "Table");
        Assert.Equal(0, audit.Code);
        var jsonEscaped = expectedOrders.Replace("\\", "\\\\");
        Assert.Contains("\"path\":\"" + jsonEscaped + "\"", audit.Out);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliDisplayPathTests"`
Expected: green on Windows and elsewhere. If a **path** assertion is red on Windows, the display rule is broken — fix `BundleDisplayPath`/`Bundle.Load`, never the expected string. If only the `audit` assertion is red, check first that the filter still selects the concept (`--type Table`) and that `JsonOutput.WriteAudit` still names the field `path`.

- [ ] **Step 3: README — one subsection**

Add under the library's API section (next to where `Bundle.Load` is introduced):

```markdown
### Loading through a provider

`Bundle.Load(string root)` is now an overload over the default
`FileSystemBundleProvider`. A host can load through any `IBundleProvider`
(`OKF4net.Providers`) — a key space of objects, read-only unless the provider
declares `Capabilities.CanWrite` — with `Bundle.Load(provider, new
BundleLoadOptions { Limits = …, KeyPolicy = … })`. Keys are validated as a tree
under the `Strict` policy by default (a bundle valid on one provider is valid on
all: no Windows reserved names, no trailing dot, no case-colliding siblings, no
file that is also a directory); `Native` keeps only the provider's own
constraints. Default bounds: 16 MiB per object, 1 000 000 entries, depth 64.
Every rejected `.md` name is a `Bundle.ParseProblems` entry (and a
`ParseErrors` line); other rejected names are skipped silently. In this
release the filesystem provider is read-only and no other provider ships; the
write side and the first remote providers are tracked in #139.
```

- [ ] **Step 4: CHANGELOG**

Under `## [Unreleased]`:

```markdown
### Added

- **Bundle provider seam (#139, lot A).** `OKF4net.Providers`: `IBundleProvider`
  and its value types, `BundleKey` with three-layer validation
  (`BundleKeyConstraints`, `BundleKeyPolicy`, `BundleKeyValidator`),
  `FileSystemBundleProvider` (read-only in this release),
  `ResourceKeyResolver`, `BundleLoadOptions`, `Bundle.Load(IBundleProvider,
  BundleLoadOptions?)`, and on `Bundle`: `Provider`, `Location`, `Version`,
  `ParseProblems`, `IndexKeys`/`LogKeys`, `ObjectVersions`, `RejectedKeys`,
  `TryResolveResourceKey`, `ReadResourceText(BundleKey)`; `Concept.Key`
  (init-only; the three-parameter constructor is unchanged);
  `ConceptId.ToKey`/`FromKey`. Every existing member keeps its value.

### Changed

- **Finite default bounds on loading** (spec §6): a concept over 16 MiB is a
  `ParseErrors` entry instead of being loaded; a listing over 1 000 000 entries
  (every entry counted, `.git`/`node_modules` included) or deeper than 64
  segments aborts the load with `BundleLoadException`.
- **Key policy `Strict` by default** (spec §2): a `.md` whose name a known
  filesystem would alias — a Windows reserved name (`con.md`, `com¹.md`), a
  trailing dot or space, a sibling differing only by case or Unicode form, a
  file that is also a directory — is rejected into `ParseErrors`; a referenced
  resource with such a name resolves as `Unsafe`. Other files were never
  examined and still are not. Pass `BundleKeyPolicy.Native` to keep only the
  host filesystem's own rules. Case collisions are detected with a
  per-code-point upper-case fold, a **conservative over-approximation** of
  NTFS's table: siblings NTFS keeps distinct but the fold merges (`σ.sql` and
  `ς.sql`) are rejected under `Native` on Windows too. Unicode-form collisions
  (`café.sql` beside `cafe` + U+0301 `.sql`, one file on APFS) are detected by
  comparing canonical decompositions from tables derived from ICU, so the
  answer is the same under the AOT binaries, where `string.Normalize` is a
  no-op; NFC itself is checked the same way, canonical ordering included.
- **Exact-case resource resolution** on case-folding volumes (Windows, macOS):
  `references/a.sql` no longer resolves `References/A.sql`; it is `Missing`.
- **A backslash in a §6.2 path-valued field is `Unsafe`** on every OS;
  `references\a.sql` resolved on Windows before.
- **NFC is decided by a quick check, never by `string.Normalize`** (a silent
  no-op in the Native AOT `okf`/`okf-render` binaries): under `Strict`, a name
  containing a character that is never NFC (e.g. U+212B) or a combining mark
  that could compose (a decomposed `é`) is rejected with the code point named;
  precomposed names (`café.sql`) pass everywhere, library and binaries alike.
  Two non-composing marks in the wrong canonical order are not detected
  (documented residual).
- **Provider I/O failures are translated**: a `BundleLoadException` or an
  `IOException` leaving `Bundle` names the key and the exception type, never
  the raw OS text, which is kept as `InnerException` for the host's log.
```

- [ ] **Step 5: Commit**

```bash
git add tests/OKF4net.Tests/CliDisplayPathTests.cs README.md CHANGELOG.md
git commit -F - <<'MSG'
test(cli): pin the display rule unnormalised; document lot A

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 8: Lot verification, the AOT fail-closed probe, and `CLAUDE.md`

**Files:**
- Modify: `CLAUDE.md` (the lenient-site count; a `Providers/` architecture bullet)
- Fixes only if verification finds something.

- [ ] **Step 1: `CLAUDE.md`** — in the `Internal/ReparsePoints.cs` bullet, replace `three walks (`Bundle.cs:500`, the load walk `CollectMarkdown`, and the two `IndexGenerator` walks)` with `three walks (`Providers/FileSystemBundleProvider.Walk`, the load walk that replaced `Bundle.CollectMarkdown`, and the two `IndexGenerator` walks)`, and keep the sentence about `TryResolveResource` (it is unchanged in this lot). Add a bullet after the `Internal/AuditText.cs` one:

```markdown
  - `Providers/` — the storage seam under `Bundle` (design: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lot A). `IBundleProvider` is a key space of byte objects that knows nothing of concepts; the core validates every raw name as a **tree** (`BundleKeyValidator`: base rules, the provider's `NativeConstraints`, the host's `BundleKeyPolicy` — `Strict` by default and never below `Native`), enforces the `BundleLimits` by counting bytes itself, resolves §6.2 paths lexically on keys (`ResourceKeyResolver`, never dereferencing a URL), and keeps the historical members (`Concept.Path`, `Bundle.Root`, `ParseErrors`) as projections of the new ones by the display rule (`BundleDisplayPath`: location + OS separator + segments, byte-identical to the old `Path.Combine`). `FileSystemBundleProvider` is the default and read-only until lot B; with a confinement boundary it re-checks the chain boundary→root STRICTly before **every** access, because the ancestor walk stops before the root. Under `InvariantGlobalization` `string.Normalize` is a silent no-op, so NFC is decided by `NfcQuickCheck`'s tables (UAX #15's quick check: `NFC_QC=No` plus the derived composing-mark set) and canonical equivalence between sibling names by `CanonicalForm` (table-driven canonical decomposition with canonical ordering — the NFD — Hangul by arithmetic), never by `Normalize`; all four tables are derived from ICU by `UnicodeTableTests`, which fails with the literals to paste whenever a host normalizer disagrees with them and compares `Decompose` with the host's NFD on every scalar — that test is the tables' executable guard.
```

- [ ] **Step 2: Full runs, with numbers**

```bash
dotnet build OKF4net.sln                                   # 0 warnings, 0 errors
dotnet format OKF4net.sln --verify-no-changes
for i in 1 2 3; do dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"; done   # 3× green, same counts
dotnet test producers/OkfProducer.sln                      # green (references src/OKF4net; Concept keeps its three positional parameters)
dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleLoadPerformanceTests"              # still under its ceiling; note the load figures in the PR
dotnet test OKF4net.sln --filter "FullyQualifiedName~Large_listings_whose_condemned" -v n     # note its duration in the PR: under a second, not the 26 s / 13 s of r7's scan
```

On Windows, additionally run the `Providers` tests as an account that can create symlinks and junctions and confirm **0 skipped** at least once: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers"`.

- [ ] **Step 3: `okf validate` on every sample bundle is byte-identical to `origin/dev`**

Both runs use the **same relative argument** from their own worktree root — the diagnostics print the root as given, so a different argument would diff for no reason.

```bash
git worktree add ../okf-dev-ref origin/dev
for b in acme_retail ga4 attestation_containers_demo meridian_transit; do
  dotnet run --project src/OKF4net.Cli -- validate bundles/$b > /tmp/new-$b.txt 2>&1
  (cd ../okf-dev-ref && dotnet run --project src/OKF4net.Cli -- validate bundles/$b > /tmp/old-$b.txt 2>&1)
  diff /tmp/old-$b.txt /tmp/new-$b.txt && echo "$b identical"
done
git worktree remove ../okf-dev-ref
```

Expected: four `identical`. (The `bundles/` paths contain no name `Strict` rejects — verified on 146 entries during the spec review — so this is a check, not a hope.)

- [ ] **Step 4: AOT publish and the invariant-globalization probe**

```bash
dotnet publish src/OKF4net.Cli -c Release      # Native AOT, InvariantGlobalization
dotnet publish src/OKF4net.Render -c Release
```

Then, with the published `okf` (`src/OKF4net.Cli/bin/Release/net10.0/<rid>/publish/okf`), build a bundle with an NFD-named concept **and** an ASCII concept referencing an NFC resource — the second is the regression the r1 review found:

```bash
mkdir -p /tmp/nfc-probe/refs
printf -- '---\ntype: T\ntitle: t\ndescription: d\n---\n' > "/tmp/nfc-probe/cafe$(printf '\xcc\x81').md"            # NFD name: e + U+0301
printf -- '---\ntype: T\ntitle: t\ndescription: d\nresource: /refs/caf\xc3\xa9.sql\n---\n' > /tmp/nfc-probe/ok.md   # NFC resource reference
printf 'select 1' > "/tmp/nfc-probe/refs/caf$(printf '\xc3\xa9').sql"                                           # NFC file name
./okf info /tmp/nfc-probe
./okf validate /tmp/nfc-probe
```

Expected from the AOT binary: `info` lists `cafe…md` under `unparseable files:` with a reason naming `U+0301`, and counts `ok` as a loaded concept; `validate` reports **no** `escapes the bundle` warning for `café.sql` (it resolves). `dotnet run --project src/OKF4net.Cli -- …` gives the **same** two outputs (the CLI project sets `InvariantGlobalization` unconditionally, and the quick check never consults `Normalize` anyway). Record both in the PR description. If `café.sql` is refused, the quick check is wrong — fix `NfcQuickCheck`/`CheckSegment`, never the expectation.

- [ ] **Step 5: `CHANGELOG.md` `[Unreleased]` has the lot's entries once; `grep -n "CollectMarkdown" src/OKF4net/Bundle.cs` prints nothing; `grep -n "File\.\|Directory\." src/OKF4net/Validate.cs` prints nothing; in `src/OKF4net/Bundle.cs` the same grep prints only `Directory.Exists` in `Load(string)` and the `File.Exists`/`File.ReadAllBytes` of the two kept historical overloads, `TryResolveResource` and `ReadResourceText(string)` — a search is not the proof here; the proof is that every *new* entry point (`Load(provider)`, `TryResolveResourceKey`, `ReadResourceText(BundleKey)`, `ValidateReserved`) reaches storage only through `Provider`, which the `InMemoryBundleProvider` tests establish by never touching the disk.**

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -F - <<'MSG'
docs(claude): record the provider seam and the lenient-walk site it replaces

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

Then open the PR against `dev` with the verification numbers, the four `identical` lines, both NFD probe outputs, and the "Decisions taken by this plan" list for the maintainer to confirm.

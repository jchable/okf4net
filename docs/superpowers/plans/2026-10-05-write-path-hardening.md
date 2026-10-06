# Write-path hardening (lot A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `OKF4net`'s write path from losing a concurrent write through a linked bundle root, give the YAML subset one nesting rule shared by its reader and writer, make two refusal/field-list APIs honest, and give producers a typed setter for §5.1's shared `usage_window`.

**Architecture:** Four independent tasks in the core library (`src/OKF4net`, plus one caller in `src/OKF4net.Agents`), each with its own tests, CHANGELOG entry and ROADMAP update, then a lot verification task.

**Tech Stack:** C# 14 / .NET 10, xunit + `Xunit.SkippableFact`, BCL only.

**Spec:** `ROADMAP.md` `## Next` (bullets "Resolve the bundle root before keying the write lock", "Reconcile the YAML depth counters between the parser and the emitter", "A typed `OkfDocumentBuilder` method for the shared `usage_window`"), issues #86, #113, #115, and `docs/spec/SPEC.md` §5.1.

## Revision history

- **r1 (2026-10-05)** — first version, commit `1f2ffa4`.
- **r2 (2026-10-05)** — after an external review of r1 (executed probes on Windows/.NET 10.0.8 and Ubuntu WSL) and two user decisions:
  - **The atomic write (r1 Task 2) is removed from this lot** and becomes its own design lot. The review found a documented Windows partial-failure state in which r1's cleanup would delete both versions (`ReplaceFile` error 1176 with no backup), a world-readable temporary before permissions were applied, a `File.Move(overwrite:false)` that is not race-proof on Unix, and Windows ACLs not addressed. Task 5 here records those findings in the ROADMAP bullet so the design lot starts from them. Nothing regresses: the current in-place write is unchanged.
  - **The lock key is resolved at every outermost acquisition** (user decision), not once at construction. Two writers built on opposite sides of a topology change (a missing directory later replaced by a junction) would otherwise keep different locks over the same files.
  - YAML: one counting rule is defined precisely and enforced in BOTH the parser and the emitter (r1 changed only the parser). Measured baseline: `Block(501)` is rejected by the parser, `Mixed(450,900)` parses but cannot be emitted, `Mixed(1,999)` emits but cannot be re-parsed, and the emitter accepts 1001 nested mappings.
  - #115: duplicate `verified` entries are compared with multiplicity. #113: migration note completed. `Provenance.UsageWindowToYaml` becomes `internal`.
  - Several r1 tests and mutations could not fail; they are replaced (see each task).

- **r3 (2026-10-05)** — implemented external-review corrections, authorized by the user:
  - Held-root lookup compares namespace-normalized lexical roots ordinally: case-distinct aliases can point at different bundles.
  - Each operation holds a stable lexical gate as well as its resolved monitor. Resolution runs after taking the gate. If the resolved monitor is busy, release the gate before waiting and retry resolution/acquisition afterwards, so an owner can re-enter through a waiting alias.
  - Lexical gates also compare ordinally: coalescing case-distinct aliases here would introduce dependencies between otherwise independent bundles. Identical normalized roots stay serialized across topology changes; different aliases under an active topology change remain outside the guarantee.
  - Added regression tests for both losses, alias contention, interrupted acquisition and scope cleanup; strengthened the unequal-entry-count mutation test; UNC normalization has a test independent of share access.
  The Task 1 design below records r2; these r3 corrections supersede its acquisition algorithm.

## Global Constraints

- Work in `E:\Sources\okf-post-audit`, branch `fix/write-path-hardening` (rebased on `origin/dev`). Never touch `E:\Sources\okf` (shared checkout; `dev` is checked out there) or any other worktree. To start from `dev`, use `git switch --detach origin/dev` or `git checkout -B <branch> origin/dev`; never `git checkout dev`.
- Zero third-party runtime dependencies in `src/OKF4net` — BCL only. No P/Invoke.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on public API, nullable on, `TreatWarningsAsErrors`.
- Source files are CRLF in the working tree. Never rewrite a file with a tool that strips CR (Git Bash `sed -i` does). Before each commit: `tr -cd '\r' < f | wc -c` equals `wc -l < f` for every touched file.
- `tests/fixtures/` is not edited to make a test pass. If a golden snapshot changes, STOP and report.
- `BundleConceptWriter` never throws for an expected error: I/O, YAML, validation and reparse-point failures surface as an `Error: ...` string through `RunTool`.
- Path-safety guards use the STRICT predicates. `ReparsePoints.CanonicalizeRoot` stays lexical (user decision). `ReparsePoints.TryResolveThroughReparsePoints` is not modified (it backs `HtmlWriter`'s guard).
- Each task adds its own `CHANGELOG.md` entry under `## [Unreleased]` (`### Changed` with a **Breaking (0.x)** prefix where it breaks, else `### Fixed` / `### Added`), describing behaviour, not commits, and removes its own bullet from `ROADMAP.md` `## Next`.
- Commit messages say only what the code actually does. End each with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never `git add -A`. Do not push.
- Verification for every task: `dotnet build OKF4net.sln` (0 warnings), `dotnet format OKF4net.sln --verify-no-changes`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` (0 failed). Tasks 2 and 4 change public `OKF4net` API, so they also run `dotnet test producers/OkfProducer.sln`.
- **Mutation checks are mandatory and must be real.** A mutant must compile (an `if (false)` guard does not compile under `TreatWarningsAsErrors` — CS0162 — and then the OLD binary runs: use a non-constant guard and confirm the build rebuilt). Report each mutant and the test that went red. A test that stays green with its guard removed is a defect to fix, not to report.
- **Skips must not hide coverage.** A `SkippableFact` that skips on a capability (link privilege, POSIX) must execute on the host or CI runner that has it. Report which tests ran and which skipped, per platform you executed.

## Review Focus

1. **Two writers constructed across a topology change** (a root path whose parent becomes a junction after the first writer was built) must serialize against each other at write time. Task 1.
2. **A nested acquisition after the topology changed mid-operation** (`OkfBundleTools` holds the lock, then calls into the writer) must reuse the lock it holds, never enter a second one — no lock-order inversion. Task 1.
3. **Windows namespace spellings** (`C:\x`, `\\?\C:\x`, `\\server\share\x`, `\\?\UNC\server\share\x`) of one directory get one lock. Task 1.
4. **A YAML value built in code** (not parsed) at exactly the shared limit is emitted and re-parsed equal, and one level deeper is refused by both sides, for every collection shape the emitter can produce, empty collections included. Task 3.
5. **Every real bundle and fixture in the repository loads with the same `ParseErrors` before and after** the YAML change. Task 3.

---

### Task 1: One write lock per physical bundle, resolved at each acquisition (#86)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (the `BundleLocks` registry doc ~136-177, `_bundleLock` ~198, the constructor ~233-262, `WriteLock` ~274, the five `lock (_bundleLock)` sites ~355/420/537/722/1201, and every doc comment that describes the lock)
- Modify: `src/OKF4net/Internal/ReparsePoints.cs` (add `ResolveLockKey`; update the remark at ~509-513 that points at #86)
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`_bundleLock` ~94/146 and its four `lock` sites ~191/203/1101/1174)
- Test: `tests/OKF4net.Tests/BundleConceptWriterLockTests.cs` (create); `tests/OKF4net.Tests/RecordVerificationTests.cs:618` (uses `writer.WriteLock`)
- Modify: `CHANGELOG.md`, `ROADMAP.md`

**Interfaces:**
- Produces: `internal static string ReparsePoints.ResolveLockKey(string root)`.
- Produces: `internal WriteLockScope BundleConceptWriter.EnterWriteLock()` — enters the bundle's lock and returns a disposable scope whose `Dispose` exits it; replaces the `WriteLock` property, which is removed.

**Design (decided: per-acquisition resolution).**

*Key.* `ResolveLockKey(root)`:
1. `lexical = CanonicalizeRoot(root)`.
2. Normalize Windows namespace forms of `lexical` before resolving: strip a leading `\\?\` (keep the drive form), and turn `\\?\UNC\server\share\…` into `\\server\share\…`. On non-Windows, no-op.
3. Repeatedly apply `TryResolveThroughReparsePoints` and normalize again, until the result is **ordinally** equal to the previous one (case-only differences are NOT convergence: a case-sensitive filesystem can have both), at most 40 rounds.
4. Any failure — an uninspectable entry, an unreadable link target, no convergence in 40 rounds — returns `lexical`.
The registry dictionary itself stays `StringComparer.OrdinalIgnoreCase` (over-coalescing two case-distinct roots only serializes them; it cannot deadlock, since the registry hands out one object per key and nothing holds two).

*Acquisition.* The key is computed when a thread takes the lock for an operation, not at construction:
- `EnterWriteLock()` checks whether THIS thread already holds a lock object for THIS writer's root (a `[ThreadStatic]` map from lexical root to the held object and a re-entry count). If it does, it re-enters that same object (Monitor is re-entrant) and never re-resolves. Otherwise it resolves `ResolveLockKey(BundleRoot)`, takes `BundleLocks.GetOrAdd(key, …)`, enters it, and records it.
- This rule is what keeps `OkfBundleTools` safe: it takes the lock, then calls writer methods that take it again; those nested entries must reuse the outer object even if the topology changed in between, or two threads could each hold one object and wait for the other.
- Every `lock (_bundleLock)` in `BundleConceptWriter` and `OkfBundleTools` becomes `using var _ = writer.EnterWriteLock();` (or the equivalent pattern; `lock` statements cannot wrap a scope object). `OkfBundleTools` stops caching a lock object; it calls `_writer.EnterWriteLock()` at each site. `WriteLock` is removed.
- Guarantee to document, exactly: writers in one process serialize when, at the moment they take the lock, their roots resolve to the same directory; an alias shares the lock; resolution failure falls back to the lexical key; a topology change DURING an operation is not followed (the outermost acquisition's object is kept for the whole operation); still in-process only.

- [ ] **Step 1: Write the failing tests** in `BundleConceptWriterLockTests` (use `TempDir.TryCreateJunctionToExternalDir(name, target)` and `Skip.IfNot(…, "no junction/symlink privilege on this host")` as the existing tests do; on this Windows host they must RUN). Each test obtains the object through a test-visible probe, e.g. an `internal object CurrentLockObjectForTest()` that resolves and returns the registry object WITHOUT entering it — define it in Step 3.
  1. `A_junction_to_the_bundle_shares_the_bundles_lock` — writer over `actual`, writer over `alias → actual`: same object.
  2. `A_root_below_a_linked_parent_shares_the_lock_of_its_real_path` — `tmp/parent → external`, writers over `tmp/parent/bundle` and `external/bundle`: same object.
  3. `A_link_found_only_after_the_first_resolution_still_converges` — a fixture that needs TWO resolution passes on Windows, so the single-pass mutant fails: create `real/k/b`; junction `tmp/m → real`; junction `tmp/a/j → tmp/m/k`. One pass on `tmp/a/j/b` resolves `j` to `tmp/m/k/b`; only a second pass resolves `m` to `real`. Assert the writer over `tmp/a/j/b` and the writer over `real/k/b` share an object. **Before relying on it, verify on this host that one call of `TryResolveThroughReparsePoints("tmp/a/j/b")` does NOT already return `real/k/b`; if it does, find a fixture that needs two passes and report it.**
  4. `Writers_built_across_a_topology_change_share_the_lock_at_write_time` — build writer A over `tmp/later/bundle` while `later` does not exist; then create `later` as a junction to `external`, create `external/bundle`; build writer B over `external/bundle`; assert A and B resolve to the same object NOW, and that two concurrent `WriteConcept` calls through A and B on the same concept both land (use the existing concurrency-test pattern in `RecordVerificationTests`).
  5. `A_nested_acquisition_reuses_the_held_lock_even_if_the_topology_changed` — on one thread: `EnterWriteLock()` on writer A; then change the topology so A's root would now resolve elsewhere; call `EnterWriteLock()` again on A; assert the second scope entered the SAME object (expose the entered object on the scope for tests) and that `Monitor.IsEntered` holds for exactly that one object.
  6. `Windows_namespace_spellings_share_one_lock` (Windows only, `Skip.IfNot(OperatingSystem.IsWindows())`) — writers over `X` and `\\?\X`; and for a UNC path to a local share if one is available (`\\localhost\C$\…` needs admin: if not reachable, skip that half and say so).
  7. `Two_lexical_spellings_still_share_one_lock` (trailing separator) and `Two_different_bundles_get_two_locks`.
  8. `A_missing_root_constructs_writes_and_shares_the_lock_once_created` — construct over a missing root (no exception), then create it, `WriteConcept` succeeds, and a second writer over the same spelling gets the same object. Do NOT assert a literal lexical string (macOS `/var` → `/private/var` makes that platform-dependent).

- [ ] **Step 2: Run to see them fail.** Build fails (`EnterWriteLock`, `ResolveLockKey` missing). After adding stubs that keep today's construction-time lexical key, tests 1–4 and 6 must FAIL (different objects). Record the output.

- [ ] **Step 3: Implement** `ResolveLockKey` (as designed), the thread-static held-lock map, `EnterWriteLock()` + its scope, `CurrentLockObjectForTest()`; replace every lock site in both classes; remove `WriteLock`; update `RecordVerificationTests:618`. Rewrite every doc comment that describes the lock (class remarks, `BundleLocks`, the constructor comment, `OkfBundleTools`' `_bundleLock` docs) to state the guarantee exactly as in the Design paragraph; remove "aliased root, none" and anything presenting #86 as open. Update `TryResolveThroughReparsePoints`' #86 pointer.

- [ ] **Step 4: Run, then mutate** (each must turn a named test red; restore after each):
  - single pass in `ResolveLockKey` → test 3 red;
  - `OrdinalIgnoreCase` convergence → write a test that would catch it if you can construct one on this host (a case-sensitive directory via `fsutil file setCaseSensitiveInfo` needs admin — if not possible, say so and keep the ordinal comparison with a comment);
  - resolve once at construction (old behaviour) → test 4 red;
  - re-resolve on nested acquisition → test 5 red;
  - drop the `\\?\` normalization → test 6 red.
  Also run the existing concurrency tests (`RecordVerificationTests`, `OkfWriteToolsTests`) three times.

- [ ] **Step 5: Docs.** `CHANGELOG.md` `### Fixed`: a junction or symlink to a bundle now shares the bundle's write lock, resolved each time the lock is taken (#86); two writers in one process can no longer interleave read-modify-write cycles through an alias; still in-process only; a topology change during an operation is not followed. `ROADMAP.md`: remove the "Resolve the bundle root before keying the write lock" bullet.

- [ ] **Step 6: Full verification and commit** — Global Constraints commands, then `git commit -m "fix(core): key the write lock on the bundle root resolved at each acquisition (#86)"` (files by name).

---

### Task 2: Refusal-detail accuracy (#115) and read-only field lists (#113)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`ContainsNaN` ~999-1005, `DescribeFrontmatterDivergence` ~1022-1057 — make both `internal static` for direct tests)
- Modify: `src/OKF4net/Frontmatter.cs` (~18-57), `src/OKF4net/Errors.cs:23` doc, `src/OKF4net/OkfDocument.cs:236` if needed
- Test: a new `tests/OKF4net.Tests/BundleConceptWriterDivergenceTests.cs`; `tests/OKF4net.Tests/FrontmatterTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Produces: `public static IReadOnlyList<string> Frontmatter.RequiredKeys { get; }` and `public static IReadOnlyList<string> Frontmatter.RecommendedFields { get; }`, backed by `Array.AsReadOnly(...)` over private arrays. `RecommendedFieldsFor` keeps its signature and returns read-only instances on both branches.

**Rules for `DescribeFrontmatterDivergence`:**
1. Compare ALL `verified` occurrences of both mappings, in order and with multiplicity (`YamlMapping` keeps duplicates — the parser uses `PushRaw`; `Get` only returns the first). If they differ in count or in any value → `"the verified block itself did not round-trip"`.
2. Otherwise, over the non-`verified` entries: name a key only when both lists have equal counts, EXACTLY ONE index differs, and the keys at that index are equal (a value change) → `"the frontmatter key '<name>' changed"`. Anything else → `"the frontmatter changed outside the verified block"`.
3. `ContainsNaN` walks mapping KEYS as well as values.

- [ ] **Step 1: Failing tests (#115)** — build every mapping by PARSING YAML text (`YamlParser`'s real entry point; check its name), never by `Insert`, so duplicates and non-string keys are real:
  - a NaN key: parse `".nan: x"` → `ContainsNaN` true;
  - a single value change (`title: Users` → `title: Clients`) → names `title`;
  - two value changes (title and tags) → generic message;
  - an inserted key that shifts positions with equal counts (`type, title, tags` vs `type, owner, title`) → generic message;
  - duplicate `verified` (`verified: []` + `type: table` vs `verified: []` + `verified: []` + `type: table`) → `"the verified block itself did not round-trip"` (the reviewer observed today's code saying "changed outside the verified block" for this pair);
  - same `verified` values in a different count → the verified message.

- [ ] **Step 2: Failing tests (#113)**:
  - `Frontmatter.RequiredKeys is string[]` and `Frontmatter.RecommendedFields is string[]` are both false;
  - mutation through the interfaces throws `NotSupportedException` for BOTH lists: `((IList<string>)list)[0] = "x"` and `((ICollection<string>)list).Add("x")`;
  - `RecommendedFieldsFor` returns a non-array, non-mutable list on BOTH branches: an ordinary frontmatter, and an Attested Computation with no `resource` key (the carve-out);
  - the existing `Assert.Equal(new[] { "type", "title", "description" }, Frontmatter.RequiredKeys)` keeps passing.

- [ ] **Step 3: Run to see them fail.** Record the output.

- [ ] **Step 4: Implement** the rules above and the `Array.AsReadOnly` properties.

- [ ] **Step 5: Mutate** (each must turn a named test red; restore): `ContainsNaN` on values only; name the first positional mismatch; compare only the first `verified`; return the raw array from `RecommendedFieldsFor`'s carve-out branch. Then Global Constraints commands AND `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 6: Docs and commit.** `CHANGELOG.md` `### Changed` — **Breaking (0.x):** `Frontmatter.RequiredKeys` and `Frontmatter.RecommendedFields` are now read-only `IReadOnlyList<string>` properties; they were mutable `public static readonly string[]` fields in 0.6.0. Binary-breaking (compiled field references do not bind to properties). Source-breaking for `.Length` (use `.Count`), array-specific operations, element assignment, passing them where a `string[]` is required, and reflection on them as fields. `### Fixed` — a verification refusal no longer names an unchanged or the wrong key, reports duplicate `verified` entries as a `verified` divergence, and gives the NaN-specific message for a NaN used as a mapping key (#115). Commit: `fix(core): honest divergence detail and read-only field lists (#113, #115)`.

---

### Task 3: One YAML nesting rule, shared by the parser and the emitter

**Files:**
- Modify: `src/OKF4net/Yaml/YamlParser.cs` (`MaxNestingDepth` ~21; `BlockParser._depth` ~360 and its guarded entry points ~365/414/496; `ParseInlineValue` ~907 and its call sites ~409/739; `FlowParser` ~1165-1215)
- Modify: `src/OKF4net/Yaml/YamlEmitter.cs` (`Emit` ~45-63, `EmitMapping` ~66-93, `EmitSequence` ~95-120, the doc comment ~20-40)
- Test: `tests/OKF4net.Tests/Yaml/YamlDepthSymmetryTests.cs` (create, next to the existing YAML tests — find their folder and namespace)
- Modify: `CHANGELOG.md`, `ROADMAP.md`, `README.md` if it states the limit

**The rule (one definition, both components):** the **depth** of a node is the number of collections (mapping or sequence, block or flow, empty or not) on the path from the root down to and including that node. A scalar adds nothing. The root collection has depth 1. A document or value is accepted when every collection in it has depth ≤ **1000** (`MaxNestingDepth`). An empty collection counts like any other, so a 1000-deep chain ending in `[]` is at the limit and one ending in a 1001st `[]` is over it.

Measured baseline (external review, executed on the r1 base): parser rejects `Block(501)` (it counts two units per block level: `ParseNode` and `ParseMapping` both increment); `Mixed(450,900)` parses, emitter rejects; `Mixed(1,999)` emits, re-parse rejects; emitter starts at depth 0, rejects only `depth > 1000`, so it accepts 1001 nested mappings; empty collections bypass the emitter's recursion.

The invariant, both directions, for every shape below:
- (R→W) text the parser accepts → the emitter accepts the parsed value;
- (W→R) a value the emitter accepts → the parser accepts the emitted text and it parses back `Equal`;
- (refusal) at depth 1001 → the parser rejects the text AND the emitter rejects the value.

- [ ] **Step 1: Measure on the current code and record it.** Write `YamlDepthSymmetryTests` with exact generators, then run it on the UNCHANGED code and paste the results (which shapes fail, where). Generators (comment the depth arithmetic in each):
  - block mappings: `k:\n  k:\n    …: v` — n mappings, depth n;
  - block sequences: `-\n  -\n    - v` — n sequences;
  - compact `- key:` mappings nested in sequences;
  - indentation-relaxed sequences (a sequence at the same indent as its parent key: `k:\n- a`), as the parser accepts them — check `YamlParser` for the exact form it supports;
  - flow sequences `k: [[[v]]]` and flow maps `k: {a: {a: v}}` (the top-level mapping counts);
  - mixed: b block levels whose innermost value is f flow levels;
  - empty leaves: each of the above ending in `[]` / `{}`.
  For W→R, ALSO build values in code (`YamlMapping`/`YamlSequence` constructors), independent of the parser, at depth 1000 and 1001, including empty-collection leaves — parser-first tests cannot exercise values the parser already rejects.

- [ ] **Step 2: Implement the rule in both components.**
  - Parser: one counter for the whole document, shared by the block parser and every flow parse it starts (pass the current depth into `FlowParser` and `ParseInlineValue`); increment exactly once per collection entered, empty ones included; keep every recursive path behind a counted point so no input can overflow the stack; keep the message `nesting depth limit exceeded` and its line numbers.
  - Emitter: root collection at depth 1, `> 1000` rejected, and empty collections counted (they are emitted inline without recursion today — count them anyway). Keep `YamlEmitException`.
  - Remove the asymmetry paragraph from `YamlEmitter`'s doc comment; state the rule once (a shared `const` or doc reference) so the two cannot drift.

- [ ] **Step 3: Hostile inputs, executed in a child process with a deadline.** A stack overflow kills the test host, so these must not run in-process. Use a small console project or `dotnet` child process with a 30 s timeout and assert it returned a `YamlParseException` result rather than crashing. Exact generators: `"k: " + new string('[', 100_000)`; `"k: " + string.Concat(Enumerable.Repeat("{a: ", 100_000))`; `string.Concat(Enumerable.Repeat("-\n", 50_000))` (this one reaches the recursive guard; note that `"- "` repeated is parsed as a scalar and is NOT a recursion test); 50 000 alternating block-mapping / inline-flow levels. Then a mutation in the same harness: remove the increment on one recursive route and show the child process crashes (stack overflow) or exceeds the limit — proving the harness can see the failure.

- [ ] **Step 4: Real-world corpus (Review Focus 5).** A test loads every bundle under `bundles/` and `tests/fixtures/` and asserts no `ParseErrors` mention `nesting depth`. Also compare `okf validate` output on every `bundles/*` against `origin/dev` and report it.

- [ ] **Step 5: Mutate** (restore after each): reset the flow parser's starting depth to 0 → a mixed shape red; start the emitter at depth 0 → a W→R refusal test red; skip counting empty collections in the emitter → an empty-leaf test red.

- [ ] **Step 6: Docs and commit.** `CHANGELOG.md` `### Changed` — **Breaking (0.x):** the YAML subset's nesting limit is one rule for block and flow, reader and writer: at most 1000 nested collections, the root and empty collections included. Block-only documents nested 501–1000 deep, which the parser used to reject, are now accepted; documents mixing block and flow deeper than 1000 in total, which used to load, are now rejected (they could not be written back); values nested 1001 deep, which the emitter used to write, are now refused. Real frontmatter is a handful of levels deep. `ROADMAP.md`: remove the bullet. `README.md`: correct any statement of the limit. Commit: `fix(yaml): one nesting rule for block and flow, shared by the parser and the emitter`.

---

### Task 4: A typed setter for the shared `usage_window` (§5.1)

**Files:**
- Modify: `src/OKF4net/Provenance.cs` (extract the window serialization at ~108-122)
- Modify: `src/OKF4net/OkfDocumentBuilder.cs`
- Test: `tests/OKF4net.Tests/OkfDocumentBuilderTests.cs` (existing — find it)
- Modify: `CHANGELOG.md`, `ROADMAP.md`, `README.md` if it lists the builder's methods

**Interfaces:**
- Produces: `internal static YamlMapping Provenance.UsageWindowToYaml(UsageWindow window)` — the one serialization of a window, used by `Provenance.ToYaml` (per-entry override) and the builder (shared window). `internal`, not public: sharing it does not need a new public API commitment. Issue #62: docs claim "`Provenance.ToYaml` has exactly one caller" — this adds a caller to the new helper, not to `ToYaml`; confirm those docs still hold.
- Produces: `public OkfDocumentBuilder SharedUsageWindow(UsageWindow window)` — sets (overwriting) the top-level `usage_window`.

§5.1 (`docs/spec/SPEC.md` ~line 332): `usage_window` is "Written once as a sibling of `sources`"; it frames every entry's `usage_count`; an entry MAY override it. `Frontmatter.KnownKeys` orders `"sources", "usage_window"`.

- [ ] **Step 1: Failing tests** (use the real API names from `OkfDocumentBuilderTests`):
  - `SharedUsageWindow_writes_the_top_level_window_right_after_sources` — with a source and a window; `doc.Frontmatter.UsageWindow` equals it; the key order has `usage_window` immediately after `sources`;
  - `A_shared_window_round_trips_through_serialization` — `OkfDocument.Parse(doc.Serialize())` gives the same `UsageWindow` (with one null bound);
  - `An_empty_shared_window_is_kept_as_an_empty_mapping` — `new UsageWindow(null, null)` stays present, as `ToYaml` does for a per-entry override;
  - `The_per_entry_override_and_the_shared_window_serialize_identically` — `UsageWindowToYaml(w)` equals the `usage_window` mapping `ToYaml` writes for an entry with the same window;
  - `An_Extension_call_on_usage_window_still_wins_as_documented`.

- [ ] **Step 2: Run to see them fail.** Record.

- [ ] **Step 3: Implement.** Extract `UsageWindowToYaml` (key order `from, to`; null bounds omitted; empty mapping kept) and call it from `ToYaml`. Builder: `private UsageWindow? _sharedUsageWindow;`, the setter, and in `Build` insert `usage_window` right after the `sources` block (whether or not there are sources), before the extensions loop. Update `Build`'s doc: fixed order `type, title, description, resource, tags, sources, usage_window`; "six well-known keys" becomes seven.

- [ ] **Step 4: Mutate** — move the insertion after the extensions loop → the Extension test red; drop the empty-mapping case → the empty-window test red. Restore. Global Constraints commands AND `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Docs and commit.** `CHANGELOG.md` `### Added`: `OkfDocumentBuilder.SharedUsageWindow(UsageWindow)` (§5.1). `ROADMAP.md`: remove the bullet. Commit: `feat(core): typed builder setter for the shared §5.1 usage_window`.

---

### Task 5: Lot verification, and hand the atomic write to its own lot

**Files:** `ROADMAP.md`; fixes only if verification finds something.

- [ ] **Step 1: Rewrite the ROADMAP's "Atomic write-then-rename" bullet** so the design lot starts from the external review's findings instead of r1's design. It must state:
  - Windows `ReplaceFile` can fail with `ERROR_UNABLE_TO_MOVE_REPLACEMENT` (1176) leaving no file at the target and the replacement under its temporary name when no backup is given; deleting the temporary on every exception would then lose both versions, so the commit protocol must separate pre-commit failures from indeterminate ones and keep recoverable artifacts;
  - a temporary must be created with restrictive permissions BEFORE content is written (POSIX `0644` under umask 022 exposed a `0600` file's content; a chmod by path afterwards can follow a substituted link), and Windows ACL inheritance at creation must be considered;
  - `File.Move(overwrite: false)` on Unix is `lstat` then `rename`: not race-proof no-clobber, and it can fall back to copying;
  - the durability promise must be stated (process crash vs power loss; directory fsync on POSIX);
  - orphaned temporaries are ignored as concepts but can block the producer's `RequireEmpty` and its pruning.
  Keep it a ROADMAP bullet, not a design document.

- [ ] **Step 2:** `ROADMAP.md` `## Next` no longer lists the lock, YAML or `usage_window` items; nothing in `ROADMAP.md`, `README.md`, `CLAUDE.md` or `src/**` doc comments still describes a fixed gap as current (`grep -rn "#86\|aliased root\|two independent counters\|TWO independent counters" --include=*.md --include=*.cs .`, excluding `docs/superpowers` and released CHANGELOG sections).

- [ ] **Step 3:** `CHANGELOG.md` `[Unreleased]` has exactly one entry per task, Breaking ones prefixed.

- [ ] **Step 4:** Full runs, with numbers: `dotnet build OKF4net.sln`, `dotnet format --verify-no-changes`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` three times, `dotnet test producers/OkfProducer.sln`, and `okf validate` on every bundle under `bundles/` compared with `origin/dev`.

- [ ] **Step 5:** `git log --oneline origin/dev..HEAD` — the plan commit plus one commit per task, messages true to their diffs.

# Write-path hardening (lot A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `OKF4net` from losing or corrupting a bundle's data on its write path: no half-written concept on an I/O failure, one write lock per physical bundle, and a YAML subset that writes back whatever it reads.

**Architecture:** Five independent fixes in the core library (`src/OKF4net`), each its own task with its own tests, CHANGELOG entry and ROADMAP update. Two of them carry design decisions already taken by the user (2026-10-05): the lock gets its own resolved key while `ReparsePoints.CanonicalizeRoot` stays lexical, and the YAML parser moves to one combined depth counter.

**Tech Stack:** C# 14 / .NET 10, xunit + `Xunit.SkippableFact`, BCL only.

**Spec:** `ROADMAP.md`, section `## Next` — the four bullets "Atomic write-then-rename in `BundleConceptWriter`", "Resolve the bundle root before keying the write lock", "Reconcile the YAML depth counters between the parser and the emitter", "A typed `OkfDocumentBuilder` method for the shared `usage_window`" — plus issues #86, #113 and #115 (`gh issue view <n> --repo jchable/okf4net`). The OKF spec itself is `docs/spec/SPEC.md` (§5.1 for `usage_window`). Read the ROADMAP bullet / issue for your task before starting: it states the threat model and what must be tested.

## Global Constraints

- Work in `E:\Sources\okf-post-audit`, branch `fix/write-path-hardening` (cut from `origin/dev` 1244474). Never touch `E:\Sources\okf` (shared checkout) or `E:\Sources\okf-lot-c` (another lot).
- Zero third-party runtime dependencies in `src/OKF4net` — BCL only.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on public API, nullable on, `TreatWarningsAsErrors`.
- Source files are CRLF. Never rewrite a file with a tool that strips CR (Git Bash `sed -i` does). Before each commit: `tr -cd '\r' < f | wc -c` equals `wc -l < f` for every touched file.
- `tests/fixtures/` is not edited to make a test pass. If a golden capture changes, STOP and report — do not regenerate it.
- `BundleConceptWriter` never throws for an expected error: I/O, YAML, validation and reparse-point failures surface as an `Error: ...` string through `RunTool`.
- Path-safety guards use the STRICT predicates (`IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor`); never add a lenient use in a guard (see `CLAUDE.md`).
- `ReparsePoints.CanonicalizeRoot` stays lexical (user decision). `ReparsePoints.TryResolveThroughReparsePoints` is not modified: it backs `HtmlWriter`'s guard.
- Each task adds its own `CHANGELOG.md` entry under `## [Unreleased]` (`### Changed` with a **Breaking (0.x)** prefix where it breaks, else `### Fixed` / `### Added`), describing behaviour, not commits, and removes or rewrites its own bullet in `ROADMAP.md` `## Next`.
- Commit messages say only what the code actually does. End each with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never `git add -A`; add files by name. Do not push.
- Verification for every task: `dotnet build OKF4net.sln` (0 warnings), `dotnet format OKF4net.sln --verify-no-changes`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` (0 failed). Tasks 3 and 5 change public `OKF4net` API, so they also run `dotnet test producers/OkfProducer.sln` (outside CI by decision).
- Every new guard gets a mutation check: remove or invert the guarded line, show the new test goes RED, restore. A test that stays green with its guard removed is a defect.

## Review Focus

The five inputs no task's happy-path tests would meet first, most likely first:

1. **A concept file held open by another program** (an editor on Windows, sharing violation) when the write commits → the write reports `Error: ...`, the original file is byte-identical, and no temporary file is left in the directory. Pinned in Task 2.
2. **A temporary file orphaned by a crashed process** sitting in a concept directory → `Bundle.Load`, `okf validate`, `okf index` and `IndexGenerator` ignore it entirely (it is not a concept, not listed, not warned about as one). Pinned in Task 2.
3. **A POSIX concept file with restrictive permissions** (e.g. `0600`) → after a write it keeps those permissions; the rename must not widen them to the umask default. Pinned in Task 2 (POSIX-only, skipped on Windows).
4. **A bundle root that does not exist yet when the writer is constructed**, then is created → the writer still works and resolution failure falls back to the lexical key, never throws from the constructor. Pinned in Task 1.
5. **Every real bundle and fixture in the repository still parses after the YAML change** → no `ParseErrors` appear on `bundles/*` or `tests/fixtures/*` that were not there before. Pinned in Task 4.

---

### Task 1: One write lock per physical bundle (#86)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (the `BundleLocks` doc at ~136-177, the constructor at ~233-262)
- Modify: `src/OKF4net/Internal/ReparsePoints.cs` (add one method; update the remark at ~509-513 that points at #86)
- Test: `tests/OKF4net.Tests/BundleConceptWriterLockTests.cs` (create)
- Modify: `CHANGELOG.md`, `ROADMAP.md`

**Interfaces:**
- Produces: `internal static string ReparsePoints.ResolveLockKey(string root)` — the key the lock registry uses. Later tasks do not call it.

Design (decided): `CanonicalizeRoot` stays lexical. The registry gets its own key: the root resolved through EVERY reparse point on its path, to a fixed point. `TryResolveThroughReparsePoints` resolves only the first link it meets walking up and "deliberately does not chase a SECOND reparse point" (its own remarks) — right for its guard, wrong for a key: `C:\a\j\b` with `j → C:\m\k` and `C:\m → E:\real` resolves once to `C:\m\k\b`, while a writer opened on `E:\real\k\b` would get another key. So `ResolveLockKey` calls it repeatedly until the result stops changing. Any failure — an uninspectable entry, an unreadable link target, or more than 40 iterations (a cycle) — falls back to `CanonicalizeRoot(root)`, which is today's key: no regression, never a throw.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.IO;
using OKF4net;
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

/// <summary>
/// #86: <see cref="BundleConceptWriter"/>'s process-wide lock must be one lock per PHYSICAL
/// bundle directory, not per spelling. Junction/symlink cases skip where the host cannot
/// create links (no privilege), never pass vacuously.
/// </summary>
public class BundleConceptWriterLockTests
{
    [SkippableFact]
    public void A_junction_to_the_bundle_shares_the_bundles_lock()
    {
        using var tmp = new TempDir();
        using var actual = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias", actual.Path), "no junction/symlink privilege on this host");

        var direct = new BundleConceptWriter(actual.Path);
        var aliased = new BundleConceptWriter(Path.Combine(tmp.Path, "alias"));

        Assert.Same(direct.WriteLock, aliased.WriteLock);
    }

    [SkippableFact]
    public void A_root_below_a_linked_parent_shares_the_lock_of_its_real_path()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(external.Path, "bundle"));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("parent", external.Path), "no junction/symlink privilege on this host");

        var viaLink = new BundleConceptWriter(Path.Combine(tmp.Path, "parent", "bundle"));
        var real = new BundleConceptWriter(Path.Combine(external.Path, "bundle"));

        Assert.Same(real.WriteLock, viaLink.WriteLock);
    }

    [SkippableFact]
    public void A_chain_of_two_links_resolves_to_the_same_lock()
    {
        // alias2 -> alias1 -> actual: the key must follow the whole chain, not stop after one hop.
        using var tmp = new TempDir();
        using var actual = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias1", actual.Path), "no junction/symlink privilege on this host");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias2", Path.Combine(tmp.Path, "alias1")), "no junction/symlink privilege on this host");

        Assert.Same(
            new BundleConceptWriter(actual.Path).WriteLock,
            new BundleConceptWriter(Path.Combine(tmp.Path, "alias2")).WriteLock);
    }

    [Fact]
    public void Two_lexical_spellings_still_share_one_lock()
    {
        using var tmp = new TempDir();
        var a = new BundleConceptWriter(tmp.Path);
        var b = new BundleConceptWriter(tmp.Path + Path.DirectorySeparatorChar);
        Assert.Same(a.WriteLock, b.WriteLock);
    }

    [Fact]
    public void Two_different_bundles_get_two_locks()
    {
        using var one = new TempDir();
        using var two = new TempDir();
        Assert.NotSame(new BundleConceptWriter(one.Path).WriteLock, new BundleConceptWriter(two.Path).WriteLock);
    }

    [Fact]
    public void A_root_that_does_not_exist_yet_constructs_and_falls_back_to_its_lexical_key()
    {
        // Review Focus 4: resolution of a missing root must not throw from the constructor.
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "not-yet", "bundle");

        var writer = new BundleConceptWriter(missing);

        Assert.Equal(ReparsePoints.CanonicalizeRoot(missing), ReparsePoints.ResolveLockKey(missing), ignoreCase: true);
        Assert.Same(writer.WriteLock, new BundleConceptWriter(missing).WriteLock);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~BundleConceptWriterLockTests"`
Expected: build FAILS (`ReparsePoints.ResolveLockKey` does not exist). After adding a stub that returns `CanonicalizeRoot(root)`, the three link tests FAIL (`Assert.Same` — two lock objects), the three others pass. Record that output in your report: it is the RED evidence.

- [ ] **Step 3: Implement `ResolveLockKey`**

In `src/OKF4net/Internal/ReparsePoints.cs`, next to `TryResolveThroughReparsePoints`:

```csharp
/// <summary>
/// The key <c>BundleConceptWriter</c>'s process-wide lock registry uses for
/// <paramref name="root"/>: the root resolved through EVERY reparse point on its
/// path, to a fixed point, then canonicalized — so a junction or symlink alias and
/// the directory it reaches share one key (#86).
///
/// <para>Applies <see cref="TryResolveThroughReparsePoints"/> repeatedly, because that
/// method resolves only the first link it meets and deliberately does not chase a
/// second one (its remarks) — the right contract for its guard, not for a key.</para>
///
/// <para>Falls back to <see cref="CanonicalizeRoot"/> — the lexical key every root
/// had before #86 — when resolution fails (an uninspectable entry, an unreadable
/// link target) or does not settle within <see cref="MaxLockKeyResolutions"/>
/// rounds (a cycle). That is a lock key, not a guard: failing open to the old key
/// costs at most the old serialization, and never throws from a constructor.
/// <see cref="CanonicalizeRoot"/> itself stays lexical; nothing that decides
/// "inside the bundle" calls this method.</para>
/// </summary>
/// <param name="root">The bundle root as the caller spelled it.</param>
/// <returns>The resolved, canonicalized key; the lexical key on any failure.</returns>
internal static string ResolveLockKey(string root)
{
    var lexical = CanonicalizeRoot(root);
    var current = lexical;
    for (var i = 0; i < MaxLockKeyResolutions; i++)
    {
        if (!TryResolveThroughReparsePoints(current, out var next))
        {
            return lexical;
        }

        next = CanonicalizeRoot(next);
        if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
        {
            return next;
        }

        current = next;
    }

    return lexical;
}

/// <summary>Upper bound on <see cref="ResolveLockKey"/>'s resolution rounds; a link cycle stops here.</summary>
private const int MaxLockKeyResolutions = 40;
```

In `BundleConceptWriter`'s constructor, replace

```csharp
var canonicalRoot = ReparsePoints.CanonicalizeRoot(bundleRoot);
_bundleLock = BundleLocks.GetOrAdd(canonicalRoot, static _ => new object());
```

with

```csharp
_bundleLock = BundleLocks.GetOrAdd(ReparsePoints.ResolveLockKey(bundleRoot), static _ => new object());
```

Rewrite the constructor comment and the `BundleLocks` / `_bundleLock` doc comments so they state the new guarantee exactly: one lock per resolved root; an alias shares the lock; resolution failure falls back to the lexical key; still in-process only (a second process is never serialized against). Remove "aliased root, none" and every sentence that describes the old gap as current. Update `TryResolveThroughReparsePoints`'s remark that points at #86 ("can call this same method ... once that fix is designed") to say `ResolveLockKey` now does.

- [ ] **Step 4: Run the tests, then the mutation check**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~BundleConceptWriterLockTests"` → all pass (or link tests skipped only where the host has no link privilege — on this Windows host they must RUN; report the counts).
Mutation 1: make `ResolveLockKey` return after the first round (`return CanonicalizeRoot(next)` inside the loop) → `A_chain_of_two_links_resolves_to_the_same_lock` must go RED. Restore.
Mutation 2: revert the constructor to `CanonicalizeRoot` → the three link tests go RED. Restore.

- [ ] **Step 5: Docs**

`CHANGELOG.md`, `### Fixed`: a junction or symlink to a bundle now shares the bundle's write lock (#86); two writers in one process can no longer interleave read-modify-write cycles through an alias and lose a stamp; still in-process only. `ROADMAP.md`: remove the "Resolve the bundle root before keying the write lock" bullet (its design paragraph included).

- [ ] **Step 6: Full verification and commit**

Run the three Global Constraints commands. Then:

```bash
git add src/OKF4net/BundleConceptWriter.cs src/OKF4net/Internal/ReparsePoints.cs tests/OKF4net.Tests/BundleConceptWriterLockTests.cs CHANGELOG.md ROADMAP.md
git commit -m "fix(core): key the write lock on the resolved bundle root (#86)"
```

---

### Task 2: Atomic write-then-rename in `BundleConceptWriter`

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`WriteValidatedContentLocked` at ~1551-1578, its remarks, the class doc at ~35 that names `File.WriteAllText` as the primitive)
- Test: `tests/OKF4net.Tests/BundleConceptWriterAtomicWriteTests.cs` (create)
- Modify: `CHANGELOG.md`, `ROADMAP.md`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `internal Action? BeforeCommitRenameForTest { get; set; }` on `BundleConceptWriter` — invoked after the temporary file is fully written and flushed, immediately before it replaces the target. Test seam only, same pattern as the existing `BeforeLateReparseCheckForTest`.
- Produces: `internal const string TempFileSuffix = ".okf-tmp";` on `BundleConceptWriter`.

Design: `WriteValidatedContentLocked` is the only line in the class that touches disk (`File.WriteAllText(targetPath, content, OkfEncodings.NoBom)`), so the change is contained there. Keep `Directory.CreateDirectory`, `BeforeLateReparseCheckForTest` and `LateReparseGuard` exactly where they are. Replace the `WriteAllText` line with:

1. A temporary path in the SAME directory (same volume, so the rename is atomic and never cross-volume): `.<file name>.<Guid N>.okf-tmp`. It does not end in `.md`, so `Bundle.Load` and `IndexGenerator` never treat an orphan as a concept, and the leading dot keeps it out of most listings.
2. Open it with `FileMode.CreateNew` (`FileShare.None`): this refuses to open an existing entry, so a pre-planted file or link at that name is never written through — the path-safety property the ROADMAP asks to hold for the temporary name. Write the bytes, `Flush(flushToDisk: true)`.
3. On POSIX only, when the target exists, copy its mode: `File.SetUnixFileMode(temp, File.GetUnixFileMode(targetPath))` (Review Focus 3). Windows keeps the target's ACLs and attributes through `File.Replace`.
4. Invoke `BeforeCommitRenameForTest`.
5. If the target exists: `File.Replace(temp, targetPath, destinationBackupFileName: null)`. Otherwise `File.Move(temp, targetPath, overwrite: false)` — never clobber a file that appeared meanwhile.
6. On ANY exception after step 2 created the file: best-effort delete of the temporary (swallow its own failure), then rethrow, so `RunTool` turns `IOException`/`UnauthorizedAccessException` into `Error: ...` exactly as today.
7. `_onWriteCommitted?.Invoke()` only after step 5 succeeded.

A rename replaces the directory ENTRY: if the target became a symlink between the late re-check and the rename, the link itself is replaced rather than followed — strictly narrower than `WriteAllText`, which writes through it. Say so in the remarks; do not remove `LateReparseGuard`. Behaviour change to document: a concept that is a HARD link no longer updates its other links (the rename gives it a new inode). Unusual in a bundle; state it in the CHANGELOG.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using OKF4net;
using Xunit;

namespace OKF4net.Tests;

/// <summary>
/// The concept write is atomic: a failure between "temporary written" and "renamed over the
/// target" leaves the previous file byte-identical and no temporary behind.
/// </summary>
public class BundleConceptWriterAtomicWriteTests
{
    private const string Original = "---\ntype: table\ntitle: Users\ndescription: Original.\n---\n\nOriginal body.\n";

    private static string NewContent(string description) =>
        $"---\ntype: table\ntitle: Users\ndescription: {description}\n---\n\nNew body.\n";

    private static string[] Temporaries(string dir) =>
        Directory.GetFiles(dir, "*" + BundleConceptWriter.TempFileSuffix, SearchOption.AllDirectories);

    [Fact]
    public void A_successful_write_replaces_the_content_and_leaves_no_temporary()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "tables", "users.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Original);

        var result = new BundleConceptWriter(tmp.Path).WriteConcept("tables/users", NewContent("Replaced."));

        Assert.StartsWith("Written tables/users (updated", result);
        Assert.Contains("description: Replaced.", File.ReadAllText(path));
        Assert.Empty(Temporaries(tmp.Path));
    }

    [Fact]
    public void A_new_concept_is_created_through_the_same_path()
    {
        using var tmp = new TempDir();
        var result = new BundleConceptWriter(tmp.Path).WriteConcept("tables/orders", NewContent("Created."));

        Assert.StartsWith("Written tables/orders (new", result);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "tables", "orders.md")));
        Assert.Empty(Temporaries(tmp.Path));
    }

    [Fact]
    public void A_failure_before_the_rename_leaves_the_original_intact_and_no_temporary()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "tables", "users.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Original);
        var before = File.ReadAllBytes(path);

        var committed = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () => committed++)
        {
            BeforeCommitRenameForTest = () => throw new IOException("simulated device error"),
        };

        var result = writer.WriteConcept("tables/users", NewContent("Lost."));

        Assert.StartsWith("Error: ", result);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Temporaries(tmp.Path));
        Assert.Equal(0, committed);
    }

    [SkippableFact]
    public void A_target_locked_by_another_program_reports_an_error_and_keeps_the_original()
    {
        // Review Focus 1: an editor holding the file without FileShare.Delete (Windows semantics).
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are Windows semantics");
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "users.md");
        File.WriteAllText(path, Original);
        var before = File.ReadAllBytes(path);

        string result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = new BundleConceptWriter(tmp.Path).WriteConcept("users", NewContent("Blocked."));
        }

        Assert.StartsWith("Error: ", result);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Temporaries(tmp.Path));
    }

    [Fact]
    public void An_orphaned_temporary_is_not_a_concept()
    {
        // Review Focus 2: a crashed process can leave one behind.
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "users.md"), Original);
        File.WriteAllText(Path.Combine(tmp.Path, ".users.md.0123456789abcdef0123456789abcdef" + BundleConceptWriter.TempFileSuffix), Original);

        var bundle = Bundle.Load(tmp.Path);

        Assert.Single(bundle.Concepts);
        Assert.Empty(bundle.ParseErrors);
        Assert.DoesNotContain(BundleConceptWriter.TempFileSuffix, IndexGenerator.GenerateAll(bundle).SelectMany(kv => kv.Value));
    }

    [SkippableFact]
    public void A_restrictive_posix_mode_survives_the_rename()
    {
        // Review Focus 3.
        Skip.If(OperatingSystem.IsWindows(), "POSIX file modes");
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "users.md");
        File.WriteAllText(path, Original);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        new BundleConceptWriter(tmp.Path).WriteConcept("users", NewContent("Private."));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
```

Adjust the exact API names to the code before running (check `WriteConcept`'s overloads, `Bundle.Concepts`, and the `IndexGenerator` entry point that returns generated index text — use whatever the existing `IndexTests` call; the assertion is that no generated index mentions the suffix). Do not weaken an assertion to make it compile.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~BundleConceptWriterAtomicWriteTests"`
Expected: build FAILS (`BeforeCommitRenameForTest`, `TempFileSuffix` missing). With those two members added but the write still `WriteAllText`, `A_failure_before_the_rename...` must FAIL (the hook is never invoked, the content is replaced). Record it.

- [ ] **Step 3: Implement** the design above in `WriteValidatedContentLocked`. Update the method's `<remarks>` and the class doc (~line 35) — they name `File.WriteAllText` as the primitive and describe the truncation risk as current.

- [ ] **Step 4: Run all writer tests, then mutations**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~BundleConceptWriter|FullyQualifiedName~RecordVerification|FullyQualifiedName~OkfWriteTools"` → 0 failed. The existing late-reparse tests (`BeforeLateReparseCheckForTest`) must still pass unchanged.
Mutation 1: remove the temporary's cleanup in the catch → `A_failure_before_the_rename...` RED. Restore.
Mutation 2: replace `FileMode.CreateNew` by `FileMode.Create` → no test may stay as the only guard of that line silently: add `A_preexisting_file_at_the_temporary_name_is_never_written_through` if no test turns RED (plant a file at a predictable temp name through a test seam for the name, or assert the open mode via the hook) — and report which way you closed it.
Mutation 3 (POSIX, run only if a POSIX shell is available — otherwise say so): drop the `SetUnixFileMode` call → `A_restrictive_posix_mode...` RED.

- [ ] **Step 5: Docs.** `CHANGELOG.md` `### Fixed`: a concept write is now atomic (temporary in the same directory, then `File.Replace` / `File.Move`); an I/O failure mid-write leaves the previous file intact; a hard-linked concept file no longer updates its other links. `ROADMAP.md`: remove the "Atomic write-then-rename" bullet. Grep the repo for other claims that writes are not atomic (`grep -rn "half-written\|truncat" README.md src/OKF4net docs/*.md`) and correct those that describe the old behaviour as current.

- [ ] **Step 6: Full verification and commit** — Global Constraints commands, then
`git commit -m "fix(core): write concepts atomically through a same-directory temporary"` (add files by name).

---

### Task 3: Refusal-detail accuracy (#115) and read-only field lists (#113)

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`ContainsNaN` ~999-1005, `DescribeFrontmatterDivergence` ~1022-1057)
- Modify: `src/OKF4net/Frontmatter.cs` (~18-57), `src/OKF4net/OkfDocument.cs:236` if it needs adjusting, `src/OKF4net/Errors.cs:23` doc
- Test: `tests/OKF4net.Tests/RecordVerificationTests.cs` (or a new `BundleConceptWriterDivergenceTests.cs`), `tests/OKF4net.Tests/FrontmatterTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Produces: `public static IReadOnlyList<string> Frontmatter.RequiredKeys { get; }` and `public static IReadOnlyList<string> Frontmatter.RecommendedFields { get; }` — properties backed by `Array.AsReadOnly(...)`, so a cast to `string[]` fails. `RecommendedFieldsFor` keeps its signature and returns read-only instances too.

`ContainsNaN` and `DescribeFrontmatterDivergence` are private; test them through internal access only if needed: make them `internal static` (the assembly already has `InternalsVisibleTo` for the tests — check `src/OKF4net/*.csproj`).

- [ ] **Step 1: Failing tests for #115**

```csharp
[Fact]
public void ContainsNaN_finds_a_NaN_used_as_a_mapping_key()
{
    var map = new YamlMapping();
    map.Insert(new YamlFloat(double.NaN), new YamlString("x"));
    Assert.True(BundleConceptWriter.ContainsNaN(map));
}

[Fact]
public void A_divergence_names_a_key_only_when_it_is_the_only_value_that_changed()
{
    var expected = Map(("type", "table"), ("title", "Users"), ("tags", "a"));
    var actual = Map(("type", "table"), ("title", "Clients"), ("tags", "a"));
    Assert.Equal("the frontmatter key 'title' changed", BundleConceptWriter.DescribeFrontmatterDivergence(expected, actual));
}

[Fact]
public void Two_changed_keys_are_not_reported_as_one()
{
    var expected = Map(("type", "table"), ("title", "Users"), ("tags", "a"));
    var actual = Map(("type", "table"), ("title", "Clients"), ("tags", "b"));
    Assert.Equal("the frontmatter changed outside the verified block", BundleConceptWriter.DescribeFrontmatterDivergence(expected, actual));
}

[Fact]
public void A_key_inserted_earlier_does_not_misname_an_unchanged_key()
{
    // Equal counts, but the positions shift: "owner" replaces "title" and "title" moves down.
    var expected = Map(("type", "table"), ("title", "Users"), ("tags", "a"));
    var actual = Map(("type", "table"), ("owner", "x"), ("title", "Users"));
    Assert.Equal("the frontmatter changed outside the verified block", BundleConceptWriter.DescribeFrontmatterDivergence(expected, actual));
}
```

(`Map` is a small local helper building a `YamlMapping` of `YamlString` keys/values.) The rule they pin: name a key only when the two non-`verified` entry lists have equal counts, exactly ONE index differs, and the keys at that index are equal (a value change). Anything else → the generic message. Also add one test for a duplicate `verified` key if the parser can produce one (check `YamlMapping.Insert` semantics first; if duplicates are impossible by construction, say so in the report instead of writing a vacuous test).

- [ ] **Step 2: Failing tests for #113**

```csharp
[Fact]
public void The_published_field_lists_cannot_be_mutated()
{
    Assert.False(Frontmatter.RequiredKeys is string[]);
    Assert.False(Frontmatter.RecommendedFields is string[]);
    Assert.Throws<NotSupportedException>(() => ((IList<string>)Frontmatter.RecommendedFields)[0] = "x");
}

[Fact]
public void RecommendedFieldsFor_never_hands_out_a_mutable_array()
{
    var fm = Frontmatter.FromMapping(new YamlMapping());
    Assert.False(Frontmatter.RecommendedFieldsFor(fm) is string[]);
}
```

Keep the existing `Assert.Equal(new[] { "type", "title", "description" }, Frontmatter.RequiredKeys)` test passing.

- [ ] **Step 3: Run to see them fail** — `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~FrontmatterTests|FullyQualifiedName~Divergence|FullyQualifiedName~ContainsNaN"`. Record the RED output.

- [ ] **Step 4: Implement.** `ContainsNaN`: `YamlMapping m => m.Entries.Any(e => ContainsNaN(e.Key) || ContainsNaN(e.Value))`. `DescribeFrontmatterDivergence`: implement the rule from Step 1 (count the differing indices; name only on exactly one, with equal keys). `Frontmatter`: private static readonly arrays + public `IReadOnlyList<string>` properties via `Array.AsReadOnly`; `RecommendedFieldsFor` returns `Array.AsReadOnly(...)` for the carve-out branch too.

- [ ] **Step 5: Run, mutate, verify.** Mutation: revert `ContainsNaN` to values only → its test RED; make `DescribeFrontmatterDivergence` name the first mismatch again → `Two_changed_keys...` or `A_key_inserted...` RED. Restore. Then the Global Constraints commands AND `dotnet test producers/OkfProducer.sln` (public API changed).

- [ ] **Step 6: Docs and commit.** `CHANGELOG.md`: `### Changed` — **Breaking (0.x):** `Frontmatter.RequiredKeys` and `Frontmatter.RecommendedFields` are now read-only `IReadOnlyList<string>` properties (they were mutable `public static readonly string[]` fields in 0.6.0: binary-breaking, and source-breaking for code that indexes-assigns or passes them as `string[]`); `### Fixed` — a verification refusal no longer names an unchanged or the wrong key, and a NaN used as a mapping key gets the NaN-specific message (#115). Commit: `fix(core): name a diverging key only when it is the sole change; read-only field lists (#113, #115)`.

---

### Task 4: One combined YAML nesting cap

**Files:**
- Modify: `src/OKF4net/Yaml/YamlParser.cs` (`MaxNestingDepth` ~21, `BlockParser._depth` ~360 and its three guarded entry points ~365/414/496, `ParseInlineValue` ~907 and its two call sites ~409/739, `FlowParser` ~1165-1215)
- Modify: `src/OKF4net/Yaml/YamlEmitter.cs` (doc comment ~20-40 that describes the asymmetry as current)
- Test: `tests/OKF4net.Tests/Yaml/YamlDepthSymmetryTests.cs` (create; put it next to the existing YAML tests — find their folder)
- Modify: `CHANGELOG.md`, `ROADMAP.md`, `README.md` if it states the nesting limit

**Interfaces:** none consumed or produced across tasks.

Decision (user, 2026-10-05): ONE cap of 1000 covering block and flow together, matching `YamlEmitter`. What the cap counts must be the SAME quantity on both sides: **the number of enclosing collections** (a mapping or a sequence, block or flow) — what `YamlEmitter` already counts (`depth + 1` per nested `EmitMapping`/`EmitSequence`). Read the parser first: `BlockParser` increments in `ParseNode` AND in `ParseMapping` (and at ~496), so today one YAML level may cost it more than one unit; `FlowParser` starts its own counter at 0 for every inline value. Measure before changing anything.

The invariant this task delivers, both directions:
- (R→W) if `YamlParser` accepts a text, `YamlEmitter.Emit` accepts the parsed value;
- (W→R) if `YamlEmitter.Emit` accepts a value, `YamlParser` accepts the emitted text and it parses back `Equal` to the value.

- [ ] **Step 1: Measure and write the failing symmetry tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using System.Text;
using OKF4net.Yaml;
using Xunit;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Whatever the YAML subset reads it writes back, and whatever it writes it reads back — at the
/// nesting cap, for block, flow and mixed shapes.
/// </summary>
public class YamlDepthSymmetryTests
{
    private const int Cap = 1000;

    // n nested block mappings: "k:\n  k:\n    k: v"
    private static string Block(int levels)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++)
        {
            sb.Append(' ', i * 2).Append("k:").Append(i == levels - 1 ? " v\n" : "\n");
        }

        return sb.ToString();
    }

    // one key whose value is n nested flow sequences: "k: [[[v]]]"
    private static string Flow(int levels) => "k: " + new string('[', levels) + "v" + new string(']', levels) + "\n";

    // b block levels, then f flow levels inside the innermost one
    private static string Mixed(int block, int flow)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < block - 1; i++)
        {
            sb.Append(' ', i * 2).Append("k:\n");
        }

        sb.Append(' ', (block - 1) * 2).Append("k: ").Append('[', flow).Append('v').Append(']', flow).Append('\n');
        return sb.ToString();
    }

    public static TheoryData<string, string> Shapes() => new()
    {
        { "block at cap", Block(Cap) },
        { "flow at cap", Flow(Cap - 1) },
        { "mixed 450+549", Mixed(450, 549) },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Anything_the_parser_accepts_the_emitter_writes_and_the_parser_reads_back(string shape, string text)
    {
        var parsed = YamlParser.Parse(text);
        var emitted = YamlEmitter.Emit(parsed);           // (R→W): must not throw
        Assert.Equal(parsed, YamlParser.Parse(emitted));  // (W→R)
        _ = shape;
    }

    [Theory]
    [InlineData("block", 0)]
    [InlineData("flow", 0)]
    [InlineData("mixed", 0)]
    public void One_level_past_the_cap_is_refused_whatever_the_style(string style, int _)
    {
        var text = style switch
        {
            "block" => Block(Cap + 1),
            "flow" => Flow(Cap),
            _ => Mixed(450, 551),
        };

        var ex = Assert.Throws<YamlParseException>(() => YamlParser.Parse(text));
        Assert.Contains("nesting depth limit exceeded", ex.Message);
    }

    [Fact]
    public void The_shape_that_used_to_parse_but_not_write_back_is_now_refused_on_read()
    {
        // ROADMAP: ~450 block levels with 900 flow levels parsed, then could not be emitted.
        Assert.Throws<YamlParseException>(() => YamlParser.Parse(Mixed(450, 900)));
    }
}
```

Fix the exact boundary numbers to the counting rule ("number of enclosing collections") once you have read the code — `Flow(n)` puts n sequences INSIDE a mapping, so it is n+1 collections; the top-level mapping counts. Compute each boundary from the rule and comment the arithmetic in the test. Check the API names (`YamlParser.Parse` vs the real entry point, the namespace of the existing YAML tests) and use the real ones. Run them on the CURRENT code first and record which fail and how — that is the measurement the ROADMAP asks for (expect the mixed shape to fail on R→W, and possibly the block-at-cap shape on W→R if the block parser double-counts).

- [ ] **Step 2: Implement one counter.** One mutable depth shared by the block parser and every flow parse it starts: give `FlowParser` a starting depth (constructor parameter) and pass `ParseInlineValue` the block parser's current depth from its two call sites; make each parser increment by exactly one per collection entered (if `ParseNode` + `ParseMapping` both increment for one mapping, keep only one increment per collection — but keep the recursion guarded: every recursive path must still pass through a counted point, so a hostile document still raises `YamlParseException` instead of overflowing the stack). Keep the message text `nesting depth limit exceeded` and the line numbers it reports.

- [ ] **Step 3: Hostile-input check (mandatory for this parser, per CLAUDE.md's adversarial-review rule).** Run, and keep as tests, inputs that would overflow the stack if any recursive path escaped the counter: 100 000 `[`; 100 000 `{a: `; 50 000 nested `- ` sequence items; 50 000 alternating block mapping / inline flow levels. Each must throw `YamlParseException`, never crash the test host.

- [ ] **Step 4: Real-world corpus (Review Focus 5).** Load every bundle under `bundles/` and every bundle under `tests/fixtures/` before and after the change and assert the set of `ParseErrors` is identical (write it as a test that loads them all and asserts no `nesting depth` parse error appears; also compare `okf validate` output on `bundles/*` before/after by hand and report it).

- [ ] **Step 5: Mutation.** Reset the flow parser's starting depth to 0 → the mixed-shape tests go RED. Restore.

- [ ] **Step 6: Docs and commit.** `YamlEmitter`'s doc comment: remove the asymmetry paragraph; state the shared rule. `CHANGELOG.md` `### Changed` — **Breaking (0.x):** the YAML subset's nesting cap now counts block and flow collections together (1000 enclosing collections), the same rule the emitter applies; a frontmatter nested deeper than that in total, which loaded before, now fails to parse (it could not be written back); real frontmatter is a handful of levels deep. `ROADMAP.md`: remove the bullet. `README.md`: correct any statement of the limit. Commit: `fix(yaml): one nesting cap for block and flow, shared with the emitter`.

---

### Task 5: A typed setter for the shared `usage_window` (§5.1)

**Files:**
- Modify: `src/OKF4net/Provenance.cs` (extract the window serialization at ~108-122)
- Modify: `src/OKF4net/OkfDocumentBuilder.cs`
- Test: `tests/OKF4net.Tests/OkfDocumentBuilderTests.cs` (existing — find it)
- Modify: `CHANGELOG.md`, `ROADMAP.md`, `README.md` if it documents the builder's methods

**Interfaces:**
- Produces: `public static YamlMapping Provenance.UsageWindowToYaml(UsageWindow window)` — the one serialization of a window, used by `Provenance.ToYaml` for per-entry overrides and by the builder for the shared one. Note issue #62: three docs claim "`Provenance.ToYaml` has exactly one caller"; this adds a caller to the NEW helper, not to `ToYaml` — check those docs still hold.
- Produces: `public OkfDocumentBuilder SharedUsageWindow(UsageWindow window)` — sets (overwriting) the top-level `usage_window`. Named `SharedUsageWindow`, not `UsageWindow`, to avoid a method sharing its parameter type's name and to say which window it is (§5.1 calls it "shared").

§5.1 (`docs/spec/SPEC.md` ~line 332): `usage_window` is "Written once as a sibling of `sources`"; it frames every entry's `usage_count`; an entry MAY override it. `Frontmatter.KnownKeys` already orders `"sources", "usage_window"`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void SharedUsageWindow_writes_the_top_level_window_after_sources()
{
    var doc = OkfDocumentBuilder.ForType("metric")
        .AddSource("bigquery://p/d/t", usageCount: 10)
        .SharedUsageWindow(new UsageWindow("2026-06-01T00:00:00Z", "2026-06-30T00:00:00Z"))
        .Body("b")
        .Build();

    Assert.Equal(new UsageWindow("2026-06-01T00:00:00Z", "2026-06-30T00:00:00Z"), doc.Frontmatter.UsageWindow);
    var keys = doc.Frontmatter.Keys.ToList();   // use the real key-enumeration API
    Assert.Equal(keys.IndexOf("sources") + 1, keys.IndexOf("usage_window"));
}

[Fact]
public void A_shared_window_round_trips_through_serialization()
{
    var doc = OkfDocumentBuilder.ForType("metric")
        .SharedUsageWindow(new UsageWindow("2026-06-01T00:00:00Z", null))
        .Body("b")
        .Build();

    var reparsed = OkfDocument.Parse(doc.Serialize());
    Assert.Equal(new UsageWindow("2026-06-01T00:00:00Z", null), reparsed.Frontmatter.UsageWindow);
}

[Fact]
public void An_empty_shared_window_is_written_as_an_empty_mapping_not_dropped()
{
    // Same rule ToYaml applies to a per-entry override: usage_window: {} is present-but-empty.
    var doc = OkfDocumentBuilder.ForType("metric").SharedUsageWindow(new UsageWindow(null, null)).Body("b").Build();
    Assert.Equal(new UsageWindow(null, null), doc.Frontmatter.UsageWindow);
}

[Fact]
public void The_per_entry_override_and_the_shared_window_serialize_identically()
{
    var w = new UsageWindow("2026-06-01T00:00:00Z", "2026-06-30T00:00:00Z");
    var perEntry = (YamlMapping)((YamlMapping)Provenance.ToYaml([new Source(null, "r", null, null, null, null, w)]).Items[0]).Get("usage_window")!;
    Assert.Equal(Provenance.UsageWindowToYaml(w), perEntry);
}

[Fact]
public void An_Extension_call_on_usage_window_still_wins_as_documented()
{
    var doc = OkfDocumentBuilder.ForType("metric")
        .SharedUsageWindow(new UsageWindow("a", "b"))
        .Extension("usage_window", new YamlString("raw"))
        .Body("b")
        .Build();
    Assert.Equal(new YamlString("raw"), doc.Frontmatter.Get("usage_window"));
}
```

Use the real API names (`Frontmatter.Get`, key enumeration, `OkfDocument.Parse`, `Serialize`) — read `OkfDocumentBuilderTests` for the idioms.

- [ ] **Step 2: Run to see them fail** (build fails: no `SharedUsageWindow`, no `UsageWindowToYaml`). Record.

- [ ] **Step 3: Implement.** Extract `UsageWindowToYaml` from `ToYaml`'s inline block (same key order `from, to`; null bounds omitted; empty mapping kept) and call it from `ToYaml`. Builder: a `private UsageWindow? _sharedUsageWindow;` field, the setter, and in `Build` insert `usage_window` right after the `sources` block (whether or not there are sources), before the extensions loop. Update `Build`'s doc: the fixed order becomes `type, title, description, resource, tags, sources, usage_window`, and "the six well-known keys" becomes seven.

- [ ] **Step 4: Run, mutate, verify.** Mutation: move the `usage_window` insertion after the extensions loop → `An_Extension_call...` RED. Restore. Global Constraints commands AND `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Docs and commit.** `CHANGELOG.md` `### Added`: `OkfDocumentBuilder.SharedUsageWindow(UsageWindow)` (§5.1) and `Provenance.UsageWindowToYaml`. `ROADMAP.md`: remove the bullet. Commit: `feat(core): typed builder setter for the shared §5.1 usage_window`.

---

### Task 6: Lot verification

**Files:** none new; fixes only if verification finds something.

- [ ] **Step 1:** `ROADMAP.md` `## Next` no longer lists any of the four items as pending; nothing else in `ROADMAP.md`, `README.md`, `CLAUDE.md` or `src/**` doc comments still describes a fixed gap as current (`grep -rn "#86\|lexical\b.*lock\|half-written\|TWO independent counters\|two independent counters" --include=*.md --include=*.cs .` excluding `docs/superpowers` and `CHANGELOG.md`'s released sections).
- [ ] **Step 2:** `CHANGELOG.md` `[Unreleased]` has exactly one entry per task, Breaking ones prefixed.
- [ ] **Step 3:** Full runs, reported with numbers: `dotnet build OKF4net.sln`, `dotnet format --verify-no-changes`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` three times (the suite has had a rare flake history), `dotnet test producers/OkfProducer.sln`, and `okf validate` on every bundle under `bundles/` compared with the output on `origin/dev`.
- [ ] **Step 4:** `git log --oneline origin/dev..HEAD` — one commit per task, messages true to their diffs.

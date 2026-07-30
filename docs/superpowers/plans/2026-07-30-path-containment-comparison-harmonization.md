# Path-Containment Comparison Harmonization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the four remaining `StringComparison`-heuristic path-containment sites identified in `docs/superpowers/specs/2026-07-30-path-containment-comparison-design.md` (commit `8223cf4`, `dev`) so case-sensitivity is never decided by an `OperatingSystem.IsWindows()||IsMacOS()` guess.

**Architecture:** Three escape-prevention sites (`ReparsePoints.IsWithinBundleRoot`, `ReparsePoints.HasReparsePointAncestor(root, path)` 2-arg, `FileMemoryStore.PathComparison`) move to unconditional `StringComparison.Ordinal`, closing the case-insensitive-volume blind spot the OS heuristic left open — at zero cost to legitimate use, per the design's §3 root-prefix-preservation argument. One misconfiguration-detection site (`MemoryServiceCollectionExtensions`'s memory/knowledge overlap check) moves to unconditional `StringComparison.OrdinalIgnoreCase`, the safer direction for a check whose false-negative is a silent leak and whose false-positive is a harmless startup exception, per the design's §3bis. `CatalogPathResolver` and `IndexGenerator.cs` are explicitly out of scope — already correct, per the design's §2.

**Tech Stack:** C# / .NET 10, xunit, zero third-party runtime dependencies (per project — see Global Constraints).

## Global Constraints

- Zero third-party dependencies in `src/OKF4net/` and `src/OKF4net.Catalog/` (BCL only); `src/OKF4net.Catalog.Hosting/` may reference only `Microsoft.Extensions.DependencyInjection.Abstractions` (already in use — this plan adds no new package reference anywhere).
- `TreatWarningsAsErrors`, nullable enabled, file-scoped namespaces, XML doc comments on public/internal API touched by this plan (all already the case for every method this plan modifies).
- `dotnet format OKF4net.sln --verify-no-changes` must stay clean after every task.
- Full suite (`dotnet test OKF4net.sln`) is currently **834/834** on `dev` — must stay green after every task; no golden-fixture regressions (this plan touches no golden-covered behavior).
- Never edit `tests/fixtures/` (not touched by this plan).
- This is an internal defense-in-depth hardening, not an OKF spec-behavior change — no §-citation applies; the design doc's §3/§3bis is the citation of record for every choice below.
- Do not introduce a runtime case-sensitivity probe or any OS-conditional comparison anywhere in this plan — the design's §4 considered and explicitly rejected that approach as solving no problem on any current call site.
- Base branch: `dev` (`E:/Sources/okf`). Work in an isolated git worktree per `superpowers:using-git-worktrees`.

---

### Task 1: Harden `ReparsePoints`' two OS-heuristic-adjacent helpers to unconditional `Ordinal`

**Files:**
- Modify: `src/OKF4net/Internal/ReparsePoints.cs:160-165` (`HasReparsePointAncestor(string bundleRoot, string path)` 2-arg overload + its doc comment at lines 139-159)
- Modify: `src/OKF4net/Internal/ReparsePoints.cs:200-220` (`IsWithinBundleRoot` + its doc comment)
- Test: `tests/OKF4net.Tests/ReparsePointsTests.cs`

**Interfaces:**
- Consumes: nothing new — both methods keep their exact existing signatures (`internal static bool HasReparsePointAncestor(string bundleRoot, string path)`, `internal static bool IsWithinBundleRoot(string root, string candidate)`). Every current caller (`Bundle.cs:404`, `BundleConceptWriter.cs:415,426,550`, `OkfBundleTools.cs`) needs no change — the design's whole point is hardening the shared helper transitively fixes every caller, including `Bundle.cs:404`'s residual P1 gap (it calls the 2-arg overload; once the overload itself uses `Ordinal`, that call site is fixed with no edit).
- Produces: nothing new for later tasks — Tasks 2 and 3 touch unrelated files (`FileMemoryStore.cs`, `MemoryServiceCollectionExtensions.cs`) with their own local comparison logic, not this method.

**Note on test shape:** `IsWithinBundleRoot` has zero disk I/O (`CanonicalizeRoot`/`Path.GetFullPath` are purely lexical), so a pure-string test fully and sufficiently exercises its fix — a junction-based test would be redundant. `HasReparsePointAncestor`'s `rootComparison` parameter, by contrast, only controls when its upward *disk walk* recognizes it has reached `root` (see its own doc comment) — the only way to discriminate `Ordinal` from `OrdinalIgnoreCase` there is a real reparse point somewhere the walk would reach only if it does *not* stop early at a case-variant of `root`. That needs a real junction. This is a deliberate difference in test shape between the two methods, not an inconsistency.

- [ ] **Step 1: Add the two failing tests to `ReparsePointsTests.cs`**

Add both methods at the end of the `ReparsePointsTests` class (before the closing `}` on line 131):

```csharp
    [Fact]
    public void IsWithinBundleRoot_ordinal_rejects_case_variant_sibling_but_accepts_exact_case_descendant()
    {
        var root = $"{Sep}tmp{Sep}Bundle";
        var caseVariantSibling = $"{Sep}tmp{Sep}bundle{Sep}secret.md";
        var exactCaseDescendant = $"{Sep}tmp{Sep}Bundle{Sep}secret.md";

        Assert.False(ReparsePoints.IsWithinBundleRoot(root, caseVariantSibling));
        Assert.True(ReparsePoints.IsWithinBundleRoot(root, exactCaseDescendant));
    }

    /// <summary>
    /// Pins <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>'s
    /// own root-comparison, independent of how today's callers happen to use
    /// it: a <c>root</c> argument that is a CASE-VARIANT of a real ancestor
    /// directory must not be treated as "root reached" -- the walk must keep
    /// going past it, so a genuine reparse point further up is still found.
    /// Constructed with a junction ABOVE the case-variant point (rather than
    /// relying on any specific caller's containment check running first) so
    /// this test exercises the helper's own contract in isolation, not a
    /// scenario that depends on other code.
    /// </summary>
    [Fact]
    public void HasReparsePointAncestor_two_arg_ordinal_does_not_stop_early_on_a_case_variant_root()
    {
        using var outer = new TempDir();
        using var external = new TempDir();

        if (!outer.TryCreateJunctionToExternalDir("Linked", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var trueRoot = Path.Combine(outer.Path, "Linked", "Bundle");
        Directory.CreateDirectory(trueRoot);
        var nested = Path.Combine(trueRoot, "a");
        Directory.CreateDirectory(nested);
        var caseVariantRoot = Path.Combine(outer.Path, "Linked", "bundle");

        Assert.True(ReparsePoints.HasReparsePointAncestor(caseVariantRoot, nested));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReparsePointsTests"`
Expected: 2 new FAILs —
`IsWithinBundleRoot_ordinal_rejects_case_variant_sibling_but_accepts_exact_case_descendant` fails on the first `Assert.False` (current code returns `true` for the case-variant sibling); `HasReparsePointAncestor_two_arg_ordinal_does_not_stop_early_on_a_case_variant_root` fails on `Assert.True` (current code returns `false`, stopping early at the case-insensitive match). (If the machine lacks junction/symlink privilege, the second test returns early and reports as passed/skipped rather than failed — that's expected on such a machine; verify the first test still fails.)

- [ ] **Step 3: Harden `HasReparsePointAncestor(string bundleRoot, string path)`**

Replace the 2-arg overload and its doc comment (`src/OKF4net/Internal/ReparsePoints.cs:139-165`):

```csharp
    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory
    /// strictly between it and <paramref name="bundleRoot"/>, is a
    /// filesystem reparse point (symlink, junction, mount point) -- resolves
    /// <paramref name="bundleRoot"/> via <see cref="CanonicalizeRoot"/> (see
    /// its remarks for why a bare <see cref="Path.GetFullPath(string)"/>
    /// is not enough here), then delegates to
    /// <see cref="HasReparsePointAncestor(string, string, StringComparison)"/>
    /// with <see cref="StringComparison.Ordinal"/>. Complements
    /// <see cref="IsWithinBundleRoot"/>: that check only compares resolved
    /// path STRINGS, so a junction that lexically resolves under
    /// <paramref name="bundleRoot"/> still passes it even though the OS
    /// follows the junction the moment something actually touches disk
    /// (<see cref="Directory.Exists(string)"/>,
    /// <see cref="File.ReadAllText(string)"/>, <see cref="File.WriteAllText(string, string)"/>)
    /// -- silently reading or writing outside the bundle. Walking every
    /// intermediate directory and rejecting on the first reparse point closes
    /// that gap. Never inspects <paramref name="path"/> itself -- a caller
    /// whose target could itself be a planted file symlink (not just an
    /// ancestor directory) must separately check <see cref="IsReparsePoint"/>
    /// on it.
    /// </summary>
    /// <remarks>
    /// <see cref="StringComparison.Ordinal"/> on every platform, not an
    /// OS-conditional choice -- case-sensitivity is a runtime property of the
    /// specific volume, not of the OS: APFS/HFS+ can be configured
    /// case-sensitive, and a volume mounted on Linux can be case-insensitive
    /// (FAT/exFAT, a case-folding network share). Every legitimate caller's
    /// <paramref name="path"/> is built via <see cref="Path.Combine(string, string)"/>
    /// from the same <paramref name="bundleRoot"/> passed to this method, so
    /// its prefix always keeps <paramref name="bundleRoot"/>'s exact casing --
    /// <c>Ordinal</c> costs nothing for legitimate input, and closes the same
    /// case-variant escape <see cref="IsWithinBundleRoot"/> closes.
    /// </remarks>
    internal static bool HasReparsePointAncestor(string bundleRoot, string path)
    {
        var fullRoot = CanonicalizeRoot(bundleRoot);
        var current = Path.GetFullPath(path);
        return HasReparsePointAncestor(fullRoot, current, StringComparison.Ordinal);
    }
```

- [ ] **Step 4: Harden `IsWithinBundleRoot`**

Replace the method and its doc comment (`src/OKF4net/Internal/ReparsePoints.cs:200-220`):

```csharp
    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, resolving <paramref name="root"/> via
    /// <see cref="CanonicalizeRoot"/> (so <paramref name="candidate"/> equal
    /// to <paramref name="root"/> itself still matches <see cref="IsWithin"/>'s
    /// exact-equality check even when <paramref name="root"/> has a trailing
    /// separator -- see <see cref="CanonicalizeRoot"/>'s remarks) and
    /// comparing with <see cref="StringComparison.Ordinal"/> on every
    /// platform. A lexical check alone: a junction/symlink among
    /// <paramref name="candidate"/>'s ancestors can still resolve here even
    /// though the OS would follow it to somewhere else entirely once actual
    /// I/O touches disk -- pair with <see cref="HasReparsePointAncestor(string, string)"/>
    /// for that.
    /// </summary>
    /// <remarks>
    /// Case-sensitivity is a runtime property of the specific volume, not of
    /// the OS: APFS/HFS+ can be configured case-sensitive, and a volume
    /// mounted on Linux can be case-insensitive (FAT/exFAT, a case-folding
    /// network share) -- an OS-conditional comparison leaves an escape open
    /// on exactly the combination it assumes cannot occur.
    /// <see cref="StringComparison.Ordinal"/> has no cost for legitimate
    /// input: every legitimate <paramref name="candidate"/> is built via
    /// <see cref="Path.Combine(string, string)"/> from the same
    /// <paramref name="root"/> passed to this method (a purely lexical
    /// operation that never re-cases from disk), so its prefix always keeps
    /// <paramref name="root"/>'s exact casing. Only a <c>..</c> climb
    /// re-entering a case-variant sibling of <paramref name="root"/> itself
    /// produces a mismatched prefix -- precisely the escape this method must
    /// reject.
    /// </remarks>
    internal static bool IsWithinBundleRoot(string root, string candidate)
    {
        var fullRoot = CanonicalizeRoot(root);
        var fullCandidate = Path.GetFullPath(candidate);
        return IsWithin(fullRoot, fullCandidate, StringComparison.Ordinal);
    }
```

- [ ] **Step 5: Run the two new tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReparsePointsTests"`
Expected: PASS (all tests in the class, including the 2 new ones and every pre-existing one — the trailing-separator regression tests must still pass unchanged).

- [ ] **Step 6: Run the broader regression set (callers of these two helpers)**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleTests|FullyQualifiedName~BundleConceptWriterTests|FullyQualifiedName~OkfBundleToolsTests|FullyQualifiedName~FrontmatterResourceTests"`
Expected: PASS, same counts as before this task (no test in these classes exercises a case-variant scenario against `Bundle`/`BundleConceptWriter`/`OkfBundleTools`, so none should change status).

- [ ] **Step 7: Full build + format check**

Run: `dotnet build OKF4net.sln` (expect 0 warnings) then `dotnet format OKF4net.sln --verify-no-changes` (expect clean).

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net/Internal/ReparsePoints.cs tests/OKF4net.Tests/ReparsePointsTests.cs
git commit -m "fix(core): harden ReparsePoints containment helpers to unconditional Ordinal

IsWithinBundleRoot and the 2-arg HasReparsePointAncestor hardcoded
OrdinalIgnoreCase, leaving the same case-insensitive-volume escape the
P1 fix (Bundle.PathComparison) already closed for Bundle.cs's own
IsWith call -- including a residual gap where Bundle.cs:404 called
straight into this now-fixed 2-arg overload. Every legitimate caller
builds its candidate via Path.Combine from the same root passed in, so
its prefix always keeps the root's exact casing: Ordinal costs nothing
for legitimate input (see docs/superpowers/specs/2026-07-30-path-containment-comparison-design.md §3)."
```

---

### Task 2: Harden `FileMemoryStore.PathComparison` to unconditional `Ordinal`

**Files:**
- Modify: `src/OKF4net.Catalog/FileMemoryStore.cs:17-18`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing new for later tasks.

**Note on test shape — no new test is added in this task, deliberately:** `IsReparseEscaped` (the sole caller of this field, at `FileMemoryStore.cs:233`) always builds its `subDir` argument via `ScopedDir(root, prefix)` = `Path.Combine([root, ...])` from the *same* `root` value it then passes alongside `subDir` into `IsReparseEscaped(root, subDir)`. That means the candidate's prefix always exactly matches `root`'s casing for every call reachable through `FileMemoryStore`'s public API (`ReadAsync`/`WriteAsync`/`DeleteScopeAsync`/`EnumerateAsync` all follow this same construction) — there is no way to reach `IsReparseEscaped` with a candidate whose prefix mismatches `root`'s case, so no test constructed against the public API can discriminate `Ordinal` from `OrdinalIgnoreCase` here (both give the identical result for every reachable call). This mirrors the design's own §3 argument exactly. The existing `Reparse_escaped_scope_directory_reports_a_diagnostic_and_never_throws` test in `FileMemoryStoreTests.cs` already provides regression coverage that the reparse-detection mechanism itself keeps working after this change — Step 2 below runs it explicitly.

- [ ] **Step 1: Harden the field**

Replace `src/OKF4net.Catalog/FileMemoryStore.cs:17-18`:

```csharp
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
```

- [ ] **Step 2: Run `FileMemoryStoreTests` to confirm no regression**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileMemoryStoreTests"`
Expected: PASS, same count as before this task (this is a regression check, not a RED/GREEN cycle — no new test was added, per the rationale above).

- [ ] **Step 3: Full build + format check**

Run: `dotnet build OKF4net.sln` (expect 0 warnings) then `dotnet format OKF4net.sln --verify-no-changes` (expect clean).

- [ ] **Step 4: Commit**

```bash
git add src/OKF4net.Catalog/FileMemoryStore.cs
git commit -m "fix(catalog): harden FileMemoryStore.PathComparison to unconditional Ordinal

Same case-insensitive-volume argument as the ReparsePoints fix: case
sensitivity is a property of the mounted volume, not the OS. No new
test is added -- IsReparseEscaped's candidate is always Path.Combine'd
from the same root it's compared against, so no reachable call through
FileMemoryStore's public API can discriminate Ordinal from
OrdinalIgnoreCase here (see docs/superpowers/specs/2026-07-30-path-containment-comparison-design.md §3); the existing reparse-escape test already covers the mechanism."
```

---

### Task 3: Harden `MemoryServiceCollectionExtensions`'s overlap check to unconditional `OrdinalIgnoreCase`

**Files:**
- Modify: `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs` (delete the `PathComparison` field at lines 110-119; update `ThrowIfMemoryOverlapsKnowledge`'s doc comment at lines 121-128; update the private `IsWithin` helper at lines 148-164)
- Test: `tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Add the failing test**

Add to `MemoryServiceCollectionExtensionsTests.cs`, after `AddMemory_throws_when_a_memory_root_is_nested_within_a_knowledge_root` (after line 88):

```csharp
    [Fact]
    public void AddMemory_throws_when_a_memory_root_overlaps_a_knowledge_root_only_by_case()
    {
        // Portable regression: pins that the comparison is OrdinalIgnoreCase
        // unconditionally, not the OS-conditional heuristic that used to
        // pick Ordinal on Linux and miss this. Both "kb" and "KB/mem" are
        // created for real (not just referenced by string) so this passes
        // identically regardless of the actual host's case-sensitivity: on a
        // case-insensitive volume the two paths are the same physical
        // subtree (a genuine overlap); on a case-sensitive one they are two
        // independent directories that this check deliberately still
        // rejects, since over-detection here is harmless (see
        // ThrowIfMemoryOverlapsKnowledge's remarks) while a missed overlap
        // is not.
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "kb"));
        Directory.CreateDirectory(Path.Combine(root.Path, "KB", "mem"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./KB/mem", "role": "memory", "tier": "user" }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IMemoryStore>());
        Assert.Contains("user-mem", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kb", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~AddMemory_throws_when_a_memory_root_overlaps_a_knowledge_root_only_by_case"`
Expected: FAIL on the `Assert.Throws` line when run on a case-sensitive filesystem (Linux CI: the old heuristic picks `Ordinal`, `IsWithin` says no overlap, `AddMemory` builds cleanly, no exception is thrown). On a case-insensitive host (Windows/macOS dev machines), the old heuristic already picked `OrdinalIgnoreCase`, so this specific test may already PASS there — that's expected and does not indicate a problem; the fix's value is in unifying the two platforms' behavior, and Step 4 below re-runs the test to confirm it passes after the fix regardless of host.

- [ ] **Step 3: Delete the `PathComparison` field and harden `IsWithin`**

Delete `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs:110-119` in full (the `PathComparison` field and its doc comment). Then replace the doc comment on `ThrowIfMemoryOverlapsKnowledge` (lines 121-128 in the current file) and the `IsWithin` method (lines 148-164) with:

```csharp
    /// <summary>
    /// Fail-fast: a memory root that equals or nests within a knowledge root
    /// (or vice-versa) would be walked and searched by
    /// <see cref="GroupedKnowledgeResolver"/> as if it were shared knowledge,
    /// defeating scoped-memory isolation. The operator must reconfigure disjoint
    /// roots, so this throws an <see cref="InvalidOperationException"/> naming
    /// the offending source ids rather than silently building a leaky store.
    /// </summary>
    /// <remarks>
    /// <see cref="IsWithin"/> compares with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> unconditionally, not
    /// an OS-conditional heuristic: unlike an escape-prevention check, a
    /// missed overlap here (false negative) is a silent memory-to-knowledge
    /// leak, while an over-detected one (false positive) is only a startup
    /// exception -- the safe direction favors the more permissive comparison.
    /// Testing <c>Ordinal</c> in addition would add nothing: for any fixed
    /// pair of strings, an <c>Ordinal</c> match always implies an
    /// <c>OrdinalIgnoreCase</c> match, so it can never change this method's
    /// verdict.
    /// </remarks>
    private static void ThrowIfMemoryOverlapsKnowledge(
        IReadOnlyList<(string Id, string Root)> memoryRoots,
        IReadOnlyList<(string Id, string Root)> knowledgeRoots)
    {
        foreach (var (memId, memRoot) in memoryRoots)
        {
            foreach (var (knowId, knowRoot) in knowledgeRoots)
            {
                if (IsWithin(knowRoot, memRoot) || IsWithin(memRoot, knowRoot))
                {
                    throw new InvalidOperationException(
                        $"Memory source '{memId}' root '{memRoot}' overlaps knowledge source '{knowId}' root '{knowRoot}': a memory root must be "
                        + "disjoint from every knowledge root, otherwise the scoped-memory subtree would be walked and searched as shared knowledge "
                        + "by the resolver. Reconfigure the sources so their roots do not nest.");
                }
            }
        }
    }

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing full paths with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> unconditionally (see
    /// <see cref="ThrowIfMemoryOverlapsKnowledge"/>'s remarks for why).
    /// Mirrors <c>OKF4net.Internal.ReparsePoints.IsWithin</c> (not visible to
    /// this assembly) rather than duplicating its containment convention
    /// loosely.
    /// </summary>
    private static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 4: Run the new test and the existing class to verify everything passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~MemoryServiceCollectionExtensionsTests"`
Expected: PASS — all 5 tests in the class (4 pre-existing + the 1 new one), on every OS.

- [ ] **Step 5: Full build + format check**

Run: `dotnet build OKF4net.sln` (expect 0 warnings) then `dotnet format OKF4net.sln --verify-no-changes` (expect clean).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs
git commit -m "fix(catalog-hosting): use unconditional OrdinalIgnoreCase for memory/knowledge overlap check

The OS-conditional heuristic picked Ordinal on Linux, missing a
memory/knowledge root overlap that only differs by case on a
case-insensitive volume mounted there. This check's safe direction is
inverted from an escape-prevention check (missing an overlap is the
dangerous outcome, over-detecting one is just a startup exception), so
it now uses OrdinalIgnoreCase unconditionally -- proven strictly
equivalent to testing both Ordinal and OrdinalIgnoreCase and rejecting
if either detects an overlap, since an Ordinal match always implies an
OrdinalIgnoreCase match for the same pair of strings (see
docs/superpowers/specs/2026-07-30-path-containment-comparison-design.md §3bis).
The OS-heuristic field is deleted, not replaced by a dual-comparison test."
```

---

### Task 4: CHANGELOG entry and final whole-suite verification

**Files:**
- Modify: `CHANGELOG.md` (`[Unreleased]` section)

**Interfaces:** None — documentation-only, no code interfaces.

- [ ] **Step 1: Add the CHANGELOG entry**

In `CHANGELOG.md`, replace:

```markdown
## [Unreleased]

## [0.3.1-preview.1] - 2026-07-30
```

with:

```markdown
## [Unreleased]

### Fixed

- **Path-containment comparisons no longer guess case-sensitivity from the OS.**
  `ReparsePoints.IsWithinBundleRoot`, the 2-arg `ReparsePoints.HasReparsePointAncestor`,
  and `FileMemoryStore`'s reparse-escape check hardcoded `OrdinalIgnoreCase`
  (or picked it via an `IsWindows()||IsMacOS()` heuristic), leaving the same
  case-insensitive-volume escape the earlier `Bundle.PathComparison` fix
  closed for `Bundle.TryResolveResource` open at these sites — including a
  residual gap where `Bundle.cs` itself called straight into the still-vulnerable
  2-arg `HasReparsePointAncestor` overload. All three now use
  `StringComparison.Ordinal` unconditionally, at no cost to legitimate use:
  every candidate path at these sites is built via `Path.Combine` from the
  same root it's compared against, so its prefix always keeps that root's
  exact casing. Separately, `MemoryServiceCollectionExtensions`'s
  memory/knowledge root overlap check — a misconfiguration-detection check
  whose safe direction is inverted from the escape-prevention sites above —
  now uses `StringComparison.OrdinalIgnoreCase` unconditionally instead of
  the same OS heuristic, catching a case-variant overlap that the heuristic
  previously missed on Linux.

## [0.3.1-preview.1] - 2026-07-30
```

- [ ] **Step 2: Full suite verification**

Run: `dotnet build OKF4net.sln` (expect 0 warnings), `dotnet test OKF4net.sln` (expect 838/838 — the 834 baseline plus this plan's 3 new tests: Task 1's two plus Task 3's one), `dotnet format OKF4net.sln --verify-no-changes` (expect clean).

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the path-containment comparison harmonization"
```

---

## Post-plan

Once Task 4 is committed and the full suite is green, use `superpowers:finishing-a-development-branch` to integrate this plan's branch back into `dev`.

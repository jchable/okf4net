# Resolver Strategies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `OKF4net.Catalog`'s single fixed grouped-by-source resolver with three selectable `IKnowledgeResolver` strategies (grouped, merged, priority-weighted), routed per host or per query.

**Architecture:** The existing resolver is renamed `GroupedKnowledgeResolver` and frozen behaviourally. Two new fused resolvers share one internal engine (source-level dedup → fan-out → stale filter → sort → optional fairness reorder) and differ only in their final comparator. A `KnowledgeResolverRouter` implementing `IKnowledgeResolver` owns all three and dispatches per call on `query.ResolverStrategy ?? defaultStrategy`; DI registers the router, so every existing consumer keeps working unchanged.

**Tech Stack:** C# / net10.0, xunit. No new packages anywhere.

**Spec:** `docs/design/specs/2026-07-28-okf4net-v2-resolver-strategies.md`

## Global Constraints

- **Zero third-party runtime dependencies in `src/OKF4net.Catalog/`** — BCL + a project reference to `OKF4net` only. Do not add any `PackageReference`.
- `src/OKF4net.Catalog.Hosting/` may reference only `Microsoft.Extensions.DependencyInjection.Abstractions` (already present). It depends on `OKF4net.Catalog`; **`OKF4net.Catalog` must never reference `OKF4net.Catalog.Hosting`** — the graph is acyclic.
- Every new source file starts with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces; nullable enabled; XML doc comments on **all** public API (`TreatWarningsAsErrors` makes a missing or broken `<see cref>` a build error).
- `dotnet build OKF4net.sln -c Release` must report **0 warnings, 0 errors**.
- `dotnet format OKF4net.sln --verify-no-changes` must stay clean.
- `dotnet test OKF4net.sln -c Release` — currently **662/662 green**; must stay green, growing only by this plan's new tests.
- Never edit anything under `tests/fixtures/` — those are byte-exact golden captures.
- This plan touches no CLI code and no golden fixtures; `GoldenParityTests` must remain untouched and passing.

---

### Task 1: Rename `DefaultKnowledgeResolver` → `GroupedKnowledgeResolver`

Purely mechanical rename with **zero behaviour change**. "Default" stops being meaningful once three strategies exist. `TreatWarningsAsErrors` turns every stale `<see cref="DefaultKnowledgeResolver"/>` into a build error, so all 32 references must move together in one commit.

**Files:**
- Rename: `src/OKF4net.Catalog/DefaultKnowledgeResolver.cs` → `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`
- Rename: `tests/OKF4net.Tests/Catalog/DefaultKnowledgeResolverTests.cs` → `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`
- Modify (`<see cref>` / usage updates only): `src/OKF4net.Catalog/IKnowledgeCatalog.cs`, `src/OKF4net.Catalog/IKnowledgeResolver.cs`, `src/OKF4net.Catalog/IKnowledgeSource.cs`, `src/OKF4net.Catalog/KnowledgePassage.cs`, `src/OKF4net.Catalog/KnowledgeQuery.cs`, `src/OKF4net.Catalog/OkfBundleKnowledgeSource.cs`, `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`, `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs`, `tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs`, `tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class GroupedKnowledgeResolver : IKnowledgeResolver` with the unchanged constructor `GroupedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null)` and `ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)`. Every later task refers to this name.

- [ ] **Step 1: Rename both files with git**

```bash
git mv src/OKF4net.Catalog/DefaultKnowledgeResolver.cs src/OKF4net.Catalog/GroupedKnowledgeResolver.cs
git mv tests/OKF4net.Tests/Catalog/DefaultKnowledgeResolverTests.cs tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs
```

- [ ] **Step 2: Replace every occurrence of the identifier across src and tests**

Replace the exact token `DefaultKnowledgeResolver` with `GroupedKnowledgeResolver` in every `.cs` file under `src/` and `tests/`. This covers the class declaration, the test class name `DefaultKnowledgeResolverTests` → `GroupedKnowledgeResolverTests`, all `<see cref="..."/>` references, all `new DefaultKnowledgeResolver(...)` call sites, and the plain-prose mentions in comments.

Verify none remain:

```bash
grep -rn "DefaultKnowledgeResolver" --include="*.cs" src/ tests/
```

Expected: no output.

- [ ] **Step 3: Update the renamed class's own summary line**

In `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`, the class summary currently opens with `The V1 <see cref="IKnowledgeResolver"/>: fans a query out ...`. Replace that opening phrase so it names the strategy rather than a version:

```csharp
/// <summary>
/// The grouped-by-source <see cref="IKnowledgeResolver"/> strategy: fans a
/// query out across every currently enabled <see cref="SourceRole.Knowledge"/>
/// source of an <see cref="IKnowledgeCatalog"/> and concatenates the results
/// **grouped by source, in priority order** -- no cross-source fusion,
/// deduplication, or merged ranking. <see cref="SourceRole.Memory"/> sources
/// are never searched here; they feed <c>IMemoryStore</c> instead (spec §5.3).
/// </summary>
```

Leave the `<remarks>` block and the whole method body **exactly as they are**. This task changes no behaviour.

- [ ] **Step 4: Update the renamed test class's summary line**

In `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`, the summary's first line reads `<see cref="GroupedKnowledgeResolver"/>: multi-source fan-out grouped by` after Step 2's replacement — that is already correct. No further edit needed here; this step is only to confirm it reads sensibly.

- [ ] **Step 5: Build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **662/662** passing (unchanged count — this task adds no tests).

- [ ] **Step 6: Commit**

```bash
git add -A src/ tests/
git commit -m "refactor(catalog): rename DefaultKnowledgeResolver to GroupedKnowledgeResolver

'Default' stops meaning anything once three resolver strategies exist side
by side; GroupedBySource names what this one actually does. Pure rename,
no behaviour change."
```

---

### Task 2: `KnowledgeResolverStrategy` enum and `KnowledgeQuery` selection fields

Adds the strategy vocabulary and the per-query override fields. No resolver reads them yet — that is Task 6's router. Isolated and independently testable.

**Files:**
- Create: `src/OKF4net.Catalog/KnowledgeResolverStrategy.cs`
- Modify: `src/OKF4net.Catalog/KnowledgeQuery.cs`
- Test: `tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs` (create)

**Interfaces:**
- Consumes: `GroupedKnowledgeResolver` (Task 1) — referenced only in doc comments.
- Produces: `public enum KnowledgeResolverStrategy { GroupedBySource, Merged, PriorityWeighted }` and, on `KnowledgeQuery`, `public KnowledgeResolverStrategy? ResolverStrategy { get; init; }` plus `public int? FairnessQuota { get; init; }`. Tasks 3–6 consume both.

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="KnowledgeQuery"/>'s per-query resolver-selection fields: both
/// default to <see langword="null"/> ("defer to the host default") and both
/// survive a <c>with</c>-expression, so a caller can override one without
/// disturbing the other or the query's pre-existing fields.
/// </summary>
public class KnowledgeQueryTests
{
    [Fact]
    public void Resolver_selection_fields_default_to_null()
    {
        var query = new KnowledgeQuery("orders");

        Assert.Null(query.ResolverStrategy);
        Assert.Null(query.FairnessQuota);
    }

    [Fact]
    public void Resolver_selection_fields_round_trip_through_an_initializer()
    {
        var query = new KnowledgeQuery("orders", "sales")
        {
            StalePolicy = StalePolicy.Strict,
            ResolverStrategy = KnowledgeResolverStrategy.Merged,
            FairnessQuota = 2,
        };

        Assert.Equal(KnowledgeResolverStrategy.Merged, query.ResolverStrategy);
        Assert.Equal(2, query.FairnessQuota);
        Assert.Equal(StalePolicy.Strict, query.StalePolicy);
        Assert.Equal("sales", query.Tag);
    }

    [Fact]
    public void Overriding_one_selection_field_leaves_the_others_intact()
    {
        var original = new KnowledgeQuery("orders")
        {
            ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted,
            FairnessQuota = 3,
        };

        var narrowed = original with { FairnessQuota = 1 };

        Assert.Equal(KnowledgeResolverStrategy.PriorityWeighted, narrowed.ResolverStrategy);
        Assert.Equal(1, narrowed.FairnessQuota);
        Assert.Equal(3, original.FairnessQuota);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeQueryTests"
```

Expected: **build failure** — `KnowledgeResolverStrategy` does not exist, and `KnowledgeQuery` has no `ResolverStrategy`/`FairnessQuota` members.

- [ ] **Step 3: Create the enum**

Create `src/OKF4net.Catalog/KnowledgeResolverStrategy.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Which ranking algorithm an <see cref="IKnowledgeResolver"/> search uses.
/// Selected per host (see <c>KnowledgeOptions.DefaultResolverStrategy</c> in
/// <c>OKF4net.Catalog.Hosting</c>) or per call
/// (<see cref="KnowledgeQuery.ResolverStrategy"/>, which overrides the host
/// default); <see cref="KnowledgeResolverRouter"/> is what dispatches on it.
/// </summary>
public enum KnowledgeResolverStrategy
{
    /// <summary>
    /// Concatenate each enabled source's own descending-score results,
    /// source by source, in descending <see cref="KnowledgeCatalogSource.Priority"/>
    /// then ascending ordinal <see cref="KnowledgeCatalogSource.Id"/> order --
    /// no cross-source fusion, deduplication, or merged ranking. The
    /// original (and default) behaviour; see
    /// <see cref="GroupedKnowledgeResolver"/>.
    /// </summary>
    GroupedBySource,

    /// <summary>
    /// Merge every source's results into one list ranked by descending
    /// <see cref="KnowledgePassage.Score"/> across all sources, with
    /// <see cref="KnowledgeCatalogSource.Priority"/> as a tie-break only.
    /// See <see cref="MergedKnowledgeResolver"/>.
    /// </summary>
    Merged,

    /// <summary>
    /// Merge every source's results into one list ranked by descending
    /// <see cref="KnowledgeCatalogSource.Priority"/> FIRST, with
    /// <see cref="KnowledgePassage.Score"/> ordering only within a single
    /// priority tier -- so a higher-priority source's passage never falls
    /// behind a lower-priority one regardless of match strength. See
    /// <see cref="PriorityWeightedKnowledgeResolver"/>.
    /// </summary>
    PriorityWeighted,
}
```

Note: this file references `MergedKnowledgeResolver`, `PriorityWeightedKnowledgeResolver`, and `KnowledgeResolverRouter`, which do not exist until Tasks 4–6. A `<see cref>` to a missing type is a **build error** here. So, for this task only, write those three names as `<c>MergedKnowledgeResolver</c>`, `<c>PriorityWeightedKnowledgeResolver</c>`, and `<c>KnowledgeResolverRouter</c>` (plain code font, not `see cref`). Task 6's final step converts all three to real `<see cref>` links once the types exist.

- [ ] **Step 4: Add the two fields to `KnowledgeQuery`**

In `src/OKF4net.Catalog/KnowledgeQuery.cs`, the record body currently holds one member:

```csharp
public sealed record KnowledgeQuery(string Text, string? Tag = null)
{
    /// <summary>How stale concepts (§5.5) are treated. Default <see cref="StalePolicy.Use"/>: surface everything.</summary>
    public StalePolicy StalePolicy { get; init; }
}
```

Add the two new members after `StalePolicy`, leaving `StalePolicy` and the record's positional parameters untouched:

```csharp
public sealed record KnowledgeQuery(string Text, string? Tag = null)
{
    /// <summary>How stale concepts (§5.5) are treated. Default <see cref="StalePolicy.Use"/>: surface everything.</summary>
    public StalePolicy StalePolicy { get; init; }

    /// <summary>
    /// Which ranking strategy to use for this one search, overriding the
    /// host's configured default. <see langword="null"/> (the default) defers
    /// to that host default -- it does NOT mean
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/>. Only
    /// <see cref="KnowledgeResolverRouter"/> reads this; a concrete resolver
    /// used directly implements exactly one strategy and ignores it.
    /// </summary>
    public KnowledgeResolverStrategy? ResolverStrategy { get; init; }

    /// <summary>
    /// The maximum number of CONSECUTIVE passages one source may contribute
    /// to a fused result before a different source's next-best passage is
    /// pulled ahead of it. <see langword="null"/> (the default) defers to the
    /// host's configured default, which is itself <see langword="null"/>
    /// (disabled -- pure ranked order) unless configured otherwise.
    /// <para>
    /// Reordering only: no passage is ever dropped, so a caller that consumes
    /// the whole result gets the same set either way. It exists for callers
    /// that truncate early -- an agent context provider spending a token
    /// budget top-down, for instance, which would otherwise let one prolific
    /// source crowd out every other source's best material.
    /// </para>
    /// <para>
    /// Meaningful only for <see cref="KnowledgeResolverStrategy.Merged"/> and
    /// <see cref="KnowledgeResolverStrategy.PriorityWeighted"/>;
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/> ignores it
    /// entirely (its output is grouped by source by definition). Must be
    /// greater than zero when set.
    /// </para>
    /// </summary>
    public int? FairnessQuota { get; init; }
}
```

Same caveat as Step 3: `KnowledgeResolverRouter` does not exist yet. Write it as `<c>KnowledgeResolverRouter</c>` here; Task 6 converts it.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeQueryTests"
```

Expected: 3 passing.

- [ ] **Step 6: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **665/665** passing (662 + 3 new).

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeResolverStrategy.cs src/OKF4net.Catalog/KnowledgeQuery.cs tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs
git commit -m "feat(catalog): add KnowledgeResolverStrategy and per-query selection fields

Both KnowledgeQuery fields default to null meaning 'defer to the host
default', not 'grouped' -- nothing reads them until the router lands."
```

---

### Task 3: Fused-resolver engine — source dedup, fan-out, stale filter, sort

The shared machinery both fused strategies run on, plus `MergedKnowledgeResolver` as its first consumer. Fairness reordering is deliberately **not** in this task (Task 5) so the ranking and dedup semantics can be reviewed on their own.

Source dedup happens **before** the fan-out: if two enabled source entries resolve to the same directory, only the surviving one is searched, so the bundle is loaded and scored once rather than twice-then-halved.

**Files:**
- Create: `src/OKF4net.Catalog/FusedResolverEngine.cs`
- Create: `src/OKF4net.Catalog/MergedKnowledgeResolver.cs`
- Modify: `src/OKF4net.Catalog/CatalogPathResolver.cs:53` (widen `PathComparison` from `private` to `internal`)
- Test: `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs` (create)

**Interfaces:**
- Consumes: `GroupedKnowledgeResolver` (Task 1, doc references only); `KnowledgeResolverStrategy` (Task 2, doc references only).
- Produces:
  - `internal readonly record struct RankedPassage(KnowledgePassage Passage, int Priority)`
  - `internal static class FusedResolverEngine` with
    `internal static async ValueTask<KnowledgeContext> SearchAsync(IKnowledgeCatalog catalog, IOkfClock clock, KnowledgeQuery query, IComparer<RankedPassage> comparer, int? fairnessQuota, CancellationToken ct)`
  - `public sealed class MergedKnowledgeResolver : IKnowledgeResolver` with constructor `MergedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)`
  - `internal static readonly StringComparison CatalogPathResolver.PathComparison`

  Task 4 reuses `FusedResolverEngine` and `RankedPassage` with a different comparer. Task 5 adds the fairness step inside the engine. Task 6 constructs `MergedKnowledgeResolver`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="MergedKnowledgeResolver"/>: one cross-source ranking by
/// descending score (priority as tie-break only), source-level dedup of two
/// manifest entries resolving to the same directory, and the shared
/// never-throw/errors-as-data contract inherited from the fused engine.
/// Exercised over <see cref="TempDir"/> copies of the
/// <c>tests/fixtures/appendix_a</c> bundle, never touching
/// <c>tests/fixtures</c> directly.
/// </summary>
public class MergedKnowledgeResolverTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private static FileKnowledgeCatalog BuildCatalog(TempDir root, string sourcesJson)
    {
        root.Write("catalog.json", $$"""
            {
              "version": 1,
              "sources": [{{sourcesJson}}]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    /// <summary>Two fixture copies as two distinct sources: "hi" (priority 10) and "lo" (priority 1).</summary>
    private static FileKnowledgeCatalog SetUpTwoSourceCatalog(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-hi"));
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-lo"));

        return BuildCatalog(root, """
            { "id": "lo", "path": "./source-lo", "priority": 1, "enabled": true },
            { "id": "hi", "path": "./source-hi", "priority": 10, "enabled": true }
            """);
    }

    [Fact]
    public async Task Passages_are_ranked_by_descending_score_across_all_sources()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.Empty(context.Diagnostics);
        Assert.NotEmpty(context.Passages);

        // The defining property of a merged ranking: scores never increase.
        var scores = context.Passages.Select(p => p.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);

        // Both sources contribute. (They are identical fixture copies, so
        // every score ties and priority orders each tie -- the ordering that
        // actually distinguishes merged from grouped is asserted by
        // PriorityWeightedKnowledgeResolverTests, which uses a catalog whose
        // score and priority orders genuinely disagree.)
        Assert.Contains(context.Passages, p => p.SourceId == "hi");
        Assert.Contains(context.Passages, p => p.SourceId == "lo");
    }

    [Fact]
    public async Task Priority_breaks_ties_between_equal_scores()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        // The two sources are byte-identical fixture copies, so for every
        // score the higher-priority source's passage must come first.
        foreach (var group in context.Passages.GroupBy(p => p.Score))
        {
            var ids = group.Select(p => p.SourceId).ToList();
            var firstLo = ids.IndexOf("lo");
            var lastHi = ids.LastIndexOf("hi");
            if (firstLo >= 0 && lastHi >= 0)
            {
                Assert.True(lastHi < firstLo, $"score {group.Key}: 'hi' must precede 'lo'");
            }
        }
    }

    [Fact]
    public async Task Two_source_entries_resolving_to_the_same_directory_are_searched_once()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "shared"));

        // Two ids, two different relative spellings, ONE resolved directory.
        using var catalog = BuildCatalog(root, """
            { "id": "alias", "path": "./shared/../shared", "priority": 1, "enabled": true },
            { "id": "primary", "path": "./shared", "priority": 10, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        // Every concept appears exactly once...
        var ids = context.Passages.Select(p => p.ConceptId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // ...attributed to the surviving (higher-priority) source, and the
        // eliminated entry contributes nothing at all.
        Assert.All(context.Passages, p => Assert.Equal("primary", p.SourceId));
        Assert.DoesNotContain(context.Diagnostics, d => d.SourceId == "alias");
    }

    [Fact]
    public async Task The_same_ConceptId_in_two_different_directories_is_never_deduped()
    {
        using var root = new TempDir();

        // Two genuinely distinct bundles that happen to share a concept id.
        root.Write(Path.Combine("a", "shared.md"), "---\ntype: Note\ntitle: Alpha\ndescription: d\n---\nOrders alpha.\n");
        root.Write(Path.Combine("b", "shared.md"), "---\ntype: Note\ntitle: Beta\ndescription: d\n---\nOrders beta.\n");

        using var catalog = BuildCatalog(root, """
            { "id": "a", "path": "./a", "priority": 1, "enabled": true },
            { "id": "b", "path": "./b", "priority": 2, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        // Same ConceptId, unrelated content: BOTH must survive. Collapsing
        // these would silently hide one bundle's concept behind another's.
        Assert.Equal(2, context.Passages.Count);
        Assert.All(context.Passages, p => Assert.Equal("shared", p.ConceptId));
        Assert.Equal(["a", "b"], context.Passages.Select(p => p.SourceId).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stale_passages_are_filtered_by_the_query_policy()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("source", "old.md"),
            "---\ntype: Metric\ntitle: Churn cohort\ndescription: d\nstale_after: 2026-01-01\n---\nChurn cohort.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "s1", "path": "./source", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog, new FixedClock(new DateOnly(2026, 7, 27)));

        var strict = await resolver.SearchAsync(new KnowledgeQuery("churn") { StalePolicy = StalePolicy.Strict });
        Assert.Empty(strict.Passages);

        var used = await resolver.SearchAsync(new KnowledgeQuery("churn"));
        Assert.Single(used.Passages);
    }

    [Fact]
    public async Task An_unresolvable_source_yields_a_diagnostic_and_the_others_still_search()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "good"));
        using var catalog = BuildCatalog(root, """
            { "id": "good", "path": "./good", "priority": 1, "enabled": true },
            { "id": "gone", "path": "./missing", "priority": 2, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("good", p.SourceId));
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Equal("gone", diagnostic.SourceId);
    }

    [Fact]
    public async Task No_enabled_sources_is_reported_as_data()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "off"));
        using var catalog = BuildCatalog(root, """
            { "id": "off", "path": "./off", "priority": 1, "enabled": false }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoEnabledSources, diagnostic.Code);
    }

    [Fact]
    public async Task No_matches_is_reported_as_data()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "src"));
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("zzzznotpresentanywhere"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoMatches, diagnostic.Code);
    }

    [Fact]
    public async Task A_blank_query_text_throws()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "src"));
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.SearchAsync(new KnowledgeQuery("   ")).AsTask());
    }

    [Fact]
    public async Task The_catalog_generation_is_stamped_on_the_result()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(catalog.Current.Generation, context.CatalogGeneration);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~MergedKnowledgeResolverTests"
```

Expected: **build failure** — `MergedKnowledgeResolver` does not exist.

- [ ] **Step 3: Widen `CatalogPathResolver.PathComparison` to `internal`**

In `src/OKF4net.Catalog/CatalogPathResolver.cs`, line 53 currently reads:

```csharp
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
```

Change **only** the accessibility keyword, leaving the existing `<remarks>` block above it untouched:

```csharp
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
```

Then append this paragraph to the **end** of that member's existing `<remarks>` block (just before its `/// </remarks>` line), so the reason it is shared is recorded next to it:

```csharp
    /// <para>
    /// <see langword="internal"/> rather than <see langword="private"/>
    /// because <see cref="FusedResolverEngine"/>'s source-level dedup must
    /// compare two resolved source directories for equality using EXACTLY
    /// this convention. A second, independently-written OS check there would
    /// be a real defect in either direction: <see cref="StringComparison.Ordinal"/>
    /// on Windows would fail to dedup two entries differing only in case (the
    /// same directory), while <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// on Linux would falsely dedup two genuinely distinct directories.
    /// </para>
```

- [ ] **Step 4: Create the fused engine**

Create `src/OKF4net.Catalog/FusedResolverEngine.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// A passage paired with the <see cref="KnowledgeCatalogSource.Priority"/> of
/// the source it came from. <see cref="KnowledgePassage"/> deliberately does
/// not carry priority (it is a catalog-configuration concern, not a property
/// of the matched concept), but the fused strategies' comparers need it, so
/// it rides alongside through ranking and is dropped again before the
/// <see cref="KnowledgeContext"/> is built.
/// </summary>
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
/// <remarks>
/// Deliberately NOT used by <see cref="GroupedKnowledgeResolver"/>, whose
/// grouped output has no cross-source ranking step to share and whose
/// behaviour is frozen.
/// </remarks>
internal static class FusedResolverEngine
{
    /// <summary>
    /// Runs <paramref name="query"/> across <paramref name="catalog"/>'s
    /// enabled knowledge sources and returns one fused, ranked
    /// <see cref="KnowledgeContext"/>.
    /// </summary>
    /// <param name="comparer">
    /// The ranking order. Must impose a TOTAL order (no ties left to
    /// <see cref="List{T}.Sort(IComparer{T})"/>'s unstable tie-breaking), so
    /// the same catalog and query always produce the same sequence.
    /// </param>
    /// <param name="fairnessQuota">
    /// Reserved for the fairness reordering step; currently unused (see the
    /// fairness task). <see langword="null"/> means disabled.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null, empty, or whitespace.</exception>
    internal static async ValueTask<KnowledgeContext> SearchAsync(
        IKnowledgeCatalog catalog,
        IOkfClock clock,
        KnowledgeQuery query,
        IComparer<RankedPassage> comparer,
        int? fairnessQuota,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            throw new ArgumentException("KnowledgeQuery.Text must be non-blank.", nameof(query));
        }

        var snapshot = catalog.Current;
        var enabledSources = snapshot.Sources
            .Where(s => s.Enabled && s.Role == SourceRole.Knowledge)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        if (enabledSources.Count == 0)
        {
            return new KnowledgeContext(
                query,
                snapshot.Generation,
                Array.Empty<KnowledgePassage>(),
                Array.AsReadOnly(new[]
                {
                    new KnowledgeDiagnostic(KnowledgeDiagnosticCode.NoEnabledSources, null, "No enabled knowledge sources are configured."),
                }));
        }

        var diagnostics = new List<KnowledgeDiagnostic>();

        // Resolve + dedup BEFORE searching. Two manifest entries pointing at
        // the same directory are the same bundle: searching both would load
        // and score it twice only to discard half the results. enabledSources
        // is already in priority-then-id order, so the first entry reaching a
        // given directory is the survivor by construction.
        var seenDirectories = new HashSet<string>(StringComparer.FromComparison(CatalogPathResolver.PathComparison));
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

            if (seenDirectories.Add(directory!))
            {
                resolved.Add((source, directory!));
            }
        }

        var ranked = new List<RankedPassage>();
        var anySourceSearchedSuccessfully = false;
        var today = clock.Today;

        foreach (var (source, directory) in resolved)
        {
            ct.ThrowIfCancellationRequested();

            var bundleSource = new OkfBundleKnowledgeSource(source.Id, directory);
            var result = await bundleSource.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
                diagnostics.Add(result.Diagnostic);
                continue;
            }

            anySourceSearchedSuccessfully = true;
            foreach (var passage in result.Passages)
            {
                if (query.StalePolicy.Admits(passage.Lifecycle, today))
                {
                    ranked.Add(new RankedPassage(passage, source.Priority));
                }
            }
        }

        ranked.Sort(comparer);

        if (ranked.Count == 0 && anySourceSearchedSuccessfully)
        {
            diagnostics.Add(new KnowledgeDiagnostic(
                KnowledgeDiagnosticCode.NoMatches, null, $"No passages matched query '{query.Text}'."));
        }

        var passages = ranked.Select(r => r.Passage).ToList();

        // .AsReadOnly() wraps each list in a genuine ReadOnlyCollection<T>
        // view rather than exposing the mutable List<T> behind
        // IReadOnlyList<T> -- otherwise a caller could cast a published
        // KnowledgeContext's collections back and mutate them.
        return new KnowledgeContext(query, snapshot.Generation, passages.AsReadOnly(), diagnostics.AsReadOnly());
    }
}
```

- [ ] **Step 5: Create `MergedKnowledgeResolver`**

Create `src/OKF4net.Catalog/MergedKnowledgeResolver.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="KnowledgeResolverStrategy.Merged"/> strategy: every enabled
/// <see cref="SourceRole.Knowledge"/> source's matches merged into ONE list
/// ranked by descending <see cref="KnowledgePassage.Score"/> across all
/// sources, with <see cref="KnowledgeCatalogSource.Priority"/> as a tie-break
/// only -- never a score multiplier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw scores are comparable across bundles.</b> The core
/// <c>ConceptSearch</c> scorer is a deterministic weighted term-count (title
/// x3, tags/description x2, body x1) with NO per-corpus statistics -- no IDF,
/// no document-frequency or bundle-size normalization. Two passages scoring
/// equally in different bundles matched an equal weight of terms, so ranking
/// them against each other directly is sound rather than merely approximate.
/// </para>
/// <para>
/// <b>Source dedup.</b> Two enabled manifest entries resolving to the same
/// directory are the same bundle mounted twice; only the survivor (higher
/// <see cref="KnowledgeCatalogSource.Priority"/>, then lower ordinal
/// <see cref="KnowledgeCatalogSource.Id"/>) is searched. The eliminated entry
/// therefore contributes neither passages NOR diagnostics -- it is never
/// searched at all. Two DIFFERENT directories that happen to produce the same
/// concept id are never merged: a concept id is derived from a path relative
/// to its own bundle root and is not a globally stable identity, so
/// collapsing them would silently conflate unrelated concepts.
/// </para>
/// </remarks>
public sealed class MergedKnowledgeResolver : IKnowledgeResolver
{
    /// <summary>
    /// Descending score, then descending source priority, then ordinal source
    /// id, then ordinal concept id. The last two exist purely to make the
    /// order TOTAL: <see cref="List{T}.Sort(IComparer{T})"/> is unstable, so
    /// any remaining tie would let equally-ranked passages shuffle between
    /// otherwise identical searches.
    /// </summary>
    private sealed class ScoreFirstComparer : IComparer<RankedPassage>
    {
        public int Compare(RankedPassage x, RankedPassage y)
        {
            var byScore = y.Passage.Score.CompareTo(x.Passage.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byPriority = y.Priority.CompareTo(x.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var bySource = string.CompareOrdinal(x.Passage.SourceId, y.Passage.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Passage.ConceptId, y.Passage.ConceptId);
        }
    }

    private static readonly ScoreFirstComparer Comparer = new();

    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;

    /// <summary>
    /// Creates a resolver over <paramref name="catalog"/>.
    /// </summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <param name="defaultFairnessQuota">
    /// The fairness quota applied when a query does not set its own
    /// <see cref="KnowledgeQuery.FairnessQuota"/>. <see langword="null"/>
    /// (the default) disables fairness reordering entirely.
    /// </param>
    public MergedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)
    {
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/> throws
    /// <see cref="ArgumentException"/> rather than being reported as a
    /// diagnostic: unlike <see cref="KnowledgeDiagnosticCode.NoMatches"/> (a
    /// legitimate zero-result outcome) or
    /// <see cref="KnowledgeDiagnosticCode.NoEnabledSources"/> (a legitimate
    /// catalog state), a blank query is a caller error -- there is no
    /// sensible search to attempt.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota, ct);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~MergedKnowledgeResolverTests"
```

Expected: 10 passing.

- [ ] **Step 7: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **675/675** passing (665 + 10 new).

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net.Catalog/FusedResolverEngine.cs src/OKF4net.Catalog/MergedKnowledgeResolver.cs src/OKF4net.Catalog/CatalogPathResolver.cs tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs
git commit -m "feat(catalog): add the fused resolver engine and MergedKnowledgeResolver

Source dedup runs BEFORE the fan-out, so a bundle mounted twice under two
manifest entries is loaded and scored once rather than twice-then-halved.
Dedup compares resolved directories with CatalogPathResolver's existing
OS-dependent PathComparison (widened private -> internal) -- a second,
independently written OS check would be wrong in opposite directions on
Windows and Linux."
```

---

### Task 4: `PriorityWeightedKnowledgeResolver`

The second fused strategy: the same engine, a different comparator. Priority becomes the primary sort key rather than a tie-break, giving an operator a hard "this source always outranks that one" guarantee without inventing a numeric blend between priority and score.

**Files:**
- Create: `src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs`
- Test: `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs` (create)

**Interfaces:**
- Consumes: `FusedResolverEngine.SearchAsync(IKnowledgeCatalog, IOkfClock, KnowledgeQuery, IComparer<RankedPassage>, int?, CancellationToken)` and `RankedPassage` (Task 3).
- Produces: `public sealed class PriorityWeightedKnowledgeResolver : IKnowledgeResolver` with constructor `PriorityWeightedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)`. Task 6 constructs it.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="PriorityWeightedKnowledgeResolver"/>: priority is the PRIMARY
/// sort key, so a higher-priority source's passage never falls behind a
/// lower-priority one however weak its match, with score ordering only
/// within a single priority tier. Uses hand-written bundles (rather than the
/// appendix_a fixture) so the score relationship between the two sources is
/// controlled by the test rather than incidental to the fixture.
/// </summary>
public class PriorityWeightedKnowledgeResolverTests
{
    private static FileKnowledgeCatalog BuildCatalog(TempDir root, string sourcesJson)
    {
        root.Write("catalog.json", $$"""
            {
              "version": 1,
              "sources": [{{sourcesJson}}]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    /// <summary>
    /// A low-priority source whose concept matches STRONGLY (the term is in
    /// the title, worth x3) and a high-priority source whose concept matches
    /// WEAKLY (body only, worth x1) -- the exact case where the two fused
    /// strategies must disagree.
    /// </summary>
    private static FileKnowledgeCatalog SetUpInvertedScores(TempDir root)
    {
        root.Write(Path.Combine("weak-hi", "note.md"),
            "---\ntype: Note\ntitle: Unrelated heading\ndescription: d\n---\nA passing mention of orders.\n");
        root.Write(Path.Combine("strong-lo", "note.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders\n---\nOrders everywhere orders.\n");

        return BuildCatalog(root, """
            { "id": "strong-lo", "path": "./strong-lo", "priority": 1, "enabled": true },
            { "id": "weak-hi", "path": "./weak-hi", "priority": 10, "enabled": true }
            """);
    }

    [Fact]
    public async Task A_higher_priority_source_outranks_a_stronger_lower_priority_match()
    {
        using var root = new TempDir();
        using var catalog = SetUpInvertedScores(root);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        Assert.Equal("weak-hi", context.Passages[0].SourceId);
        Assert.Equal("strong-lo", context.Passages[1].SourceId);

        // ...and this is genuinely the priority ordering winning, not the
        // score ordering coinciding with it: the first passage scores LOWER.
        Assert.True(
            context.Passages[0].Score < context.Passages[1].Score,
            "the fixture must put the weaker match in the higher-priority source for this test to mean anything");
    }

    [Fact]
    public async Task Merged_ranks_the_same_catalog_the_other_way_round()
    {
        using var root = new TempDir();
        using var catalog = SetUpInvertedScores(root);
        var merged = new MergedKnowledgeResolver(catalog);

        var context = await merged.SearchAsync(new KnowledgeQuery("orders"));

        // The companion assertion to the test above: same catalog, same
        // query, opposite order -- proving the two strategies are actually
        // distinct rather than both quietly sorting by score.
        Assert.Equal("strong-lo", context.Passages[0].SourceId);
        Assert.Equal("weak-hi", context.Passages[1].SourceId);
    }

    [Fact]
    public async Task Score_still_orders_passages_within_one_priority_tier()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("tier", "strong.md"),
            "---\ntype: Note\ntitle: Orders orders\ndescription: orders\n---\nOrders orders.\n");
        root.Write(Path.Combine("tier", "weak.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: d\n---\nOne mention of orders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "tier", "path": "./tier", "priority": 5, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        Assert.Equal("strong", context.Passages[0].ConceptId);
        Assert.Equal("weak", context.Passages[1].ConceptId);
    }

    [Fact]
    public async Task Two_source_entries_resolving_to_the_same_directory_are_searched_once()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("shared", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "alias", "path": "./shared/../shared", "priority": 1, "enabled": true },
            { "id": "primary", "path": "./shared", "priority": 10, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        var passage = Assert.Single(context.Passages);
        Assert.Equal("primary", passage.SourceId);
    }

    [Fact]
    public async Task A_blank_query_text_throws()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("src", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.SearchAsync(new KnowledgeQuery("   ")).AsTask());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~PriorityWeightedKnowledgeResolverTests"
```

Expected: **build failure** — `PriorityWeightedKnowledgeResolver` does not exist.

- [ ] **Step 3: Create the resolver**

Create `src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="KnowledgeResolverStrategy.PriorityWeighted"/> strategy:
/// the same fusion pipeline as <see cref="MergedKnowledgeResolver"/>, but
/// ranked by descending <see cref="KnowledgeCatalogSource.Priority"/> FIRST,
/// with <see cref="KnowledgePassage.Score"/> ordering only WITHIN a single
/// priority tier. A higher-priority source's passage therefore never falls
/// behind a lower-priority source's, however much stronger the latter's
/// match.
/// </summary>
/// <remarks>
/// <para>
/// This is a lexicographic sort-key swap, NOT a numeric blend of priority
/// into the score. A blend (say <c>score + priority * K</c>) would require
/// inventing a scale relating two quantities that have no common unit, with
/// no principled default and surprising behaviour at the boundaries. Sorting
/// on priority first delivers the guarantee an operator actually asks for --
/// "this source is authoritative" -- exactly, and needs no such mapping.
/// </para>
/// <para>
/// Source dedup, stale filtering, diagnostics, and the never-throw contract
/// are all inherited unchanged from <see cref="FusedResolverEngine"/>; see
/// <see cref="MergedKnowledgeResolver"/>'s remarks for their semantics.
/// </para>
/// </remarks>
public sealed class PriorityWeightedKnowledgeResolver : IKnowledgeResolver
{
    /// <summary>
    /// Descending source priority, then descending score, then ordinal source
    /// id, then ordinal concept id. The last two exist purely to make the
    /// order TOTAL: <see cref="List{T}.Sort(IComparer{T})"/> is unstable, so
    /// any remaining tie would let equally-ranked passages shuffle between
    /// otherwise identical searches.
    /// </summary>
    private sealed class PriorityFirstComparer : IComparer<RankedPassage>
    {
        public int Compare(RankedPassage x, RankedPassage y)
        {
            var byPriority = y.Priority.CompareTo(x.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byScore = y.Passage.Score.CompareTo(x.Passage.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var bySource = string.CompareOrdinal(x.Passage.SourceId, y.Passage.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Passage.ConceptId, y.Passage.ConceptId);
        }
    }

    private static readonly PriorityFirstComparer Comparer = new();

    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;

    /// <summary>
    /// Creates a resolver over <paramref name="catalog"/>.
    /// </summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <param name="defaultFairnessQuota">
    /// The fairness quota applied when a query does not set its own
    /// <see cref="KnowledgeQuery.FairnessQuota"/>. <see langword="null"/>
    /// (the default) disables fairness reordering entirely.
    /// </param>
    public PriorityWeightedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)
    {
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/> throws
    /// <see cref="ArgumentException"/>, exactly as in
    /// <see cref="MergedKnowledgeResolver.SearchAsync"/>.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota, ct);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~PriorityWeightedKnowledgeResolverTests"
```

Expected: 5 passing.

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **680/680** passing (675 + 5 new).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs
git commit -m "feat(catalog): add PriorityWeightedKnowledgeResolver

Priority becomes the primary sort key, score orders only within a tier --
a lexicographic sort-key swap rather than a numeric blend, so no scale
relating priority to score has to be invented. Same engine as Merged."
```

---

### Task 5: Fairness reordering

Adds the opt-in interleaving step to the shared engine, so a caller that truncates early (an agent spending a token budget top-down) sees several sources rather than one prolific source's entire output. Reorders only — never drops a passage.

**Files:**
- Modify: `src/OKF4net.Catalog/FusedResolverEngine.cs`
- Test: `tests/OKF4net.Tests/Catalog/FairnessReorderTests.cs` (create)

**Interfaces:**
- Consumes: `FusedResolverEngine`, `RankedPassage` (Task 3); `MergedKnowledgeResolver` (Task 3), `PriorityWeightedKnowledgeResolver` (Task 4).
- Produces: `internal static List<RankedPassage> FusedResolverEngine.ApplyFairness(List<RankedPassage> ranked, int quota)`, and makes the engine's previously-ignored `fairnessQuota` parameter live. No public API changes.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/FairnessReorderTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The fused strategies' opt-in fairness reordering: no source contributes
/// more than the quota's worth of CONSECUTIVE passages while another source
/// still has passages left, and nothing is ever dropped. Built on a catalog
/// where one source deliberately outnumbers the other, since that is the only
/// shape where the quota changes anything.
/// </summary>
public class FairnessReorderTests
{
    private static FileKnowledgeCatalog BuildCatalog(TempDir root, string sourcesJson)
    {
        root.Write("catalog.json", $$"""
            {
              "version": 1,
              "sources": [{{sourcesJson}}]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    /// <summary>
    /// "big" holds 5 matching concepts scoring higher than "small"'s 2, so
    /// unfair (pure score) order drains all of "big" before "small" appears.
    /// </summary>
    private static FileKnowledgeCatalog SetUpLopsidedCatalog(TempDir root)
    {
        for (var i = 0; i < 5; i++)
        {
            root.Write(Path.Combine("big", $"b{i}.md"),
                $"---\ntype: Note\ntitle: Orders orders {i}\ndescription: orders\n---\nOrders orders.\n");
        }

        for (var i = 0; i < 2; i++)
        {
            root.Write(Path.Combine("small", $"s{i}.md"),
                $"---\ntype: Note\ntitle: Unrelated {i}\ndescription: d\n---\nOne mention of orders.\n");
        }

        return BuildCatalog(root, """
            { "id": "big", "path": "./big", "priority": 1, "enabled": true },
            { "id": "small", "path": "./small", "priority": 1, "enabled": true }
            """);
    }

    /// <summary>The length of the longest run of consecutive same-source passages.</summary>
    private static int LongestRun(IReadOnlyList<KnowledgePassage> passages)
    {
        var longest = 0;
        var current = 0;
        string? previous = null;

        foreach (var p in passages)
        {
            current = p.SourceId == previous ? current + 1 : 1;
            previous = p.SourceId;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    [Fact]
    public async Task Without_a_quota_one_source_can_monopolize_the_head_of_the_result()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        // The baseline this whole feature exists to fix: a caller truncating
        // after 5 passages would never see "small" at all.
        Assert.Equal(7, context.Passages.Count);
        Assert.All(context.Passages.Take(5), p => Assert.Equal("big", p.SourceId));
    }

    [Fact]
    public async Task A_quota_of_two_breaks_up_the_monopoly()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 2 });

        // "small" now appears within the first 3, so an early-truncating
        // caller sees both sources.
        Assert.Contains(context.Passages.Take(3), p => p.SourceId == "small");
    }

    [Fact]
    public async Task A_quota_never_drops_a_passage()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var unfair = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var fair = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // Same multiset, different order -- reordering only, no filtering.
        Assert.Equal(
            unfair.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}").OrderBy(s => s, StringComparer.Ordinal),
            fair.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}").OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_quota_is_honored_until_the_smaller_source_runs_out()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // With quota 1 and 5-vs-2 passages, the best possible interleave is
        // big, small, big, small, big, big, big -- so the only run longer
        // than 1 is the unavoidable tail after "small" is exhausted.
        var tail = context.Passages.Skip(4).ToList();
        Assert.All(tail, p => Assert.Equal("big", p.SourceId));
        Assert.Equal(3, LongestRun(tail));

        var head = context.Passages.Take(4).ToList();
        Assert.Equal(1, LongestRun(head));
    }

    [Fact]
    public async Task A_quota_applies_to_the_priority_weighted_strategy_too()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        Assert.Equal(7, context.Passages.Count);
        Assert.Equal(1, LongestRun(context.Passages.Take(4).ToList()));
    }

    [Fact]
    public async Task A_constructor_default_quota_applies_when_the_query_sets_none()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: 1);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(1, LongestRun(context.Passages.Take(4).ToList()));
    }

    [Fact]
    public async Task A_query_quota_overrides_the_constructor_default()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: 1);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 5 });

        // Quota 5 is large enough that "big"'s whole run fits, so the result
        // is the unfair order again -- proving the query value won.
        Assert.All(context.Passages.Take(5), p => Assert.Equal("big", p.SourceId));
    }

    [Fact]
    public async Task A_non_positive_quota_is_rejected()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 0 }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = -1 }).AsTask());
    }

    [Fact]
    public async Task A_single_source_result_is_unaffected_by_a_quota()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("only", "a.md"), "---\ntype: Note\ntitle: Orders a\ndescription: orders\n---\nOrders.\n");
        root.Write(Path.Combine("only", "b.md"), "---\ntype: Note\ntitle: Orders b\ndescription: orders\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "only", "path": "./only", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var unfair = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var fair = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // No alternative source exists, so the quota cannot be honored and
        // the algorithm simply drains the one source in ranked order.
        Assert.Equal(
            unfair.Passages.Select(p => p.ConceptId),
            fair.Passages.Select(p => p.ConceptId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~FairnessReorderTests"
```

Expected: the quota tests FAIL — the engine currently ignores `fairnessQuota`, so `A_quota_of_two_breaks_up_the_monopoly`, `The_quota_is_honored_until_the_smaller_source_runs_out`, and `A_non_positive_quota_is_rejected` do not hold. (`Without_a_quota_...` and `A_quota_never_drops_a_passage` may already pass.)

- [ ] **Step 3: Implement the reorder in the engine**

In `src/OKF4net.Catalog/FusedResolverEngine.cs`, replace the `fairnessQuota` parameter's doc comment:

```csharp
    /// <param name="fairnessQuota">
    /// Reserved for the fairness reordering step; currently unused (see the
    /// fairness task). <see langword="null"/> means disabled.
    /// </param>
```

with:

```csharp
    /// <param name="fairnessQuota">
    /// The maximum number of CONSECUTIVE passages one source may contribute
    /// before another source's next-best passage is pulled ahead of it;
    /// <see langword="null"/> disables the reorder entirely. See
    /// <see cref="ApplyFairness"/>.
    /// </param>
```

Extend that method's `<exception>` documentation to cover the new rejection:

```csharp
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace, or the effective
    /// <paramref name="fairnessQuota"/> is not greater than zero.
    /// </exception>
```

Then, immediately after the existing blank-text guard, add the quota guard:

```csharp
        if (fairnessQuota is <= 0)
        {
            throw new ArgumentException(
                $"A fairness quota must be greater than zero (got {fairnessQuota}); use null to disable fairness reordering.",
                nameof(fairnessQuota));
        }
```

Next, replace the single line

```csharp
        ranked.Sort(comparer);
```

with

```csharp
        ranked.Sort(comparer);

        if (fairnessQuota is { } quota && ranked.Count > 1)
        {
            ranked = ApplyFairness(ranked, quota);
        }
```

Finally, add this method to the class, after `SearchAsync`:

```csharp
    /// <summary>
    /// Reorders an already-ranked list so no source contributes more than
    /// <paramref name="quota"/> CONSECUTIVE passages while another source
    /// still has passages left. Returns a new list containing exactly the
    /// same passages -- nothing is dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely a reordering, because the problem it solves is early
    /// truncation, not result size: a consumer spending a token budget
    /// top-down (an agent context provider, say) stops partway down the list,
    /// and without this a single prolific source's whole run can consume the
    /// budget before any other source is reached. A consumer that reads the
    /// entire list is unaffected by design.
    /// </para>
    /// <para>
    /// When every remaining passage belongs to one source there is nothing to
    /// pull forward, so the quota simply stops applying and the rest of that
    /// source drains in ranked order. The quota is a fairness goal for
    /// interleavable results, not a hard guarantee obtainable from a
    /// single-source result set.
    /// </para>
    /// <para>
    /// Quadratic in the worst case (each pick may scan the remainder for a
    /// different source), which is deliberate: a search result is tens to low
    /// hundreds of passages, and a linear-time bucketed variant would cost
    /// more in complexity than it saves in time at that size.
    /// </para>
    /// </remarks>
    internal static List<RankedPassage> ApplyFairness(List<RankedPassage> ranked, int quota)
    {
        var remaining = new LinkedList<RankedPassage>(ranked);
        var result = new List<RankedPassage>(ranked.Count);

        string? runSource = null;
        var runLength = 0;

        while (remaining.First is { } head)
        {
            var pick = head;

            // The head would extend the current run past the quota: look for
            // the best-ranked passage from any OTHER source to interleave. If
            // there is none, keep the head -- draining is the only option.
            if (runLength >= quota && string.Equals(head.Value.Passage.SourceId, runSource, StringComparison.Ordinal))
            {
                var candidate = head.Next;
                while (candidate is not null && string.Equals(candidate.Value.Passage.SourceId, runSource, StringComparison.Ordinal))
                {
                    candidate = candidate.Next;
                }

                pick = candidate ?? head;
            }

            var chosen = pick.Value;
            remaining.Remove(pick);
            result.Add(chosen);

            if (string.Equals(chosen.Passage.SourceId, runSource, StringComparison.Ordinal))
            {
                runLength++;
            }
            else
            {
                runSource = chosen.Passage.SourceId;
                runLength = 1;
            }
        }

        return result;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~FairnessReorderTests"
```

Expected: 9 passing.

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **689/689** passing (680 + 9 new).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/FusedResolverEngine.cs tests/OKF4net.Tests/Catalog/FairnessReorderTests.cs
git commit -m "feat(catalog): add opt-in fairness reordering to the fused resolvers

Reorders only, never drops: the problem is early truncation by a
budget-bounded consumer, not result size, so a caller reading the whole
list is unaffected. A non-positive quota is rejected rather than silently
treated as disabled -- null is the way to disable it."
```

---

### Task 6: `KnowledgeResolverRouter` and host-level configuration

The piece that makes strategy selection reachable through the single `IKnowledgeResolver` every existing consumer already injects. The router lives in `OKF4net.Catalog` and must **not** reference `KnowledgeOptions`, which lives in the `Hosting` package downstream of it.

**Files:**
- Create: `src/OKF4net.Catalog/KnowledgeResolverRouter.cs`
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`
- Modify: `src/OKF4net.Catalog/KnowledgeResolverStrategy.cs` (convert the three `<c>` placeholders from Task 2 to real `<see cref>`)
- Modify: `src/OKF4net.Catalog/KnowledgeQuery.cs` (same conversion)
- Test: `tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs` (create)
- Test: `tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs` (modify — add strategy-configuration facts)

**Interfaces:**
- Consumes: `GroupedKnowledgeResolver` (Task 1), `KnowledgeResolverStrategy` + `KnowledgeQuery.ResolverStrategy`/`FairnessQuota` (Task 2), `MergedKnowledgeResolver` (Task 3), `PriorityWeightedKnowledgeResolver` (Task 4).
- Produces: `public sealed class KnowledgeResolverRouter : IKnowledgeResolver` with constructor `KnowledgeResolverRouter(IKnowledgeCatalog catalog, KnowledgeResolverStrategy defaultStrategy = KnowledgeResolverStrategy.GroupedBySource, int? defaultFairnessQuota = null, IOkfClock? clock = null)`; `KnowledgeOptions.DefaultResolverStrategy` and `KnowledgeOptions.DefaultFairnessQuota`.

- [ ] **Step 1: Write the failing router tests**

Create `tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="KnowledgeResolverRouter"/>: dispatches each search to the
/// strategy named by the query, falling back to the configured default, so
/// the single injected <see cref="IKnowledgeResolver"/> every consumer
/// already depends on gains per-call strategy selection without any of them
/// changing.
/// </summary>
public class KnowledgeResolverRouterTests
{
    private static FileKnowledgeCatalog BuildCatalog(TempDir root, string sourcesJson)
    {
        root.Write("catalog.json", $$"""
            {
              "version": 1,
              "sources": [{{sourcesJson}}]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    /// <summary>
    /// A low-priority source matching strongly and a high-priority source
    /// matching weakly -- the three strategies order this catalog
    /// differently, which is how each test tells them apart.
    /// </summary>
    private static FileKnowledgeCatalog SetUpDistinguishingCatalog(TempDir root)
    {
        root.Write(Path.Combine("weak-hi", "note.md"),
            "---\ntype: Note\ntitle: Unrelated heading\ndescription: d\n---\nA passing mention of orders.\n");
        root.Write(Path.Combine("strong-lo", "note.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders\n---\nOrders everywhere orders.\n");

        return BuildCatalog(root, """
            { "id": "strong-lo", "path": "./strong-lo", "priority": 1, "enabled": true },
            { "id": "weak-hi", "path": "./weak-hi", "priority": 10, "enabled": true }
            """);
    }

    [Fact]
    public async Task The_default_strategy_is_grouped_by_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        var viaRouter = await router.SearchAsync(new KnowledgeQuery("orders"));
        var viaGrouped = await new GroupedKnowledgeResolver(catalog).SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(
            viaGrouped.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"),
            viaRouter.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"));
    }

    [Fact]
    public async Task A_query_strategy_overrides_the_default()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog); // default: GroupedBySource

        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });

        // Merged ranks by raw score, so the strong-but-low-priority source wins.
        Assert.Equal("strong-lo", merged.Passages[0].SourceId);
    }

    [Fact]
    public async Task The_configured_default_applies_when_the_query_names_none()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog, KnowledgeResolverStrategy.Merged);

        var context = await router.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal("strong-lo", context.Passages[0].SourceId);
    }

    [Fact]
    public async Task Each_strategy_is_reachable_by_name()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });
        var weighted = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted });
        var grouped = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource });

        Assert.Equal("strong-lo", merged.Passages[0].SourceId);
        Assert.Equal("weak-hi", weighted.Passages[0].SourceId);
        Assert.Equal("weak-hi", grouped.Passages[0].SourceId); // grouped leads with the highest-priority source
    }

    [Fact]
    public async Task The_default_fairness_quota_reaches_the_fused_strategies()
    {
        using var root = new TempDir();
        for (var i = 0; i < 4; i++)
        {
            root.Write(Path.Combine("big", $"b{i}.md"),
                $"---\ntype: Note\ntitle: Orders orders {i}\ndescription: orders\n---\nOrders orders.\n");
        }

        root.Write(Path.Combine("small", "s0.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: d\n---\nOne mention of orders.\n");

        using var catalog = BuildCatalog(root, """
            { "id": "big", "path": "./big", "priority": 1, "enabled": true },
            { "id": "small", "path": "./small", "priority": 1, "enabled": true }
            """);
        var router = new KnowledgeResolverRouter(catalog, KnowledgeResolverStrategy.Merged, defaultFairnessQuota: 1);

        var context = await router.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal("small", context.Passages[1].SourceId);
    }

    [Fact]
    public async Task A_blank_query_text_throws_whichever_strategy_is_selected()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        await Assert.ThrowsAsync<ArgumentException>(
            () => router.SearchAsync(new KnowledgeQuery("  ") { ResolverStrategy = KnowledgeResolverStrategy.Merged }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => router.SearchAsync(new KnowledgeQuery("  ") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource }).AsTask());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeResolverRouterTests"
```

Expected: **build failure** — `KnowledgeResolverRouter` does not exist.

- [ ] **Step 3: Create the router**

Create `src/OKF4net.Catalog/KnowledgeResolverRouter.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="IKnowledgeResolver"/> a host actually injects: it owns one
/// instance of each concrete strategy and dispatches every search to
/// <see cref="KnowledgeQuery.ResolverStrategy"/>, or to the configured
/// default when the query names none.
/// </summary>
/// <remarks>
/// <para>
/// Registering this as the single <see cref="IKnowledgeResolver"/> is what
/// makes per-query strategy selection reachable without any existing consumer
/// changing: they keep resolving one <see cref="IKnowledgeResolver"/> from the
/// container and simply gain the ability to set
/// <see cref="KnowledgeQuery.ResolverStrategy"/> on a query.
/// </para>
/// <para>
/// <b>Why the defaults are plain constructor parameters.</b> They come from
/// <c>KnowledgeOptions</c>, which lives in <c>OKF4net.Catalog.Hosting</c> --
/// a package that depends on THIS one. Referencing it here would invert that
/// dependency and make the graph cyclic, so the hosting layer reads its own
/// options and passes the two values in.
/// </para>
/// </remarks>
public sealed class KnowledgeResolverRouter : IKnowledgeResolver
{
    private readonly GroupedKnowledgeResolver _grouped;
    private readonly MergedKnowledgeResolver _merged;
    private readonly PriorityWeightedKnowledgeResolver _priorityWeighted;
    private readonly KnowledgeResolverStrategy _defaultStrategy;

    /// <summary>
    /// Creates a router over <paramref name="catalog"/>, constructing all
    /// three strategies eagerly (each is a stateless wrapper over the shared
    /// catalog, so this is cheap).
    /// </summary>
    /// <param name="catalog">The catalog every strategy searches.</param>
    /// <param name="defaultStrategy">
    /// The strategy used when a query leaves
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> unset. Defaults to
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/> -- the
    /// behaviour every pre-existing deployment already has, so an upgrade
    /// never silently reorders anyone's results.
    /// </param>
    /// <param name="defaultFairnessQuota">
    /// The fairness quota the fused strategies use when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset;
    /// <see langword="null"/> (the default) disables reordering.
    /// </param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    public KnowledgeResolverRouter(
        IKnowledgeCatalog catalog,
        KnowledgeResolverStrategy defaultStrategy = KnowledgeResolverStrategy.GroupedBySource,
        int? defaultFairnessQuota = null,
        IOkfClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var effectiveClock = clock ?? new SystemClock();
        _grouped = new GroupedKnowledgeResolver(catalog, effectiveClock);
        _merged = new MergedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota);
        _priorityWeighted = new PriorityWeightedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota);
        _defaultStrategy = defaultStrategy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the selected strategy; every contract the strategies
    /// document (the blank-query <see cref="ArgumentException"/>,
    /// errors-as-data diagnostics, generation stamping) is theirs unchanged.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return (query.ResolverStrategy ?? _defaultStrategy) switch
        {
            KnowledgeResolverStrategy.Merged => _merged.SearchAsync(query, ct),
            KnowledgeResolverStrategy.PriorityWeighted => _priorityWeighted.SearchAsync(query, ct),
            _ => _grouped.SearchAsync(query, ct),
        };
    }
}
```

- [ ] **Step 4: Run the router tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeResolverRouterTests"
```

Expected: 6 passing.

- [ ] **Step 5: Add the two host options**

In `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`, add these two public properties immediately after the `private int _catalogFileCallCount;` field and before the `internal string? CatalogFilePath` property:

```csharp
    /// <summary>
    /// The <see cref="KnowledgeResolverStrategy"/> used for searches whose
    /// query leaves <see cref="KnowledgeQuery.ResolverStrategy"/> unset.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="KnowledgeResolverStrategy.GroupedBySource"/>:
    /// the behaviour every existing deployment already has, so upgrading
    /// never silently reorders anyone's results. A host wanting one merged
    /// cross-source ranking -- typically to feed a consumer that truncates
    /// under a token budget -- opts in here.
    /// </remarks>
    public KnowledgeResolverStrategy DefaultResolverStrategy { get; set; } = KnowledgeResolverStrategy.GroupedBySource;

    /// <summary>
    /// The fairness quota the fused strategies apply when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset; <see langword="null"/>
    /// (the default) disables fairness reordering. Ignored by
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/>.
    /// </summary>
    public int? DefaultFairnessQuota { get; set; }
```

Add `using OKF4net.Catalog;` to the file's usings if the build reports the types as unresolved (the file's namespace is `OKF4net.Catalog.Hosting`, so the types may already resolve).

- [ ] **Step 6: Register the router in DI**

In `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`, replace line 77:

```csharp
        services.TryAddSingleton<IKnowledgeResolver>(sp => new DefaultKnowledgeResolver(sp.GetRequiredService<IKnowledgeCatalog>()));
```

(after Task 1's rename this reads `GroupedKnowledgeResolver`) with:

```csharp
        var defaultStrategy = options.DefaultResolverStrategy;
        var defaultFairnessQuota = options.DefaultFairnessQuota;
        services.TryAddSingleton<IKnowledgeResolver>(sp => new KnowledgeResolverRouter(
            sp.GetRequiredService<IKnowledgeCatalog>(), defaultStrategy, defaultFairnessQuota));
```

The two values are captured into locals **before** the factory closure so the registration cannot observe later mutation of the `options` instance.

Then update the method's `<summary>`, replacing:

```csharp
    /// Configures a <see cref="KnowledgeOptions"/> via <paramref name="configure"/>
    /// and registers a <see cref="FileKnowledgeCatalog"/> (as
    /// <see cref="IKnowledgeCatalog"/>) and a <see cref="GroupedKnowledgeResolver"/>
    /// (as <see cref="IKnowledgeResolver"/>) built from it.
```

with:

```csharp
    /// Configures a <see cref="KnowledgeOptions"/> via <paramref name="configure"/>
    /// and registers a <see cref="FileKnowledgeCatalog"/> (as
    /// <see cref="IKnowledgeCatalog"/>) and a <see cref="KnowledgeResolverRouter"/>
    /// (as <see cref="IKnowledgeResolver"/>) built from it. The router
    /// dispatches each search to the strategy named by the query, or to
    /// <see cref="KnowledgeOptions.DefaultResolverStrategy"/> when the query
    /// names none.
```

And in the same file's `<b>Lifetimes.</b>` paragraph, replace the phrase `and <see cref="GroupedKnowledgeResolver"/> is stateless over that same singleton catalog` with `and <see cref="KnowledgeResolverRouter"/> (with the three strategy instances it owns) is stateless over that same singleton catalog`.

- [ ] **Step 7: Add the hosting-level tests**

Append these facts to the existing class in `tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs`. If the file's existing tests use a different helper to build a catalog directory, reuse that helper instead of re-creating one; the assertions below are what matter.

```csharp
    [Fact]
    public void AddKnowledge_registers_a_router_as_the_resolver()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("src", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        root.Write("catalog.json", """
            { "version": 1, "sources": [{ "id": "src", "path": "./src", "priority": 1, "enabled": true }] }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<KnowledgeResolverRouter>(provider.GetRequiredService<IKnowledgeResolver>());
    }

    [Fact]
    public async Task The_configured_default_strategy_reaches_the_registered_resolver()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("weak-hi", "note.md"),
            "---\ntype: Note\ntitle: Unrelated heading\ndescription: d\n---\nA passing mention of orders.\n");
        root.Write(Path.Combine("strong-lo", "note.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders\n---\nOrders everywhere orders.\n");
        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "strong-lo", "path": "./strong-lo", "priority": 1, "enabled": true },
                { "id": "weak-hi", "path": "./weak-hi", "priority": 10, "enabled": true }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o =>
        {
            o.AddCatalogFile(Path.Combine(root.Path, "catalog.json"));
            o.DefaultResolverStrategy = KnowledgeResolverStrategy.Merged;
        });
        using var provider = services.BuildServiceProvider();

        var context = await provider.GetRequiredService<IKnowledgeResolver>().SearchAsync(new KnowledgeQuery("orders"));

        // Merged ranks by raw score, so the strong-but-low-priority source
        // leads -- the opposite of the GroupedBySource default.
        Assert.Equal("strong-lo", context.Passages[0].SourceId);
    }
```

- [ ] **Step 8: Convert the Task 2 doc placeholders to real `<see cref>` links**

All three forward-referenced types now exist, so the placeholder `<c>` spans can become real links.

In `src/OKF4net.Catalog/KnowledgeResolverStrategy.cs`, replace:
- `<c>MergedKnowledgeResolver</c>` → `<see cref="MergedKnowledgeResolver"/>`
- `<c>PriorityWeightedKnowledgeResolver</c>` → `<see cref="PriorityWeightedKnowledgeResolver"/>`
- `<c>KnowledgeResolverRouter</c>` → `<see cref="KnowledgeResolverRouter"/>`

In `src/OKF4net.Catalog/KnowledgeQuery.cs`, replace both occurrences of `<c>KnowledgeResolverRouter</c>` with `<see cref="KnowledgeResolverRouter"/>`.

Verify none remain:

```bash
grep -n "<c>MergedKnowledgeResolver</c>\|<c>PriorityWeightedKnowledgeResolver</c>\|<c>KnowledgeResolverRouter</c>" src/OKF4net.Catalog/*.cs
```

Expected: no output.

- [ ] **Step 9: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **697/697** passing (689 + 6 router + 2 hosting).

- [ ] **Step 10: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeResolverRouter.cs src/OKF4net.Catalog/KnowledgeResolverStrategy.cs src/OKF4net.Catalog/KnowledgeQuery.cs src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs
git commit -m "feat(catalog): route searches to a strategy per host or per query

AddKnowledge now registers KnowledgeResolverRouter as IKnowledgeResolver,
so every existing consumer gains per-query strategy selection without
changing. The router takes plain (strategy, quota) parameters rather than
KnowledgeOptions: that type lives in Hosting, which depends on Catalog,
and referencing it here would make the package graph cyclic."
```

---

### Task 7: Documentation and CHANGELOG

The behaviour is shipped; every doc that still describes fusion as absent or unimplemented is now wrong. `IKnowledgeResolver`'s own doc is the most load-bearing of these — it states the ordering as *the interface contract*, which is exactly what stopped being fixed.

**Files:**
- Modify: `src/OKF4net.Catalog/IKnowledgeResolver.cs`
- Modify: `src/OKF4net.Catalog/KnowledgeContext.cs`
- Modify: `src/OKF4net.Catalog/README.md`
- Modify: `README.md` (repo root)
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: every type from Tasks 1–6.
- Produces: no code changes — documentation only.

- [ ] **Step 1: Rewrite `IKnowledgeResolver`'s contract doc**

In `src/OKF4net.Catalog/IKnowledgeResolver.cs`, replace the interface's entire `<summary>` block:

```csharp
/// <summary>
/// Searches across every enabled source of an <see cref="IKnowledgeCatalog"/>
/// and returns a single, grouped-by-source <see cref="KnowledgeContext"/>.
/// See <see cref="GroupedKnowledgeResolver"/> for the V1 implementation
/// (no cross-source fusion/dedup/merged ranking).
/// </summary>
```

with:

```csharp
/// <summary>
/// Searches across every enabled <see cref="SourceRole.Knowledge"/> source of
/// an <see cref="IKnowledgeCatalog"/> and returns a single
/// <see cref="KnowledgeContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering is the implementation's contract, not this interface's.</b>
/// Each strategy documents its own, and they genuinely differ:
/// <see cref="GroupedKnowledgeResolver"/> concatenates each source's results
/// grouped by source; <see cref="MergedKnowledgeResolver"/> merges them into
/// one ranking by descending score; <see cref="PriorityWeightedKnowledgeResolver"/>
/// merges them ranked by source priority first. Callers that need a
/// particular ordering must select it -- see
/// <see cref="KnowledgeResolverStrategy"/> and
/// <see cref="KnowledgeResolverRouter"/> -- rather than relying on whatever
/// the injected implementation happens to be.
/// </para>
/// <para>
/// Common to every implementation: <see cref="SourceRole.Memory"/> sources
/// are never searched (they feed <c>IMemoryStore</c> instead), non-fatal
/// conditions come back as <see cref="KnowledgeContext.Diagnostics"/> rather
/// than exceptions, and a failing source never prevents the others from
/// being searched.
/// </para>
/// </remarks>
```

- [ ] **Step 2: Rewrite `KnowledgeContext.Passages`'s doc**

In `src/OKF4net.Catalog/KnowledgeContext.cs`, replace the `<param name="Passages">` block:

```csharp
/// <param name="Passages">
/// The matching passages, concatenated **in source order** (descending
/// <see cref="KnowledgeCatalogSource.Priority"/> then ascending ordinal
/// <see cref="KnowledgeCatalogSource.Id"/>) and, within a source, in that
/// source's own descending-score order. There is deliberately no
/// cross-source fusion, deduplication, or merged ranking (V1 scope).
/// </param>
```

with:

```csharp
/// <param name="Passages">
/// The matching passages, each carrying its originating
/// <see cref="KnowledgePassage.SourceId"/>. Their ORDER is defined by the
/// <see cref="IKnowledgeResolver"/> that produced this result, not by this
/// type: see <see cref="KnowledgeResolverStrategy"/> for the available
/// orderings and each resolver's own documentation for its exact guarantee.
/// </param>
```

Also update the type's `<summary>`, replacing `A structured knowledge-search result: passages grouped by source with full provenance, plus diagnostics` with `A structured knowledge-search result: passages with full provenance, plus diagnostics`.

- [ ] **Step 3: Update `src/OKF4net.Catalog/README.md`**

Replace the bullet at lines 34-41 (`**Searches every enabled source, grouped by source.**` through the design-notes link) with:

```markdown
- **Searches every enabled source, with a selectable ranking strategy.**
  `IKnowledgeResolver` fans a query out across every enabled
  `KnowledgeCatalogSource` (using the same `ConceptSearch` scorer the
  `OKF4net.Agents` tools use). How the results come back is your choice:
  - `GroupedBySource` (default) — each source's own ranked results
    concatenated, source by source, in priority order. No fusion or dedup.
  - `Merged` — one cross-source ranking by descending score, with priority
    as a tie-break only.
  - `PriorityWeighted` — one cross-source ranking by source priority first,
    score only within a priority tier.

  Both merged strategies also collapse two manifest entries that resolve to
  the same directory (searching that bundle once, not twice) and accept an
  optional fairness quota that interleaves sources so one prolific source
  cannot crowd out the rest of a budget-truncated result.
```

Then replace the "V1 limits" third bullet (lines 164-165):

```markdown
- All enabled sources are searched and results are grouped by source; there is
  no fusion, deduplication, or merged cross-source ranking.
```

with:

```markdown
- No semantic/fuzzy deduplication — two concepts with similar content in
  genuinely different bundles are both returned. Only two manifest entries
  resolving to the *same directory* are collapsed.
```

Finally, add this section immediately before the `## V1 limits` heading. (The
block below is fenced with FOUR backticks so its inner C# fence survives; write
the section into the README with the inner three-backtick fence only.)

````markdown
## Choosing a ranking strategy

Set a default for the whole host, and override it per query where needed:

```csharp
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

services.AddKnowledge(o =>
{
    o.AddCatalogFile("./config/catalog.json");
    o.DefaultResolverStrategy = KnowledgeResolverStrategy.Merged;
    o.DefaultFairnessQuota = 2;   // optional; null (the default) disables it
});

// Per-query override, through the same injected IKnowledgeResolver:
var context = await resolver.SearchAsync(new KnowledgeQuery("refund policy")
{
    ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted,
});
```

`DefaultResolverStrategy` defaults to `GroupedBySource`, so upgrading changes
no existing deployment's result ordering until you opt in.

A fairness quota caps how many *consecutive* passages one source may
contribute before another source's next-best passage is pulled ahead. It
reorders and never drops, so it matters only to consumers that truncate
early — an agent context provider spending a token budget top-down, for
instance, which would otherwise let one source's whole run consume the
budget.
````

- [ ] **Step 4: Update the root `README.md`**

Replace the third "V1 limits, stated exactly" bullet (lines 370-371):

```markdown
- All enabled sources are searched, but results are **grouped by source — no
  fusion, deduplication, or merged cross-source ranking**.
```

with:

```markdown
- No semantic/fuzzy deduplication across sources (two manifest entries
  resolving to the *same directory* are collapsed; similar content in
  genuinely different bundles is not).
```

Then replace the "V2 preview (not implemented)" paragraph (lines 384-389) with:

```markdown
**Cross-source ranking (shipped):** three selectable resolver strategies —
`GroupedBySource` (the default, unchanged behaviour), `Merged` (one ranking
by descending score across every source), and `PriorityWeighted` (source
priority first, score within a tier) — chosen per host or per query, with
optional fairness interleaving for budget-truncated consumers. See
[the resolver-strategies design](docs/design/specs/2026-07-28-okf4net-v2-resolver-strategies.md)
and [`OKF4net.Catalog`'s README](src/OKF4net.Catalog/README.md#choosing-a-ranking-strategy).

**V2 preview (not implemented):** application-filtered bundles — per-caller
or per-tenant visibility of which sources are searched at all. See
[§9 of the local catalog design](docs/design/specs/2026-07-24-okf4net-local-catalog-design.md#9-v2-design-team-scoped-bundles)
for the open questions there.
```

- [ ] **Step 5: Add the CHANGELOG entry**

In `CHANGELOG.md`, under the existing `## [Unreleased]` heading, add:

```markdown
### Added

- **Selectable resolver ranking strategies.** `IKnowledgeResolver` searches
  can now be ranked three ways: `GroupedBySource` (each source's results
  concatenated in priority order — the previous and still-default
  behaviour), `Merged` (one cross-source ranking by descending score, with
  source priority as a tie-break only), and `PriorityWeighted` (source
  priority first, score only within a priority tier). Choose one per host
  via `KnowledgeOptions.DefaultResolverStrategy`, or per call via
  `KnowledgeQuery.ResolverStrategy`. `AddKnowledge` now registers
  `KnowledgeResolverRouter` as the `IKnowledgeResolver`, so existing
  consumers gain per-query selection without any code change, and result
  ordering is unchanged until a host opts in.
- **Fairness interleaving for fused strategies.** An optional
  `FairnessQuota` (host-level `KnowledgeOptions.DefaultFairnessQuota` or
  per-query `KnowledgeQuery.FairnessQuota`) caps how many consecutive
  passages one source may contribute before another source's next-best
  passage is pulled ahead. It reorders only — no passage is ever dropped —
  so it affects consumers that truncate early, such as an agent context
  provider spending a token budget top-down.
- **Same-directory source dedup.** The merged strategies collapse two
  enabled manifest entries that resolve to the same directory, searching
  that bundle once instead of twice. Two *different* directories that
  happen to share a concept id are never merged: a concept id is relative
  to its own bundle root and is not a globally stable identity.

### Changed

- **`DefaultKnowledgeResolver` is renamed `GroupedKnowledgeResolver`**
  (behaviour identical). Code that resolves `IKnowledgeResolver` from DI is
  unaffected; only direct references to the concrete type name need
  updating.
```

- [ ] **Step 6: Verify every documented symbol actually exists**

```bash
grep -n "KnowledgeResolverStrategy\|KnowledgeResolverRouter\|DefaultResolverStrategy\|DefaultFairnessQuota\|GroupedKnowledgeResolver\|MergedKnowledgeResolver\|PriorityWeightedKnowledgeResolver" src/OKF4net.Catalog/README.md README.md CHANGELOG.md
```

Then confirm each name found appears in source:

```bash
grep -rn "class KnowledgeResolverRouter\|enum KnowledgeResolverStrategy\|class MergedKnowledgeResolver\|class PriorityWeightedKnowledgeResolver\|class GroupedKnowledgeResolver\|DefaultResolverStrategy\|DefaultFairnessQuota" --include="*.cs" src/
```

Expected: every documented symbol resolves to a real declaration.

- [ ] **Step 7: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **697/697** passing (unchanged — this task adds no tests).

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net.Catalog/IKnowledgeResolver.cs src/OKF4net.Catalog/KnowledgeContext.cs src/OKF4net.Catalog/README.md README.md CHANGELOG.md
git commit -m "docs(catalog): document the selectable resolver strategies

IKnowledgeResolver's own doc stated the grouped-by-source ordering as the
INTERFACE contract and named one implementation as the implementation --
both halves stop being true with three strategies, so ordering moves to
each implementation's own documentation. Also converts the root README's
'V2 preview' fusion paragraph to shipped, leaving only the genuinely
unimplemented half (application-filtered bundles) as preview."
```

---

## Definition of done

- `dotnet build OKF4net.sln -c Release` — 0 warnings, 0 errors.
- `dotnet format OKF4net.sln --verify-no-changes` — clean.
- `dotnet test OKF4net.sln -c Release` — **697/697** passing.
- `grep -rn "DefaultKnowledgeResolver" --include="*.cs" src/ tests/` — no output.
- No `PackageReference` added to any project.
- `tests/fixtures/` untouched.

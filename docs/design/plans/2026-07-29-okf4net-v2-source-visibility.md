# Per-Caller Source Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a host restrict which enabled `Knowledge`-role catalog sources a caller's search may see, based on the caller's `KnowledgeAccessScope`.

**Architecture:** Two mutually-exclusive mechanisms on `KnowledgeQuery` (`PermittedSourceIds`, a host-precomputed set; `SourceVisibilityPolicy`, a per-source delegate) plus a host-level default for the delegate form, resolved by one new shared helper (`SourceVisibility`, mirroring `ResolverGuards`) and applied upstream of the fan-out in both `GroupedKnowledgeResolver` and `FusedResolverEngine`.

**Tech Stack:** C# / net10.0, xunit. No new packages anywhere.

**Spec:** `docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md`

## Global Constraints

- **Zero third-party runtime dependencies in `src/OKF4net.Catalog/`** — BCL + a project reference to `OKF4net` only. Do not add any `PackageReference`.
- `src/OKF4net.Catalog.Hosting/` may reference only `Microsoft.Extensions.DependencyInjection.Abstractions` (already present).
- Every new source file starts with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces; nullable enabled; XML doc comments on all public API. `TreatWarningsAsErrors` makes a missing or broken `<see cref>` a build error.
- `dotnet build OKF4net.sln -c Release` must report 0 warnings, 0 errors.
- `dotnet format OKF4net.sln --verify-no-changes` must stay clean.
- `dotnet test OKF4net.sln -c Release` — baseline is **718/718 green** at plan-writing time. **Before starting Task 1, run the suite yourself and use the number you actually observe as ground truth** — this repo has frequent parallel-session activity on `dev`, so the baseline may have shifted; the expected counts below assume 718 and must be adjusted by whatever delta you find.
- Never edit anything under `tests/fixtures/` — byte-exact golden captures.
- `OKF4net.Catalog.Hosting`'s namespace (`OKF4net.Catalog.Hosting`) is textually nested under `OKF4net.Catalog`, so types in `OKF4net.Catalog` (e.g. `KnowledgeAccessScope`, `KnowledgeCatalogSource`) resolve there without an explicit `using` — this is why `KnowledgeOptions.cs` today references `KnowledgeResolverStrategy` unqualified. Do not add a redundant `using OKF4net.Catalog;` to that file.
- **Task order matters:** Tasks 1 and 2 can be done in either order relative to each other but both must land before Task 3. Task 3 must land before Task 4. Task 4 must land before Task 5. Task 5 must land before Task 6. Task 7 (documentation) must land last, since it describes the finished surface.

---

### Task 1: `KnowledgeQuery` data model

Adds the three new members and rewrites the class's `<remarks>` documenting why the prior "no identity fields" restriction no longer applies. No resolver reads them yet.

**Files:**
- Modify: `src/OKF4net.Catalog/KnowledgeQuery.cs`
- Test: `tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs` (already exists — 3 facts for `ResolverStrategy`/`FairnessQuota`; add to it, do not replace it)

**Interfaces:**
- Consumes: `KnowledgeAccessScope` (existing, `src/OKF4net.Catalog/KnowledgeAccessScope.cs`), `KnowledgeCatalogSource` (existing).
- Produces: `KnowledgeQuery.Scope` (`KnowledgeAccessScope`, non-nullable, defaults to `KnowledgeAccessScope.Local`), `KnowledgeQuery.PermittedSourceIds` (`IReadOnlySet<string>?`), `KnowledgeQuery.SourceVisibilityPolicy` (`Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>?`). Every later task references these three exact names and types.

- [ ] **Step 1: Write the failing tests**

Append these four facts to `tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs`, inside the existing `KnowledgeQueryTests` class, after `Overriding_one_selection_field_leaves_the_others_intact`:

```csharp
    [Fact]
    public void Visibility_fields_default_to_unrestricted()
    {
        var query = new KnowledgeQuery("orders");

        Assert.Equal(KnowledgeAccessScope.Local, query.Scope);
        Assert.Null(query.PermittedSourceIds);
        Assert.Null(query.SourceVisibilityPolicy);
    }

    [Fact]
    public void Visibility_fields_round_trip_through_an_initializer()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        var permitted = new HashSet<string> { "a", "b" };
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool> policy = (_, source) => source.Id == "a";

        var query = new KnowledgeQuery("orders")
        {
            Scope = scope,
            PermittedSourceIds = permitted,
            SourceVisibilityPolicy = policy,
        };

        Assert.Equal(scope, query.Scope);
        Assert.Same(permitted, query.PermittedSourceIds);
        Assert.Same(policy, query.SourceVisibilityPolicy);
    }

    [Fact]
    public void Overriding_PermittedSourceIds_leaves_Scope_and_the_policy_intact()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool> policy = (_, source) => source.Id == "a";
        var original = new KnowledgeQuery("orders")
        {
            Scope = scope,
            SourceVisibilityPolicy = policy,
        };

        var narrowed = original with { PermittedSourceIds = new HashSet<string> { "a" } };

        Assert.Equal(scope, narrowed.Scope);
        Assert.Same(policy, narrowed.SourceVisibilityPolicy);
        Assert.Null(original.PermittedSourceIds);
    }

    [Fact]
    public void KnowledgeAccessScope_Local_is_all_null_and_equal_by_value()
    {
        var a = new KnowledgeQuery("orders");
        var b = new KnowledgeQuery("orders");

        Assert.True(a.Scope.IsLocal);
        Assert.Equal(a.Scope, b.Scope);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeQueryTests"
```

Expected: **build failure** — `KnowledgeQuery` has no `Scope`/`PermittedSourceIds`/`SourceVisibilityPolicy` members.

- [ ] **Step 3: Add the three members and rewrite the `<remarks>`**

In `src/OKF4net.Catalog/KnowledgeQuery.cs`, replace the type-level `<remarks>` block:

```csharp
/// <remarks>
/// Deliberately V1-scoped: no user/tenant/path fields. Those are identity and
/// routing concerns the OKF spec (§8) keeps orthogonal to a search query, and
/// adding them here would be premature surface before an actual multi-tenant
/// consumer exists.
/// </remarks>
```

with:

```csharp
/// <remarks>
/// Carries the caller's identity (<see cref="Scope"/>) and, optionally, which
/// sources that caller may see (<see cref="PermittedSourceIds"/> or
/// <see cref="SourceVisibilityPolicy"/>) -- the "actual multi-tenant consumer"
/// an earlier version of this remark said would justify adding identity
/// fields here has now materialized; see
/// docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md.
/// </remarks>
```

Then add the three new members inside the record body, after the existing `FairnessQuota` property (the last member):

```csharp
    /// <summary>
    /// The caller's identity, for source-visibility filtering
    /// (<see cref="PermittedSourceIds"/>/<see cref="SourceVisibilityPolicy"/>)
    /// and for any <see cref="SourceVisibilityPolicy"/> a host or this query
    /// supplies. Defaults to <see cref="KnowledgeAccessScope.Local"/> -- the
    /// same all-null sentinel already used throughout the memory-scoping
    /// work -- rather than a second nullability story for "no identity
    /// supplied." A policy evaluated against <c>Local</c> decides for itself
    /// whether an unscoped caller sees everything or nothing.
    /// </summary>
    public KnowledgeAccessScope Scope { get; init; } = KnowledgeAccessScope.Local;

    /// <summary>
    /// When set, only sources whose <see cref="KnowledgeCatalogSource.Id"/>
    /// is in this set are searched -- the default/recommended visibility
    /// mechanism: a host precomputes the exact set of source IDs a caller
    /// may see (however it wants -- tenant lookup, application/purpose
    /// lookup, or both combined) and hands it to the query. Always wins over
    /// any host-level <c>DefaultSourceVisibilityPolicy</c>, being more
    /// specific to this one call. Has no host-level default: a static ID set
    /// cannot represent "differs by tenant" at host-configuration time.
    /// <see langword="null"/> (the default) applies no restriction from this
    /// field. Mutually exclusive with <see cref="SourceVisibilityPolicy"/> on
    /// the same query -- setting both throws (see
    /// <c>ResolverGuards.ValidateQuery</c>).
    /// </summary>
    public IReadOnlySet<string>? PermittedSourceIds { get; init; }

    /// <summary>
    /// When set, only sources for which this function returns
    /// <see langword="true"/> (given <see cref="Scope"/> and the source
    /// under consideration) are searched -- the override mechanism, for
    /// visibility rules a flat ID list can't express conveniently. Overrides
    /// any host-level default policy for this one call.
    /// <see langword="null"/> (the default) defers to that host default.
    /// Mutually exclusive with <see cref="PermittedSourceIds"/> on the same
    /// query -- setting both throws (see <c>ResolverGuards.ValidateQuery</c>).
    /// Synchronous by design: a host needing asynchronous work (e.g. a
    /// database call) to determine visibility does it once, before
    /// constructing the query, via <see cref="PermittedSourceIds"/> instead --
    /// not per source inside a resolver's fan-out loop.
    /// </summary>
    public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? SourceVisibilityPolicy { get; init; }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeQueryTests"
```

Expected: 7 passing (3 existing + 4 new).

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **722/722** passing (718 + 4 new; adjust both numbers by your Step-0 baseline delta if it differed from 718).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeQuery.cs tests/OKF4net.Tests/Catalog/KnowledgeQueryTests.cs
git commit -m "feat(catalog): add Scope/PermittedSourceIds/SourceVisibilityPolicy to KnowledgeQuery

Nothing reads these yet -- this is the data model only. PermittedSourceIds
has no host-level default (a static set can't represent 'differs by
tenant'); SourceVisibilityPolicy does, because a function can still vary
per call by reading the Scope argument it's given."
```

---

### Task 2: `ResolverGuards` mutual-exclusion validation

Setting both `PermittedSourceIds` and `SourceVisibilityPolicy` on the same query is a caller-created contradiction, not something to silently resolve — reject it at the shared validation boundary every resolver already calls, so it fails identically regardless of strategy.

**Files:**
- Modify: `src/OKF4net.Catalog/ResolverGuards.cs`
- Test: `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`, `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs`, `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs` (add one fact to each — the spec's acceptance criteria (§10) require the rejection to work "identically across all three strategies," so all three get their own proof, not just two)

**Interfaces:**
- Consumes: `KnowledgeQuery.PermittedSourceIds`/`SourceVisibilityPolicy` (Task 1).
- Produces: no new public API. `ResolverGuards.ValidateQuery` now also rejects this combination; every resolver's existing call to it inherits the new check for free.

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`, inside the class, after `SearchAsync_rejects_an_undefined_ResolverStrategy_even_though_it_never_reads_it`:

```csharp
    [Fact]
    public async Task SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

        // Even though this strategy never reads either field -- the same
        // reasoning already established for FairnessQuota/ResolverStrategy:
        // a malformed query fails the same way whichever strategy runs it.
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "hi" },
            SourceVisibilityPolicy = (_, _) => true,
        }));

        Assert.Contains("PermittedSourceIds", ex.Message, StringComparison.Ordinal);
    }
```

Append to `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs`, inside the class, after `SearchAsync_rejects_an_undefined_ResolverStrategy`:

```csharp
    [Fact]
    public async Task SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "hi" },
            SourceVisibilityPolicy = (_, _) => true,
        }));

        Assert.Contains("PermittedSourceIds", ex.Message, StringComparison.Ordinal);
    }
```

Append to `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs`, inside the class, after `SearchAsync_rejects_an_undefined_ResolverStrategy`:

```csharp
    [Fact]
    public async Task SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("src", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "src" },
            SourceVisibilityPolicy = (_, _) => true,
        }));

        Assert.Contains("PermittedSourceIds", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together"
```

Expected: all three **pass unexpectedly with no exception thrown** (or rather, the assertion inside `Assert.ThrowsAsync` fails because no exception is thrown) -- `ResolverGuards.ValidateQuery` doesn't check this combination yet.

- [ ] **Step 3: Add the check**

In `src/OKF4net.Catalog/ResolverGuards.cs`, extend `ValidateQuery`'s `<exception>` doc:

```csharp
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace; its <see cref="KnowledgeQuery.FairnessQuota"/>
    /// is set but not greater than zero; its
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> is set to a value that
    /// is not a defined <see cref="KnowledgeResolverStrategy"/> member; or
    /// both <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> are set.
    /// </exception>
    internal static void ValidateQuery(KnowledgeQuery query)
```

Then, at the end of the method body (after the existing `ResolverStrategy` check, before the closing brace), add:

```csharp
        // A caller-created contradiction, not something to silently resolve:
        // which one should win is not this method's call to make. Checked
        // here (not in SourceVisibility.Filter) so it fails identically
        // whichever strategy runs the query, same reasoning as every other
        // check in this method.
        if (query.PermittedSourceIds is not null && query.SourceVisibilityPolicy is not null)
        {
            throw new ArgumentException(
                "KnowledgeQuery.PermittedSourceIds and SourceVisibilityPolicy cannot both be set on the same query; choose one.",
                nameof(query));
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together"
```

Expected: 3 passing.

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **725/725** passing (722 + 3 new).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/ResolverGuards.cs tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs
git commit -m "feat(catalog): reject PermittedSourceIds+SourceVisibilityPolicy set together

Same reasoning already established for FairnessQuota/ResolverStrategy in
ResolverGuards: a malformed query must fail identically whichever strategy
happens to run it, including GroupedKnowledgeResolver, which will never
read either field. All three concrete strategies get their own proof
(spec acceptance criteria require the rejection to work identically
across all three), not just two."
```

---

### Task 3: `SourceVisibility` shared resolution helper

The algorithm itself, in one place, tested standalone against hand-built source lists — no catalog or bundle needed, since this is pure list filtering. Not wired into any resolver yet.

**Files:**
- Create: `src/OKF4net.Catalog/SourceVisibility.cs`
- Test: `tests/OKF4net.Tests/Catalog/SourceVisibilityTests.cs` (create)

**Interfaces:**
- Consumes: `KnowledgeCatalogSource` (existing), `KnowledgeQuery.Scope`/`PermittedSourceIds`/`SourceVisibilityPolicy` (Task 1).
- Produces: `internal static class SourceVisibility` with `internal static List<KnowledgeCatalogSource> Filter(List<KnowledgeCatalogSource> sources, KnowledgeQuery query, Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultPolicy)`. Task 4 calls this from both `GroupedKnowledgeResolver` and `FusedResolverEngine`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Catalog/SourceVisibilityTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="SourceVisibility.Filter"/>: the shared resolution algorithm
/// both <see cref="GroupedKnowledgeResolver"/> and
/// <see cref="FusedResolverEngine"/> apply before searching. Exercised
/// directly against hand-built source lists -- pure list filtering, no
/// catalog or bundle needed.
/// </summary>
public class SourceVisibilityTests
{
    private static KnowledgeCatalogSource Source(string id) =>
        new(id, $"./{id}", 0, true, SourceRole.Knowledge);

    [Fact]
    public void No_restriction_returns_every_source_unchanged()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x");

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(sources, result);
    }

    [Fact]
    public void PermittedSourceIds_keeps_only_the_named_sources()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b"), Source("c") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "a", "c" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(new[] { "a", "c" }, result.Select(s => s.Id));
    }

    [Fact]
    public void PermittedSourceIds_wins_over_a_configured_default_policy()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "a" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, _) => false);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Query_level_policy_receives_the_query_Scope_and_each_source()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        var query = new KnowledgeQuery("x")
        {
            Scope = scope,
            SourceVisibilityPolicy = (s, source) => s == scope && source.Id == "b",
        };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(new[] { "b" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Query_level_policy_overrides_the_host_default()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x") { SourceVisibilityPolicy = (_, source) => source.Id == "a" };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, _) => true);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Host_default_policy_applies_when_the_query_sets_neither_field()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x");

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, source) => source.Id == "b");

        Assert.Equal(new[] { "b" }, result.Select(s => s.Id));
    }

    [Fact]
    public void An_unmatched_permitted_id_yields_an_empty_result_not_an_error()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "typo-id" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~SourceVisibilityTests"
```

Expected: **build failure** — `SourceVisibility` does not exist.

- [ ] **Step 3: Create the helper**

Create `src/OKF4net.Catalog/SourceVisibility.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Filters an already priority/id-ordered enabled-source list down to the
/// subset visible to one query's caller, per the resolution order in
/// docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md §5.
/// </summary>
/// <remarks>
/// Shared by <see cref="GroupedKnowledgeResolver"/> and
/// <see cref="FusedResolverEngine"/> -- the two places an enabled-source
/// list gets narrowed before searching -- so this algorithm cannot drift
/// between them, the same reasoning <see cref="ResolverGuards"/> already
/// applies to query validation.
/// </remarks>
internal static class SourceVisibility
{
    /// <summary>
    /// Returns the subset of <paramref name="sources"/> visible to
    /// <paramref name="query"/>'s caller.
    /// </summary>
    /// <param name="sources">The enabled, knowledge-role sources under consideration.</param>
    /// <param name="query">
    /// The query whose <see cref="KnowledgeQuery.Scope"/>/
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/>/
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> govern filtering.
    /// </param>
    /// <param name="defaultPolicy">
    /// The host's configured default policy, used when the query sets
    /// neither <see cref="KnowledgeQuery.PermittedSourceIds"/> nor
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/>.
    /// </param>
    /// <remarks>
    /// PRECONDITION: <paramref name="query"/> already passed
    /// <see cref="ResolverGuards.ValidateQuery"/> -- callers are guaranteed
    /// not to have both <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> set, so this
    /// method never needs to re-check that.
    /// </remarks>
    internal static List<KnowledgeCatalogSource> Filter(
        List<KnowledgeCatalogSource> sources,
        KnowledgeQuery query,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultPolicy)
    {
        if (query.PermittedSourceIds is { } permitted)
        {
            return sources.Where(s => permitted.Contains(s.Id)).ToList();
        }

        var policy = query.SourceVisibilityPolicy ?? defaultPolicy;
        if (policy is null)
        {
            return sources;
        }

        return sources.Where(s => policy(query.Scope, s)).ToList();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~SourceVisibilityTests"
```

Expected: 7 passing.

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **732/732** passing (725 + 7 new).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/SourceVisibility.cs tests/OKF4net.Tests/Catalog/SourceVisibilityTests.cs
git commit -m "feat(catalog): add the SourceVisibility resolution helper

Not wired into any resolver yet -- pure list filtering, tested standalone.
PermittedSourceIds always wins when set; otherwise query.SourceVisibilityPolicy
?? defaultPolicy applies; otherwise no restriction."
```

---

### Task 4: Wire visibility filtering into `GroupedKnowledgeResolver` and `FusedResolverEngine`

The actual behavior change: both places an enabled-source list gets computed now narrow it through `SourceVisibility.Filter` before anything else happens. Touches four files because `MergedKnowledgeResolver`/`PriorityWeightedKnowledgeResolver` both need a new constructor parameter to reach `FusedResolverEngine`'s new parameter — mechanically identical to how `defaultFairnessQuota` already threads through these same four files.

**Files:**
- Modify: `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`
- Modify: `src/OKF4net.Catalog/FusedResolverEngine.cs`
- Modify: `src/OKF4net.Catalog/MergedKnowledgeResolver.cs`
- Modify: `src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs`
- Test: `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`, `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs`, `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs`

**Interfaces:**
- Consumes: `SourceVisibility.Filter` (Task 3).
- Produces: `GroupedKnowledgeResolver(IKnowledgeCatalog, IOkfClock?, Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>?)`; `FusedResolverEngine.SearchAsync(..., int? fairnessQuota, Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy, CancellationToken ct)` (new parameter inserted between `fairnessQuota` and `ct`); `MergedKnowledgeResolver`/`PriorityWeightedKnowledgeResolver` constructors each gain a fourth parameter of the same delegate type. Task 5 (`KnowledgeResolverRouter`) passes this new parameter to all three.

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs`, after `SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together` (added in Task 2):

```csharp
    [Fact]
    public async Task SearchAsync_with_PermittedSourceIds_only_searches_the_named_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "hi" },
        });

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }

    [Fact]
    public async Task SearchAsync_with_a_SourceVisibilityPolicy_receives_the_query_Scope()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        var observedScopes = new List<KnowledgeAccessScope>();

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            Scope = scope,
            SourceVisibilityPolicy = (s, source) =>
            {
                observedScopes.Add(s);
                return source.Id == "lo";
            },
        });

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("lo", p.SourceId));
        Assert.All(observedScopes, s => Assert.Equal(scope, s));
    }

    [Fact]
    public async Task SearchAsync_with_a_constructor_default_policy_applies_it_when_the_query_sets_neither_field()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog, defaultSourceVisibilityPolicy: (_, source) => source.Id == "hi");

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }
```

Append to `tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs`, after `SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together` (added in Task 2):

```csharp
    [Fact]
    public async Task SearchAsync_with_PermittedSourceIds_only_searches_the_named_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales")
        {
            PermittedSourceIds = new HashSet<string> { "lo" },
        });

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("lo", p.SourceId));
    }

    [Fact]
    public async Task SearchAsync_with_a_constructor_default_policy_applies_it_when_the_query_sets_neither_field()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, defaultSourceVisibilityPolicy: (_, source) => source.Id == "hi");

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }
```

Append to `tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs`, after `SearchAsync_rejects_an_undefined_ResolverStrategy` (if a prior task added one there) or after the last existing fact:

```csharp
    [Fact]
    public async Task SearchAsync_with_PermittedSourceIds_only_searches_the_named_source()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("only", "a.md"), "---\ntype: Note\ntitle: Orders a\ndescription: orders\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./only", "priority": 1, "enabled": true },
            { "id": "hidden", "path": "./only", "priority": 2, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "src" },
        });

        var passage = Assert.Single(context.Passages);
        Assert.Equal("src", passage.SourceId);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~SearchAsync_with_PermittedSourceIds_only_searches_the_named_source|FullyQualifiedName~SearchAsync_with_a_SourceVisibilityPolicy_receives_the_query_Scope|FullyQualifiedName~SearchAsync_with_a_constructor_default_policy_applies_it_when_the_query_sets_neither_field"
```

Expected: **build failure** — `GroupedKnowledgeResolver`, `MergedKnowledgeResolver`, `PriorityWeightedKnowledgeResolver` have no `defaultSourceVisibilityPolicy` constructor parameter, and `PermittedSourceIds`/`SourceVisibilityPolicy` are not yet consulted by either search path even where they compile.

- [ ] **Step 3: Wire `GroupedKnowledgeResolver`**

In `src/OKF4net.Catalog/GroupedKnowledgeResolver.cs`, replace the field declarations and constructor:

```csharp
    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;

    /// <summary>Creates a resolver over <paramref name="catalog"/>; <paramref name="clock"/> supplies "today" for stale-policy filtering (defaults to the system clock).</summary>
    public GroupedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null)
    {
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
    }
```

with:

```csharp
    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? _defaultSourceVisibilityPolicy;

    /// <summary>Creates a resolver over <paramref name="catalog"/>; <paramref name="clock"/> supplies "today" for stale-policy filtering (defaults to the system clock).</summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy applied when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction -- every
    /// enabled source stays visible to every caller.
    /// </param>
    public GroupedKnowledgeResolver(
        IKnowledgeCatalog catalog,
        IOkfClock? clock = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null)
    {
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultSourceVisibilityPolicy = defaultSourceVisibilityPolicy;
    }
```

Then, in `SearchCoreAsync`, insert the filter call immediately after the existing `enabledSources` computation:

```csharp
        var snapshot = _catalog.Current;
        var enabledSources = snapshot.Sources
            .Where(s => s.Enabled && s.Role == SourceRole.Knowledge)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        enabledSources = SourceVisibility.Filter(enabledSources, query, _defaultSourceVisibilityPolicy);

        if (enabledSources.Count == 0)
```

(The rest of the method — the `NoEnabledSources` diagnostic, the fan-out loop, dedup-free per-source search, `NoMatches` — is unchanged. A visibility-filtered-to-zero result naturally produces `NoEnabledSources`, exactly as a genuinely empty enabled-source list already does — no new diagnostic needed.)

- [ ] **Step 4: Wire `FusedResolverEngine`**

In `src/OKF4net.Catalog/FusedResolverEngine.cs`, add the new `<param>` doc immediately after `fairnessQuota`'s:

```csharp
    /// <param name="fairnessQuota">
    /// The maximum number of CONSECUTIVE passages one source may contribute
    /// before another source's next-best passage is pulled ahead of it;
    /// <see langword="null"/> disables the reorder entirely. Already
    /// validated by <see cref="ResolverGuards"/> at the public boundary. See
    /// <see cref="ApplyFairness"/>.
    /// </param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The host's configured default visibility policy, used when
    /// <paramref name="query"/> sets neither
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> nor
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/>. See
    /// <see cref="SourceVisibility.Filter"/>.
    /// </param>
    /// <param name="ct">A cancellation token observed between sources.</param>
```

(This replaces the existing block that currently ends with `fairnessQuota`'s `</param>` immediately followed by `ct`'s — insert the new `defaultSourceVisibilityPolicy` block between them.)

Change the method signature:

```csharp
    internal static async ValueTask<KnowledgeContext> SearchAsync(
        IKnowledgeCatalog catalog,
        IOkfClock clock,
        KnowledgeQuery query,
        IComparer<RankedPassage> comparer,
        int? fairnessQuota,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy,
        CancellationToken ct)
```

Then, immediately after the existing `enabledSources` computation (before the `if (enabledSources.Count == 0)` check), insert:

```csharp
        enabledSources = SourceVisibility.Filter(enabledSources, query, defaultSourceVisibilityPolicy);
```

`enabledSources` is declared with `var`, so this reassignment (still a `List<KnowledgeCatalogSource>`) compiles without any other change. The rest of the method (dedup, fan-out, stale filter, sort, fairness) is unchanged.

- [ ] **Step 5: Update `MergedKnowledgeResolver`**

In `src/OKF4net.Catalog/MergedKnowledgeResolver.cs`, replace the field declarations and constructor:

```csharp
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than zero.
    /// </exception>
    public MergedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)
    {
        ResolverGuards.ValidateDefaultFairnessQuota(defaultFairnessQuota, nameof(defaultFairnessQuota));

        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
    }
```

with:

```csharp
    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;
    private readonly Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? _defaultSourceVisibilityPolicy;

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
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy applied when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than zero.
    /// </exception>
    public MergedKnowledgeResolver(
        IKnowledgeCatalog catalog,
        IOkfClock? clock = null,
        int? defaultFairnessQuota = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null)
    {
        ResolverGuards.ValidateDefaultFairnessQuota(defaultFairnessQuota, nameof(defaultFairnessQuota));

        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
        _defaultSourceVisibilityPolicy = defaultSourceVisibilityPolicy;
    }
```

Then update `SearchAsync`'s call to the engine:

```csharp
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ResolverGuards.ValidateQuery(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota,
            _defaultSourceVisibilityPolicy, ct);
    }
```

- [ ] **Step 6: Update `PriorityWeightedKnowledgeResolver`**

In `src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs`, apply the identical change: add the `_defaultSourceVisibilityPolicy` field, the fourth constructor parameter (with the same `<param>` doc text used in Step 5), thread it into the constructor body, and update `SearchAsync`:

```csharp
    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;
    private readonly Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? _defaultSourceVisibilityPolicy;

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
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy applied when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than zero.
    /// </exception>
    public PriorityWeightedKnowledgeResolver(
        IKnowledgeCatalog catalog,
        IOkfClock? clock = null,
        int? defaultFairnessQuota = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null)
    {
        ResolverGuards.ValidateDefaultFairnessQuota(defaultFairnessQuota, nameof(defaultFairnessQuota));

        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
        _defaultSourceVisibilityPolicy = defaultSourceVisibilityPolicy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/>, a non-positive
    /// <see cref="KnowledgeQuery.FairnessQuota"/>, or an undefined
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> throws
    /// <see cref="ArgumentException"/> SYNCHRONOUSLY, exactly as in
    /// <see cref="MergedKnowledgeResolver.SearchAsync"/>.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ResolverGuards.ValidateQuery(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota,
            _defaultSourceVisibilityPolicy, ct);
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~GroupedKnowledgeResolverTests|FullyQualifiedName~MergedKnowledgeResolverTests|FullyQualifiedName~PriorityWeightedKnowledgeResolverTests"
```

Expected: all passing, including the new facts from this task and Task 2.

- [ ] **Step 8: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **738/738** passing (732 + 6 new: 3 in Grouped, 2 in Merged, 1 in PriorityWeighted). Trust your own count of facts you actually added in this task's Step 1 over this arithmetic if they diverge.

- [ ] **Step 9: Commit**

```bash
git add src/OKF4net.Catalog/GroupedKnowledgeResolver.cs src/OKF4net.Catalog/FusedResolverEngine.cs src/OKF4net.Catalog/MergedKnowledgeResolver.cs src/OKF4net.Catalog/PriorityWeightedKnowledgeResolver.cs tests/OKF4net.Tests/Catalog/GroupedKnowledgeResolverTests.cs tests/OKF4net.Tests/Catalog/MergedKnowledgeResolverTests.cs tests/OKF4net.Tests/Catalog/PriorityWeightedKnowledgeResolverTests.cs
git commit -m "feat(catalog): apply source-visibility filtering before the fan-out

GroupedKnowledgeResolver and FusedResolverEngine both narrow their
enabled-source list through SourceVisibility.Filter immediately after
computing it. A visibility-filtered-to-zero result naturally produces the
existing NoEnabledSources diagnostic -- no new diagnostic code needed.
MergedKnowledgeResolver/PriorityWeightedKnowledgeResolver each gain a
fourth constructor parameter threading the host default through to the
engine, mechanically identical to how defaultFairnessQuota already does."
```

---

### Task 5: `KnowledgeResolverRouter`, `KnowledgeOptions`, and `AddKnowledge`

The host-facing configuration surface: a default policy set once, reachable through the single `IKnowledgeResolver` every consumer already injects.

**Files:**
- Modify: `src/OKF4net.Catalog/KnowledgeResolverRouter.cs`
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`
- Modify: `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`
- Test: `tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs`, `tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: the four-parameter constructors from Task 4.
- Produces: `KnowledgeResolverRouter(IKnowledgeCatalog, KnowledgeResolverStrategy, int?, Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>?, IOkfClock?)` (new parameter inserted between `defaultFairnessQuota` and `clock`); `KnowledgeOptions.DefaultSourceVisibilityPolicy` (`Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>?`, settable, default `null`). Task 6 does not depend on either directly (it goes through `OkfContextProvider`'s existing `IKnowledgeResolver` field), but both must exist and be wired for Task 6's E2E test to demonstrate anything meaningful end to end through DI.

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs`, after the last existing fact:

```csharp
    [Fact]
    public async Task A_constructor_default_visibility_policy_reaches_every_strategy()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(
            catalog, defaultSourceVisibilityPolicy: (_, source) => source.Id == "weak-hi");

        var grouped = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource });
        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });
        var weighted = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted });

        Assert.All(grouped.Passages, p => Assert.Equal("weak-hi", p.SourceId));
        Assert.All(merged.Passages, p => Assert.Equal("weak-hi", p.SourceId));
        Assert.All(weighted.Passages, p => Assert.Equal("weak-hi", p.SourceId));
        Assert.NotEmpty(grouped.Passages);
    }

    [Fact]
    public async Task A_query_level_PermittedSourceIds_overrides_the_router_default_policy()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(
            catalog, defaultSourceVisibilityPolicy: (_, source) => source.Id == "weak-hi");

        var context = await router.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "strong-lo" },
        });

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("strong-lo", p.SourceId));
    }
```

Append to `tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs`, after the last existing fact:

```csharp
    [Fact]
    public async Task AddKnowledge_wires_DefaultSourceVisibilityPolicy_end_to_end()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o =>
        {
            o.AddCatalogFile(catalogPath);
            o.DefaultSourceVisibilityPolicy = (_, source) => source.Id == "hi";
        });
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IKnowledgeResolver>();

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~A_constructor_default_visibility_policy_reaches_every_strategy|FullyQualifiedName~A_query_level_PermittedSourceIds_overrides_the_router_default_policy|FullyQualifiedName~AddKnowledge_wires_DefaultSourceVisibilityPolicy_end_to_end"
```

Expected: **build failure** — `KnowledgeResolverRouter` has no `defaultSourceVisibilityPolicy` parameter, `KnowledgeOptions` has no `DefaultSourceVisibilityPolicy` property.

- [ ] **Step 3: Update `KnowledgeResolverRouter`**

In `src/OKF4net.Catalog/KnowledgeResolverRouter.cs`, replace the constructor's `<param>` docs and signature:

```csharp
    /// <param name="defaultFairnessQuota">
    /// The fairness quota the fused strategies use when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset;
    /// <see langword="null"/> (the default) disables reordering.
    /// </param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="defaultStrategy"/> is not a defined
    /// <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    public KnowledgeResolverRouter(
        IKnowledgeCatalog catalog,
        KnowledgeResolverStrategy defaultStrategy = KnowledgeResolverStrategy.GroupedBySource,
        int? defaultFairnessQuota = null,
        IOkfClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ResolverGuards.ValidateStrategy(defaultStrategy, nameof(defaultStrategy));

        var effectiveClock = clock ?? new SystemClock();
        _grouped = new GroupedKnowledgeResolver(catalog, effectiveClock);
        _merged = new MergedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota);
        _priorityWeighted = new PriorityWeightedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota);
        _defaultStrategy = defaultStrategy;
    }
```

with:

```csharp
    /// <param name="defaultFairnessQuota">
    /// The fairness quota the fused strategies use when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset;
    /// <see langword="null"/> (the default) disables reordering.
    /// </param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy every strategy uses when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction.
    /// </param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="defaultStrategy"/> is not a defined
    /// <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    public KnowledgeResolverRouter(
        IKnowledgeCatalog catalog,
        KnowledgeResolverStrategy defaultStrategy = KnowledgeResolverStrategy.GroupedBySource,
        int? defaultFairnessQuota = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null,
        IOkfClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ResolverGuards.ValidateStrategy(defaultStrategy, nameof(defaultStrategy));

        var effectiveClock = clock ?? new SystemClock();
        _grouped = new GroupedKnowledgeResolver(catalog, effectiveClock, defaultSourceVisibilityPolicy);
        _merged = new MergedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota, defaultSourceVisibilityPolicy);
        _priorityWeighted = new PriorityWeightedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota, defaultSourceVisibilityPolicy);
        _defaultStrategy = defaultStrategy;
    }
```

- [ ] **Step 4: Update `KnowledgeOptions`**

In `src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`, add this property immediately after `DefaultFairnessQuota`:

```csharp
    /// <summary>
    /// The visibility policy every strategy applies when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction -- every
    /// enabled knowledge source stays visible to every caller, the
    /// behaviour every pre-existing deployment already has.
    /// </summary>
    public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? DefaultSourceVisibilityPolicy { get; set; }
```

No change to `Validate()`: unlike `DefaultFairnessQuota` (an `int?` with an invalid range) and `DefaultResolverStrategy` (an enum that can hold an undefined value via a config bind), a `Func<...>` reference is either `null` or a valid delegate — there is no "malformed" state to reject at registration time.

- [ ] **Step 5: Update `AddKnowledge`**

In `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`, replace:

```csharp
        var defaultStrategy = options.DefaultResolverStrategy;
        var defaultFairnessQuota = options.DefaultFairnessQuota;
        services.TryAddSingleton<IKnowledgeResolver>(sp => new KnowledgeResolverRouter(
            sp.GetRequiredService<IKnowledgeCatalog>(), defaultStrategy, defaultFairnessQuota));
```

with:

```csharp
        var defaultStrategy = options.DefaultResolverStrategy;
        var defaultFairnessQuota = options.DefaultFairnessQuota;
        var defaultSourceVisibilityPolicy = options.DefaultSourceVisibilityPolicy;
        services.TryAddSingleton<IKnowledgeResolver>(sp => new KnowledgeResolverRouter(
            sp.GetRequiredService<IKnowledgeCatalog>(), defaultStrategy, defaultFairnessQuota, defaultSourceVisibilityPolicy));
```

(Capturing into a local before the factory closure, exactly like the two existing defaults, so the registration cannot observe a later mutation of `options`.)

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~KnowledgeResolverRouterTests|FullyQualifiedName~KnowledgeServiceCollectionExtensionsTests"
```

Expected: all passing, including the 3 new facts from this task.

- [ ] **Step 7: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **741/741** passing (738 + 3 new; adjust if Task 4's actual count differed from the plan's estimate, per that task's own note).

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeResolverRouter.cs src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/KnowledgeResolverRouterTests.cs tests/OKF4net.Tests/Catalog/Hosting/KnowledgeServiceCollectionExtensionsTests.cs
git commit -m "feat(catalog): host-level default source-visibility policy

KnowledgeOptions.DefaultSourceVisibilityPolicy threads through AddKnowledge
into KnowledgeResolverRouter, which passes it to all three constructed
strategies -- the same plumbing DefaultFairnessQuota already uses. No
registration-time validation added: a delegate reference has no invalid
range the way an int or an enum member does."
```

---

### Task 6: `OkfContextProvider` integration

The surgical change the whole design exists for: the `KnowledgeAccessScope` already resolved for memory now also reaches knowledge search.

**Files:**
- Modify: `src/OKF4net.Agents/OkfContextProvider.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfContextProviderVisibilityTests.cs` (create)

**Interfaces:**
- Consumes: `KnowledgeQuery.Scope` (Task 1), the fully-wired resolver chain (Tasks 4-5).
- Produces: no new public API — `ProvideScopedAsync`'s internal knowledge query now carries `Scope`.

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Agents/OkfContextProviderVisibilityTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <see cref="OkfContextProvider"/>'s scoped (V2) knowledge read: the same
/// <see cref="KnowledgeAccessScope"/> already resolved for the memory read
/// (via <c>ScopeAccessor</c>) now also reaches the knowledge query, so a
/// host-configured <see cref="KnowledgeResolverRouter"/> default visibility
/// policy can restrict what a given caller's invocation ever sees.
/// </summary>
public class OkfContextProviderVisibilityTests
{
    private sealed class TestAgentSession : AgentSession { }

    private static FileKnowledgeCatalog SetUpTenantScopedCatalog(TempDir root)
    {
        root.Write(Path.Combine("acme-kb", "note.md"),
            "---\ntype: Note\ntitle: Orders acme\ndescription: orders\n---\nAcme orders detail.\n");
        root.Write(Path.Combine("beta-kb", "note.md"),
            "---\ntype: Note\ntitle: Orders beta\ndescription: orders\n---\nBeta orders detail.\n");

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "acme-kb", "path": "./acme-kb", "role": "knowledge" },
                { "id": "beta-kb", "path": "./beta-kb", "role": "knowledge" }
              ]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    private static FileMemoryStore EmptyMemoryStore(TempDir root)
    {
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        return new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.User] = Path.Combine(root.Path, "mem"),
        });
    }

    private static AIContextProvider.InvokingContext Invoking(AgentSession? session, string userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session, ai);
#pragma warning restore MAAI001
    }

    [Fact]
    public async Task A_router_default_visibility_policy_restricts_what_a_scoped_caller_sees()
    {
        using var root = new TempDir();
        using var catalog = SetUpTenantScopedCatalog(root);

        // Policy: a tenant may only see the knowledge source whose id starts
        // with its own tenant id -- a simple, realistic per-tenant rule.
        var resolver = new KnowledgeResolverRouter(
            catalog,
            defaultSourceVisibilityPolicy: (scope, source) => source.Id.StartsWith(scope.TenantId ?? string.Empty, StringComparison.Ordinal));

        var options = new OkfContextProviderOptions
        {
            TokenBudget = 2000,
            KnowledgeBudgetShare = 1.0,
            MemoryBudgetShare = 0.0,
            MemoryCapture = MemoryCaptureMode.Disabled,
            ScopeAccessor = _ => new KnowledgeAccessScope(tenantId: "acme"),
        };
        var provider = new OkfContextProvider(resolver, EmptyMemoryStore(root), options);

        var result = await provider.ProvideForTest(Invoking(new TestAgentSession(), "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        Assert.Contains("knowledge:acme-kb:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledge:beta-kb:", text, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~OkfContextProviderVisibilityTests"
```

Expected: the assertion fails — `beta-kb` content is present, because `OkfContextProvider` does not yet pass `Scope` on the knowledge query, so the router's default policy is evaluated against `KnowledgeAccessScope.Local` (all-null `TenantId`), and `source.Id.StartsWith("")` is `true` for every source.

- [ ] **Step 3: Thread `Scope` into the knowledge query**

In `src/OKF4net.Agents/OkfContextProvider.cs`, inside `ProvideScopedAsync`, replace:

```csharp
            var knowledgeQuery = new KnowledgeQuery(query) { FairnessQuota = _options.KnowledgeQueryFairnessQuota };
```

with:

```csharp
            var knowledgeQuery = new KnowledgeQuery(query) { FairnessQuota = _options.KnowledgeQueryFairnessQuota, Scope = scope };
```

(`scope` is already resolved a few lines earlier in the same method, at `var scope = _options.ScopeAccessor?.Invoke(context) ?? KnowledgeAccessScope.Local;`, and is already used unchanged for the memory read later in the same method. No other line in this method changes.)

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test OKF4net.sln -c Release --filter "FullyQualifiedName~OkfContextProviderVisibilityTests"
```

Expected: 1 passing.

- [ ] **Step 5: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **742/742** passing (741 + 1 new; adjust for any drift from earlier tasks' actual counts).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Agents/OkfContextProvider.cs tests/OKF4net.Tests/Agents/OkfContextProviderVisibilityTests.cs
git commit -m "feat(agents): thread the resolved scope into the knowledge query

The same KnowledgeAccessScope OkfContextProvider already resolves for the
memory read now also reaches the knowledge query -- the identity plumbing
already existed; only the wiring was missing. No new option: ScopeAccessor
already supplies everything needed."
```

---

### Task 7: Documentation and CHANGELOG

The behaviour is shipped; every doc that still describes source visibility as absent or unimplemented is now wrong.

**Files:**
- Modify: `src/OKF4net.Catalog/IKnowledgeResolver.cs`
- Modify: `src/OKF4net.Catalog/README.md`
- Modify: `README.md` (repo root)
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: every type from Tasks 1-6.
- Produces: no code changes — documentation only.

- [ ] **Step 1: Rewrite `IKnowledgeResolver`'s contract doc**

In `src/OKF4net.Catalog/IKnowledgeResolver.cs`, replace the interface's `<summary>`:

```csharp
/// <summary>
/// Searches across every enabled <see cref="SourceRole.Knowledge"/> source of
/// an <see cref="IKnowledgeCatalog"/> and returns a single
/// <see cref="KnowledgeContext"/>.
/// </summary>
```

with:

```csharp
/// <summary>
/// Searches across every enabled, *visible* <see cref="SourceRole.Knowledge"/>
/// source of an <see cref="IKnowledgeCatalog"/> and returns a single
/// <see cref="KnowledgeContext"/>. Visibility -- which sources a given
/// caller may see at all -- is governed by
/// <see cref="KnowledgeQuery.PermittedSourceIds"/>/
/// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> and any host-level
/// default; see <see cref="SearchAsync"/>.
/// </summary>
```

Then replace `SearchAsync`'s `<summary>` and `<exception>`:

```csharp
    /// <summary>
    /// Runs <paramref name="query"/> against the catalog's currently enabled
    /// sources.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace, or its <see cref="KnowledgeQuery.FairnessQuota"/>
    /// is set but not greater than zero.
    /// </exception>
    ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
```

with:

```csharp
    /// <summary>
    /// Runs <paramref name="query"/> against the catalog's currently
    /// enabled, visible sources.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace; its <see cref="KnowledgeQuery.FairnessQuota"/>
    /// is set but not greater than zero; its
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> is set to a value that
    /// is not a defined <see cref="KnowledgeResolverStrategy"/> member; or
    /// both <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> are set.
    /// </exception>
    ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
```

(The `ResolverStrategy` clause was already true of the shipped behaviour before this plan but missing from this doc comment — a pre-existing gap this task closes while the block is already being touched.)

- [ ] **Step 2: Update `src/OKF4net.Catalog/README.md`**

Replace the "V1 limits" bullet that currently reads:

```markdown
- One shared catalog per `FileKnowledgeCatalog` instance — no per-caller or
  per-tenant filtering of which sources are visible.
```

with:

```markdown
- One shared catalog per `FileKnowledgeCatalog` instance.
```

Then add this new section immediately before `## V1 limits`, following the same shape as the existing "Choosing a ranking strategy" section:

```markdown
## Choosing source visibility

Restrict which sources a caller may see, per host default or per query:

```csharp
services.AddKnowledge(o =>
{
    o.AddCatalogFile("./config/catalog.json");
    o.DefaultSourceVisibilityPolicy = (scope, source) =>
        source.Id.StartsWith(scope.TenantId ?? "", StringComparison.Ordinal);
});

// Per-query override, through the same injected IKnowledgeResolver:
var context = await resolver.SearchAsync(new KnowledgeQuery("refund policy")
{
    Scope = new KnowledgeAccessScope(tenantId: "acme"),
    PermittedSourceIds = new HashSet<string> { "acme-support", "acme-billing" },
});
```

Two mutually exclusive mechanisms — setting both on the same query throws:

- `PermittedSourceIds` — a host-precomputed set of source IDs, the
  recommended default. A host does whatever lookup it needs (tenant,
  application, or both) and hands the resulting set to the query; `OKF4net.Catalog`
  never needs to know how it was computed. Always wins over any host-level
  default policy for that one call.
- `SourceVisibilityPolicy` — a function evaluated per source, for rules a
  flat ID list can't express conveniently. Configurable once per host
  (`DefaultSourceVisibilityPolicy`) and overridable per query, mirroring
  `DefaultResolverStrategy`.

Neither has any effect on a query that sets neither field and a host that
configures no default: every enabled source stays visible to every caller,
exactly as before this feature existed.
```

- [ ] **Step 3: Update the root `README.md`**

Replace the "V1 limits, stated exactly" bullet:

```markdown
- One shared catalog (no per-caller or per-tenant filtering of which sources
  are visible).
```

with:

```markdown
- One shared catalog.
```

Then replace the "V2 preview (not implemented)" paragraph:

```markdown
**V2 preview (not implemented):** application-filtered bundles — per-caller
or per-tenant visibility of which sources are searched at all. See
[§9 of the local catalog design](docs/design/specs/2026-07-24-okf4net-local-catalog-design.md#9-v2-design-team-scoped-bundles)
for the open questions there.
```

with:

```markdown
**Source visibility (shipped):** restrict which sources a caller may see,
per host default or per query — a host-precomputed `PermittedSourceIds` set
(the recommended default) or a `SourceVisibilityPolicy` function evaluated
per source, either overridable per query. See
[the source-visibility design](docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md)
and [`OKF4net.Catalog`'s README](src/OKF4net.Catalog/README.md#choosing-source-visibility).
```

- [ ] **Step 4: Add the CHANGELOG entry**

In `CHANGELOG.md`, under `## [Unreleased]` (currently empty), add:

```markdown
### Added

- **Per-caller source visibility.** `IKnowledgeResolver` searches can now be
  restricted to a subset of enabled `Knowledge`-role sources, based on the
  caller's `KnowledgeAccessScope`. Two mutually-exclusive mechanisms on
  `KnowledgeQuery`: `PermittedSourceIds` (a host-precomputed set of source
  IDs — the recommended default, no host-level default since a static set
  can't represent "differs by tenant") and `SourceVisibilityPolicy` (a
  per-source function, with a `KnowledgeOptions.DefaultSourceVisibilityPolicy`
  host default a function can still vary per call by reading the scope it's
  given). `PermittedSourceIds` always wins over a configured default when
  set. `OkfContextProvider`'s scoped (V2) mode now passes the same
  `KnowledgeAccessScope` it already resolves for memory into the knowledge
  query too.

### Changed

- **`KnowledgeQuery` is no longer V1-scoped.** It gains `Scope`
  (`KnowledgeAccessScope`, defaults to `KnowledgeAccessScope.Local`) — the
  "actual multi-tenant consumer" an earlier doc comment said would justify
  adding identity fields has materialized.
```

- [ ] **Step 5: Verify every documented symbol actually exists**

```bash
grep -n "PermittedSourceIds\|SourceVisibilityPolicy\|DefaultSourceVisibilityPolicy" src/OKF4net.Catalog/README.md README.md CHANGELOG.md
```

Then confirm each name found resolves to a real declaration:

```bash
grep -rn "PermittedSourceIds\|SourceVisibilityPolicy\|DefaultSourceVisibilityPolicy" --include="*.cs" src/
```

Expected: every documented symbol appears in both, with matching names.

- [ ] **Step 6: Full build, format, test**

```bash
dotnet build OKF4net.sln -c Release
dotnet format OKF4net.sln --verify-no-changes
dotnet test OKF4net.sln -c Release
```

Expected: 0 warnings, format clean, **742/742** passing (unchanged — this task adds no tests; use whatever your actual running total was after Task 6).

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Catalog/IKnowledgeResolver.cs src/OKF4net.Catalog/README.md README.md CHANGELOG.md
git commit -m "docs(catalog): document per-caller source visibility

IKnowledgeResolver's own contract doc said 'every enabled source' and its
exception doc omitted the ResolverStrategy case entirely -- both closed in
the same pass, since this design touches that exact block anyway. Converts
the root README's 'V2 preview' paragraph (and both READMEs' matching V1-limits
bullets) from not-implemented to shipped, mirroring how the resolver-strategies
and scoped-memory work updated the same files before."
```

---

## Definition of done

- `dotnet build OKF4net.sln -c Release` — 0 warnings, 0 errors.
- `dotnet format OKF4net.sln --verify-no-changes` — clean.
- `dotnet test OKF4net.sln -c Release` — all passing (baseline 718 + however many facts were actually added across Tasks 1-6; trust the running total from your own Step-5/Step-8/etc. counts over this document's arithmetic if a discrepancy shows up).
- `grep -rn "no per-caller or per-tenant" README.md src/OKF4net.Catalog/README.md` — no output.
- No `PackageReference` added to any project.
- `tests/fixtures/` untouched.

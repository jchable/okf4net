# OKF4net V2 Session/Tenant Memory Tiers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove — through real test coverage, not new design — that the
session and tenant memory tiers Lot 3 built generically actually work, close
the one real isolation gap found in spec review (session paths didn't nest
under tenant/user), and document the deployment pattern.

**Architecture:** No new types, no new public methods, no new storage
implementation. One one-line production fix to `MemoryPath.For`'s `Session`
case (nests it under tenant/user, mirroring how `User` already nests under
`Tenant`). Everything else is test coverage mirroring the existing
`User`-tier test patterns in `FileMemoryStoreTests.cs` and
`OkfContextProviderScopedTests.cs`, plus fixing stale documentation.

**Tech Stack:** C# / .NET 10, xUnit, the existing `OKF4net.Catalog` and
`OKF4net.Agents` projects. No new dependencies.

## Global Constraints

- Zero third-party runtime dependencies — this plan adds no dependencies to
  any project (test-only `xunit` already referenced everywhere).
- File-scoped namespaces, XML doc comments on public API, nullable enabled,
  `TreatWarningsAsErrors` (`Directory.Build.props`) — every new/changed
  method must build with 0 warnings.
- `dotnet format OKF4net.sln --verify-no-changes` and
  `dotnet test OKF4net.sln` must both be clean after every task.
- Never touch `tests/fixtures/` (not applicable here — this plan touches
  none of those files).
- Spec: [`docs/design/specs/2026-07-28-okf4net-v2-session-tenant-tiers.md`](../specs/2026-07-28-okf4net-v2-session-tenant-tiers.md).
  Work in the worktree at `E:/Sources/okf/.claude/worktrees/dev`, branch
  `okf4net-dev`.

---

### Task 1: Fix `MemoryPath.For`'s session-tier nesting

**Files:**
- Modify: `src/OKF4net.Catalog/MemoryPath.cs:53-71` (the `For` method)
- Modify: `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs:50-57` (`Session_tier_keeps_its_literal_prefix_with_an_encoded_segment`)
- Modify: `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs:77-84` (`Fully_local_scope_is_all_bare_sentinels_for_every_tier`)

**Interfaces:**
- Consumes: nothing new — `MemoryPath.For(MemoryTier tier, KnowledgeAccessScope scope)`'s signature is unchanged.
- Produces: `MemoryPath.For(MemoryTier.Session, scope)` now returns
  `"memory-session/{tenant}/{user}/{session}"` instead of
  `"memory-session/{session}"` — every later task's session-tier path
  assertions must use this 4-segment shape (see `MemPath` helper usage in
  Tasks 2 and 4).

- [ ] **Step 1: Update the two existing tests to the new expected shape (they will fail against the current code — that's the point)**

In `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs`, DELETE the entire
existing `Session_tier_keeps_its_literal_prefix_with_an_encoded_segment`
fact (lines 50-57) and put this fact in its place — do not leave the old
one in the file alongside the new one:

```csharp
[Fact]
public void Session_tier_nests_an_encoded_session_segment_under_tenant_and_user()
{
    var segments = MemoryPath.For(MemoryTier.Session, new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "s1")).Split('/');
    Assert.Equal(4, segments.Length);
    Assert.Equal("memory-session", segments[0]);
    AssertEncoded(segments[1], "acme");
    AssertEncoded(segments[2], "alice");
    AssertEncoded(segments[3], "s1");
}
```

And update `Fully_local_scope_is_all_bare_sentinels_for_every_tier`'s
session line:

```csharp
[Fact]
public void Fully_local_scope_is_all_bare_sentinels_for_every_tier()
{
    var local = KnowledgeAccessScope.Local;
    Assert.Equal("memory-user/_local/_local", MemoryPath.For(MemoryTier.User, local));
    Assert.Equal("memory-session/_local/_local/_local", MemoryPath.For(MemoryTier.Session, local));
    Assert.Equal("memory-tenant/_local", MemoryPath.For(MemoryTier.Tenant, local));
}
```

- [ ] **Step 2: Run the tests to verify they fail against the current code**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MemoryPathTests" -c Release`
Expected: FAIL — `Session_tier_nests_an_encoded_session_segment_under_tenant_and_user` expects 4 segments but gets 2; `Fully_local_scope_is_all_bare_sentinels_for_every_tier` expects `"memory-session/_local/_local/_local"` but gets `"memory-session/_local"`.

- [ ] **Step 3: Fix `MemoryPath.For`**

In `src/OKF4net.Catalog/MemoryPath.cs`, the `For` method currently reads:

```csharp
        return tier switch
        {
            MemoryTier.Tenant => $"memory-tenant/{tenant}",
            MemoryTier.User => $"memory-user/{tenant}/{user}",
            MemoryTier.Session => $"memory-session/{session}",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
        };
```

Change the `Session` line to nest under `tenant` and `user` (both already
computed above this switch, used unconditionally by the other two arms):

```csharp
        return tier switch
        {
            MemoryTier.Tenant => $"memory-tenant/{tenant}",
            MemoryTier.User => $"memory-user/{tenant}/{user}",
            MemoryTier.Session => $"memory-session/{tenant}/{user}/{session}",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
        };
```

Also update the method's XML doc comment (`src/OKF4net.Catalog/MemoryPath.cs:45-52`),
which currently reads:

```csharp
    /// <summary>
    /// The '/'-joined concept-path prefix for <paramref name="tier"/> under
    /// <paramref name="scope"/> (e.g. <c>memory-user/acme-1a2b…/alice-3c4d…</c>).
    /// Each non-null scope segment is <see cref="Encode(string)">encoded</see>;
    /// a null segment is the bare <see cref="LocalSentinel"/>. The fixed tier
    /// prefixes (<c>memory-tenant</c>/<c>memory-user</c>/<c>memory-session</c>)
    /// are literals.
    /// </summary>
```

to also describe the session nesting, matching the type-level `<remarks>`
that already documents user's nesting (`src/OKF4net.Catalog/MemoryPath.cs:35-38`):

```csharp
    /// <summary>
    /// The '/'-joined concept-path prefix for <paramref name="tier"/> under
    /// <paramref name="scope"/> (e.g. <c>memory-user/acme-1a2b…/alice-3c4d…</c>).
    /// Each non-null scope segment is <see cref="Encode(string)">encoded</see>;
    /// a null segment is the bare <see cref="LocalSentinel"/>. The fixed tier
    /// prefixes (<c>memory-tenant</c>/<c>memory-user</c>/<c>memory-session</c>)
    /// are literals. <see cref="MemoryTier.Session"/> nests under both tenant
    /// and user (<c>memory-session/&lt;tenant&gt;/&lt;user&gt;/&lt;session&gt;</c>),
    /// the same way <see cref="MemoryTier.User"/> nests under tenant: a bare
    /// <c>memory-session/&lt;session&gt;</c> path would make isolation depend
    /// entirely on the host guaranteeing globally-unique session ids, rather
    /// than being impossible-by-construction the way tenant/user isolation is.
    /// </summary>
```

Also update the type-level `<remarks>` paragraph
(`src/OKF4net.Catalog/MemoryPath.cs:30-38`), which currently ends "User memory
nests under tenant, so cross-tenant collision is impossible by construction,
and the all-null 'local' scope is a valid path for every tier." — extend it
to cover session too:

```csharp
/// carries a <c>-{hash}</c> suffix, the bare sentinel is provably distinct from
/// any encoded value (and <see cref="KnowledgeAccessScope"/> additionally
/// rejects <c>"_local"</c> as an explicit segment). User memory nests under
/// tenant, and session memory nests under both tenant and user, so
/// cross-tenant and cross-user collision are impossible by construction, and
/// the all-null "local" scope is a valid path for every tier.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MemoryPathTests" -c Release`
Expected: PASS, all facts in `MemoryPathTests.cs` (including the untouched
ones — `Tenant_tier_keeps_its_literal_prefix_with_an_encoded_segment`,
`User_tier_nests_an_encoded_user_segment_under_the_encoded_tenant_segment`,
etc. — must remain green, they weren't touched).

- [ ] **Step 5: Full build and format check**

Run: `dotnet build OKF4net.sln -c Release` — expect 0 warnings, 0 errors.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect no output (clean).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/MemoryPath.cs tests/OKF4net.Tests/Catalog/MemoryPathTests.cs
git commit -m "fix(catalog): nest session-tier memory paths under tenant and user

Session's path carried no tenant/user segment, unlike user (nested under
tenant) -- isolation depended entirely on the host guaranteeing globally
unique SessionIds, undocumented and untested. Nests session under both,
mirroring user's precedent; MemoryPath.For already computed tenant/user
unconditionally, so this is a one-line change."
```

---

### Task 2: Session-tier test coverage for `FileMemoryStore`

**Files:**
- Modify: `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: `FileMemoryStore` (`src/OKF4net.Catalog/FileMemoryStore.cs`,
  unchanged by this task — `ReadAsync`/`WriteAsync`/`DeleteScopeAsync`/`EnumerateAsync`
  already generic per Discovery in the spec), `MemoryPath.For` (fixed by
  Task 1), the existing `MemPath`/`Entry` helpers already in this file
  (`tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs:14-30`).
- Produces: a `SessionStore(TempDir tmp)` helper (mirrors the existing
  `UserStore` at line 23-24) for Task 4 or any later task in this file to
  reuse — but Task 4 lives in a different test class
  (`OkfContextProviderScopedTests.cs`), so this is scoped to this file only.

- [ ] **Step 1: Add a `SessionStore` helper next to the existing `UserStore` one**

In `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`, immediately after
the existing `UserStore` helper (line 23-24):

```csharp
    private static FileMemoryStore UserStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.User] = tmp.Path });

    private static FileMemoryStore SessionStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.Session] = tmp.Path });
```

- [ ] **Step 2: Write the failing tests**

Add these facts to `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`,
mirroring the existing `User`-tier facts one-for-one:

```csharp
    [Fact]
    public async Task Write_then_read_round_trips_under_the_session_tier()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-1");

        var write = await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.Session);
        Assert.True(write.Written);
        Assert.Null(write.Error);

        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Session, scope, "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("orders"));
        Assert.Empty(read.Diagnostics);
        Assert.NotEmpty(read.Passages);
        Assert.All(read.Passages, p => Assert.Equal("memory:Session", p.SourceId));
    }

    [Fact]
    public async Task Two_scopes_with_the_same_SessionId_but_different_tenant_and_user_do_not_collide()
    {
        // The regression test for the Task 1 fix: before it, MemoryPath.For
        // produced "memory-session/{session}" with no tenant/user segment,
        // so two different tenants sharing the same SessionId would have
        // written to and read from the exact same path.
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "tenant-a", userId: "alice", sessionId: "shared-session-id");
        var b = new KnowledgeAccessScope(tenantId: "tenant-b", userId: "bob", sessionId: "shared-session-id");

        await store.WriteAsync(a, Entry("tenant-a-secret-nonce"), MemoryTier.Session);

        var readB = await store.ReadAsync(b, new KnowledgeQuery("tenant-a-secret-nonce"));
        Assert.Empty(readB.Passages);

        Assert.NotEqual(
            MemPath(tmp.Path, MemoryTier.Session, a),
            MemPath(tmp.Path, MemoryTier.Session, b));
    }

    [Fact]
    public async Task Case_distinct_sessions_cannot_read_each_others_memory()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var upper = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "Sess1");
        var lower = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess1");

        await store.WriteAsync(upper, Entry("case-variant-secret"), MemoryTier.Session);

        var readLower = await store.ReadAsync(lower, new KnowledgeQuery("case-variant-secret"));
        Assert.Empty(readLower.Passages);
    }

    [Fact]
    public async Task Delete_removes_only_the_target_session_scope_subtree()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-a");
        var b = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-b");
        await store.WriteAsync(a, Entry("session a data"), MemoryTier.Session);
        await store.WriteAsync(b, Entry("session b data"), MemoryTier.Session);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Session);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Session, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.Session, b)));
    }

    [Fact]
    public async Task Session_Read_ConceptId_is_fully_qualified_matching_Enumerate()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-1");
        await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.Session);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("orders"));
        var passage = Assert.Single(read.Passages);

        var listed = await store.EnumerateAsync(scope);
        var concept = Assert.Single(listed);

        Assert.Equal($"{MemoryPath.For(MemoryTier.Session, scope)}/2026-07-27", passage.ConceptId);
        Assert.Equal(concept.ConceptId, passage.ConceptId);
    }

    [Fact]
    public async Task Session_Enumerate_does_not_list_a_different_scopes_concepts()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-a");
        var b = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-b");
        await store.WriteAsync(a, Entry("session a's day"), MemoryTier.Session);

        var listedAsB = await store.EnumerateAsync(b);
        Assert.Empty(listedAsB);
    }

    [Fact]
    public async Task Local_scope_reads_and_writes_the_local_session_subtree()
    {
        using var tmp = new TempDir();
        var store = SessionStore(tmp);

        await store.WriteAsync(KnowledgeAccessScope.Local, Entry("local session notes"), MemoryTier.Session);
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Session, KnowledgeAccessScope.Local, "2026-07-27.md")));

        var read = await store.ReadAsync(KnowledgeAccessScope.Local, new KnowledgeQuery("notes"));
        Assert.NotEmpty(read.Passages);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail (store not yet reachable — actually verify against Task 1's already-applied fix)**

Since Task 1 already fixed `MemoryPath.For`, these tests should PASS on
first run against the current `FileMemoryStore` (nothing in `FileMemoryStore`
itself needs to change — the Discovery section of the spec already verified
its logic is tier-generic). Run them anyway to confirm:

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~FileMemoryStoreTests" -c Release`
Expected: PASS — all Session-tier facts plus all pre-existing `User`-tier facts green.

If `Two_scopes_with_the_same_SessionId_but_different_tenant_and_user_do_not_collide`
fails, Task 1 was not completed correctly — stop and re-check `MemoryPath.For`
before proceeding.

- [ ] **Step 4: Full build and format check**

Run: `dotnet build OKF4net.sln -c Release` — expect 0 warnings, 0 errors.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect no output.

- [ ] **Step 5: Commit**

```bash
git add tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs
git commit -m "test(catalog): cover FileMemoryStore session-tier round-trip and isolation

Session tier had zero test coverage before this -- the mechanism was
already generic (verified in the design spec), but nothing proved it.
Includes the regression test for the MemoryPath.For nesting fix: two
scopes sharing a SessionId but differing in tenant/user must not collide."
```

---

### Task 3: Tenant-tier test coverage for `FileMemoryStore`

**Files:**
- Modify: `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: same as Task 2, plus the `SessionStore` naming pattern it
  introduced (this task adds `TenantStore` the same way).
- Produces: nothing further tasks depend on.

- [ ] **Step 1: Add a `TenantStore` helper next to `UserStore`/`SessionStore`**

```csharp
    private static FileMemoryStore TenantStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.Tenant] = tmp.Path });
```

- [ ] **Step 2: Write the failing tests**

```csharp
    [Fact]
    public async Task Write_then_read_round_trips_under_the_tenant_tier()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme");

        var write = await store.WriteAsync(scope, Entry("company-wide policy notes"), MemoryTier.Tenant);
        Assert.True(write.Written);
        Assert.Null(write.Error);

        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Tenant, scope, "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("policy"));
        Assert.Empty(read.Diagnostics);
        Assert.NotEmpty(read.Passages);
        Assert.All(read.Passages, p => Assert.Equal("memory:Tenant", p.SourceId));
    }

    [Fact]
    public async Task A_tenant_A_scope_cannot_read_tenant_B_tenant_tier_memory()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a");
        var b = new KnowledgeAccessScope(tenantId: "b");

        await store.WriteAsync(a, Entry("tenant-a-secret-nonce"), MemoryTier.Tenant);

        var readB = await store.ReadAsync(b, new KnowledgeQuery("tenant-a-secret-nonce"));
        Assert.Empty(readB.Passages);
    }

    [Fact]
    public async Task Case_distinct_tenants_cannot_read_each_others_tenant_tier_memory()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var upper = new KnowledgeAccessScope(tenantId: "Acme");
        var lower = new KnowledgeAccessScope(tenantId: "acme");

        await store.WriteAsync(upper, Entry("case-variant-secret"), MemoryTier.Tenant);

        var readLower = await store.ReadAsync(lower, new KnowledgeQuery("case-variant-secret"));
        Assert.Empty(readLower.Passages);
    }

    [Fact]
    public async Task Delete_removes_only_the_target_tenant_scope_subtree()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a");
        var b = new KnowledgeAccessScope(tenantId: "b");
        await store.WriteAsync(a, Entry("tenant a data"), MemoryTier.Tenant);
        await store.WriteAsync(b, Entry("tenant b data"), MemoryTier.Tenant);

        var del = await store.DeleteScopeAsync(a, MemoryTier.Tenant);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Tenant, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.Tenant, b)));
    }

    [Fact]
    public async Task Tenant_Read_ConceptId_is_fully_qualified_matching_Enumerate()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        await store.WriteAsync(scope, Entry("company-wide policy notes"), MemoryTier.Tenant);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("policy"));
        var passage = Assert.Single(read.Passages);

        var listed = await store.EnumerateAsync(scope);
        var concept = Assert.Single(listed);

        Assert.Equal($"{MemoryPath.For(MemoryTier.Tenant, scope)}/2026-07-27", passage.ConceptId);
        Assert.Equal(concept.ConceptId, passage.ConceptId);
    }

    [Fact]
    public async Task Tenant_Enumerate_does_not_list_a_different_scopes_concepts()
    {
        using var tmp = new TempDir();
        var store = TenantStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a");
        var b = new KnowledgeAccessScope(tenantId: "b");
        await store.WriteAsync(a, Entry("tenant a's notes"), MemoryTier.Tenant);

        var listedAsB = await store.EnumerateAsync(b);
        Assert.Empty(listedAsB);
    }
```

- [ ] **Step 3: Run the new tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~FileMemoryStoreTests" -c Release`
Expected: PASS — all `Tenant`-tier facts plus every previously-passing fact
in the file still green.

- [ ] **Step 4: Full build and format check**

Run: `dotnet build OKF4net.sln -c Release` — expect 0 warnings, 0 errors.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect no output.

- [ ] **Step 5: Commit**

```bash
git add tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs
git commit -m "test(catalog): cover FileMemoryStore tenant-tier round-trip and isolation

Mirrors the existing user-tier and the just-added session-tier coverage --
tenant tier had zero test coverage before this despite the mechanism
already being generic."
```

---

### Task 4: `OkfContextProvider` end-to-end capture/recall test with `CaptureTier = Session`

**Files:**
- Modify: `tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs`

**Interfaces:**
- Consumes: `OkfContextProvider`, `OkfContextProviderOptions.CaptureTier`
  (`src/OKF4net.Agents/OkfContextProviderOptions.cs:64`, unchanged — already
  public and settable), the existing `SetUp`/`ScopedOptions`/`MemPath`/`Invoking`/`Invoked`
  helpers in this file (lines 44-91).
- Produces: nothing further tasks depend on.

- [ ] **Step 1: Extend `SetUp` to also configure a session-tier root**

In `tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs`, `SetUp`
currently (lines 44-61) only wires the `User` tier:

```csharp
    private static (IKnowledgeResolver Resolver, FileMemoryStore Store, TempDir Root) SetUp(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "kb"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        root.Write("catalog.json", """
            { "version": 1, "sources": [ { "id": "kb", "path": "./kb", "role": "knowledge" } ] }
            """);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var resolver = new DefaultKnowledgeResolver(catalog);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string> { [MemoryTier.User] = Path.Combine(root.Path, "mem") });
        return (resolver, store, root);
    }
```

Change it to also configure a `Session` tier root, so tests in this file can
opt into session-tier capture without a second setup helper (existing tests
are unaffected: their scopes never set `SessionId`, so `IsApplicable` never
routes them into the session tier regardless of whether its root is
configured):

```csharp
    private static (IKnowledgeResolver Resolver, FileMemoryStore Store, TempDir Root) SetUp(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "kb"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem-session"));
        root.Write("catalog.json", """
            { "version": 1, "sources": [ { "id": "kb", "path": "./kb", "role": "knowledge" } ] }
            """);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var resolver = new DefaultKnowledgeResolver(catalog);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.User] = Path.Combine(root.Path, "mem"),
            [MemoryTier.Session] = Path.Combine(root.Path, "mem-session"),
        });
        return (resolver, store, root);
    }
```

- [ ] **Step 2: Give `ScopedOptions` a `captureTier` parameter**

Currently (lines 80-85):

```csharp
    private static OkfContextProviderOptions ScopedOptions(KnowledgeAccessScope scope) => new()
    {
        MemoryCapture = MemoryCaptureMode.Enabled,
        CaptureTier = MemoryTier.User,
        ScopeAccessor = _ => scope,
    };
```

Change to:

```csharp
    private static OkfContextProviderOptions ScopedOptions(KnowledgeAccessScope scope, MemoryTier captureTier = MemoryTier.User) => new()
    {
        MemoryCapture = MemoryCaptureMode.Enabled,
        CaptureTier = captureTier,
        ScopeAccessor = _ => scope,
    };
```

(Default value keeps every existing call site — which passes only `scope` —
compiling unchanged and still targeting the user tier.)

- [ ] **Step 3: Write the failing test**

```csharp
    [Fact]
    public async Task Capture_then_recall_round_trips_under_the_session_tier()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "sess-42");
        var session = new TestAgentSession();
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(scope, MemoryTier.Session));
        provider.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        await provider.ProvideForTest(Invoking(session, "hello"), CancellationToken.None);
        await provider.StoreForTest(Invoked(session, "remember nonce-sx77", "acknowledged nonce-sx77"));

        Assert.Null(provider.LastMemoryError);
        Assert.True(File.Exists(MemPath(Path.Combine(root.Path, "mem-session"), MemoryTier.Session, scope, "2026-07-27.md")));

        var recall = await provider.ProvideForTest(Invoking(session, "nonce-sx77"), CancellationToken.None);
        var text = Assert.Single(recall.Messages!).Text;
        Assert.Contains("nonce-sx77", text, StringComparison.Ordinal);
    }
```

- [ ] **Step 4: Run it**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~OkfContextProviderScopedTests" -c Release`
Expected: PASS — the new fact plus every pre-existing fact in this class
(all of which use the `ScopedOptions(scope)` one-arg overload, still
defaulting to `MemoryTier.User`) stay green.

- [ ] **Step 5: Full build and format check**

Run: `dotnet build OKF4net.sln -c Release` — expect 0 warnings, 0 errors.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect no output.

- [ ] **Step 6: Commit**

```bash
git add tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs
git commit -m "test(agents): cover OkfContextProvider end-to-end capture with CaptureTier=Session

Only the user tier was ever exercised end-to-end through the provider.
Extends the shared SetUp helper with a session-tier root and ScopedOptions
with an optional captureTier parameter (defaulting to User, so every
existing call site is unaffected) to add the same round-trip coverage
Task 2 added at the FileMemoryStore level."
```

---

### Task 5: Fix stale documentation and add the deployment pattern example

**Files:**
- Modify: `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs:16-21`
- Modify: `src/OKF4net.Catalog/README.md`
- Modify: `README.md` (repo root, lines 375-382)

**Interfaces:**
- Consumes: nothing (documentation-only task).
- Produces: nothing further tasks depend on.

This task has no test cycle of its own (it is pure documentation) — verify
it by re-reading the changed files and confirming `dotnet build`/`dotnet format`
stay clean (the `AddMemory` doc-comment edit is real code, everything else is
Markdown).

- [ ] **Step 1: Fix `AddMemory`'s stale doc comment**

In `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs`, the
summary currently reads (lines 15-22):

```csharp
    /// <summary>
    /// Registers a singleton <see cref="IMemoryStore"/> (<see cref="FileMemoryStore"/>)
    /// whose per-tier roots are the catalog's currently-enabled
    /// <c>role:memory</c> sources, each resolved via
    /// <see cref="CatalogPathResolver.TryResolve"/>. This lot wires the user
    /// tier; a source that fails to resolve, or a tier not present in the
    /// manifest, is simply absent from the store.
    /// </summary>
```

Replace the stale middle sentence:

```csharp
    /// <summary>
    /// Registers a singleton <see cref="IMemoryStore"/> (<see cref="FileMemoryStore"/>)
    /// whose per-tier roots are the catalog's currently-enabled
    /// <c>role:memory</c> sources, each resolved via
    /// <see cref="CatalogPathResolver.TryResolve"/>. Wires whichever tiers
    /// (<see cref="MemoryTier.Session"/>, <see cref="MemoryTier.User"/>,
    /// <see cref="MemoryTier.Tenant"/>) the manifest declares — a source
    /// that fails to resolve, or a tier not present in the manifest, is
    /// simply absent from the store.
    /// </summary>
```

- [ ] **Step 2: Fix the package README's stale `role` line and add a memory-tier deployment example**

In `src/OKF4net.Catalog/README.md`, the `## Minimal catalog.json` section's
bullet list (around line 61-62) currently reads:

```markdown
- `role` (optional, default `"knowledge"`) — the only legal value in V1; any
  other string is rejected.
```

Replace it (V1's `knowledge`-only claim is stale — `role: "memory"` shipped
in Lot 3):

```markdown
- `role` (optional, default `"knowledge"`) — `"knowledge"` (read-only, searched
  by the resolver) or `"memory"` (writable, scoped by tier — see below); any
  other string is rejected.
- `tier` — required when `role` is `"memory"`, one of `"session"`, `"user"`,
  or `"tenant"`; not allowed otherwise.
```

Then add a new section after `## Quick start` (before `## V1 limits`, i.e.
after the existing content ending at line 95) documenting the memory-tier
deployment pattern:

```markdown
## Scoped memory (`role: "memory"`)

A `role: "memory"` source is written by capture (e.g.
`OkfContextProvider.CaptureTier` in `OKF4net.Agents`), not searched by
`IKnowledgeResolver` — it feeds an `IMemoryStore` instead. Configure one
source per tier you need:

```json
{
  "version": 1,
  "sources": [
    { "id": "kb", "path": "./bundles/products", "role": "knowledge" },
    { "id": "mem-user", "path": "./memory/user", "role": "memory", "tier": "user" },
    { "id": "mem-tenant", "path": "./memory/tenant", "role": "memory", "tier": "tenant" },
    { "id": "mem-session", "path": "/tmp/okf-session-memory", "role": "memory", "tier": "session" }
  ]
}
```

```csharp
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

services.AddKnowledge(o => o.AddCatalogFile("./config/catalog.json"));
services.AddMemory();

// Elsewhere:
IMemoryStore memory = provider.GetRequiredService<IMemoryStore>();
await memory.DeleteScopeAsync(scope, MemoryTier.Session); // e.g. when a conversation ends
```

**Ephemeral vs. persistent is entirely which path you configure** — there is
no code-level distinction between them. Point a tier's source `path` at a
temp/ephemeral location (as `mem-session` does above) for data that should
not outlive that location's lifecycle, or at a durable directory (like
`mem-user`/`mem-tenant` above) for data meant to persist. Either way,
`IMemoryStore.DeleteScopeAsync` is the explicit cleanup call — nothing purges
automatically.

**V1 limitation:** `OKF4net.Catalog.Hosting`'s `AddMemory()` resolves the set
of `role:memory` sources once, at first `IMemoryStore` resolution from the
container, and does not pick up a source added/removed/edited afterward
(including via `IKnowledgeCatalog.ReloadAsync()`) — see `AddMemory`'s own XML
doc for the full explanation. Per-scope path resolution (the tenant/user/session
segments) stays fully live on every call; only the fixed set of configured
tiers is frozen.
```

- [ ] **Step 3: Fix the root README's stale "V2 preview (not implemented)" paragraph**

In `README.md` (repo root), lines 375-382 currently read:

```markdown
**V2 preview (not implemented):** application-filtered bundles (per-caller
source visibility), a read-only `knowledge` vs writable `memory` source
`role` split, and host-scoped, layered memory tiers (session / user /
tenant) so captured memory can be enabled on a multi-user deployment without
cross-scope leakage. See
[the V2 scoped-memory design notes](docs/design/specs/2026-07-24-okf4net-v2-scoped-memory-notes.md)
for the full reasoning — these are design notes only, not approved for
implementation, and nothing described there ships in the current package.
```

This is stale — the `knowledge`/`memory` role split and all three memory
tiers already ship. Replace it:

```markdown
**Scoped memory (shipped):** a read-only `knowledge` vs writable `memory`
source `role` split, and host-scoped, layered memory tiers (session / user /
tenant) so captured memory can be enabled on a multi-user deployment without
cross-scope leakage — see
[the scoped-memory design](docs/design/specs/2026-07-27-okf4net-v2-scoped-memory.md)
for the full reasoning and
[`OKF4net.Catalog`'s README](src/OKF4net.Catalog/README.md#scoped-memory-role-memory)
for the deployment example.

**V2 preview (not implemented):** application-filtered bundles (per-caller
source visibility) and cross-source result fusion (score normalization,
deduplication, a single merged ranking across sources — today's resolver
groups results by source instead). See
[§9 of the local catalog design](docs/design/specs/2026-07-24-okf4net-local-catalog-design.md#9-v2-design-team-scoped-bundles)
for the open questions there.
```

- [ ] **Step 4: Verify nothing broke**

Run: `dotnet build OKF4net.sln -c Release` — expect 0 warnings, 0 errors
(only the `AddMemory` doc comment is real code; a bad `<see cref>` would
fail the build under `TreatWarningsAsErrors`).
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect no output.
Run: `dotnet test OKF4net.sln -c Release` — expect the full suite green
(no test asserts on these doc comments or README files).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs src/OKF4net.Catalog/README.md README.md
git commit -m "docs(catalog): fix stale role:memory documentation, add deployment example

AddMemory's XML doc still said 'this lot wires the user tier'; the package
README still said role:knowledge was the only legal V1 value; the root
README still called the whole memory-role/tier feature an unshipped V2
preview. All three predate Lot 3, which already shipped role:memory and
the tier contract. Corrects all three and adds a full 3-tier catalog.json
example plus the ephemeral-vs-persistent deployment note to the package
README."
```

---

## Final Verification

After all five tasks:

- [ ] Run `dotnet test OKF4net.sln -c Release` — full suite green (should be
  578 + roughly 16 new facts from Tasks 2-4, plus the 2 modified facts from
  Task 1 — exact count isn't load-bearing, "0 failed" is).
- [ ] Run `dotnet format OKF4net.sln --verify-no-changes` — clean.
- [ ] Run `dotnet build OKF4net.sln -c Release` — 0 warnings, 0 errors.
- [ ] Re-read the spec's Acceptance Criteria
  (`docs/design/specs/2026-07-28-okf4net-v2-session-tenant-tiers.md`) and
  confirm each one has a corresponding task above:
  - Session path nesting fixed + 2 existing tests updated → Task 1.
  - Same-SessionId-different-tenant regression test → Task 2.
  - Session/tenant test coverage matching user's depth → Tasks 2, 3.
  - `AddMemory` doc comment fixed → Task 5.
  - `catalog.json` example with ephemeral-session-via-temp-path pattern →
    Task 5.
  - No new public API surface, no new storage implementation, no TTL/expiry
    — confirmed: `ScopedOptions`'s new parameter is a test-helper change, not
    public API; `MemoryPath.For`'s signature is unchanged.

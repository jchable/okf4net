// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// User-tier <see cref="FileMemoryStore"/>: write/read/enumerate/delete
/// round-trip, cross-scope isolation (the crux), and never-throw. Uses
/// <see cref="TempDir"/> for the memory source root — never touches
/// tests/fixtures/.
/// </summary>
public class FileMemoryStoreTests
{
    private const string Frontmatter =
        "type: AgentMemory\n"
        + "title: Agent memory 2026-07-27\n"
        + "description: Captured exchanges.\n"
        + "timestamp: 2026-07-27T10:00:00Z\n";

    private static MemoryEntry Entry(string body) =>
        new("2026-07-27", Frontmatter, $"## 10:00:00 UTC\n\n{body}\n");

    private static FileMemoryStore UserStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.User] = tmp.Path });

    private static FileMemoryStore SessionStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.Session] = tmp.Path });

    private static FileMemoryStore TenantStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.Tenant] = tmp.Path });

    // The on-disk path a scope maps to, DERIVED from MemoryPath.For so the
    // assertions track the (case-injective, encoded) scope-key form rather than
    // hardcoding it — with an optional trailing file/segment appended.
    private static string MemPath(string root, MemoryTier tier, KnowledgeAccessScope scope, params string[] tail) =>
        Path.Combine([root, .. MemoryPath.For(tier, scope).Split('/'), .. tail]);

    [Fact]
    public async Task Write_then_read_round_trips_under_the_user_tier()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.User);
        Assert.True(write.Written);
        Assert.Null(write.Error);

        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, scope, "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("orders"));
        Assert.Empty(read.Diagnostics);
        Assert.NotEmpty(read.Passages);
        Assert.All(read.Passages, p => Assert.Equal("memory:User", p.SourceId));
    }

    [Fact]
    public async Task A_tenant_A_scope_cannot_read_tenant_B_memory()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        var b = new KnowledgeAccessScope(tenantId: "b", userId: "bob");

        await store.WriteAsync(a, Entry("secret-a-nonce"), MemoryTier.User);

        var readB = await store.ReadAsync(b, new KnowledgeQuery("secret-a-nonce"));
        Assert.Empty(readB.Passages);
    }

    [Fact]
    public async Task Case_distinct_tenants_cannot_read_each_others_memory()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var upper = new KnowledgeAccessScope(tenantId: "Acme", userId: "alice");
        var lower = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        await store.WriteAsync(upper, Entry("case-variant-secret"), MemoryTier.User);

        var readLower = await store.ReadAsync(lower, new KnowledgeQuery("case-variant-secret"));
        Assert.Empty(readLower.Passages);
    }

    [Fact]
    public async Task Delete_removes_only_the_target_scope_subtree()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        var b = new KnowledgeAccessScope(tenantId: "a", userId: "bob");
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(b, Entry("bob data"), MemoryTier.User);

        var del = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, a)));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, b)));
    }

    [Fact]
    public async Task Read_ConceptId_is_fully_qualified_matching_Enumerate()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.User);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("orders"));
        var passage = Assert.Single(read.Passages);

        var listed = await store.EnumerateAsync(scope);
        var concept = Assert.Single(listed);

        Assert.Equal($"{MemoryPath.For(MemoryTier.User, scope)}/2026-07-27", passage.ConceptId);
        Assert.Equal(concept.ConceptId, passage.ConceptId);
    }

    [Fact]
    public async Task Enumerate_lists_only_the_scopes_own_concepts()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        await store.WriteAsync(scope, Entry("day one"), MemoryTier.User);

        var listed = await store.EnumerateAsync(scope);
        var concept = Assert.Single(listed);
        Assert.Equal(MemoryTier.User, concept.Tier);
        Assert.Equal($"{MemoryPath.For(MemoryTier.User, scope)}/2026-07-27", concept.ConceptId);
    }

    [Fact]
    public async Task Enumerate_does_not_list_a_different_scopes_concepts()
    {
        // The test above only proves a scope sees what IT wrote; this proves
        // isolation -- the actual crux -- by writing under tenant "a" and
        // enumerating as tenant "b".
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        var b = new KnowledgeAccessScope(tenantId: "b", userId: "bob");
        await store.WriteAsync(a, Entry("alice's day"), MemoryTier.User);

        var listedAsB = await store.EnumerateAsync(b);
        Assert.Empty(listedAsB);
    }

    [Fact]
    public async Task Reparse_escaped_scope_directory_reports_a_diagnostic_and_never_throws()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        if (!tmp.TryCreateJunctionToExternalDir(MemoryPath.For(MemoryTier.User, scope), external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var read = await store.ReadAsync(scope, new KnowledgeQuery("anything"));

        Assert.Empty(read.Passages);
        var diagnostic = Assert.Single(read.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
    }

    [Fact]
    public async Task Malformed_bundle_reports_a_diagnostic_and_never_throws()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        Directory.CreateDirectory(scopeDir);
        // A lone continuation byte is not valid UTF-8 on its own or as a
        // continuation, forcing Bundle.Load's strict-UTF8 decode to throw
        // BundleLoadException -- distinct from the permissive per-file
        // ParseErrors path malformed FRONTMATTER/markdown content takes.
        File.WriteAllBytes(Path.Combine(scopeDir, "bad.md"), [0x80, 0x81, 0x82]);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("anything"));

        Assert.Empty(read.Passages);
        var diagnostic = Assert.Single(read.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Contains("could not be loaded", diagnostic.Message);
    }

    [Fact]
    public async Task Write_to_an_unconfigured_tier_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp); // only User configured
        var scope = new KnowledgeAccessScope(sessionId: "s1");

        var write = await store.WriteAsync(scope, Entry("x"), MemoryTier.Session);
        Assert.False(write.Written);
        Assert.NotNull(write.Error);
    }

    [Fact]
    public async Task Local_scope_reads_and_writes_the_local_user_subtree()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);

        await store.WriteAsync(KnowledgeAccessScope.Local, Entry("local orders"), MemoryTier.User);
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, KnowledgeAccessScope.Local, "2026-07-27.md")));

        var read = await store.ReadAsync(KnowledgeAccessScope.Local, new KnowledgeQuery("orders"));
        Assert.NotEmpty(read.Passages);
    }

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
}

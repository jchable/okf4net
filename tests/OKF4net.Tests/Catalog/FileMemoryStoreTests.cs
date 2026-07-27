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

    [Fact]
    public async Task Write_then_read_round_trips_under_the_user_tier()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.User);
        Assert.True(write.Written);
        Assert.Null(write.Error);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "memory-user", "acme", "alice", "2026-07-27.md")));

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

        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "memory-user", "a", "alice")));
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "memory-user", "a", "bob")));
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
        Assert.Equal("memory-user/acme/alice/2026-07-27", concept.ConceptId);
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
        Assert.True(File.Exists(Path.Combine(tmp.Path, "memory-user", "_local", "_local", "2026-07-27.md")));

        var read = await store.ReadAsync(KnowledgeAccessScope.Local, new KnowledgeQuery("orders"));
        Assert.NotEmpty(read.Passages);
    }
}

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
        var deletion = Assert.Single(del.Tiers);
        Assert.Equal(MemoryTier.User, deletion.Tier);
        Assert.Equal([$"{MemoryPath.For(MemoryTier.User, a)}/2026-07-27.md"], deletion.Report.Deleted.Select(k => k.ToString()).ToList());
        Assert.True(deletion.Report.Succeeded);
        Assert.Null(del.Error);

        // The scope's objects and its now-empty directory are gone (erasure leaves nothing named
        // after the scope); the sibling scope, the tenant directory they share and the tier root
        // are intact.
        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, a)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(MemPath(tmp.Path, MemoryTier.User, a))));
        Assert.True(Directory.Exists(tmp.Path));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, b, "2026-07-27.md")));
        Assert.Empty(await store.EnumerateAsync(a));
        Assert.Single(await store.EnumerateAsync(b));
    }

    [Fact]
    public async Task A_scope_whose_identifier_has_a_reserved_base_is_written_read_and_deleted_under_its_prefixed_segment()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "con.foo", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("reserved-base notes"), MemoryTier.User);
        Assert.True(write.Written, write.Error);
        Assert.StartsWith("memory-user/_con.foo-", MemoryPath.For(MemoryTier.User, scope));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, scope, "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("reserved-base"));
        Assert.Single(read.Passages);
        Assert.Single(await store.EnumerateAsync(scope));

        var del = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Empty(await store.EnumerateAsync(scope));
    }

    [Fact]
    public async Task Deleting_an_absent_or_already_emptied_scope_touches_no_tier()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");

        var absent = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Empty(absent.Tiers);
        Assert.Null(absent.Error);

        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        Assert.Single((await store.DeleteScopeAsync(a, MemoryTier.User)).Tiers);
        var again = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Empty(again.Tiers);
        Assert.Null(again.Error);
        Assert.Empty((await store.ReadAsync(a, new KnowledgeQuery("alice"))).Passages);
    }

    [SkippableFact]
    public async Task A_object_that_cannot_be_deleted_is_reported_and_the_deletion_can_be_run_again()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "holding a file open with FileShare.None blocks its deletion on Windows only");
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(a, new MemoryEntry("2026-07-28", Frontmatter, "## 10:00:00 UTC\n\nmore\n"), MemoryTier.User);
        var heldPath = MemPath(tmp.Path, MemoryTier.User, a, "2026-07-27.md");

        MemoryDeleteResult partial;
        using (new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            partial = await store.DeleteScopeAsync(a, MemoryTier.User);
        }

        var deletion = Assert.Single(partial.Tiers);
        Assert.Equal([$"{MemoryPath.For(MemoryTier.User, a)}/2026-07-28.md"], deletion.Report.Deleted.Select(k => k.ToString()).ToList());
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal($"{MemoryPath.For(MemoryTier.User, a)}/2026-07-27.md", failure.Key.ToString());
        Assert.StartsWith("Memory tier 'User' subtree could not be deleted: ", partial.Error);
        Assert.True(File.Exists(heldPath));

        // The failure names the key and the exception's type, never the runtime's message (it can name a host path).
        Assert.StartsWith($"'{failure.Key}' could not be deleted (", failure.Reason);
        Assert.DoesNotContain(tmp.Path, failure.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tmp.Path, partial.Error, StringComparison.OrdinalIgnoreCase);

        // The scope's directory stays while an object does; the rerun deletes the rest and then the directory.
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, a)));
        var again = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.True(Assert.Single(again.Tiers).Report.Succeeded);
        Assert.Null(again.Error);
        Assert.False(File.Exists(heldPath));
        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, a)));
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

    [SkippableFact]
    public async Task Reparse_escaped_scope_directory_reports_a_diagnostic_and_never_throws()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(MemoryPath.For(MemoryTier.User, scope), external.Path), "no junction/symlink privilege on this machine");

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
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Session, a)));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Session, b, "2026-07-27.md")));
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
        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.Tenant, a)));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.Tenant, b, "2026-07-27.md")));
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

    [SkippableFact]
    public async Task Write_refuses_a_scope_directory_reached_through_a_junction_whose_link_status_cannot_be_inspected()
    {
        // Task H1: the user tier's "memory-user" directory is a junction to
        // `external` whose attributes the current user cannot read (deny
        // ReadAttributes on it, deny listing on the tier root). Writes go
        // through BundleConceptWriter's guards, which on the lenient
        // predicate answered "not a link" and wrote the day's memory file
        // under `external`.
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction("memory-user", external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("secret"), MemoryTier.User);

        Assert.False(write.Written);
        Assert.Contains("resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle", write.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [SkippableFact]
    public async Task DeleteScope_refuses_a_scope_directory_reached_through_a_junction_whose_link_status_cannot_be_inspected()
    {
        // Task H1, IsReparseEscaped's side: the scope's subtree exists under
        // `external`, reached only through an uninspectable "memory-user"
        // junction. The lenient walk answered "no link", and DeleteScope
        // recursively deleted a directory outside the tier root.
        using var tmp = new TempDir();
        using var external = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var outsideFile = Path.Combine([external.Path, .. MemoryPath.For(MemoryTier.User, scope).Split('/').Skip(1), "2026-07-27.md"]);
        Directory.CreateDirectory(Path.GetDirectoryName(outsideFile)!);
        File.WriteAllText(outsideFile, "must survive");
        using var junction = tmp.TryCreateUninspectableJunction("memory-user", external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var store = UserStore(tmp);

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        var deletion = Assert.Single(delete.Tiers);
        Assert.Empty(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete.", delete.Error);
        Assert.Equal(delete.Error, failure.Reason);
        Assert.True(File.Exists(outsideFile));

        // The same chain refuses a read and an enumeration, with the read's diagnostic.
        var read = await store.ReadAsync(scope, new KnowledgeQuery("survive"));
        Assert.Empty(read.Passages);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to read.", Assert.Single(read.Diagnostics).Message);
        Assert.Empty(await store.EnumerateAsync(scope));
    }

    [SkippableFact]
    public async Task A_scope_directory_that_is_itself_a_junction_is_refused_by_every_operation()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        external.Write("2026-07-27.md", "---\ntype: AgentMemory\ntitle: t\ndescription: d\n---\nsecret outside\n");
        var scopeDir = Path.Combine(MemoryPath.For(MemoryTier.User, scope).Split('/'));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(scopeDir, external.Path), "needs a junction or a directory symlink");
        var store = UserStore(tmp);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("secret"));
        Assert.Empty(read.Passages);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to read.", Assert.Single(read.Diagnostics).Message);
        Assert.Empty(await store.EnumerateAsync(scope));

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete.", delete.Error);
        Assert.True(File.Exists(Path.Combine(external.Path, "2026-07-27.md")));
    }

    [SkippableFact]
    public async Task A_write_through_a_scope_directory_that_is_a_junction_is_refused_and_creates_nothing_outside()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var scopeDir = Path.Combine(MemoryPath.For(MemoryTier.User, scope).Split('/'));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(scopeDir, external.Path), "needs a junction or a directory symlink");
        var store = UserStore(tmp);

        var write = await store.WriteAsync(scope, Entry("must not land outside"), MemoryTier.User);

        Assert.False(write.Written);
        Assert.NotNull(write.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [Fact]
    public async Task Deleting_a_scope_leaves_a_sibling_scope_whose_encoded_segment_it_is_a_string_prefix_of()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var alice = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var aliceSegment = MemoryPath.For(MemoryTier.User, alice).Split('/')[^1];

        // A user whose raw id is alice's encoded directory name plus a letter: its own encoded
        // segment starts with alice's whole segment.
        var longer = new KnowledgeAccessScope(tenantId: "acme", userId: aliceSegment + "x");
        Assert.StartsWith(aliceSegment, MemoryPath.For(MemoryTier.User, longer).Split('/')[^1], StringComparison.Ordinal);
        await store.WriteAsync(alice, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(longer, Entry("longer data"), MemoryTier.User);

        var del = await store.DeleteScopeAsync(alice, MemoryTier.User);

        Assert.True(Assert.Single(del.Tiers).Report.Succeeded);
        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, alice)));
        Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, longer, "2026-07-27.md")));
        Assert.Single(await store.EnumerateAsync(longer));
    }

    [Fact]
    public async Task Deleting_a_scope_that_holds_only_empty_directories_removes_them()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        Directory.CreateDirectory(Path.Combine(scopeDir, "left", "over"));
        Directory.CreateDirectory(Path.Combine(scopeDir, "other"));

        var del = await store.DeleteScopeAsync(scope, MemoryTier.User);

        // No key was under the scope, so no tier is reported; its directories are removed all the same.
        Assert.Empty(del.Tiers);
        Assert.Null(del.Error);
        Assert.False(Directory.Exists(scopeDir));
        Assert.True(Directory.Exists(tmp.Path));
    }

    [SkippableFact]
    public async Task A_link_inside_a_scope_is_never_followed_by_the_deletion_and_is_reported_as_a_failure()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        external.Write("outside.md", "must survive");
        Directory.CreateDirectory(Path.Combine(external.Path, "empty-outside"));
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
        var linkRelative = Path.Combine([.. MemoryPath.For(MemoryTier.User, scope).Split('/'), "ln"]);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(linkRelative, external.Path), "needs a junction or a directory symlink");
        var linkPath = Path.Combine(tmp.Path, linkRelative);

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        // The object is deleted; the link is left in place, so the scope's directory stays and the tier failed.
        var deletion = Assert.Single(delete.Tiers);
        Assert.Single(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.False(deletion.Report.Succeeded);
        Assert.StartsWith("Memory tier 'User' subtree could not be deleted: ", delete.Error);
        Assert.DoesNotContain(tmp.Path, delete.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(external.Path, delete.Error, StringComparison.OrdinalIgnoreCase);
        AssertExternalUntouched(external, linkPath);

        // The run can be repeated (no key is left, only the link): the same failure, still nothing outside touched.
        var again = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.False(Assert.Single(again.Tiers).Report.Succeeded);
        Assert.Empty(Assert.Single(again.Tiers).Report.Deleted);
        AssertExternalUntouched(external, linkPath);
    }

    private static void AssertExternalUntouched(TempDir external, string linkPath)
    {
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(external.Path, "outside.md")));
        Assert.True(Directory.Exists(Path.Combine(external.Path, "empty-outside")));
        Assert.True(Directory.Exists(linkPath));
    }

    [Fact]
    public async Task A_file_that_is_no_key_is_reported_and_keeps_the_scope_directory()
    {
        using var tmp = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);

        // U+0085 (next line) is no valid key character; written as a char so no escape is needed in this file.
        var stray = MemPath(tmp.Path, MemoryTier.User, scope, "stray" + (char)0x85 + "name.md");
        File.WriteAllText(stray, "not a key");

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        var deletion = Assert.Single(delete.Tiers);
        Assert.Single(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.Contains("is not a key", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("is not a key", delete.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(stray));
        Assert.True(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, scope)));
    }

    [SkippableFact]
    public async Task A_subdirectory_that_cannot_be_listed_makes_the_scope_unavailable_as_data_never_an_exception()
    {
        using var tmp = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("locked scope data"), MemoryTier.User);
        var locked = MemPath(tmp.Path, MemoryTier.User, scope, "locked");
        Directory.CreateDirectory(locked);
        Skip.IfNot(TryDenyListing(locked, out var undo), "needs a directory whose listing this process is denied (not root)");
        var sawLockedRead = false;
        try
        {
            var read = await store.ReadAsync(scope, new KnowledgeQuery("locked"));
            Assert.Empty(read.Passages);
            var diagnostic = Assert.Single(read.Diagnostics);
            Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
            Assert.Equal("Memory tier 'User' could not be loaded: UnauthorizedAccessException", diagnostic.Message);
            sawLockedRead = true;

            Assert.Empty(await store.EnumerateAsync(scope));

            var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);
            var deletion = Assert.Single(delete.Tiers);
            Assert.Empty(deletion.Report.Deleted);
            var failure = Assert.Single(deletion.Report.Failed);
            Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
            Assert.Equal("Memory tier 'User' subtree could not be deleted: the scope could not be listed (UnauthorizedAccessException) - nothing was deleted", delete.Error);
            Assert.DoesNotContain(tmp.Path, delete.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(MemPath(tmp.Path, MemoryTier.User, scope, "2026-07-27.md")));
        }
        finally
        {
            undo();
        }

        Assert.True(sawLockedRead);

        // Once the listing is allowed again the same store deletes the scope, directories included.
        var after = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.True(Assert.Single(after.Tiers).Report.Succeeded);
        Assert.False(Directory.Exists(MemPath(tmp.Path, MemoryTier.User, scope)));
    }

    private static bool CanLink(TempDir tmp, TempDir external)
    {
        if (!tmp.TryCreateJunctionToExternalDir("probe-link", external.Path))
        {
            return false;
        }

        Directory.Delete(Path.Combine(tmp.Path, "probe-link")); // removes the link only
        return true;
    }

    /// <summary>Moves <paramref name="directory"/> aside and puts a junction to <paramref name="target"/> under its name: what another process could do between the store's inspection and its system call.</summary>
    private static void ReplaceWithLink(TempDir tmp, string directory, string target)
    {
        Directory.Move(directory, directory + ".moved");
        if (!tmp.TryCreateJunctionToExternalDir(Path.GetRelativePath(tmp.Path, directory), target))
        {
            throw new InvalidOperationException("the link could not be created");
        }
    }

    private static void AssertLeftInPlaceAsFailureUnderThePrefix(MemoryDeleteResult delete, KnowledgeAccessScope scope, TempDir tmp, TempDir external)
    {
        var deletion = Assert.Single(delete.Tiers);
        Assert.Single(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.Contains("a link, or an entry that could not be inspected", failure.Reason, StringComparison.Ordinal);
        Assert.False(deletion.Report.Succeeded);
        Assert.StartsWith("Memory tier 'User' subtree could not be deleted: ", delete.Error);
        Assert.DoesNotContain(tmp.Path, delete.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(external.Path, delete.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task A_scope_directory_swapped_for_a_link_after_the_listing_is_left_alone_and_nothing_outside_is_removed()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Skip.IfNot(CanLink(tmp, external), "needs a junction or a directory symlink");
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        Directory.CreateDirectory(Path.Combine(external.Path, "outside-empty"));
        var swapped = false;
        store.BeforeScopeDirectoriesRemovedForTest = path =>
        {
            if (!swapped && path == scopeDir)
            {
                swapped = true;
                ReplaceWithLink(tmp, scopeDir, external.Path);
            }
        };

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        Assert.True(swapped);
        AssertLeftInPlaceAsFailureUnderThePrefix(delete, scope, tmp, external);
        Assert.True(Directory.Exists(Path.Combine(external.Path, "outside-empty")));
    }

    [SkippableFact]
    public async Task A_tenant_directory_swapped_for_a_link_after_the_listing_is_left_alone_and_nothing_outside_is_removed()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Skip.IfNot(CanLink(tmp, external), "needs a junction or a directory symlink");
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        var tenantDir = Path.GetDirectoryName(scopeDir)!;

        // Behind the tenant link the same user directory exists, with an empty directory in it.
        Directory.CreateDirectory(Path.Combine(external.Path, Path.GetFileName(scopeDir), "outside-empty"));
        var swapped = false;
        store.BeforeScopeDirectoriesRemovedForTest = path =>
        {
            if (!swapped && path == scopeDir)
            {
                swapped = true;
                ReplaceWithLink(tmp, tenantDir, external.Path);
            }
        };

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        Assert.True(swapped);
        AssertLeftInPlaceAsFailureUnderThePrefix(delete, scope, tmp, external);
        Assert.True(Directory.Exists(Path.Combine(external.Path, Path.GetFileName(scopeDir), "outside-empty")));
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_nested_directory_whose_parent_is_swapped_for_a_link_is_left_alone_and_nothing_outside_is_removed(int swapAtCall)
    {
        // Call 1 is the nested directory's guard before it is listed, call 2 its guard before it is removed.
        using var tmp = new TempDir();
        using var external = new TempDir();
        Skip.IfNot(CanLink(tmp, external), "needs a junction or a directory symlink");
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        Directory.CreateDirectory(Path.Combine(scopeDir, "a", "b"));

        // Behind the link "a" there is a "b" with an empty directory in it.
        Directory.CreateDirectory(Path.Combine(external.Path, "b", "c"));
        var nested = Path.Combine(scopeDir, "a", "b");
        var calls = 0;
        store.BeforeScopeDirectoriesRemovedForTest = path =>
        {
            if (path == nested && ++calls == swapAtCall)
            {
                ReplaceWithLink(tmp, Path.Combine(scopeDir, "a"), external.Path);
            }
        };

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        Assert.True(calls >= swapAtCall);
        AssertLeftInPlaceAsFailureUnderThePrefix(delete, scope, tmp, external);
        Assert.True(Directory.Exists(Path.Combine(external.Path, "b", "c")));
    }

    [Fact]
    public async Task A_scope_nested_deeper_than_the_depth_bound_is_unavailable_as_data()
    {
        using var tmp = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("deep scope data"), MemoryTier.User);
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
        CreateChain(scopeDir, OKF4net.Providers.BundleLimits.Default.MaxDepth + 6);

        var read = await store.ReadAsync(scope, new KnowledgeQuery("deep"));
        Assert.Empty(read.Passages);
        Assert.Equal("Memory tier 'User' could not be loaded: BundleLimitExceededException", Assert.Single(read.Diagnostics).Message);
        Assert.Empty(await store.EnumerateAsync(scope));

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);
        var deletion = Assert.Single(delete.Tiers);
        Assert.Empty(deletion.Report.Deleted);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), Assert.Single(deletion.Report.Failed).Key.ToString());
        Assert.Equal("Memory tier 'User' subtree could not be deleted: the scope could not be listed (BundleLimitExceededException) - nothing was deleted", delete.Error);
        Assert.True(File.Exists(Path.Combine(scopeDir, "2026-07-27.md")));
    }

    [Fact]
    public async Task A_tree_deepened_past_the_depth_bound_after_the_listing_is_left_in_place_without_unbounded_recursion()
    {
        using var tmp = new TempDir();
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var store = UserStore(tmp);
        await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
        var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);

        // The bound is lowered through the seam; the default is pinned by the 64/65-level tests below.
        store.ScopeDirectoryDepthBoundForTest = 4;
        var deepened = false;
        store.BeforeScopeDirectoriesRemovedForTest = path =>
        {
            if (!deepened && path == scopeDir)
            {
                deepened = true;
                CreateChain(scopeDir, 10);
            }
        };

        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

        Assert.True(deepened);
        var deletion = Assert.Single(delete.Tiers);
        Assert.Single(deletion.Report.Deleted);
        var failure = Assert.Single(deletion.Report.Failed);
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
        Assert.Contains("depth bound", failure.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(scopeDir));
    }

    [Fact]
    public async Task The_directory_cleanup_goes_64_levels_below_the_scope_and_refuses_beyond_with_a_bounded_cost()
    {
        using (var probe = new TempDir())
        {
            Assert.Equal(64, UserStore(probe).ScopeDirectoryDepthBoundForTest);
        }

        // 64 levels below the scope: all removed, the scope's directory included.
        using (var tmp = new TempDir())
        {
            var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
            var store = UserStore(tmp);
            await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
            var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
            CreateChain(scopeDir, 64);

            var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

            Assert.True(Assert.Single(delete.Tiers).Report.Succeeded);
            Assert.Null(delete.Error);
            Assert.False(Directory.Exists(scopeDir));
        }

        // 65 levels: the objects are deleted by key, the cleanup stops at the bound, and at most
        // the scope's directory and 64 levels are ever listed (the guard-call count, never a clock).
        using (var tmp = new TempDir())
        {
            var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
            var store = UserStore(tmp);
            await store.WriteAsync(scope, Entry("alice data"), MemoryTier.User);
            var scopeDir = MemPath(tmp.Path, MemoryTier.User, scope);
            CreateChain(scopeDir, 65);
            var listed = 0;
            store.BeforeScopeDirectoriesRemovedForTest = _ => listed++;

            var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);

            var deletion = Assert.Single(delete.Tiers);
            Assert.Single(deletion.Report.Deleted);
            var failure = Assert.Single(deletion.Report.Failed);
            Assert.Equal(MemoryPath.For(MemoryTier.User, scope), failure.Key.ToString());
            Assert.Contains("depth bound", failure.Reason, StringComparison.Ordinal);
            Assert.Equal(65, listed);
            Assert.True(Directory.Exists(scopeDir));
        }
    }

    private static void CreateChain(string parent, int depth) =>
        Directory.CreateDirectory(Path.Combine(parent, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", depth))));

    /// <summary>Denies listing <paramref name="directory"/> (not its parent): icacls (RD) on Windows, mode bits elsewhere. <see langword="false"/> when this process can list it anyway (root) or the change is refused; the denial is undone then.</summary>
    private static bool TryDenyListing(string directory, out Action undo)
    {
        undo = () => { };
        Action restore;
        if (OperatingSystem.IsWindows())
        {
            var sid = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            if (!Icacls(directory, "/deny", sid + ":(RD)"))
            {
                return false;
            }

            restore = () => Icacls(directory, "/remove:d", sid);
        }
        else
        {
            try
            {
                var original = File.GetUnixFileMode(directory);
                File.SetUnixFileMode(directory, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                restore = () =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(directory, original);
                    }
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            undo = restore;
            return true;
        }

        restore();
        return false;
    }

    // A scope id bounded only by its character class can make the scope's key
    // longer than BundleKey.MaxUtf8Bytes (1 024): that is a storage condition
    // reported as data, never an exception out of the store (before the seam it was
    // Directory.Exists == false, silently skipped).
    [Theory]
    [InlineData(MemoryTier.Tenant)]
    [InlineData(MemoryTier.User)]
    [InlineData(MemoryTier.Session)]
    public async Task A_scope_id_whose_key_exceeds_the_key_limit_is_reported_as_data_by_all_four_operations(MemoryTier tier)
    {
        using var tmp = new TempDir();
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string> { [tier] = tmp.Path });
        var scope = new KnowledgeAccessScope(tenantId: new string('t', 1100), userId: "alice", sessionId: "s1");

        var read = await store.ReadAsync(scope, new KnowledgeQuery("anything"));
        Assert.Empty(read.Passages);
        var diagnostic = Assert.Single(read.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Equal($"Memory tier '{tier}' could not be loaded: key is longer than 1024 UTF-8 bytes", diagnostic.Message);
        Assert.DoesNotContain("ttttt", diagnostic.Message);

        Assert.Empty(await store.EnumerateAsync(scope));

        var write = await store.WriteAsync(scope, Entry("never written"), tier);
        Assert.False(write.Written);
        Assert.NotNull(write.Error);

        var delete = await store.DeleteScopeAsync(scope, tier);
        Assert.NotNull(delete.Error);
        Assert.Contains($"Memory tier '{tier}' subtree could not be deleted", delete.Error);
        Assert.Contains("key is longer than 1024 UTF-8 bytes", delete.Error);
        Assert.DoesNotContain("ttttt", delete.Error);
        var deletion = Assert.Single(delete.Tiers);
        Assert.Equal(tier, deletion.Tier);
        Assert.Empty(deletion.Report.Deleted);
        Assert.Single(deletion.Report.Failed);

        // Nothing was created or touched under the tier's root.
        Assert.Empty(Directory.GetFileSystemEntries(tmp.Path));
    }

    // The same over-long id, without a tier: DeleteScopeAsync(scope) walks every applicable tier.
    [Fact]
    public async Task Deleting_every_tier_of_an_over_long_scope_id_reports_each_without_throwing()
    {
        using var tmp = new TempDir();
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.Tenant] = tmp.Path,
            [MemoryTier.User] = tmp.Path,
            [MemoryTier.Session] = tmp.Path,
        });
        var scope = new KnowledgeAccessScope(tenantId: new string('t', 1100), userId: "alice", sessionId: "s1");

        var delete = await store.DeleteScopeAsync(scope);
        Assert.Equal(3, delete.Tiers.Count);
        Assert.All(delete.Tiers, d => Assert.Empty(d.Report.Deleted));
        Assert.Empty(Directory.GetFileSystemEntries(tmp.Path));
    }

    // An id longer than the OS file-name component limit (but inside the key limit) is not
    // an over-long key: nothing throws, and where the operating system rejects the name (Linux:
    // 255 bytes per component) the provider's STRICT guard refuses it and that refusal is what is
    // reported (Windows accepts a name this long, so there it is simply absent).
    [Fact]
    public async Task A_scope_id_longer_than_the_os_component_limit_throws_nothing_and_reports_at_most_the_guards_refusal()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: new string('t', 300), userId: "alice");

        var read = await store.ReadAsync(scope, new KnowledgeQuery("anything"));
        Assert.Empty(read.Passages);
        Assert.All(read.Diagnostics, d => Assert.Equal("Memory tier 'User' path is a reparse point or could not be inspected; refusing to read.", d.Message));
        var refused = read.Diagnostics.Count == 1;

        Assert.Empty(await store.EnumerateAsync(scope));
        var delete = await store.DeleteScopeAsync(scope, MemoryTier.User);
        Assert.Equal(refused ? "Memory tier 'User' path is a reparse point or could not be inspected; refusing to delete." : null, delete.Error);
        Assert.Empty(Directory.GetFileSystemEntries(tmp.Path));
    }

    private static bool Icacls(params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("icacls.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}

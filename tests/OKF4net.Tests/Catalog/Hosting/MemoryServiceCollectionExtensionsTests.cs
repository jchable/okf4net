// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog.Hosting;

public class MemoryServiceCollectionExtensionsTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    // The on-disk path a scope maps to under a memory-source root, DERIVED from
    // MemoryPath.For so assertions track the encoded scope-key form.
    private static string MemPath(string root, MemoryTier tier, KnowledgeAccessScope scope, params string[] tail) =>
        Path.Combine([root, .. MemoryPath.For(tier, scope).Split('/'), .. tail]);

    [Fact]
    public async Task AddMemory_registers_a_store_wired_to_the_user_tier_source()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "mem", "user"));
        Directory.CreateDirectory(Path.Combine(root.Path, "kb"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user" }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IMemoryStore>();
        var scope = new KnowledgeAccessScope(userId: "alice");
        var write = await store.WriteAsync(
            scope,
            new MemoryEntry("2026-07-27", "type: AgentMemory\ntitle: t\ndescription: d\ntimestamp: 2026-07-27T00:00:00Z\n", "## s\n\nhello orders\n"),
            MemoryTier.User);

        Assert.True(write.Written);
        Assert.True(File.Exists(MemPath(Path.Combine(root.Path, "mem", "user"), MemoryTier.User, scope, "2026-07-27.md")));
    }

    [Fact]
    public void AddMemory_throws_when_a_memory_root_is_nested_within_a_knowledge_root()
    {
        using var root = new TempDir();
        // Memory root ./kb/mem is NESTED inside knowledge root ./kb, so the
        // resolver would walk the scoped-memory subtree as shared knowledge --
        // exactly the isolation defect this guards. The disjoint sibling tests
        // (kb + ./mem/...) prove a disjoint config still builds cleanly.
        Directory.CreateDirectory(Path.Combine(root.Path, "kb", "mem"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./kb/mem", "role": "memory", "tier": "user" }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        // The overlap check runs in the singleton factory, i.e. at first
        // IMemoryStore resolution, and names both offending source ids.
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IMemoryStore>());
        Assert.Contains("user-mem", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kb", ex.Message, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task AddMemory_does_not_wire_a_disabled_memory_source()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "kb"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        // The disabled source's "path" is never even created: neither the
        // catalog's own load-time validation nor AddMemory's factory resolve
        // a disabled source's path at all (both gate on Enabled first), so a
        // disabled role:memory source pointing at a directory that doesn't
        // exist must still build cleanly.
        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user", "enabled": false }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IMemoryStore>();
        var write = await store.WriteAsync(
            new KnowledgeAccessScope(userId: "alice"),
            new MemoryEntry("2026-07-27", "type: AgentMemory\ntitle: t\ndescription: d\ntimestamp: 2026-07-27T00:00:00Z\n", "## s\n\nhello orders\n"),
            MemoryTier.User);

        Assert.False(write.Written);
        Assert.NotNull(write.Error);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "mem")));
    }

    [Fact]
    public async Task AddMemory_skips_a_memory_source_whose_path_no_longer_resolves_but_another_tier_still_works()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "mem", "user"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem", "tenant"));
        Directory.CreateDirectory(Path.Combine(root.Path, "kb"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user" },
                { "id": "tenant-mem", "path": "./mem/tenant", "role": "memory", "tier": "tenant" }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        // Force the catalog to construct -- and validate both memory paths at
        // load time -- while both directories still exist. Mirrors
        // GroupedKnowledgeResolverTests.SearchAsync_reports_SourceUnavailable_for_a_deleted_source_but_still_returns_the_other:
        // the catalog's own load-time validation must succeed first, so what
        // is observed below is AddMemory's own re-resolution at first
        // IMemoryStore resolution, not a catalog construction failure.
        sp.GetRequiredService<IKnowledgeCatalog>();

        // Remove the user-tier directory so its path no longer resolves by
        // the time AddMemory's singleton factory runs its own
        // CatalogPathResolver.TryResolve.
        Directory.Delete(Path.Combine(root.Path, "mem", "user"), recursive: true);

        var store = sp.GetRequiredService<IMemoryStore>();

        var userWrite = await store.WriteAsync(
            new KnowledgeAccessScope(userId: "alice"),
            new MemoryEntry("2026-07-27", "type: AgentMemory\ntitle: t\ndescription: d\ntimestamp: 2026-07-27T00:00:00Z\n", "## s\n\nhello orders\n"),
            MemoryTier.User);
        Assert.False(userWrite.Written);
        Assert.NotNull(userWrite.Error);

        var tenantWrite = await store.WriteAsync(
            new KnowledgeAccessScope(tenantId: "acme"),
            new MemoryEntry("2026-07-27", "type: AgentMemory\ntitle: t\ndescription: d\ntimestamp: 2026-07-27T00:00:00Z\n", "## s\n\nhello orders\n"),
            MemoryTier.Tenant);
        Assert.True(tenantWrite.Written);
        Assert.True(File.Exists(MemPath(Path.Combine(root.Path, "mem", "tenant"), MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme"), "2026-07-27.md")));
    }
}

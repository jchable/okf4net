// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog.Hosting;

public class MemoryServiceCollectionExtensionsTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

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
        Assert.True(File.Exists(Path.Combine(root.Path, "mem", "user", "memory-user", "_local", "alice", "2026-07-27.md")));
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="FileMemoryStore"/> over providers the host gives, one per tier (issue #140,
/// design §11 "Lecture seule": a memory write and a scope deletion over a filesystem provider
/// downgraded to read-only both fail, as data, with nothing changed on disk), a round trip
/// over the in-memory provider, and the equivalence of a scope's reads with the directory
/// constructor's. Never touches tests/fixtures/.
/// </summary>
public class FileMemoryStoreProviderTests
{
    private const string Frontmatter =
        "type: AgentMemory\n"
        + "title: Agent memory 2026-07-27\n"
        + "description: Captured exchanges.\n";

    private const string ReadOnlyMessage = "the provider is read-only; the write was refused before being attempted";

    private static readonly MemoryTier[] AllTiers = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    private static readonly KnowledgeAccessScope Alice = new(tenantId: "acme", userId: "alice", sessionId: "s1");

    private static readonly KnowledgeAccessScope Bob = new(tenantId: "globex", userId: "bob", sessionId: "s2");

    private static MemoryEntry Entry(string name, string body) =>
        new(name, Frontmatter, $"## 10:00:00 UTC\n\n{body}\n");

    private static Dictionary<MemoryTier, IBundleProvider> EveryTier(IBundleProvider provider) =>
        AllTiers.ToDictionary(t => t, _ => provider);

    /// <summary>Every directory and file under <paramref name="root"/>, with each file's SHA-256: what "nothing changed on disk" compares.</summary>
    private static List<string> Snapshot(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => File.Exists(p)
                ? $"{Path.GetRelativePath(root, p)} {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))}"
                : $"{Path.GetRelativePath(root, p)}/")
            .Order(StringComparer.Ordinal)];

    /// <summary>Alice's memory in all three tiers, written through the directory constructor.</summary>
    private static async Task SeedAsync(string root)
    {
        var seeding = new FileMemoryStore(AllTiers.ToDictionary(t => t, _ => root));
        foreach (var tier in AllTiers)
        {
            Assert.True((await seeding.WriteAsync(Alice, Entry("2026-07-27", $"{tier} refunds notes"), tier)).Written);
        }
    }

    [Fact]
    public async Task Over_a_read_only_filesystem_provider_every_memory_write_fails_as_data_and_nothing_is_written()
    {
        using var tmp = new TempDir();
        await SeedAsync(tmp.Path);
        var before = Snapshot(tmp.Path);
        var store = new FileMemoryStore(EveryTier(new FileSystemBundleProvider(tmp.Path).AsReadOnly()));

        foreach (var tier in AllTiers)
        {
            // An append to an existing concept, a new concept in an existing scope, a concept in a scope that does not exist.
            foreach (var (scope, name) in new[] { (Alice, "2026-07-27"), (Alice, "2026-07-28"), (Bob, "2026-07-27") })
            {
                var write = await store.WriteAsync(scope, Entry(name, "more notes"), tier);
                Assert.False(write.Written);
                Assert.Equal($"Error: Memory tier '{tier}' could not be written: {ReadOnlyMessage}", write.Error);
            }
        }

        Assert.Equal(before, Snapshot(tmp.Path));

        // The downgrade changes what can be written, never what is read.
        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        Assert.Empty(read.Diagnostics);
        Assert.Equal(["memory:Session", "memory:User", "memory:Tenant"], read.Passages.Select(p => p.SourceId).ToList());
    }

    [Fact]
    public async Task Over_a_read_only_filesystem_provider_a_scope_deletion_fails_per_tier_as_data_and_nothing_is_deleted()
    {
        using var tmp = new TempDir();
        await SeedAsync(tmp.Path);
        var before = Snapshot(tmp.Path);
        var store = new FileMemoryStore(EveryTier(new FileSystemBundleProvider(tmp.Path).AsReadOnly()));

        var result = await store.DeleteScopeAsync(Alice);

        Assert.Equal(AllTiers, result.Tiers.Select(d => d.Tier).ToArray());
        foreach (var deletion in result.Tiers)
        {
            Assert.Empty(deletion.Report.Written);
            Assert.Empty(deletion.Report.Deleted);
            var failure = Assert.Single(deletion.Report.Failed);
            Assert.Equal($"{MemoryPath.For(deletion.Tier, Alice)}/2026-07-27.md", failure.Key.ToString());
            Assert.Equal(ReadOnlyMessage, failure.Reason);
        }

        Assert.Equal(
            string.Join("; ", AllTiers.Select(t => $"Memory tier '{t}' subtree could not be deleted: {ReadOnlyMessage}")),
            result.Error);
        Assert.Equal(before, Snapshot(tmp.Path));

        // One tier only: the same failure, for that tier alone.
        var user = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        Assert.Equal(MemoryTier.User, Assert.Single(user.Tiers).Tier);
        Assert.Equal(before, Snapshot(tmp.Path));
    }

    [Fact]
    public async Task Over_a_read_only_filesystem_provider_an_emptied_scope_directory_is_left_in_place()
    {
        using var tmp = new TempDir();
        var scopeDirectory = Path.Combine([tmp.Path, .. MemoryPath.For(MemoryTier.User, Alice).Split('/')]);
        Directory.CreateDirectory(scopeDirectory);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = new FileSystemBundleProvider(tmp.Path).AsReadOnly() });

        // A scope with no key is absent: no tier reported, and the directory constructor's cleanup of leftover directories does not run.
        var result = await store.DeleteScopeAsync(Alice, MemoryTier.User);

        Assert.Empty(result.Tiers);
        Assert.Null(result.Error);
        Assert.True(Directory.Exists(scopeDirectory));
    }

    [Fact]
    public async Task Over_a_read_only_view_of_a_writable_provider_no_write_or_delete_reaches_the_storage()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var key = $"{MemoryPath.For(MemoryTier.User, Alice)}/2026-07-27.md";
        storage.Add(key, $"---\n{Frontmatter}---\n\n## 10:00:00 UTC\n\nrefunds notes\n");
        var store = new FileMemoryStore(EveryTier(storage.AsReadOnly()));

        var write = await store.WriteAsync(Alice, Entry("2026-07-28", "more"), MemoryTier.User);
        var deletion = await store.DeleteScopeAsync(Alice);

        Assert.False(write.Written);
        Assert.Equal($"Error: Memory tier 'User' could not be written: {ReadOnlyMessage}", write.Error);
        var user = Assert.Single(deletion.Tiers);
        Assert.Equal(MemoryTier.User, user.Tier);
        Assert.Equal(key, Assert.Single(user.Report.Failed).Key.ToString());
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal) || c.StartsWith("delete ", StringComparison.Ordinal));
        Assert.True(storage.Exists(BundleKey.Parse(key)));
    }

    [Fact]
    public async Task Write_read_enumerate_and_delete_round_trip_over_one_in_memory_provider_for_every_tier()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var store = new FileMemoryStore(EveryTier(storage));

        foreach (var tier in AllTiers)
        {
            Assert.Equal(new MemoryWriteResult(true, null), await store.WriteAsync(Alice, Entry("2026-07-27", $"alice {tier} refunds"), tier));
            Assert.Equal(new MemoryWriteResult(true, null), await store.WriteAsync(Bob, Entry("2026-07-27", $"bob {tier} refunds"), tier));
        }

        // An append keeps the earlier section.
        Assert.True((await store.WriteAsync(Alice, Entry("2026-07-27", "alice second user note"), MemoryTier.User)).Written);

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        Assert.Empty(read.Diagnostics);
        Assert.Equal(
            AllTiers.Select(t => ($"memory:{t}", $"{MemoryPath.For(t, Alice)}/2026-07-27", "2026-07-27.md")).ToList(),
            read.Passages.Select(p => (p.SourceId, p.ConceptId, p.BundleRelativePath)).ToList());
        Assert.All(read.Passages, p => Assert.Equal("Agent memory 2026-07-27", p.Title));
        Assert.Contains("alice second user note", (await store.ReadAsync(Alice, new KnowledgeQuery("second"))).Passages.Single().Excerpt, StringComparison.Ordinal);

        // Bob (another tenant) is in none of Alice's scopes.
        Assert.DoesNotContain(read.Passages, p => p.Excerpt.Contains("bob", StringComparison.Ordinal));

        var enumerated = await store.EnumerateAsync(Alice);
        Assert.Equal(
            AllTiers.Select(t => new MemoryConcept(t, $"{MemoryPath.For(t, Alice)}/2026-07-27", "Agent memory 2026-07-27")).ToList(),
            enumerated.ToList());

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        var user = Assert.Single(deletion.Tiers);
        Assert.True(user.Report.Succeeded);
        Assert.Equal([$"{MemoryPath.For(MemoryTier.User, Alice)}/2026-07-27.md"], user.Report.Deleted.Select(k => k.ToString()).ToList());
        Assert.Null(deletion.Error);
        Assert.Empty(storage.Enumerate(BundleKey.Parse(MemoryPath.For(MemoryTier.User, Alice)), BundleLimits.Default));

        // The rest is untouched, and a second deletion finds nothing.
        Assert.Equal(5, storage.Enumerate(null, BundleLimits.Default).Count());
        Assert.Empty((await store.DeleteScopeAsync(Alice, MemoryTier.User)).Tiers);
        Assert.Equal([MemoryTier.Session, MemoryTier.Tenant], (await store.EnumerateAsync(Alice)).Select(c => c.Tier).ToList());
    }

    [Fact]
    public async Task A_scope_over_a_given_filesystem_provider_reads_as_it_does_through_the_directory_constructor()
    {
        using var tmp = new TempDir();
        await SeedAsync(tmp.Path);
        var byDirectory = new FileMemoryStore(AllTiers.ToDictionary(t => t, _ => tmp.Path));
        var byProvider = new FileMemoryStore(EveryTier(new FileSystemBundleProvider(tmp.Path)));
        var query = new KnowledgeQuery("refunds");

        Assert.Equal((await byDirectory.ReadAsync(Alice, query)).Passages, (await byProvider.ReadAsync(Alice, query)).Passages);
        Assert.Equal(await byDirectory.EnumerateAsync(Alice), await byProvider.EnumerateAsync(Alice));
        Assert.NotEmpty(await byProvider.EnumerateAsync(Alice));
    }

    [Fact]
    public async Task A_deletion_over_a_given_filesystem_provider_deletes_the_keys_and_leaves_the_scope_directory()
    {
        using var tmp = new TempDir();
        await SeedAsync(tmp.Path);
        var store = new FileMemoryStore(EveryTier(new FileSystemBundleProvider(tmp.Path)));
        var scopeDirectory = Path.Combine([tmp.Path, .. MemoryPath.For(MemoryTier.User, Alice).Split('/')]);

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);

        Assert.True(Assert.Single(deletion.Tiers).Report.Succeeded);
        Assert.Null(deletion.Error);

        // The documented limit: only the directory constructor removes a scope's emptied directories.
        Assert.True(Directory.Exists(scopeDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scopeDirectory));
    }

    [Fact]
    public async Task A_tier_without_a_provider_has_no_source_configured()
    {
        var store = new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.Tenant] = new InMemoryBundleProvider(canWrite: true) });

        var write = await store.WriteAsync(Alice, Entry("2026-07-27", "x"), MemoryTier.User);

        Assert.Equal(new MemoryWriteResult(false, "No memory source configured for tier 'User'."), write);
        Assert.Empty((await store.DeleteScopeAsync(Alice, MemoryTier.User)).Tiers);
    }

    [Fact]
    public void The_provider_constructor_refuses_a_null_dictionary_or_provider()
    {
        Assert.Throws<ArgumentNullException>("tierProviders", () => new FileMemoryStore((IReadOnlyDictionary<MemoryTier, IBundleProvider>)null!));
        Assert.Throws<ArgumentException>("tierProviders", () => new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = null! }));
    }

    [Fact]
    public async Task The_provider_dictionary_is_copied_at_construction()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var providers = new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = storage };
        var store = new FileMemoryStore(providers);
        providers.Clear();

        Assert.True((await store.WriteAsync(Alice, Entry("2026-07-27", "x"), MemoryTier.User)).Written);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #152 step 2: the asynchronous load and the synchronous bridge that never waits. Every
/// pending operation here is held by a <see cref="TaskCompletionSource"/> and released by the
/// test, never by a delay.
/// </summary>
public class SyncBridgeTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    private static BundleKey K(string raw) => BundleKey.Parse(raw);

    /// <summary>A provider whose every operation stays pending until the test releases it.</summary>
    private sealed class GatedProvider : IBundleProvider
    {
        private readonly InMemoryBundleProvider _store;

        public GatedProvider(InMemoryBundleProvider store, bool canWrite = false)
        {
            _store = store;
            CanWrite = canWrite;
        }

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanWrite { get; }

        public int Calls { get; private set; }

        public string Id => "gated";

        public string Location => "memory://gated";

        public BundleProviderCapabilities Capabilities => new(CanWrite, CompletesSynchronously: false);

        public BundleKeyConstraints NativeConstraints => _store.NativeConstraints;

        public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await _store.GetVersionAsync(limits, ct).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var l in _store.EnumerateAsync(prefix, limits, ct).ConfigureAwait(false))
            {
                yield return l;
            }
        }

        public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await _store.ReadAsync(key, expected, ct).ConfigureAwait(false);
        }

        public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await _store.ExistsAsync(key, ct).ConfigureAwait(false);
        }

        public async ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await _store.WriteAsync(key, content, condition, ct).ConfigureAwait(false);
        }

        public async ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            await _store.DeleteAsync(key, condition, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task The_bridge_throws_on_an_incomplete_task_and_does_not_wait()
    {
        var store = new InMemoryBundleProvider().Add("a.md", $"---\n{Fm}\n---\n\nA\n");
        var gated = new GatedProvider(store);

        var failure = Assert.Throws<InvalidOperationException>(() => Bundle.Load(gated));

        Assert.Equal(SyncBridge.IncompleteMessage, failure.Message);
        // It returned at once: the gate it would have waited for is still closed.
        Assert.False(gated.Gate.Task.IsCompleted);

        // The async load over the same provider is simply pending, and finishes when released.
        var load = Bundle.LoadAsync(gated);
        Assert.False(load.IsCompleted);
        gated.Gate.SetResult();
        var bundle = await load;
        Assert.Single(bundle.Concepts);
    }

    [Fact]
    public async Task A_cancelled_load_ends_with_a_cancellation_and_is_not_a_load_failure()
    {
        var store = new InMemoryBundleProvider().Add("a.md", $"---\n{Fm}\n---\n\nA\n");
        var gated = new GatedProvider(store);
        using var cts = new CancellationTokenSource();

        var load = Bundle.LoadAsync(gated, null, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public void A_sync_write_on_a_provider_that_does_not_complete_synchronously_is_refused_before_any_io()
    {
        var store = new InMemoryBundleProvider(canWrite: true);
        var gated = new GatedProvider(store, canWrite: true);
        var writer = new BundleConceptWriter(gated);

        Assert.Throws<InvalidOperationException>(() => writer.WriteConcept("notes/new", Fm, "body\n"));
        Assert.Throws<InvalidOperationException>(() => writer.AppendLog("Update", "x"));
        Assert.Throws<InvalidOperationException>(() => IndexGenerator.RegenerateIndexes(gated));

        // Nothing was started: no operation reached the provider, so releasing it commits nothing.
        Assert.Equal(0, gated.Calls);
        gated.Gate.SetResult();
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task The_async_load_equals_the_sync_load_over_the_filesystem()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n");
        tmp.Write("tables/orders.md", $"---\n{Fm}\n---\n\nSee [c](/tables/customers.md).\n");
        tmp.Write("tables/customers.md", $"---\n{Fm}\n---\n\nBody.\n");
        tmp.Write("tables/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n");
        var bySync = Bundle.Load(tmp.Path);

        var byAsync = await Bundle.LoadAsync(new FileSystemBundleProvider(tmp.Path));

        Assert.Equal(bySync.Concepts.Select(c => c.Id), byAsync.Concepts.Select(c => c.Id));
        Assert.Equal(bySync.Concepts.Select(c => c.Document.Body), byAsync.Concepts.Select(c => c.Document.Body));
        Assert.Equal(bySync.IndexKeys, byAsync.IndexKeys);
        Assert.Equal(bySync.ParseProblems.Select(p => p.RawName), byAsync.ParseProblems.Select(p => p.RawName));
        Assert.Equal(bySync.OkfVersion, byAsync.OkfVersion);
        Assert.Equal(bySync.Version, byAsync.Version);
    }

    [Fact]
    public async Task An_async_load_lists_each_directory_once_like_the_sync_load()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 6; i++)
        {
            tmp.Write($"d{i}/c.md", $"---\n{Fm}\n---\n\nb\n");
        }

        var syncProvider = new FileSystemBundleProvider(tmp.Path);
        _ = Bundle.Load(syncProvider);
        var asyncProvider = new FileSystemBundleProvider(tmp.Path);
        _ = await Bundle.LoadAsync(asyncProvider);

        Assert.True(syncProvider.DirectoryListings > 0);
        Assert.Equal(syncProvider.DirectoryListings, asyncProvider.DirectoryListings);
    }

    [Fact]
    public async Task The_async_resource_forms_agree_with_the_sync_ones()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", $"---\n{Fm}\n---\n\nA\n");
        tmp.Write("notes/refs/data.sql", "select 1;");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single();

        foreach (var raw in new[] { "refs/data.sql", "refs/absent.sql", "https://example.com/x", "../../../escape" })
        {
            bundle.TryResolveResourceKey(concept, raw, out var syncKey, out var syncStatus);
            var (key, status) = await bundle.ResolveResourceKeyAsync(concept, raw);
            Assert.Equal(syncStatus, status);
            Assert.Equal(syncKey, key);
        }

        var (text, version) = await bundle.ReadResourceTextAsync(K("notes/refs/data.sql"));
        Assert.Equal("select 1;", text);
        Assert.Equal(bundle.ReadResourceText(K("notes/refs/data.sql")), text);
        var pinned = await bundle.ReadResourceTextAsync(K("notes/refs/data.sql"), version);
        Assert.Equal(version, pinned.Version);
        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () =>
            await bundle.ReadResourceTextAsync(K("notes/refs/data.sql"), new BundleObjectVersion("other")));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await bundle.ReadResourceTextAsync(K("notes/refs/absent.sql")));
    }
}

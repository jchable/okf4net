// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>Spec of 2026-11-16 (S3) §4.7 (a): the load reads the bundle token before it lists.</summary>
public class LoadOrderTests
{
    private const string Note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nBody.\n";

    /// <summary>Adds b.md right after the FIRST of (version, listing) completes, whichever the core calls first.</summary>
    private sealed class MutatesBetweenProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        private int _firstDone;

        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        private void AfterFirst()
        {
            if (Interlocked.Exchange(ref _firstDone, 1) == 0)
            {
                inner.Add("b.md", Note);
            }
        }

        public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
        {
            var v = await inner.GetVersionAsync(limits, ct);
            AfterFirst();
            return v;
        }

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var all = new List<BundleListing>();
            await foreach (var l in inner.EnumerateAsync(prefix, limits, ct))
            {
                all.Add(l);
            }

            if (prefix is null)
            {
                AfterFirst();
            }

            foreach (var l in all)
            {
                yield return l;
            }
        }

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);
        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);
        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    [Fact]
    public async Task An_object_added_between_the_token_and_the_listing_is_never_served_stale_forever()
    {
        var store = new InMemoryBundleProvider().Add("a.md", Note);
        using var handle = new BundleHandle(new MutatesBetweenProvider(store), refreshPolicy: BundleRefreshPolicy.OnAccess);

        _ = await handle.GetCurrentAsync();
        var second = await handle.GetCurrentAsync();

        Assert.Contains(second.Concepts, c => c.Id.ToString() == "b");
    }

    /// <summary>Changes a.md to B right after the token is read, and back to A right after a.md is read: the image holds B under A's token.</summary>
    private sealed class AbaDuringLoadProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        private int _armed = 1;

        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
        {
            var v = await inner.GetVersionAsync(limits, ct);
            if (Volatile.Read(ref _armed) == 1)
            {
                inner.Add("a.md", Changed);
            }

            return v;
        }

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

        public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            var o = await inner.ReadAsync(key, expected, ct);
            if (key.ToString() == "a.md" && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                inner.Add("a.md", Note);
            }

            return o;
        }

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);
        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    private const string Changed = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nChanged.\n";

    [Fact]
    public async Task A_change_undone_before_the_next_comparison_keeps_the_intermediate_bytes_the_documented_ABA_limit()
    {
        var store = new InMemoryBundleProvider().Add("a.md", Note);
        using var handle = new BundleHandle(new AbaDuringLoadProvider(store), refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();
        Assert.Contains("Changed.", first.Concepts.Single().Document.Body, StringComparison.Ordinal); // B was read during the load

        var second = await handle.GetCurrentAsync();

        Assert.Same(first, second); // the token is A's again: no reload, B stays served (spec §4.3, limit 1)
    }
}

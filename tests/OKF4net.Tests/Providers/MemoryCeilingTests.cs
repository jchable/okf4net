// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #151: the memory ceiling of <see cref="BundleLoadOptions.MemoryCeilingBytes"/>. A load over the ceiling is
/// refused before any read when the listing's declared sizes already exceed it, else as soon as the running
/// estimate crosses it — never after reading everything — and a <see cref="BundleHandle"/> keeps serving its
/// previous image, flagged stale. No ceiling (the default) changes nothing.
/// </summary>
public class MemoryCeilingTests
{
    private const string Doc = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\n{0}\n";

    private static InMemoryBundleProvider Store(int count, int bodyChars = 1_000)
    {
        var store = new InMemoryBundleProvider();
        for (var i = 0; i < count; i++)
        {
            store.Add($"n{i:D3}.md", string.Format(null, Doc, new string('x', bodyChars)));
        }

        return store;
    }

    private static int Reads(InMemoryBundleProvider store) => store.Calls.Count(c => c.StartsWith("read ", StringComparison.Ordinal));

    /// <summary>The estimate of one of <see cref="Store"/>'s objects (no link).</summary>
    private static long OneObject(int bodyChars = 1_000) => SnapshotMemory.OfText(string.Format(null, Doc, new string('x', bodyChars)));

    [Fact]
    public void Without_a_ceiling_the_image_reports_its_estimate_and_loads_as_before()
    {
        var store = Store(10);

        var bundle = Bundle.Load(store);

        Assert.Equal(10, bundle.Count);
        Assert.Equal(10 * OneObject(), bundle.EstimatedMemoryBytes);
        Assert.Null(BundleLoadOptions.Default.MemoryCeilingBytes);
    }

    [Fact]
    public void A_ceiling_the_image_fits_under_changes_nothing()
    {
        var store = Store(10);

        var bundle = Bundle.Load(store, new BundleLoadOptions { MemoryCeilingBytes = 10 * OneObject() });

        Assert.Equal(10, bundle.Count); // equal to the ceiling is not over it
        Assert.Equal(10 * OneObject(), bundle.EstimatedMemoryBytes);
    }

    [Fact]
    public void Declared_sizes_over_the_ceiling_refuse_the_load_before_any_read()
    {
        var store = Store(10);
        store.ClearCalls();

        var failure = Assert.Throws<BundleLoadException>(() => Bundle.Load(store, new BundleLoadOptions { MemoryCeilingBytes = 3 * OneObject() }));

        var refusal = Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
        Assert.Equal(3 * OneObject(), refusal.CeilingBytes);
        Assert.Equal(BundleBound.Memory, refusal.Bound);
        Assert.True(refusal.EstimatedBytes > refusal.CeilingBytes);
        Assert.Equal($"the bundle exceeds the memory bound ({3 * OneObject()})", failure.Message);
        Assert.Equal(0, Reads(store));
    }

    [Fact]
    public void Without_declared_sizes_the_load_stops_at_the_object_that_crosses_the_ceiling()
    {
        var store = Store(10);
        var hidden = new UndeclaredSizes(store);
        store.ClearCalls();

        var failure = Assert.Throws<BundleLoadException>(() => Bundle.Load(hidden, new BundleLoadOptions { MemoryCeilingBytes = (3 * OneObject()) + 1 }));

        var refusal = Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
        Assert.Equal(4 * OneObject(), refusal.EstimatedBytes); // the fourth object crossed it
        Assert.Equal(4, Reads(store)); // and nothing after it was read
    }

    [Fact]
    public async Task The_parallel_load_stops_starting_reads_once_the_ceiling_is_crossed_and_refuses_the_same_way()
    {
        var store = Store(200);
        var hidden = new UndeclaredSizes(store);
        store.ClearCalls();
        var options = new BundleLoadOptions { MemoryCeilingBytes = (3 * OneObject()) + 1, Limits = new BundleLimits { MaxConcurrentRequests = 4 } };

        var failure = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(hidden, options));

        var refusal = Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
        Assert.Equal(4 * OneObject(), refusal.EstimatedBytes); // the same object, whatever finished first
        Assert.True(Reads(store) < 200, $"the parallel load read {Reads(store)} of 200 objects");
    }

    [Fact]
    public void Links_count_toward_the_ceiling_after_the_parse()
    {
        var store = new InMemoryBundleProvider()
            .Add("a.md", "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\n- [b](b.md)\n- [c](c.md)\n")
            .Add("b.md", string.Format(null, Doc, "b"))
            .Add("c.md", string.Format(null, Doc, "c"));

        var bundle = Bundle.Load(store);

        // The estimate is the bytes of the three objects plus a.md's two links.
        var bytesOnly = new[] { "a.md", "b.md", "c.md" }.Sum(k => SnapshotMemory.OfText(ReadText(store, k)));
        Assert.Equal(bytesOnly + SnapshotMemory.OfLinks(2), bundle.EstimatedMemoryBytes);

        // A ceiling between the bytes and the bytes plus links is crossed by the links, after the parse of a.md.
        var failure = Assert.Throws<BundleLoadException>(() => Bundle.Load(new UndeclaredSizes(store), new BundleLoadOptions { MemoryCeilingBytes = bytesOnly + 1 }));
        Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
    }

    [Fact]
    public void An_object_over_the_byte_bound_is_not_counted_by_the_declared_sizes()
    {
        // The read loop skips an object over MaxObjectBytes as a parse problem and never adds it to the estimate,
        // so the preflight must not charge it either: the ceiling a successful load reports is a ceiling it passes.
        var store = Store(2);
        store.Add("huge.md", string.Format(null, Doc, new string('x', 5_000)));
        var limits = new BundleLimits { MaxObjectBytes = 2_000 };

        var unbounded = Bundle.Load(store, new BundleLoadOptions { Limits = limits });
        Assert.Equal(2, unbounded.Count);
        Assert.Single(unbounded.ParseProblems);

        var declared = Bundle.Load(store, new BundleLoadOptions { Limits = limits, MemoryCeilingBytes = unbounded.EstimatedMemoryBytes });
        var undeclared = Bundle.Load(new UndeclaredSizes(store), new BundleLoadOptions { Limits = limits, MemoryCeilingBytes = unbounded.EstimatedMemoryBytes });

        Assert.Equal(unbounded.EstimatedMemoryBytes, declared.EstimatedMemoryBytes);
        Assert.Equal(unbounded.EstimatedMemoryBytes, undeclared.EstimatedMemoryBytes);
    }

    [Fact]
    public async Task A_parallel_load_refused_by_links_reads_no_more_than_one_window_of_objects()
    {
        // The links of a concept are known only after its parse, so with a ceiling the reads are not run ahead of
        // the parse: a bundle whose bytes fit but whose first concept's links do not is refused after one window
        // of reads (the degree), never after downloading all of it.
        // a.md sorts first and holds 500 links, which alone cost more than every other object together.
        var links = string.Concat(Enumerable.Range(0, 500).Select(i => $"- [n{i}](n{i % 30:D3}.md)\n"));
        var store = new InMemoryBundleProvider()
            .Add("a.md", "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\n" + links);
        for (var i = 0; i < 30; i++)
        {
            store.Add($"n{i:D3}.md", string.Format(null, Doc, new string('x', 1_000)));
        }

        var bytesOnly = (30 * OneObject()) + SnapshotMemory.OfText(ReadText(store, "a.md"));
        store.ClearCalls();
        var options = new BundleLoadOptions { MemoryCeilingBytes = bytesOnly + 1, Limits = new BundleLimits { MaxConcurrentRequests = 4 } };

        var failure = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(new UndeclaredSizes(store), options));

        Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
        Assert.True(Reads(store) <= 4, $"the load read {Reads(store)} of 31 objects before refusing on the links of the first");
    }

    [Fact]
    public async Task A_refresh_over_the_ceiling_keeps_the_previous_image_served_and_stale()
    {
        var store = Store(3);
        using var handle = new BundleHandle(store, new BundleLoadOptions { MemoryCeilingBytes = 5 * OneObject() });
        var first = await handle.GetCurrentAsync();
        Assert.Equal(3, first.Count);

        for (var i = 3; i < 8; i++)
        {
            store.Add($"n{i:D3}.md", string.Format(null, Doc, new string('x', 1_000)));
        }

        handle.Invalidate();
        var served = await handle.GetCurrentAsync();

        Assert.Same(first, served);
        Assert.True(handle.IsStale);
        var error = Assert.IsType<BundleLoadException>(handle.LastLoadError);
        Assert.IsType<BundleMemoryCeilingExceededException>(error.InnerException);
        await Assert.ThrowsAsync<BundleLoadException>(() => handle.RefreshAsync());
        Assert.Same(first, await handle.GetCurrentAsync());
    }

    [Fact]
    public async Task A_first_load_over_the_ceiling_throws_from_the_handle()
    {
        using var handle = new BundleHandle(Store(10), new BundleLoadOptions { MemoryCeilingBytes = OneObject() });

        var failure = await Assert.ThrowsAsync<BundleLoadException>(() => handle.GetCurrentAsync());
        Assert.IsType<BundleMemoryCeilingExceededException>(failure.InnerException);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_ceiling_that_is_not_positive_is_refused(long ceiling) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BundleLoadOptions { MemoryCeilingBytes = ceiling });

    private static string ReadText(InMemoryBundleProvider store, string key)
    {
        using var obj = store.Read(BundleKey.Parse(key));
        return new StreamReader(obj.Content).ReadToEnd();
    }

    /// <summary>The same objects, listed without their sizes: the ceiling can only be enforced during the reads.</summary>
    private sealed class UndeclaredSizes(InMemoryBundleProvider inner) : SyncTestProvider
    {
        public override string Id => inner.Id;

        public override string Location => inner.Location;

        public override BundleProviderCapabilities Capabilities => inner.Capabilities;

        public override BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        protected override BundleVersion GetVersionSync(BundleLimits limits) => inner.GetVersion(limits);

        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits) =>
            inner.Enumerate(prefix, limits).Select(l => l with { Length = null });

        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => inner.Read(key, expected);

        protected override bool ExistsSync(BundleKey key) => inner.Exists(key);

        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => throw new NotSupportedException();

        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => throw new NotSupportedException();
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// <see cref="BundleHandle"/> (spec §5.1 to §5.3): one image per handle, replaced as a whole;
/// the four refresh policies, all lazy; the reload obligation and its acknowledgement; a failed
/// reload that keeps the previous image served; one load at a time under concurrent access.
/// Every test drives an <see cref="InMemoryBundleProvider"/>, so the storage changes "behind
/// the bundle's back" through <see cref="InMemoryBundleProvider.Add"/>.
/// </summary>
public class BundleHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n";

    private static InMemoryBundleProvider Storage() => new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc);

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    private static int VersionChecks(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "version");

    [Fact]
    public void Manual_loads_once_and_serves_the_same_image_until_refreshed()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);

        var first = handle.Current;
        storage.Add("b.md", Doc);
        var second = handle.Current;

        Assert.Same(first, second);
        Assert.Equal(1, first.Count);
        Assert.Equal(1, Loads(storage));
        Assert.False(handle.IsStale);

        var refreshed = handle.Refresh();
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, refreshed.Count);
        Assert.Same(refreshed, handle.Current);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void Invalidate_obliges_the_next_access_to_reload_under_Manual()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var first = handle.Current;
        storage.Add("b.md", Doc);

        handle.Invalidate();

        Assert.True(handle.IsStale);
        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.False(handle.IsStale);
        Assert.Same(second, handle.Current);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void OnAccess_compares_the_token_at_every_access_and_reloads_only_when_it_changed()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        Assert.Same(first, handle.Current);
        Assert.Same(first, handle.Current);
        Assert.Equal(1, Loads(storage));
        Assert.Equal(3, VersionChecks(storage)); // one at the load, one per later access

        storage.Add("b.md", Doc);

        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public void Interval_compares_the_token_at_most_once_per_interval()
    {
        var storage = Storage();
        var time = new ManualTime();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromSeconds(10)), time: time);
        var first = handle.Current;
        storage.Add("b.md", Doc);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Same(first, handle.Current); // inside the interval: no comparison, no reload
        Assert.Equal(1, VersionChecks(storage));

        time.Advance(TimeSpan.FromSeconds(2));
        var second = handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal(3, VersionChecks(storage)); // the comparison, then the reload's own

        Assert.Same(second, handle.Current); // the interval restarts at the reload
        Assert.Equal(3, VersionChecks(storage));
    }

    [Fact]
    public void Notify_needs_a_notifier_and_treats_a_signal_as_a_hint()
    {
        var storage = Storage();
        Assert.Throws<ArgumentException>(() => new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.Notify));

        var notifying = new NotifyingBundleProvider(storage);
        using var handle = new BundleHandle(notifying, refreshPolicy: BundleRefreshPolicy.Notify);
        var first = handle.Current;
        Assert.Same(first, handle.Current);
        Assert.Equal(1, VersionChecks(storage)); // no signal: no comparison

        notifying.Signal(); // spurious: nothing changed
        Assert.Same(first, handle.Current);
        Assert.Equal(2, VersionChecks(storage));
        Assert.Equal(1, Loads(storage));

        storage.Add("b.md", Doc);
        Assert.Same(first, handle.Current); // a change without a signal is not seen
        notifying.Signal();
        var second = handle.Current;
        Assert.Equal(2, second.Count);
        Assert.Equal(2, Loads(storage));

        handle.Dispose();
        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_failed_reload_keeps_the_previous_image_served_and_marks_it_stale()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        storage.Add("b.md", Doc);
        storage.FailEnumerateWith = new IOException("network down");

        Assert.Same(first, handle.Current);
        Assert.True(handle.IsStale);
        Assert.IsType<BundleLoadException>(handle.LastLoadError);

        var thrown = Assert.Throws<BundleLoadException>(() => handle.Refresh());
        Assert.Same(handle.LastLoadError, thrown);
        Assert.Same(first, handle.Current);

        storage.FailEnumerateWith = null;
        var second = handle.Current;
        Assert.Equal(2, second.Count);
        Assert.False(handle.IsStale);
        Assert.Null(handle.LastLoadError);
    }

    [Fact]
    public void The_first_load_failing_throws()
    {
        var storage = Storage();
        storage.FailEnumerateWith = new IOException("network down");
        using var handle = new BundleHandle(storage);

        Assert.Throws<BundleLoadException>(() => handle.Current);
        Assert.Throws<BundleLoadException>(() => handle.Refresh());
        Assert.NotNull(handle.LastLoadError);
    }

    /// <summary>
    /// Spec §5.1 (revision 6): an obligation is acknowledged only by a successful load started
    /// after it. A load already running when <see cref="BundleHandle.Invalidate"/> is called may
    /// have surveyed the old state, so it does not acknowledge, and the next access reloads
    /// again; a load that fails leaves it pending too.
    /// </summary>
    [Fact]
    public void An_obligation_made_during_a_load_is_not_acknowledged_by_that_load()
    {
        var storage = Storage();
        var hooked = new HookedProvider(storage);
        using var handle = new BundleHandle(hooked);
        hooked.OnEnumerate = () => handle.Invalidate(); // fires inside the first load
        var first = handle.Current;
        hooked.OnEnumerate = null;

        Assert.True(handle.IsStale);
        Assert.Equal(1, Loads(storage));
        var second = handle.Current; // reloads: the first load started before the obligation
        Assert.NotSame(first, second);
        Assert.Equal(2, Loads(storage));
        Assert.False(handle.IsStale);

        handle.Invalidate();
        storage.FailEnumerateWith = new IOException("network down");
        Assert.Same(second, handle.Current); // the failed reload leaves the obligation pending
        Assert.True(handle.IsStale);
        storage.FailEnumerateWith = null;
        Assert.NotSame(second, handle.Current); // acknowledged by the successful one
        Assert.False(handle.IsStale);
    }

    [Fact]
    public void Concurrent_first_accesses_share_one_load()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var results = new Bundle[16];
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
        {
            start.Wait();
            results[i] = handle.Current;
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(1, Loads(storage));
        Assert.All(results, b => Assert.Same(results[0], b));
    }

    /// <summary>A handle holds exactly one image: the one a refresh replaces is not retained (the memory of a long-lived host grows with the bundle, not with its history).</summary>
    [Fact]
    public void A_replaced_image_is_not_retained()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var previous = LoadAndForget(handle);
        storage.Add("b.md", Doc);
        handle.Refresh();

        for (var i = 0; i < 5 && previous.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(previous.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndForget(BundleHandle handle) => new(handle.Current);

    [Fact]
    public void A_writer_over_a_handle_invalidates_it_after_each_write_and_still_notifies()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var notified = 0;
        var writer = new BundleConceptWriter(handle, onWriteCommitted: () => notified++);
        Assert.Equal(1, handle.Current.Count);

        var result = writer.WriteConcept("b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n");

        Assert.StartsWith("Written", result, StringComparison.Ordinal);
        Assert.Equal(1, notified);
        Assert.True(handle.IsStale);
        Assert.Equal(2, handle.Current.Count);
        Assert.Throws<NotSupportedException>(() => writer.BundleRoot);
    }

    /// <summary>A clock the test moves by hand, for <see cref="BundleRefreshMode.Interval"/>.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond; // never 0: the handle reads 0 as "never checked"

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A provider that runs a callback at the start of every listing: the one moment a load is provably in progress.</summary>
    private sealed class HookedProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        public Action? OnEnumerate { get; set; }

        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);

        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
        {
            OnEnumerate?.Invoke();
            return inner.Enumerate(prefix, limits);
        }

        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);
        public bool Exists(BundleKey key) => inner.Exists(key);
        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
        public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }
}

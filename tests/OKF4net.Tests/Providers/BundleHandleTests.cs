// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Deterministic: the first listing blocks until every other thread is parked on the handle's
    /// load lock, so each of them would load again if the re-check under the lock were missing.
    /// </summary>
    [Fact]
    public void Concurrent_first_accesses_share_one_load()
    {
        var storage = Storage();
        var hooked = new HookedProvider(storage);
        using var handle = new BundleHandle(hooked);
        var results = new Bundle[16];
        using var release = new ManualResetEventSlim(false);
        using var inFirstLoad = new ManualResetEventSlim(false);
        hooked.OnEnumerate = () =>
        {
            inFirstLoad.Set();
            release.Wait(Timeout);
        };
        var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() => results[i] = handle.Current)).ToList();
        threads[0].Start();
        Assert.True(inFirstLoad.Wait(Timeout), "the first load never started");
        threads.Skip(1).ToList().ForEach(t => t.Start());
        foreach (var t in threads.Skip(1))
        {
            Assert.True(SpinWait.SpinUntil(() => t.ThreadState.HasFlag(ThreadState.WaitSleepJoin), Timeout), "a thread never parked on the load lock");
        }

        release.Set();
        Assert.All(threads, t => Assert.True(t.Join(Timeout), "a thread never finished"));

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
        var staleInCallback = false;
        var writer = new BundleConceptWriter(handle, onWriteCommitted: () =>
        {
            notified++;
            staleInCallback = handle.IsStale; // the handle is invalidated BEFORE the caller's notification
        });
        Assert.Equal(1, handle.Current.Count);

        var result = writer.WriteConcept("b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n");

        Assert.StartsWith("Written", result, StringComparison.Ordinal);
        Assert.Equal(1, notified);
        Assert.True(staleInCallback);
        Assert.True(handle.IsStale);
        Assert.Equal(2, handle.Current.Count);
        Assert.Throws<NotSupportedException>(() => writer.BundleRoot);
    }

    [Fact]
    public void A_refused_write_does_not_invalidate_the_handle()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var notified = 0;
        var writer = new BundleConceptWriter(handle, onWriteCommitted: () => notified++);
        _ = handle.Current;

        var result = writer.WriteConcept("a", "type: Note\ntitle: A\ndescription: a.\n", "A2.\n", BundleWriteCondition.IfMatch(new BundleObjectVersion("not-the-current-version")));

        Assert.StartsWith("Error", result, StringComparison.Ordinal);
        Assert.Equal(0, notified);
        Assert.False(handle.IsStale);
    }

    [Fact]
    public void A_signal_consumed_by_a_failed_reload_is_not_lost_under_Notify()
    {
        var storage = Storage();
        var notifying = new NotifyingBundleProvider(storage);
        using var handle = new BundleHandle(notifying, refreshPolicy: BundleRefreshPolicy.Notify);
        var first = handle.Current;
        storage.Add("b.md", Doc);
        storage.FailEnumerateWith = new IOException("network down");
        notifying.Signal();

        Assert.Same(first, handle.Current);
        Assert.True(handle.IsStale);

        storage.FailEnumerateWith = null; // no second signal
        var second = handle.Current;
        Assert.Equal(2, second.Count);
        Assert.False(handle.IsStale);
    }

    [Fact]
    public void An_unreadable_token_counts_as_changed_and_never_throws_from_Current()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        storage.FailVersionWith = new IOException("network down");
        var loadsBefore = Loads(storage);

        Assert.Same(first, handle.Current); // the comparison fails, the reload fails too: the previous image is served
        Assert.True(handle.IsStale);
        Assert.IsType<BundleLoadException>(handle.LastLoadError);
        Assert.True(Loads(storage) > loadsBefore); // a reload was attempted
    }

    [Fact]
    public void A_comparison_that_finds_the_image_current_clears_the_stale_mark()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = handle.Current;
        storage.FailVersionWith = new IOException("network down");
        storage.FailEnumerateWith = new IOException("network down");
        Assert.Same(first, handle.Current);
        Assert.True(handle.IsStale);

        storage.FailVersionWith = null; // storage recovers with the same token
        storage.FailEnumerateWith = null;
        Assert.Same(first, handle.Current);
        Assert.False(handle.IsStale);
        Assert.Null(handle.LastLoadError);
    }

    [Fact]
    public void Refresh_rethrows_with_the_stack_of_the_failure()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        _ = handle.Current;
        storage.FailEnumerateWith = new IOException("network down");

        var thrown = Assert.Throws<BundleLoadException>(() => handle.Refresh());

        Assert.Contains("LoadLocked", thrown.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// A raw exception (neither a provider, an I/O nor an access exception) from a host provider
    /// is a load failure like any other: the previous image is served stale with a
    /// <see cref="BundleLoadException"/> that wraps it, and the next access after the provider
    /// recovers reloads. The signal or the interval that the comparison consumed must not make
    /// the outdated image current (Notify re-arms the signal; Interval waits its next slot).
    /// </summary>
    [Theory]
    [InlineData("OnAccess", "version")]
    [InlineData("OnAccess", "enumerate")]
    [InlineData("OnAccess", "read")]
    [InlineData("Interval", "version")]
    [InlineData("Interval", "enumerate")]
    [InlineData("Interval", "read")]
    [InlineData("Notify", "version")]
    [InlineData("Notify", "enumerate")]
    [InlineData("Notify", "read")]
    public void A_raw_exception_from_a_host_provider_is_a_load_failure_served_stale(string policy, string where)
    {
        using var rig = MakeRig(policy);
        var first = rig.Handle.Current;
        var raw = new InvalidOperationException("secret-host-detail");
        rig.Storage.Add("b.md", Doc);
        Fail(rig.Storage, where, raw);
        rig.Signal();

        Assert.Same(first, rig.Handle.Current);
        Assert.True(rig.Handle.IsStale);
        var error = Assert.IsType<BundleLoadException>(rig.Handle.LastLoadError);
        Assert.Same(raw, error.InnerException);
        Assert.DoesNotContain("secret-host-detail", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error.Message, StringComparison.Ordinal);

        Fail(rig.Storage, where, null); // the provider recovers; no new signal under Notify
        rig.Elapse();

        var second = rig.Handle.Current;
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.False(rig.Handle.IsStale);
        Assert.Null(rig.Handle.LastLoadError);
    }

    /// <summary>The token comparison alone: a raw exception from <see cref="IBundleProvider.GetVersion"/> there counts as "changed", so the reload (which succeeds) runs.</summary>
    [Theory]
    [InlineData("OnAccess")]
    [InlineData("Interval")]
    [InlineData("Notify")]
    public void A_raw_exception_from_the_token_comparison_alone_counts_as_changed(string policy)
    {
        using var rig = MakeRig(policy);
        var first = rig.Handle.Current;
        rig.Storage.Add("b.md", Doc);
        var thrown = false;
        rig.Hooked.OnGetVersion = () =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("secret-host-detail");
            }
        };
        rig.Signal();

        var second = rig.Handle.Current;

        Assert.True(thrown);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.False(rig.Handle.IsStale);
        Assert.Null(rig.Handle.LastLoadError);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("enumerate")]
    [InlineData("read")]
    public void A_raw_exception_at_the_first_load_throws_a_BundleLoadException(string where)
    {
        using var rig = MakeRig("OnAccess");
        var raw = new InvalidOperationException("secret-host-detail");
        Fail(rig.Storage, where, raw);

        var thrown = Assert.Throws<BundleLoadException>(() => rig.Handle.Current);

        Assert.Same(raw, thrown.InnerException);
        Assert.DoesNotContain("secret-host-detail", thrown.Message, StringComparison.Ordinal);
        Assert.Same(thrown, rig.Handle.LastLoadError);
        Assert.False(rig.Handle.IsStale);

        Fail(rig.Storage, where, null);
        Assert.Equal(1, rig.Handle.Current.Count);
        Assert.Null(rig.Handle.LastLoadError);
    }

    [Fact]
    public void Refresh_wraps_a_raw_exception_and_keeps_the_previous_image_served()
    {
        using var rig = MakeRig("Manual");
        var first = rig.Handle.Current;
        var raw = new InvalidOperationException("secret-host-detail");
        rig.Storage.FailEnumerateWith = raw;

        var thrown = Assert.Throws<BundleLoadException>(() => rig.Handle.Refresh());

        Assert.Same(raw, thrown.InnerException);
        Assert.Same(first, rig.Handle.Current);
        Assert.True(rig.Handle.IsStale);
    }

    /// <summary>An <see cref="OutOfMemoryException"/> is fatal, never a provider failure: it propagates from both the comparison and the load.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("enumerate")]
    public void An_OutOfMemoryException_still_propagates(string where)
    {
        using var rig = MakeRig("OnAccess");
        _ = rig.Handle.Current;
        rig.Storage.Add("b.md", Doc);
        Fail(rig.Storage, where, new OutOfMemoryException());

        Assert.Throws<OutOfMemoryException>(() => rig.Handle.Current);
    }

    private static void Fail(InMemoryBundleProvider storage, string where, Exception? failure)
    {
        switch (where)
        {
            case "version":
                storage.FailVersionWith = failure;
                break;
            case "enumerate":
                storage.FailEnumerateWith = failure;
                break;
            default:
                storage.FailReadsWith = failure;
                break;
        }
    }

    /// <summary>A handle over the in-memory storage under one policy, with the gestures that make the policy compare the token.</summary>
    private sealed class Rig(InMemoryBundleProvider storage, HookedProvider hooked, BundleHandle handle, Action signal, Action elapse) : IDisposable
    {
        public InMemoryBundleProvider Storage => storage;
        public HookedProvider Hooked => hooked;
        public BundleHandle Handle => handle;

        /// <summary>What makes the policy compare at the next access: a signal under Notify, the interval elapsing under Interval, nothing under OnAccess.</summary>
        public void Signal() => signal();

        /// <summary>What a recovered provider gets with no new signal: the interval elapsing under Interval (it has no re-arm), nothing otherwise.</summary>
        public void Elapse() => elapse();

        public void Dispose() => handle.Dispose();
    }

    private static Rig MakeRig(string policy)
    {
        var storage = Storage();
        var hooked = new HookedProvider(storage);
        var time = new ManualTime();
        switch (policy)
        {
            case "Notify":
                {
                    var notifying = new NotifyingBundleProvider(hooked);
                    return new Rig(storage, hooked, new BundleHandle(notifying, refreshPolicy: BundleRefreshPolicy.Notify), notifying.Signal, () => { });
                }

            case "Interval":
                {
                    var handle = new BundleHandle(hooked, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromSeconds(10)), time: time);
                    return new Rig(storage, hooked, handle, () => time.Advance(TimeSpan.FromSeconds(11)), () => time.Advance(TimeSpan.FromSeconds(11)));
                }

            case "OnAccess":
                return new Rig(storage, hooked, new BundleHandle(hooked, refreshPolicy: BundleRefreshPolicy.OnAccess), () => { }, () => { });
            default:
                return new Rig(storage, hooked, new BundleHandle(hooked), () => { }, () => { });
        }
    }

    [Fact]
    public void Every_needs_a_positive_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BundleRefreshPolicy.Every(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => BundleRefreshPolicy.Every(TimeSpan.FromSeconds(-1)));
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
    private sealed class HookedProvider(InMemoryBundleProvider inner) : SyncTestProvider
    {
        public Action? OnEnumerate { get; set; }

        public Action? OnGetVersion { get; set; }

        public override string Id => inner.Id;
        public override string Location => inner.Location;
        public override BundleProviderCapabilities Capabilities => inner.Capabilities;
        public override BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        protected override BundleVersion GetVersionSync(BundleLimits limits)
        {
            OnGetVersion?.Invoke();
            return inner.GetVersion(limits);
        }

        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits)
        {
            OnEnumerate?.Invoke();
            return inner.Enumerate(prefix, limits);
        }

        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => inner.Read(key, expected);
        protected override bool ExistsSync(BundleKey key) => inner.Exists(key);
        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #152 step 6: the asynchronous load reads concepts with bounded parallelism, and the outcome
/// never depends on which read finishes first. Orderings are forced by the provider's tasks, never by a delay.
/// </summary>
public class ParallelLoadTests
{
    private const string Fm = "type: Note\ntitle: T\ndescription: D.";

    /// <summary>A provider that answers reads itself: each read waits for its own gate, counts how many are in flight at once, and can fail.</summary>
    private sealed class ControlledReads(InMemoryBundleProvider store) : IBundleProvider
    {
        private int _inFlight;
        private int _peak;

        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        public Dictionary<string, Exception> Failures { get; } = [];

        public int Peak => Volatile.Read(ref _peak);

        public string Id => "controlled";

        public string Location => "memory://controlled";

        public BundleProviderCapabilities Capabilities => new(false, CompletesSynchronously: false);

        public BundleKeyConstraints NativeConstraints => store.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => store.GetVersionAsync(limits, ct);

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => store.EnumerateAsync(prefix, limits, ct);

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => store.ExistsAsync(key, ct);

        public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }

            try
            {
                if (Gates.TryGetValue(key.ToString(), out var gate))
                {
                    await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                if (Failures.TryGetValue(key.ToString(), out var failure))
                {
                    throw failure;
                }

                return await store.ReadAsync(key, expected, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => store.WriteAsync(key, content, condition, ct);

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => store.DeleteAsync(key, condition, ct);
    }

    private static InMemoryBundleProvider Store(int count)
    {
        var store = new InMemoryBundleProvider();
        for (var i = 0; i < count; i++)
        {
            store.Add($"n{i:D2}.md", $"---\n{Fm}\n---\n\nbody {i}\n");
        }

        return store;
    }

    [Fact]
    public async Task A_parallel_load_gives_the_same_bundle_as_the_sequential_one()
    {
        var store = Store(12);
        var sequential = Bundle.Load(store);

        var parallel = await Bundle.LoadAsync(store, new BundleLoadOptions { Limits = new BundleLimits { MaxConcurrentRequests = 4 } });

        Assert.Equal(sequential.Concepts.Select(c => c.Id), parallel.Concepts.Select(c => c.Id));
        Assert.Equal(sequential.Concepts.Select(c => c.Document.Body), parallel.Concepts.Select(c => c.Document.Body));
        Assert.Equal(sequential.ParseProblems.Select(p => p.RawName), parallel.ParseProblems.Select(p => p.RawName));
        Assert.Equal(sequential.ObjectVersions.OrderBy(p => p.Key.ToString()), parallel.ObjectVersions.OrderBy(p => p.Key.ToString()));
    }

    [Fact]
    public async Task Concurrency_never_exceeds_MaxConcurrentRequests_and_does_overlap()
    {
        var controlled = new ControlledReads(Store(10));
        var gate = new TaskCompletionSource();
        foreach (var i in Enumerable.Range(0, 10))
        {
            controlled.Gates[$"n{i:D2}.md"] = gate;
        }

        var load = Bundle.LoadAsync(controlled, new BundleLoadOptions { Limits = new BundleLimits { MaxConcurrentRequests = 3 } }).AsTask();
        SpinWait.SpinUntil(() => controlled.Peak >= 3, TimeSpan.FromSeconds(30));
        Assert.Equal(3, controlled.Peak);
        gate.SetResult();
        var bundle = await load;

        Assert.Equal(10, bundle.Concepts.Count);
        Assert.Equal(3, controlled.Peak);
    }

    [Fact]
    public async Task A_global_failure_names_the_key_of_the_smallest_rank_whatever_finished_first()
    {
        var controlled = new ControlledReads(Store(10));
        var slowFirst = new TaskCompletionSource();
        controlled.Gates["n03.md"] = slowFirst;
        controlled.Failures["n03.md"] = new IOException("rank three");
        controlled.Failures["n07.md"] = new IOException("rank seven");

        var load = Bundle.LoadAsync(controlled, new BundleLoadOptions { Limits = new BundleLimits { MaxConcurrentRequests = 16 } }).AsTask();
        // n07 fails at once; n03 (the lower rank) is still pending. The load must wait for it and report n03.
        Assert.False(load.IsCompleted);
        slowFirst.SetResult();

        var failure = await Assert.ThrowsAsync<BundleLoadException>(() => load);
        Assert.Contains("n03.md", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("n07.md", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_parallel_load_ends_with_a_cancellation_after_every_started_read_has_ended()
    {
        var controlled = new ControlledReads(Store(6));
        var gate = new TaskCompletionSource();
        foreach (var i in Enumerable.Range(0, 6))
        {
            controlled.Gates[$"n{i:D2}.md"] = gate;
        }

        using var cts = new CancellationTokenSource();
        var load = Bundle.LoadAsync(controlled, new BundleLoadOptions { Limits = new BundleLimits { MaxConcurrentRequests = 4 } }, cts.Token).AsTask();
        SpinWait.SpinUntil(() => controlled.Peak >= 4, TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }
}

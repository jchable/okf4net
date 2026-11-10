// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// The whole-branch review of #152 (one fix pass): a cancellation that arrives while an object's I/O
/// is in flight after a first commit must not lose what landed, the tools' invalidation must not
/// depend on the (cancelled) token, the computation tools must not call a synchronous bridge, and a
/// cancelled parallel load must not return while its started reads are still running.
/// </summary>
public class FinalReviewFixTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    /// <summary>
    /// Forwards to an in-memory provider, but its Nth write announces itself and then waits for a
    /// release, honouring the caller's token: the shape of a real asynchronous provider mid-request.
    /// </summary>
    private sealed class NthWriteHeld(InMemoryBundleProvider inner, int nth) : IBundleProvider
    {
        private int _writes;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => new(true, CompletesSynchronously: false);

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

        public async ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _writes) == nth)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return await inner.WriteAsync(key, content, condition, ct).ConfigureAwait(false);
        }

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    [Fact]
    public async Task A_cancellation_during_the_in_flight_write_of_a_later_object_still_reports_what_landed()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("p/a.md", Doc).Add("p/b.md", Doc).Add("p/c.md", Doc);
        var held = new NthWriteHeld(inner, nth: 2);
        var notified = 0;
        var writer = new BundleConceptWriter(held, onWriteCommitted: () => notified++);
        using var cts = new CancellationTokenSource();

        var batch = writer.RecordVerificationsAsync(["p/a", "p/b", "p/c"], "human:ada", null, cts.Token).AsTask();
        await held.Started.Task;
        await cts.CancelAsync(); // while b's write is in flight, a already committed
        held.Release.SetResult();
        var outcome = await batch; // before the fix: an OperationCanceledException that lost a's stamp and its notification

        Assert.False(outcome.Recorded);
        Assert.Equal(["p/a", "p/b"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal([BundleKey.Parse("p/c.md")], outcome.Report.Failed.Select(f => f.Key));
        Assert.Equal(2, notified);
    }

    [Fact]
    public async Task A_cancellation_during_an_index_write_after_the_first_index_still_returns_the_report()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("a/x.md", Doc).Add("b/y.md", Doc).Add("c/z.md", Doc);
        var held = new NthWriteHeld(inner, nth: 2);
        using var cts = new CancellationTokenSource();

        var run = IndexGenerator.RegenerateIndexesAsync(held, null, cts.Token).AsTask();
        await held.Started.Task;
        await cts.CancelAsync();
        held.Release.SetResult();
        var report = await run;

        Assert.True(report.Written.Count >= 2);
        Assert.All(report.Failed, f => Assert.Equal("not attempted (cancelled)", f.Reason));
        Assert.NotEmpty(report.Failed);
    }

    [Fact]
    public async Task VerifyAsync_cancelled_between_two_objects_returns_what_landed_instead_of_throwing()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("p/a.md", Doc).Add("p/b.md", Doc).Add("p/c.md", Doc);
        using var cts = new CancellationTokenSource();
        var store = new BundleConceptWriterAsyncTests.CancelAfterWrites(inner, cts, writes: 1);
        var tools = new OkfBundleTools(new BundleHandle(store));

        var text = await tools.VerifyAsync("p/a,p/b,p/c", "human:ada", null, cts.Token);

        Assert.Contains("cancelled after 1 of 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetComputationAsync_asks_a_provider_that_completes_later_asynchronously()
    {
        var store = new InMemoryBundleProvider()
            .Add("references/revenue.sql", "SELECT 1\n")
            .Add("att.py", "def attest(**_):\n    return {}\n")
            .Add("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\nattester: { resource: att.py }\n---\n");
        var gated = new GatedProvider(store);
        gated.Gate.SetResult();
        var tools = new OkfBundleTools(new BundleHandle(gated));
        _ = await tools.BrowseAsync(); // loads the image while the provider answers at once
        gated.Close(); // from now on the provider answers later

        var shown = tools.GetComputationAsync("c/rev");
        Assert.False(shown.IsCompleted); // before the fix: InvalidOperationException from a synchronous bridge
        gated.Gate.SetResult();

        Assert.Contains("SELECT 1", await shown, StringComparison.Ordinal);
    }
}

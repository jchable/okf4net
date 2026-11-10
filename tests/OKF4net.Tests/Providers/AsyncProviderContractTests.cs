// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #152 step 2: the asynchronous provider contract as the filesystem, read-only and in-memory
/// providers honour it. The concurrency-free half: everything here completes before it returns.
/// </summary>
public class AsyncProviderContractTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    private static BundleKey K(string raw) => BundleKey.Parse(raw);

    [Fact]
    public void The_filesystem_provider_completes_every_task_over_a_load_a_write_a_regeneration_and_a_conflict()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/seed.md", $"---\n{Fm}\n---\n\nseed\n");
        var tracked = new CompletionTrackingProvider(new FileSystemBundleProvider(tmp.Path));

        _ = Bundle.Load(tracked);
        var writer = new BundleConceptWriter(tracked);
        Assert.DoesNotContain("Error", writer.WriteConcept("notes/new", Fm, "body\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Error", writer.AppendToConceptAtomic("notes/seed", Fm, b => (b ?? string.Empty) + "more\n"), StringComparison.Ordinal);
        IndexGenerator.RegenerateIndexes(tracked);

        // A provoked conflict: a stale IfMatch write is refused, and the refusal is a completed
        // (faulted) task too, not a pending one.
        var stale = new BundleObjectVersion("not-the-current-version");
        var refused = tracked.WriteAsync(K("notes/seed.md"), "x"u8.ToArray(), BundleWriteCondition.IfMatch(stale), CancellationToken.None);
        Assert.True(refused.IsCompleted);
        Assert.True(refused.IsFaulted);
        Assert.IsType<BundleWriteConflictException>(refused.AsTask().Exception!.InnerException);

        Assert.True(tracked.Observed > 10, "the run should have exercised the provider");
        Assert.Empty(tracked.Incomplete);
        Assert.True(tracked.Capabilities.CompletesSynchronously);
    }

    [Fact]
    public void A_listing_that_runs_past_the_entry_bound_stops_there_and_is_disposed()
    {
        var disposed = false;
        var produced = 0;
        var provider = new EndlessListing(() => produced++, () => disposed = true);

        var failure = Assert.Throws<BundleLoadException>(() => Bundle.Load(provider, new BundleLoadOptions { Limits = new BundleLimits { MaxEntries = 5 } }));

        Assert.IsType<BundleLimitExceededException>(failure.InnerException);
        Assert.InRange(produced, 5, 7); // MaxEntries + 1 names were taken, then the enumeration was abandoned
        Assert.True(disposed, "the enumerator was not disposed");
    }

    [Fact]
    public async Task The_read_only_decorator_forwards_the_callers_token_and_reports_the_inners_completion()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("a.md", $"---\n{Fm}\n---\n\nA\n");
        var recording = new TokenRecordingProvider(inner);
        var readOnly = recording.AsReadOnly();
        using var cts = new CancellationTokenSource();

        using var obj = await readOnly.ReadAsync(K("a.md"), null, cts.Token);

        Assert.Equal(cts.Token, recording.LastToken);
        Assert.False(readOnly.Capabilities.CanWrite);
        Assert.True(readOnly.Capabilities.CompletesSynchronously);
        var refused = readOnly.WriteAsync(K("a.md"), "x"u8.ToArray(), BundleWriteCondition.None, cts.Token);
        Assert.True(refused.IsFaulted);
        Assert.IsType<BundleReadOnlyException>(refused.AsTask().Exception!.InnerException);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_the_operation_through_the_task()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", $"---\n{Fm}\n---\n\nA\n");
        var provider = new FileSystemBundleProvider(tmp.Path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var read = provider.ReadAsync(K("a.md"), null, cts.Token);
        Assert.True(read.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.EnumerateAsync(null, BundleLimits.Default, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task A_listing_honours_both_the_token_it_was_given_and_the_one_of_its_enumerator()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", $"---\n{Fm}\n---\n\nA\n");
        var provider = new FileSystemBundleProvider(tmp.Path);
        using var given = new CancellationTokenSource();
        using var enumerator = new CancellationTokenSource();
        await given.CancelAsync();

        // The enumerator's own token is alive; the token EnumerateAsync was given is not: the listing must still stop.
        await using var e = provider.EnumerateAsync(null, BundleLimits.Default, given.Token).GetAsyncEnumerator(enumerator.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await e.MoveNextAsync());
    }

    private sealed class EndlessListing(Action onProduced, Action onDisposed) : SyncTestProvider
    {
        public override string Id => "endless";

        public override string Location => "memory://endless";

        public override BundleProviderCapabilities Capabilities => new(CanWrite: false, CompletesSynchronously: true);

        public override BundleKeyConstraints NativeConstraints => BundleKeyConstraints.None;

        protected override BundleVersion GetVersionSync(BundleLimits limits) => new("v");

        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    onProduced();
                    yield return new BundleListing($"n{i}.md", 1, null);
                }
            }
            finally
            {
                onDisposed();
            }
        }

        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => throw new BundleObjectNotFoundException(key);

        protected override bool ExistsSync(BundleKey key) => false;

        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => throw new NotSupportedException();

        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => throw new NotSupportedException();
    }

    private sealed class TokenRecordingProvider(IBundleProvider inner) : IBundleProvider
    {
        public CancellationToken LastToken { get; private set; }

        public string Id => inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            LastToken = ct;
            return inner.ReadAsync(key, expected, ct);
        }

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
            inner.WriteAsync(key, content, condition, ct);

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }
}

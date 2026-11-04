// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests;

/// <summary>
/// A decorator that records whether every <see cref="ValueTask"/> the inner provider returned was
/// already complete when it came back, listing steps and disposals included (#152): the proof
/// that a provider declaring <see cref="BundleProviderCapabilities.CompletesSynchronously"/>
/// does so for the whole of an operation, retries and conflicts included, not just for the
/// happy path.
/// </summary>
public sealed class CompletionTrackingProvider(IBundleProvider inner) : IBundleProvider
{
    private readonly List<string> _incomplete = [];

    /// <summary>How many tasks were observed.</summary>
    public int Observed { get; private set; }

    /// <summary>The operations whose task was not complete when it was returned.</summary>
    public IReadOnlyList<string> Incomplete => _incomplete;

    public string Id => inner.Id;

    public string Location => inner.Location;

    public BundleProviderCapabilities Capabilities => inner.Capabilities;

    public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => Track(nameof(GetVersionAsync), inner.GetVersionAsync(limits, ct));

    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) =>
        new TrackedListing(this, inner.EnumerateAsync(prefix, limits, ct));

    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => Track(nameof(ReadAsync), inner.ReadAsync(key, expected, ct));

    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => Track(nameof(ExistsAsync), inner.ExistsAsync(key, ct));

    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        Track(nameof(WriteAsync), inner.WriteAsync(key, content, condition, ct));

    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        var task = inner.DeleteAsync(key, condition, ct);
        Record(nameof(DeleteAsync), task.IsCompleted);
        return task;
    }

    private ValueTask<T> Track<T>(string operation, ValueTask<T> task)
    {
        Record(operation, task.IsCompleted);
        return task;
    }

    private void Record(string operation, bool completed)
    {
        Observed++;
        if (!completed)
        {
            _incomplete.Add(operation);
        }
    }

    private sealed class TrackedListing(CompletionTrackingProvider owner, IAsyncEnumerable<BundleListing> source) : IAsyncEnumerable<BundleListing>
    {
        public IAsyncEnumerator<BundleListing> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new TrackedEnumerator(owner, source.GetAsyncEnumerator(cancellationToken));
    }

    private sealed class TrackedEnumerator(CompletionTrackingProvider owner, IAsyncEnumerator<BundleListing> inner) : IAsyncEnumerator<BundleListing>
    {
        public BundleListing Current => inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            var task = inner.MoveNextAsync();
            owner.Record("EnumerateAsync.MoveNextAsync", task.IsCompleted);
            return task;
        }

        public ValueTask DisposeAsync()
        {
            var task = inner.DisposeAsync();
            owner.Record("EnumerateAsync.DisposeAsync", task.IsCompleted);
            return task;
        }
    }
}

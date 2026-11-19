// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests;

/// <summary>
/// The base of every in-test provider that stays synchronous inside (#152): the test double writes
/// its six operations as plain synchronous <c>…Sync</c> methods, and this class completes the
/// asynchronous contract over them with already-completed <see cref="ValueTask"/>s, failures
/// delivered through the task, exactly as the filesystem provider does. A test that needs a
/// provider that really completes later writes its own (<see cref="CompletionTrackingProvider"/>
/// observes completion; the bridge tests use a task-source-driven one).
/// </summary>
public abstract class SyncTestProvider : IBundleProvider
{
    public abstract string Id { get; }

    public abstract string Location { get; }

    public abstract BundleProviderCapabilities Capabilities { get; }

    public abstract BundleKeyConstraints NativeConstraints { get; }

    protected abstract BundleVersion GetVersionSync(BundleLimits limits);

    protected abstract IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits);

    protected abstract BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected);

    protected abstract bool ExistsSync(BundleKey key);

    protected abstract BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    protected abstract void DeleteSync(BundleKey key, BundleWriteCondition condition);

    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => Complete(() => GetVersionSync(limits), ct);

    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) =>
        SyncAsyncEnumerable.Create(() => EnumerateSync(prefix, limits), ct);

    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => Complete(() => ReadSync(key, expected), ct);

    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => Complete(() => ExistsSync(key), ct);

    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        Complete(() => WriteSync(key, content, condition), ct);

    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            DeleteSync(key, condition);
            return ValueTask.CompletedTask;
        }
        catch (Exception e)
        {
            return ValueTask.FromException(e);
        }
    }

    private static ValueTask<T> Complete<T>(Func<T> operation, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(operation());
        }
        catch (Exception e)
        {
            return ValueTask.FromException<T>(e);
        }
    }
}

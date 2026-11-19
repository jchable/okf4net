// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// A provider whose every operation stays pending until the test releases <see cref="Gate"/>
/// (#152): the doubles of the single-flight, cancellation and bridge tests. It does not declare
/// <see cref="BundleProviderCapabilities.CompletesSynchronously"/>, so the synchronous surface
/// refuses it. Calls and abandoned enumerations are counted, so a test can say "exactly one
/// listing" or "the enumerator was disposed" without a clock.
/// </summary>
public sealed class GatedProvider(InMemoryBundleProvider store, bool canWrite = false) : IBundleProvider
{
    private int _calls;
    private int _enumerations;
    private int _disposedEnumerations;
    private int _versions;

    /// <summary>Released by the test to let every pending (and later) operation proceed.</summary>
    public TaskCompletionSource Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Closes the gate again after it was released: the operations that follow stay pending until the next release. Operations already waiting on the previous gate are unaffected.</summary>
    public void Close() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called, with the number of names produced so far, after each listed name: a test cancels or fails here.</summary>
    public Action<int>? AfterListedName { get; set; }

    /// <summary>Every operation that reached the provider.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The listings started.</summary>
    public int Enumerations => Volatile.Read(ref _enumerations);

    /// <summary>The listings whose enumerator was disposed.</summary>
    public int DisposedEnumerations => Volatile.Read(ref _disposedEnumerations);

    /// <summary>The token comparisons made.</summary>
    public int VersionReads => Volatile.Read(ref _versions);

    public string Id => "gated";

    public string Location => "memory://gated";

    public BundleProviderCapabilities Capabilities => new(canWrite, CompletesSynchronously: false);

    public BundleKeyConstraints NativeConstraints => store.NativeConstraints;

    public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Increment(ref _versions);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return await store.GetVersionAsync(limits, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Increment(ref _enumerations);
        try
        {
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            var produced = 0;
            await foreach (var listing in store.EnumerateAsync(prefix, limits, ct).ConfigureAwait(false))
            {
                yield return listing;
                AfterListedName?.Invoke(++produced);
            }
        }
        finally
        {
            Interlocked.Increment(ref _disposedEnumerations);
        }
    }

    public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return await store.ReadAsync(key, expected, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return await store.ExistsAsync(key, ct).ConfigureAwait(false);
    }

    public async ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return await store.WriteAsync(key, content, condition, ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        await store.DeleteAsync(key, condition, ct).ConfigureAwait(false);
    }
}

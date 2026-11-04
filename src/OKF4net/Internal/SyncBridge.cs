// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net;

/// <summary>
/// The synchronous way into the asynchronous provider contract (#152): it asks an operation
/// that must already be complete for its result, and <b>never waits</b>. A task still pending
/// when the call returns is an <see cref="InvalidOperationException"/> naming the async
/// overload, so the bridge can neither block nor deadlock, under any synchronization
/// context. It serves the filesystem provider (whose operations complete before they
/// return) and any provider declaring
/// <see cref="BundleProviderCapabilities.CompletesSynchronously"/>.
/// </summary>
internal static class SyncBridge
{
    /// <summary>The message of the exception thrown when a bridged operation did not complete.</summary>
    internal const string IncompleteMessage = "provider completed asynchronously; use the async overload";

    /// <summary>The result of <paramref name="task"/>, which must already be complete.</summary>
    /// <exception cref="InvalidOperationException">The task is still pending.</exception>
    internal static T Run<T>(ValueTask<T> task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException(IncompleteMessage);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts <paramref name="start"/> with a token this call owns and returns its result, which
    /// must be complete when it returns. A pending operation is cancelled through that token and
    /// abandoned, and the call throws <see cref="InvalidOperationException"/>: for the reads that
    /// have no side effect to protect, completion is checked after the start, and what was
    /// started stops as soon as the provider next looks at its token.
    /// </summary>
    /// <exception cref="InvalidOperationException">The operation was still pending.</exception>
    internal static T RunCancellable<T>(Func<CancellationToken, ValueTask<T>> start)
    {
        using var cts = new CancellationTokenSource();
        var task = start(cts.Token);
        if (!task.IsCompleted)
        {
            cts.Cancel();
            throw new InvalidOperationException(IncompleteMessage);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// For an operation that WRITES: the provider must declare
    /// <see cref="BundleProviderCapabilities.CompletesSynchronously"/>, or the call is refused
    /// before any I/O starts. Checking completion afterwards would be too late: the abandoned
    /// operation would commit when it resumed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider does not complete synchronously; nothing was started.</exception>
    internal static void RequireSynchronousProvider(IBundleProvider provider)
    {
        if (!provider.Capabilities.CompletesSynchronously)
        {
            throw new InvalidOperationException(IncompleteMessage);
        }
    }

    /// <summary>Observes <paramref name="task"/>, which must already be complete.</summary>
    /// <exception cref="InvalidOperationException">The task is still pending.</exception>
    internal static void Run(ValueTask task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException(IncompleteMessage);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// <paramref name="source"/> as a synchronous sequence, every step of which must already
    /// be complete. The enumerator is disposed (its disposal must be complete too) when the
    /// sequence ends, fails or is abandoned.
    /// </summary>
    internal static IEnumerable<T> Enumerate<T>(IAsyncEnumerable<T> source)
    {
        var enumerator = source.GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (true)
            {
                if (!Run(enumerator.MoveNextAsync()))
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            Run(enumerator.DisposeAsync());
        }
    }
}

/// <summary>
/// The synchronous spelling of <see cref="IBundleProvider"/>, for the code that has not become
/// asynchronous yet (#152, temporary: each use goes away with its own caller's async form).
/// Each method is <see cref="SyncBridge"/> over the async member, with
/// <see cref="CancellationToken.None"/>.
/// </summary>
internal static class SyncProviderExtensions
{
    internal static BundleVersion GetVersion(this IBundleProvider provider, BundleLimits limits) =>
        SyncBridge.Run(provider.GetVersionAsync(limits, CancellationToken.None));

    internal static IEnumerable<BundleListing> Enumerate(this IBundleProvider provider, BundleKey? prefix, BundleLimits limits) =>
        SyncBridge.Enumerate(provider.EnumerateAsync(prefix, limits, CancellationToken.None));

    internal static BundleObject Read(this IBundleProvider provider, BundleKey key, BundleObjectVersion? expected = null) =>
        SyncBridge.Run(provider.ReadAsync(key, expected, CancellationToken.None));

    internal static bool Exists(this IBundleProvider provider, BundleKey key) =>
        SyncBridge.Run(provider.ExistsAsync(key, CancellationToken.None));

    internal static BundleObjectVersion Write(this IBundleProvider provider, BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        SyncBridge.Run(provider.WriteAsync(key, content, condition, CancellationToken.None));

    internal static void Delete(this IBundleProvider provider, BundleKey key, BundleWriteCondition condition) =>
        SyncBridge.Run(provider.DeleteAsync(key, condition, CancellationToken.None));
}

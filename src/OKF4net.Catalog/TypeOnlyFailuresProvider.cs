// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// A host's provider as <see cref="FileMemoryStore"/> sees it: every operation forwarded
/// unchanged, and every failure it raises that the library did not author replaced by an
/// <see cref="IOException"/> whose message names the failure's type only (spec §7). The
/// library's own exceptions (the typed <see cref="BundleProviderException"/>s, whose messages
/// it builds from their fields) pass through, as do a cancellation of the caller's token and
/// <see cref="OutOfMemoryException"/>. Without this, the core writer, which reports an
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> by its message, and the
/// store's deletion, which reports a <see cref="BundleProviderException"/> by its message, would
/// hand a host provider's own text (a connection string, a path) to whoever reads the result:
/// a host can throw either, or derive its own <see cref="BundleProviderException"/>.
/// </summary>
/// <remarks>Only a host's provider is wrapped: a <see cref="FileSystemBundleProvider"/> is the
/// library's own, and wrapping it would change the writer's lock key (derived from its root),
/// which the writer shares with every other writer of that directory.</remarks>
internal sealed class TypeOnlyFailuresProvider(IBundleProvider inner) : IBundleProvider, IProviderScope
{
    /// <summary>The provider this one wraps.</summary>
    internal IBundleProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public string Id => Inner.Id;

    /// <inheritdoc/>
    public string Location => Inner.Location;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => Inner.Capabilities;

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => Inner.NativeConstraints;

    /// <summary>The inner provider's scope, when it has one.</summary>
    IDisposable IProviderScope.BeginScope() => ProviderScopes.Begin(Inner) ?? ProviderScopes.None;

    /// <inheritdoc/>
    public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        try
        {
            return await Inner.GetVersionAsync(limits, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<BundleListing> enumerator;
        try
        {
            enumerator = Inner.EnumerateAsync(prefix, limits, ct).GetAsyncEnumerator(ct);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }

        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (Replacement(e, ct) is { } replacement)
                {
                    throw replacement;
                }

                if (!more)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        try
        {
            return await Inner.ReadAsync(key, expected, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        try
        {
            return await Inner.ExistsAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
    {
        try
        {
            return await Inner.WriteAsync(key, content, condition, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        try
        {
            await Inner.DeleteAsync(key, condition, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Replacement(e, ct) is { } replacement)
        {
            throw replacement;
        }
    }

    /// <summary>
    /// The exception to throw instead of <paramref name="e"/>, or <see langword="null"/> to let it
    /// through: the library's own exceptions, a cancellation of <paramref name="ct"/> and
    /// <see cref="OutOfMemoryException"/> pass; anything else becomes an <see cref="IOException"/>
    /// naming its type, with the original as its inner exception for the host's log.
    /// </summary>
    private static HostProviderFailureException? Replacement(Exception e, CancellationToken ct) =>
        e is OutOfMemoryException
        || (e is OperationCanceledException && ct.IsCancellationRequested)
        || e.GetType().Assembly == typeof(BundleProviderException).Assembly
            ? null
            : new HostProviderFailureException(e);
}

/// <summary>
/// A failure of a host's provider, as <see cref="TypeOnlyFailuresProvider"/> rethrows it: an
/// <see cref="IOException"/> (so the core writer reports it like any I/O failure) whose message
/// names the original's type only, the original kept as the inner exception for the host's log.
/// </summary>
internal sealed class HostProviderFailureException(Exception inner)
    : IOException($"the memory tier's provider failed ({inner.GetType().Name})", inner)
{
    /// <summary>The name of the original failure's type, which the memory store reports in its place.</summary>
    internal string FailureType { get; } = inner.GetType().Name;
}

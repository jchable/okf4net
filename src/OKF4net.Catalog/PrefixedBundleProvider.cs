// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// The view of one prefix of another provider's key space as a bundle of its own: key
/// <c>k</c> of the view is key <c>prefix/k</c> of the inner provider, and the view lists only
/// what lies strictly under <c>prefix/</c>, with that prefix removed. This is how
/// <see cref="FileMemoryStore"/> loads one memory scope from a tier provider a host handed it
/// (spec §9), since the provider seam has no way to re-root an arbitrary provider. Every access
/// goes through the inner provider on the full key, so whatever guards it applies to a key's
/// path (a boundary, a link refusal) still run, on the whole path from its own root.
/// </summary>
/// <remarks>
/// <para><see cref="IBundleProvider.GetVersionAsync"/> is the inner provider's token for its whole key space: a
/// change anywhere in it changes the view's token, so the token can change when the scope did
/// not, never the reverse. A key whose full form is no valid key of the inner provider (over
/// <see cref="BundleKey.MaxUtf8Bytes"/>) holds nothing there: it is read as absent, and a write
/// to it is refused as <see cref="BundleWriteConflictReason.NameUnavailable"/>.</para>
/// </remarks>
internal sealed class PrefixedBundleProvider : IBundleProvider, IProviderScope
{
    private readonly IBundleProvider _inner;
    private readonly BundleKey _prefix;
    private readonly string _strip;

    /// <summary>The view of <paramref name="prefix"/> in <paramref name="inner"/>.</summary>
    internal PrefixedBundleProvider(IBundleProvider inner, BundleKey prefix)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        _strip = prefix + "/";
    }

    /// <inheritdoc/>
    public string Id => _inner.Id + "/" + _prefix;

    /// <summary>The inner provider's location followed by the prefix's segments, by the display rule (<see cref="BundleDisplayPath"/>).</summary>
    public string Location => BundleDisplayPath.Compose(_inner.Location, _prefix);

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => _inner.NativeConstraints;

    /// <summary>The inner provider's scope, when it has one.</summary>
    IDisposable IProviderScope.BeginScope() => ProviderScopes.Begin(_inner) ?? ProviderScopes.None;

    /// <inheritdoc/>
    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => _inner.GetVersionAsync(limits, ct);

    /// <inheritdoc/>
    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct)
    {
        BundleKey start;
        if (prefix is null)
        {
            start = _prefix;
        }
        else if (Full(prefix) is { } full)
        {
            start = full;
        }
        else
        {
            return SyncAsyncEnumerable.Create<BundleListing>(static () => [], ct);
        }

        return Strip(_inner.EnumerateAsync(start, limits, ct), ct);
    }

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) =>
        Full(key) is { } full
            ? _inner.ReadAsync(full, expected, ct)
            : ValueTask.FromException<BundleObject>(new BundleObjectNotFoundException(key));

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) =>
        Full(key) is { } full ? _inner.ExistsAsync(full, ct) : ValueTask.FromResult(false);

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        Full(key) is { } full
            ? _inner.WriteAsync(full, content, condition, ct)
            : ValueTask.FromException<BundleObjectVersion>(new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        if (Full(key) is { } full)
        {
            return _inner.DeleteAsync(full, condition, ct);
        }

        // Nothing exists there, so no version can match.
        return condition.Kind == BundleWriteConditionKind.IfMatch
            ? ValueTask.FromException(new BundleWriteConflictException(key))
            : ValueTask.CompletedTask;
    }

    /// <summary>The inner key of <paramref name="key"/>, or <see langword="null"/> when the joined name is no valid key.</summary>
    private BundleKey? Full(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return BundleKey.TryParse(_strip + key, out var full, out _) ? full : null;
    }

    private async IAsyncEnumerable<BundleListing> Strip(IAsyncEnumerable<BundleListing> listings, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var listing in listings.WithCancellation(ct).ConfigureAwait(false))
        {
            // An object under the prefix key itself, or a name a buggy provider listed outside it, is not in the view.
            if (listing.RawName.Length > _strip.Length && listing.RawName.StartsWith(_strip, StringComparison.Ordinal))
            {
                yield return listing with { RawName = listing.RawName[_strip.Length..] };
            }
        }
    }
}

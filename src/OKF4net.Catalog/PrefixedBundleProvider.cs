// SPDX-License-Identifier: LGPL-3.0-or-later
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
/// <para><see cref="GetVersion"/> is the inner provider's token for its whole key space: a
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
    public BundleVersion GetVersion(BundleLimits limits) => _inner.GetVersion(limits);

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
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
            return [];
        }

        // Called here, not inside the iterator, so a refusal surfaces at the call, as the inner provider's would.
        return Strip(_inner.Enumerate(start, limits));
    }

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) =>
        _inner.Read(Full(key) ?? throw new BundleObjectNotFoundException(key), expected);

    /// <inheritdoc/>
    public bool Exists(BundleKey key) => Full(key) is { } full && _inner.Exists(full);

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        _inner.Write(Full(key) ?? throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable), content, condition);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition)
    {
        if (Full(key) is { } full)
        {
            _inner.Delete(full, condition);
        }
        else if (condition.Kind == BundleWriteConditionKind.IfMatch)
        {
            throw new BundleWriteConflictException(key); // nothing exists there, so no version can match
        }
    }

    /// <summary>The inner key of <paramref name="key"/>, or <see langword="null"/> when the joined name is no valid key.</summary>
    private BundleKey? Full(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return BundleKey.TryParse(_strip + key, out var full, out _) ? full : null;
    }

    private IEnumerable<BundleListing> Strip(IEnumerable<BundleListing> listings)
    {
        foreach (var listing in listings)
        {
            // An object under the prefix key itself, or a name a buggy provider listed outside it, is not in the view.
            if (listing.RawName.Length > _strip.Length && listing.RawName.StartsWith(_strip, StringComparison.Ordinal))
            {
                yield return listing with { RawName = listing.RawName[_strip.Length..] };
            }
        }
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
/// <para><see cref="GetVersionAsync"/> is a token for the view alone, built from its own listing
/// and objects, never a walk of the inner provider's whole key space. A key whose full form is
/// no valid key of the inner provider (over <see cref="BundleKey.MaxUtf8Bytes"/>) holds nothing
/// there: it is read as absent, and a write to it is refused as
/// <see cref="BundleWriteConflictReason.NameUnavailable"/>. Such a name is still listed: the view
/// declares the bytes its prefix takes (<see cref="IReservedKeyBytes"/>), so a load counts it as
/// over-long, exactly as a name past the bound in an ordinary bundle (never read, a parse
/// problem for a concept, its ancestors still seen by the tree validation).</para>
/// </remarks>
internal sealed class PrefixedBundleProvider : IBundleProvider, IProviderScope, IReservedKeyBytes
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

    /// <summary>The prefix and its separator: what a key of the view takes of <see cref="BundleKey.MaxUtf8Bytes"/> in the inner provider.</summary>
    int IReservedKeyBytes.ReservedKeyBytes => Encoding.UTF8.GetByteCount(_strip);

    /// <summary>
    /// A token for the view alone, so that loading one scope never walks the rest of the inner
    /// provider: a directory of another scope that cannot be listed, or that breaks a bound, does
    /// not make this one unavailable, and a read of one scope does not cost a walk of every
    /// scope. It is built from the view's listing (under the core's own bound on entries) with
    /// each object's length and version. A provider that cannot give a version for free in its
    /// listing (the filesystem) has each object opened for it, so the token changes whenever an
    /// object of the view does, a same-length edit included, at the price of one read per object;
    /// a memory scope is small. An object that cannot be read for its version (gone since the
    /// listing, too large, refused) enters the token as that failure's type, and the load that
    /// follows decides what it means.
    /// </summary>
    public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var entries = new List<string>();
        var count = 0;
        await foreach (var listing in EnumerateAsync(null, limits, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            // Every field is length-prefixed: a version token is an opaque string and can hold
            // any separator, so plain delimiters would let one entry spell two (or two spell one).
            var version = await VersionOfAsync(listing, ct).ConfigureAwait(false);
            entries.Add($"{Field(listing.RawName)}{Field(listing.Length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")}{Field(version)}");
        }

        entries.Sort(StringComparer.Ordinal);
        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))));
    }

    /// <summary>The version of one listed object of the view: the listing's when it has one, else read from the inner provider.</summary>
    private async ValueTask<string> VersionOfAsync(BundleListing listing, CancellationToken ct)
    {
        if (listing.Version is { } listed)
        {
            return listed.Token;
        }

        if (!BundleKey.TryParse(listing.RawName, out var key, out _) || Full(key) is not { } full)
        {
            return "!key";
        }

        try
        {
            var read = await _inner.ReadAsync(full, null, ct).ConfigureAwait(false);
            await using (read.ConfigureAwait(false))
            {
                return read.Version.Token;
            }
        }
        catch (Exception e) when (e is BundleObjectNotFoundException or BundleObjectTooLargeException or BundleAccessRefusedException or IOException or UnauthorizedAccessException)
        {
            return "!" + e.GetType().Name;
        }
    }

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

        return Strip(_inner.EnumerateAsync(start, limits, ct), limits, ct);
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

    /// <summary>
    /// The inner listing seen through the view. Every raw entry the inner provider produces counts
    /// against <see cref="BundleLimits.MaxEntries"/>, including those the view then drops: a
    /// provider that ignores the bound and lists names outside the prefix cannot keep a listing
    /// running past it, since a consumer counts only what reaches it.
    /// </summary>
    private async IAsyncEnumerable<BundleListing> Strip(IAsyncEnumerable<BundleListing> listings, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        await foreach (var listing in listings.WithCancellation(ct).ConfigureAwait(false))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            // An object under the prefix key itself, or a name a buggy provider listed outside it, is not in the view.
            if (!(listing.RawName.Length > _strip.Length && listing.RawName.StartsWith(_strip, StringComparison.Ordinal)))
            {
                continue;
            }

            // A name too long to be a key of the inner provider is still listed: the view's
            // IReservedKeyBytes makes a load count it as over-long, never read it.
            yield return listing with { RawName = listing.RawName[_strip.Length..] };
        }
    }

    /// <summary>One field of a token entry, length-prefixed so no content can be read as a separator.</summary>
    private static string Field(string value) => $"{value.Length}:{value}";
}

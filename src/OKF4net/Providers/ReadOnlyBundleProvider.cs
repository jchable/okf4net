// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Downgrades a provider to read-only (spec §3): every read delegates, every write throws <see cref="BundleReadOnlyException"/> before reaching the inner provider.</summary>
public sealed class ReadOnlyBundleProvider : IBundleProvider
{
    private readonly IBundleProvider _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public ReadOnlyBundleProvider(IBundleProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public string Id => _inner.Id;

    /// <inheritdoc/>
    public string Location => _inner.Location;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => _inner.NativeConstraints;

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits) => _inner.GetVersion(limits);

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => _inner.Enumerate(prefix, limits);

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => _inner.Read(key, expected);

    /// <inheritdoc/>
    public bool Exists(BundleKey key) => _inner.Exists(key);

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);
}

/// <summary>Extension methods on <see cref="IBundleProvider"/>.</summary>
public static class BundleProviderExtensions
{
    /// <summary>Returns a read-only view of <paramref name="provider"/>; a provider that already is one is returned as is. A host can downgrade, never upgrade.</summary>
    public static IBundleProvider AsReadOnly(this IBundleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider is ReadOnlyBundleProvider ? provider : new ReadOnlyBundleProvider(provider);
    }
}

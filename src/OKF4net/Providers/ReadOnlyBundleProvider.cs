// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Downgrades a provider to read-only (spec §3): every read delegates, every write throws <see cref="BundleReadOnlyException"/> before reaching the inner provider.</summary>
public sealed class ReadOnlyBundleProvider : IBundleProvider, IProviderScope
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
    public BundleProviderCapabilities Capabilities => new(CanWrite: false, CompletesSynchronously: _inner.Capabilities.CompletesSynchronously);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => _inner.NativeConstraints;

    /// <inheritdoc/>
    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => _inner.GetVersionAsync(limits, ct);

    /// <inheritdoc/>
    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => _inner.EnumerateAsync(prefix, limits, ct);

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => _inner.ReadAsync(key, expected, ct);

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => _inner.ExistsAsync(key, ct);

    /// <summary>The inner provider's scope, when it has one: the downgrade changes what can be written, never what is read.</summary>
    IDisposable IProviderScope.BeginScope() => (_inner as IProviderScope)?.BeginScope() ?? ProviderScopes.None;

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));
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

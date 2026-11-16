// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>Delegates reads to <paramref name="inner"/> and captures writes in memory: IndexGenerator has no public render-without-writing API (spec §6.3), and no fixture may be written.</summary>
internal sealed class CapturingProvider(IBundleProvider inner) : IBundleProvider
{
    private readonly SortedDictionary<string, byte[]> _written = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Written => _written;

    public string Id => "capture:" + inner.Id;

    public string Location => inner.Location;

    public BundleProviderCapabilities Capabilities => new(CanWrite: true, CompletesSynchronously: true);

    public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);

    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
    {
        _written[key.ToString()] = content.ToArray();
        return ValueTask.FromResult(new BundleObjectVersion("captured:" + _written.Count));
    }

    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        _written[key.ToString()] = [];
        return ValueTask.CompletedTask;
    }
}

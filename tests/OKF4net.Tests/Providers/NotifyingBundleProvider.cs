// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A provider that can raise change signals (spec §5.3, <see cref="IBundleChangeNotifier"/>),
/// over any inner provider: <see cref="Signal"/> invokes every subscriber, as a watcher would.
/// Shared by the handle tests and the agent tool tests that drive the <c>Notify</c> policy.
/// </summary>
public sealed class NotifyingBundleProvider(IBundleProvider inner) : SyncTestProvider, IBundleChangeNotifier
{
    private readonly List<Action> _subscribers = [];

    /// <summary>The number of live subscriptions.</summary>
    public int Subscribers => _subscribers.Count;

    /// <summary>Raises a change signal to every subscriber.</summary>
    public void Signal() => _subscribers.ToList().ForEach(s => s());

    /// <inheritdoc/>
    public IDisposable Subscribe(Action onChange)
    {
        _subscribers.Add(onChange);
        return new Subscription(() => _subscribers.Remove(onChange));
    }

    public override string Id => inner.Id;
    public override string Location => inner.Location;
    public override BundleProviderCapabilities Capabilities => inner.Capabilities;
    public override BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
    protected override BundleVersion GetVersionSync(BundleLimits limits) => inner.GetVersion(limits);
    protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);
    protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => inner.Read(key, expected);
    protected override bool ExistsSync(BundleKey key) => inner.Exists(key);
    protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
    protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

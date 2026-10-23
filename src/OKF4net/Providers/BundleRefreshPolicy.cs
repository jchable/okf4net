// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>How a <see cref="BundleHandle"/> decides to reload its image (spec §5.3).</summary>
public enum BundleRefreshMode
{
    /// <summary>The host calls <see cref="BundleHandle.Refresh"/>; nothing else reloads (the general default).</summary>
    Manual,

    /// <summary>The provider's version token is compared with the image's before every use; a different token reloads.</summary>
    OnAccess,

    /// <summary>As <see cref="OnAccess"/>, but the token is compared at most once per <see cref="BundleRefreshPolicy.Interval"/>, at an access.</summary>
    Interval,

    /// <summary>A signal from the provider's <see cref="IBundleChangeNotifier"/> marks the image "to check"; the token is compared at the next access. A signal is a hint, never a reload by itself.</summary>
    Notify,
}

/// <summary>
/// The refresh policy of a <see cref="BundleHandle"/> (spec §5.3). Every policy is lazy: the
/// core runs no timer and no background thread, and a reload only ever happens inside an
/// access to <see cref="BundleHandle.Current"/> or a call to <see cref="BundleHandle.Refresh"/>.
/// Two events reload whatever the policy (spec §5.1, §5.2): the handle's own writes (a
/// <see cref="BundleConceptWriter"/> built over it), a host calling <see cref="BundleHandle.Invalidate"/> for another reason (for instance on a write conflict or a refused pin of
/// sanctioned code) — each through <see cref="BundleHandle.Invalidate"/>.
/// </summary>
public sealed record BundleRefreshPolicy
{
    private BundleRefreshPolicy(BundleRefreshMode mode, TimeSpan interval)
    {
        Mode = mode;
        Interval = interval;
    }

    /// <summary>The host reloads by calling <see cref="BundleHandle.Refresh"/>; the general default.</summary>
    public static BundleRefreshPolicy Manual { get; } = new(BundleRefreshMode.Manual, TimeSpan.Zero);

    /// <summary>The version token is compared before every use of the image.</summary>
    public static BundleRefreshPolicy OnAccess { get; } = new(BundleRefreshMode.OnAccess, TimeSpan.Zero);

    /// <summary>The version token is compared at most every <paramref name="every"/>, at an access.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="every"/> is not positive.</exception>
    public static BundleRefreshPolicy Every(TimeSpan every)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero);
        return new(BundleRefreshMode.Interval, every);
    }

    /// <summary>A change signal from the provider (<see cref="IBundleChangeNotifier"/>) makes the next access compare the token. Only for a provider that implements <see cref="IBundleChangeNotifier"/>.</summary>
    public static BundleRefreshPolicy Notify { get; } = new(BundleRefreshMode.Notify, TimeSpan.Zero);

    /// <summary>The mode.</summary>
    public BundleRefreshMode Mode { get; }

    /// <summary>The minimum time between two token comparisons under <see cref="BundleRefreshMode.Interval"/>; <see cref="TimeSpan.Zero"/> otherwise.</summary>
    public TimeSpan Interval { get; }
}

/// <summary>
/// A provider that can signal that its storage may have changed (spec §5.3). A signal is a
/// hint: the handle compares the version token at its next access and reloads only when the
/// token differs — never blindly, since a watcher can lose events as well as raise spurious ones.
/// </summary>
public interface IBundleChangeNotifier
{
    /// <summary>Registers <paramref name="onChange"/>, invoked (on any thread) whenever the storage may have changed, until the returned subscription is disposed.</summary>
    IDisposable Subscribe(Action onChange);
}

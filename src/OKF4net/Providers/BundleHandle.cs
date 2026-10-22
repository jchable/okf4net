// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.ExceptionServices;

namespace OKF4net.Providers;

/// <summary>
/// The holder of one bundle's image (spec §5.2): a provider, the options every load uses, a
/// refresh policy, and the current <see cref="Bundle"/>. One object per source, meant to be
/// shared by every consumer of that source (the agent tools, a context provider, a catalog
/// source) so that they serve one image; the sharing is explicit — a consumer built from a
/// root string creates its own handle, and two such consumers have two images.
/// <para><b>One load at a time.</b> The token comparison of <see cref="BundleRefreshMode.OnAccess"/> and <see cref="BundleRefreshMode.Interval"/> also runs serially under the load lock (on the filesystem provider it is a walk), blocking concurrent callers. Concurrent callers wait for the load in progress instead of
/// each starting one; the image is replaced as a whole, so a call never sees two versions.</para>
/// <para><b>Failure.</b> When a reload fails (the storage is unreachable, a bound is exceeded) the
/// previous image stays served and <see cref="IsStale"/> says so; only the very first load throws from <see cref="Current"/> (<see cref="Refresh"/> also throws on a failed reload).</para>
/// <para><b>The reload obligation</b> (spec §5.1): <see cref="Invalidate"/> obliges the next access
/// to reload, whatever the policy, and the obligation is acknowledged only by a successful load
/// started after it — a load already running when it is made, or a load that fails, leaves it
/// pending. The obligation is a generation counter: every load records the generation it
/// started at, and an image whose generation is behind the required one is reloaded.</para>
/// <para><b>Not frozen</b> (spec §5.1, D3): the image is replaced as a whole, but its documents
/// are the mutable <see cref="Frontmatter"/> of today; modifying a document of a shared image is
/// not supported.</para>
/// </summary>
public sealed class BundleHandle : IDisposable
{
    private sealed record Image(Bundle Bundle, long Generation);

    private readonly Lock _loadLock = new();
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private Image? _image;
    private long _required;
    private bool _checkRequested;
    private long _lastCheck;
    private bool _stale;
    private Exception? _lastLoadError;

    /// <summary>Creates a handle over <paramref name="provider"/>. Nothing is loaded until <see cref="Current"/> or <see cref="Refresh"/> is used.</summary>
    /// <param name="provider">Where the bundle is read.</param>
    /// <param name="options">The options of every load (bounds, key policy); <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <param name="refreshPolicy">When the image is reloaded; <see langword="null"/>: <see cref="BundleRefreshPolicy.Manual"/>.</param>
    /// <param name="time">The clock of <see cref="BundleRefreshMode.Interval"/>; <see langword="null"/>: <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="refreshPolicy"/> is <see cref="BundleRefreshPolicy.Notify"/> and <paramref name="provider"/> is not an <see cref="IBundleChangeNotifier"/>: an explicit configuration error (spec §5.3).</exception>
    public BundleHandle(IBundleProvider provider, BundleLoadOptions? options = null, BundleRefreshPolicy? refreshPolicy = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        Options = options ?? BundleLoadOptions.Default;
        RefreshPolicy = refreshPolicy ?? BundleRefreshPolicy.Manual;
        _time = time ?? TimeProvider.System;
        if (RefreshPolicy.Mode == BundleRefreshMode.Notify)
        {
            if (provider is not IBundleChangeNotifier notifier)
            {
                throw new ArgumentException("the Notify refresh policy needs a provider that implements IBundleChangeNotifier", nameof(refreshPolicy));
            }

            _subscription = notifier.Subscribe(() => Volatile.Write(ref _checkRequested, true));
        }
    }

    /// <summary>Where the bundle is read.</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The options of every load.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>When the image is reloaded.</summary>
    public BundleRefreshPolicy RefreshPolicy { get; }

    /// <summary>The provider's displayable location.</summary>
    public string Location => Provider.Location;

    /// <summary>
    /// <see langword="true"/> when the image served is known to be behind: the last reload
    /// failed (<see cref="LastLoadError"/>), or an obligation to reload is pending. Reset by a
    /// successful load.
    /// </summary>
    public bool IsStale => Volatile.Read(ref _stale) || (_image is { } image && image.Generation < Volatile.Read(ref _required));

    /// <summary>The failure of the last load, or <see langword="null"/>: a reload that kept the previous image served (then <see cref="IsStale"/> is set), or a failed first load (then there is no image and <see cref="IsStale"/> stays <see langword="false"/>). Cleared by a successful load, or by a comparison that finds the image current.</summary>
    public Exception? LastLoadError => Volatile.Read(ref _lastLoadError);

    /// <summary>
    /// The image, loaded on first use and reloaded under <see cref="RefreshPolicy"/> and the
    /// reload obligation. When a reload fails the previous image is returned and
    /// <see cref="IsStale"/> is set.
    /// </summary>
    /// <exception cref="BundleLoadException">No image could ever be loaded (the first load failed).</exception>
    public Bundle Current
    {
        get
        {
            var image = Volatile.Read(ref _image);
            if (image is not null && RefreshPolicy.Mode == BundleRefreshMode.Manual && image.Generation >= Volatile.Read(ref _required))
            {
                return image.Bundle; // the fast path: nothing to compare under Manual
            }

            lock (_loadLock)
            {
                image = _image;
                if (image is not null && !NeedsReload(image))
                {
                    return image.Bundle;
                }

                return LoadLocked().Bundle;
            }
        }
    }

    /// <summary>Reloads now, whatever the policy, and returns the new image. A failure keeps the previous image served (<see cref="IsStale"/>) and is thrown.</summary>
    /// <exception cref="BundleLoadException">The load failed.</exception>
    public Bundle Refresh()
    {
        lock (_loadLock)
        {
            var image = LoadLocked();
            if (_lastLoadError is { } error)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return image.Bundle;
        }
    }

    /// <summary>
    /// Obliges the next use to reload (spec §5.1, §5.2): after this handle's own writes, or
    /// whenever a host calls it (for instance on a write conflict or a refused pin of sanctioned
    /// code). Never blocks and never throws; the
    /// obligation is acknowledged only by a successful load started after this call.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _required);

    /// <summary>
    /// Ends the subscription to the provider's change signals. A <see cref="BundleRefreshMode.Notify"/>
    /// handle should be disposed: the subscription closure captures the handle, so a long-lived
    /// notifier keeps an undisposed handle and its image alive. After disposal the handle still
    /// serves and reloads: <see cref="BundleRefreshMode.OnAccess"/> and <see cref="BundleRefreshMode.Interval"/>
    /// handles keep reloading by policy, and so does any handle on an obligation or a
    /// <see cref="Refresh"/>; only the signals of a <see cref="BundleRefreshMode.Notify"/> handle are lost.
    /// </summary>
    public void Dispose() => _subscription?.Dispose();

    private bool NeedsReload(Image image)
    {
        if (image.Generation < Volatile.Read(ref _required))
        {
            return true;
        }

        switch (RefreshPolicy.Mode)
        {
            case BundleRefreshMode.OnAccess:
                return CheckToken(image);
            case BundleRefreshMode.Interval:
                {
                    var now = _time.GetTimestamp();
                    if (_lastCheck != 0 && _time.GetElapsedTime(_lastCheck, now) < RefreshPolicy.Interval)
                    {
                        return false;
                    }

                    _lastCheck = now;
                    return CheckToken(image);
                }

            case BundleRefreshMode.Notify:
                if (!Volatile.Read(ref _checkRequested))
                {
                    return false;
                }

                Volatile.Write(ref _checkRequested, false);
                return CheckToken(image);
            default:
                return false;
        }
    }

    /// <summary>Compares the tokens; an unchanged one means the image served is in fact current, so a stale mark left by an earlier failure is cleared (an obligation is not: it is generation-based).</summary>
    private bool CheckToken(Image image)
    {
        if (TokenChanged(image))
        {
            return true;
        }

        Volatile.Write(ref _lastLoadError, null);
        Volatile.Write(ref _stale, false);
        return false;
    }

    /// <summary>A token that cannot be read counts as changed: the reload then fails the same way, and the image is marked stale.</summary>
    private bool TokenChanged(Image image)
    {
        try
        {
            return Provider.GetVersion(Options.Limits) != image.Bundle.Version;
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private Image LoadLocked()
    {
        var startedAt = Volatile.Read(ref _required);
        try
        {
            var bundle = Bundle.Load(Provider, Options);
            var image = new Image(bundle, startedAt);
            Volatile.Write(ref _image, image);
            Volatile.Write(ref _lastLoadError, null);
            Volatile.Write(ref _stale, false);
            _lastCheck = _time.GetTimestamp();
            return image;
        }
        catch (BundleLoadException e)
        {
            Volatile.Write(ref _lastLoadError, e);
            if (_image is not { } previous)
            {
                throw;
            }

            Volatile.Write(ref _stale, true);
            if (RefreshPolicy.Mode == BundleRefreshMode.Notify)
            {
                Volatile.Write(ref _checkRequested, true); // the signal was consumed by the comparison: re-arm it so the next access retries
            }

            return previous;
        }
    }
}

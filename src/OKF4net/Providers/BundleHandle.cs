// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.ExceptionServices;

namespace OKF4net.Providers;

/// <summary>
/// The holder of one bundle's image (spec §5.2): a provider, the options every load uses, a
/// refresh policy, and the current <see cref="Bundle"/>. One object per source, meant to be
/// shared by every consumer of that source (the agent tools, a context provider, a catalog
/// source) so that they serve one image; the sharing is explicit — a consumer built from a
/// root string creates its own handle, and two such consumers have two images.
/// <para><b>One load at a time, shared.</b> The token comparison of <see cref="BundleRefreshMode.OnAccess"/> and <see cref="BundleRefreshMode.Interval"/> and the load it may lead to are ONE operation, and there is at most one running per handle (#152, single flight): concurrent callers join the operation in progress instead of each starting one, so N callers share one comparison, and the image is replaced as a whole, so a call never sees two versions.
/// A caller joins only an operation that started at a generation at least its own required one (see the reload obligation): an access made after <see cref="Invalidate"/> waits for the older operation, then starts or joins one that covers it. The operation runs under no caller's token: a caller that cancels stops waiting (<see cref="OperationCanceledException"/>) and the others still get the image.</para>
/// <para><b>Failure.</b> When a reload fails (the storage is unreachable, a bound is exceeded) the
/// previous image stays served and <see cref="IsStale"/> says so; only the very first load throws from <see cref="GetCurrentAsync"/> (<see cref="RefreshAsync"/> also throws on a failed reload).
/// This holds for a raw exception from a host provider too (neither a provider, an I/O nor an access exception, <see cref="InvalidOperationException"/> say), at the token comparison or in a load: it is wrapped in a
/// <see cref="BundleLoadException"/> whose message names its type only and which keeps it as the <see cref="Exception.InnerException"/>, so <see cref="LastLoadError"/> carries it, a first load throws it wrapped, and a
/// consumed <see cref="BundleRefreshMode.Notify"/> signal is re-armed. Only an <see cref="OutOfMemoryException"/> propagates. The operation holds no caller token, so a cancellation raised by the provider is such a failure too.</para>
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

    /// <summary>Guards <see cref="_inflight"/> and <see cref="_inflightRequired"/> only: never held across I/O or an <see langword="await"/>.</summary>
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private Task<Image>? _inflight;
    private long _inflightRequired;
    private Image? _image;
    private long _required;
    private bool _checkRequested;
    private long _lastCheck;
    private bool _stale;
    private Exception? _lastLoadError;

    /// <summary>Creates a handle over <paramref name="provider"/>. Nothing is loaded until <see cref="GetCurrentAsync"/> or <see cref="RefreshAsync"/> is used.</summary>
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
    public bool IsStale => Volatile.Read(ref _stale) || (Volatile.Read(ref _image) is { } image && image.Generation < Volatile.Read(ref _required));

    /// <summary>The failure of the last load, or <see langword="null"/>: a reload that kept the previous image served (then <see cref="IsStale"/> is set), or a failed first load (then there is no image and <see cref="IsStale"/> stays <see langword="false"/>). Cleared by a successful load, or by a comparison that finds the image current.</summary>
    public Exception? LastLoadError => Volatile.Read(ref _lastLoadError);

    /// <summary>
    /// The image, loaded on first use and reloaded under <see cref="RefreshPolicy"/> and the
    /// reload obligation. When a reload fails the previous image is returned and
    /// <see cref="IsStale"/> is set.
    /// </summary>
    /// <param name="ct">Stops the wait: the call ends with an <see cref="OperationCanceledException"/>. The load itself, shared with the other callers, is not cancelled.</param>
    /// <exception cref="BundleLoadException">No image could ever be loaded (the first load failed).</exception>
    public async Task<Bundle> GetCurrentAsync(CancellationToken ct = default)
    {
        var image = Volatile.Read(ref _image);
        if (image is not null && RefreshPolicy.Mode == BundleRefreshMode.Manual && image.Generation >= Volatile.Read(ref _required))
        {
            return image.Bundle; // the fast path: nothing to compare under Manual
        }

        ct.ThrowIfCancellationRequested();
        while (true)
        {
            var required = Volatile.Read(ref _required);
            var (operation, blocker) = JoinOrStart(required, force: false);
            if (operation is not null)
            {
                return (await operation.WaitAsync(ct).ConfigureAwait(false)).Bundle;
            }

            // An older operation is running, started before the obligation this caller must meet:
            // wait for it to end (its outcome does not matter), then join or start one that covers it.
            await WaitForEndAsync(blocker!, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reloads now, whatever the policy, and returns the new image. A failure keeps the previous image served (<see cref="IsStale"/>) and is thrown. If an operation is in progress it is waited for first, then a new one starts, as the serialized load always did.</summary>
    /// <param name="ct">Stops the wait: the call ends with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="BundleLoadException">The load failed.</exception>
    public async Task<Bundle> RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            var (operation, blocker) = JoinOrStart(Volatile.Read(ref _required), force: true);
            if (operation is null)
            {
                await WaitForEndAsync(blocker!, ct).ConfigureAwait(false);
                continue;
            }

            var image = await operation.WaitAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref _lastLoadError) is { } error)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return image.Bundle;
        }
    }

    /// <summary>
    /// The synchronous spelling of <see cref="GetCurrentAsync"/>, for the code that has not become
    /// asynchronous yet (#152): only for a provider that declares
    /// <see cref="BundleProviderCapabilities.CompletesSynchronously"/> (refused otherwise, before
    /// anything starts), and a caller that finds an operation already running on another thread
    /// waits for it, as the serialized load always made it wait.
    /// </summary>
    internal Bundle Current
    {
        get
        {
            var image = Volatile.Read(ref _image);
            if (image is not null && RefreshPolicy.Mode == BundleRefreshMode.Manual && image.Generation >= Volatile.Read(ref _required))
            {
                return image.Bundle;
            }

            SyncBridge.RequireSynchronousProvider(Provider);
            return GetCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>The synchronous spelling of <see cref="RefreshAsync"/>; see <see cref="Current"/>.</summary>
    internal Bundle Refresh()
    {
        SyncBridge.RequireSynchronousProvider(Provider);
        return RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
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
    /// <see cref="RefreshAsync"/>; only the signals of a <see cref="BundleRefreshMode.Notify"/> handle are lost. A load in progress
    /// is not cancelled either: a catalog disposes the handles a reload replaced while a search on the previous snapshot may still need them.
    /// </summary>
    public void Dispose() => _subscription?.Dispose();

    /// <summary>
    /// The operation a caller needing generation <paramref name="required"/> should await: the one
    /// in progress when it started at a generation at least that, a new one when none is running,
    /// or — with the older operation as the second item — neither: the caller waits for that one to
    /// end and asks again. Starting runs the operation's first step on the calling flow, outside
    /// the gate (the gate only publishes it).
    /// </summary>
    private (Task<Image>? Operation, Task<Image>? Blocker) JoinOrStart(long required, bool force)
    {
        TaskCompletionSource<Image> published;
        lock (_gate)
        {
            if (_inflight is { } running)
            {
                // A forced refresh never joins: it must run its own load after what is in progress.
                return !force && Volatile.Read(ref _inflightRequired) >= required ? (running, null) : (null, running);
            }

            published = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight = published.Task;
            _inflightRequired = required;
        }

        _ = RunOperationAsync(published, force);
        return (published.Task, null);
    }

    private async Task RunOperationAsync(TaskCompletionSource<Image> published, bool force)
    {
        try
        {
            published.SetResult(await ObtainAsync(force).ConfigureAwait(false));
        }
        catch (Exception e)
        {
            published.SetException(e);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inflight, published.Task))
                {
                    _inflight = null;
                }
            }
        }
    }

    private static async Task WaitForEndAsync(Task<Image> operation, CancellationToken ct)
    {
        try
        {
            await operation.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The outcome of an operation that is not ours does not matter: only its end.
        }
    }

    /// <summary>The body of one operation: the token comparison the policy asks for, then the load if the image is behind (or always, when forced).</summary>
    private async Task<Image> ObtainAsync(bool force)
    {
        var image = _image;
        if (!force && image is not null && !await NeedsReloadAsync(image).ConfigureAwait(false))
        {
            return image;
        }

        return await LoadAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> NeedsReloadAsync(Image image)
    {
        if (image.Generation < Volatile.Read(ref _required))
        {
            return true;
        }

        switch (RefreshPolicy.Mode)
        {
            case BundleRefreshMode.OnAccess:
                return await CheckTokenAsync(image).ConfigureAwait(false);
            case BundleRefreshMode.Interval:
                {
                    var now = _time.GetTimestamp();
                    if (_lastCheck != 0 && _time.GetElapsedTime(_lastCheck, now) < RefreshPolicy.Interval)
                    {
                        return false;
                    }

                    _lastCheck = now;
                    return await CheckTokenAsync(image).ConfigureAwait(false);
                }

            case BundleRefreshMode.Notify:
                if (!Volatile.Read(ref _checkRequested))
                {
                    return false;
                }

                Volatile.Write(ref _checkRequested, false);
                return await CheckTokenAsync(image).ConfigureAwait(false);
            default:
                return false;
        }
    }

    /// <summary>Compares the tokens; an unchanged one means the image served is in fact current, so a stale mark left by an earlier failure is cleared (an obligation is not: it is generation-based).</summary>
    private async ValueTask<bool> CheckTokenAsync(Image image)
    {
        if (await TokenChangedAsync(image).ConfigureAwait(false))
        {
            return true;
        }

        Volatile.Write(ref _lastLoadError, null);
        Volatile.Write(ref _stale, false);
        return false;
    }

    /// <summary>
    /// A token that cannot be read counts as changed, whatever the exception (a host provider may
    /// raise one that is neither a provider, an I/O nor an access exception): the reload then
    /// fails the same way, and the image is marked stale. Only an <see cref="OutOfMemoryException"/>
    /// propagates. The operation holds no caller token, so a cancellation raised by the provider is a
    /// provider failure like the others.
    /// </summary>
    private async ValueTask<bool> TokenChangedAsync(Image image)
    {
        try
        {
            return await Provider.GetVersionAsync(Options.Limits, CancellationToken.None).ConfigureAwait(false) != image.Bundle.Version;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return true;
        }
    }

    private async Task<Image> LoadAsync()
    {
        var startedAt = Volatile.Read(ref _required);
        try
        {
            var bundle = await Bundle.LoadAsync(Provider, Options, CancellationToken.None).ConfigureAwait(false);
            var image = new Image(bundle, startedAt);
            Volatile.Write(ref _image, image);
            Volatile.Write(ref _lastLoadError, null);
            Volatile.Write(ref _stale, false);
            _lastCheck = _time.GetTimestamp();
            return image;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A BundleLoadException is the core's own translation of a provider failure. Any other
            // exception can only be a host provider's raw one (the core translates the provider,
            // I/O and access exceptions): it is a load failure like the others, wrapped with a
            // message built from its type alone (spec §7, never its text) and kept as the cause.
            var failure = e as BundleLoadException ?? new BundleLoadException($"provider failure ({e.GetType().Name})", e);
            Volatile.Write(ref _lastLoadError, failure);
            if (_image is not { } previous)
            {
                if (ReferenceEquals(failure, e))
                {
                    throw;
                }

                throw failure;
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

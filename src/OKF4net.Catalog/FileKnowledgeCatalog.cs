// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// A <see cref="IKnowledgeCatalog"/> backed by a <c>catalog.json</c> file on
/// disk, with atomic-snapshot-swap reloads and a best-effort
/// <see cref="FileSystemWatcher"/> for hot reload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction</b> loads, parses (<see cref="CatalogManifestParser.TryParse"/>),
/// and path-validates (<see cref="CatalogPathResolver.TryResolve"/>) every
/// <em>enabled</em> source's path against <see cref="KnowledgeCatalogOptions.CatalogRoot"/>,
/// then publishes the result as <see cref="Current"/> at
/// <see cref="KnowledgeCatalogSnapshot.Generation"/> 1. An invalid initial
/// catalog throws <see cref="CatalogException"/> -- fail-fast, so a caller
/// (e.g. a DI container at startup) never silently gets an empty or partial
/// catalog and there is no "error snapshot" state to represent.
/// </para>
/// <para>
/// <b>Runtime reloads are errors-as-data.</b> <see cref="ReloadAsync"/> parses
/// and validates a complete new snapshot before ever touching <see cref="Current"/>;
/// only a fully valid replacement is swapped in (atomically, under a single
/// lock covering both the read and the write side of <see cref="Current"/>),
/// with <see cref="KnowledgeCatalogSnapshot.Generation"/> incremented by one
/// and <see cref="LastReloadDiagnostics"/> cleared. A malformed or invalid
/// replacement leaves <see cref="Current"/> and its generation exactly as
/// they were and records the reject reasons in <see cref="LastReloadDiagnostics"/>
/// instead -- <see cref="ReloadAsync"/> never throws for that. This is a
/// deliberate all-or-nothing design: if <em>any</em> enabled source's path
/// fails validation, the whole reload is rejected rather than dropping just
/// that source, keeping the "every snapshot is fully valid" invariant simple
/// and the failure mode obvious (nothing silently disappears from the
/// catalog on a typo).
/// </para>
/// <para>
/// <b>The <see cref="FileSystemWatcher"/> is best-effort.</b> It watches only
/// <see cref="KnowledgeCatalogOptions.CatalogFilePath"/> itself (never the
/// bundle directories the catalog points at), debounced by
/// <see cref="KnowledgeCatalogOptions.ReloadDebounce"/> so a burst of editor
/// save/rename events collapses into a single reload. Depending on OS,
/// filesystem, and container layer, watcher events can be missed entirely or
/// delivered more than once for a single change -- callers that need a
/// reliable, synchronous guarantee that a specific edit has been picked up
/// should call <see cref="ReloadAsync"/> explicitly rather than rely on the
/// watcher.
/// </para>
/// </remarks>
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;

    private KnowledgeCatalogSnapshot _current;
    private IReadOnlyList<CatalogDiagnostic> _lastReloadDiagnostics = [];
    private bool _disposed;

    /// <summary>
    /// Loads, validates, and publishes the initial snapshot at
    /// <see cref="KnowledgeCatalogSnapshot.Generation"/> 1, then -- unless
    /// <see cref="KnowledgeCatalogOptions.WatchForChanges"/> is
    /// <see langword="false"/> -- starts a best-effort, debounced
    /// <see cref="FileSystemWatcher"/> on <see cref="KnowledgeCatalogOptions.CatalogFilePath"/>.
    /// </summary>
    /// <param name="options">The catalog's configuration.</param>
    /// <exception cref="CatalogException">
    /// The initial <see cref="KnowledgeCatalogOptions.CatalogFilePath"/> could
    /// not be read, parsed, or path-validated. The exception message
    /// aggregates every underlying <see cref="CatalogDiagnostic"/>.
    /// </exception>
    public FileKnowledgeCatalog(KnowledgeCatalogOptions options)
    {
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);

        if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
        }

        _current = snapshot! with { Generation = 1 };

        if (options.WatchForChanges)
        {
            _debounceTimer = new Timer(FireDebouncedReload, state: null, Timeout.Infinite, Timeout.Infinite);

            // If CreateWatcher() throws (e.g. the manifest directory is
            // otherwise unwatchable), the constructor itself throws and never
            // returns a disposable instance for a caller to clean up --
            // without this try/catch the already-constructed _debounceTimer
            // would leak. CreateWatcher() applies the same belt-and-suspenders
            // disposal to its own partially-constructed FileSystemWatcher.
            try
            {
                _watcher = CreateWatcher();
            }
            catch
            {
                _debounceTimer.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public string CatalogRoot => _options.CatalogRoot;

    /// <inheritdoc/>
    public KnowledgeCatalogSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<CatalogDiagnostic> LastReloadDiagnostics
    {
        get
        {
            lock (_gate)
            {
                return _lastReloadDiagnostics;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The reload itself is a fast, local, synchronous file read plus
    /// in-memory validation, run under <see cref="_gate"/> so it can never
    /// interleave with another reload or with a concurrent read of
    /// <see cref="Current"/>; the <see cref="ValueTask{TResult}"/> shape
    /// exists purely to satisfy <see cref="IKnowledgeCatalog"/>'s async
    /// contract, not because real asynchronous work happens here.
    /// <paramref name="cancellationToken"/> is honored only before the work
    /// starts (<see cref="CancellationToken.ThrowIfCancellationRequested"/>)
    /// -- once underway the operation is effectively atomic and completes
    /// rather than being interrupted partway.
    /// </remarks>
    public ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_disposed)
            {
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            if (!TryLoadSnapshot(out var snapshot, out var diagnostics))
            {
                _lastReloadDiagnostics = diagnostics;
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            _current = snapshot! with { Generation = _current.Generation + 1 };
            _lastReloadDiagnostics = [];
            return new ValueTask<KnowledgeCatalogSnapshot>(_current);
        }
    }

    /// <summary>
    /// Stops the watcher and debounce timer (if any) and prevents any future
    /// reload from applying. Cancellation-safe (never leaves a torn snapshot)
    /// and idempotent -- safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
        }
    }

    private bool TryLoadSnapshot(out KnowledgeCatalogSnapshot? snapshot, out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        string json;
        try
        {
            // Strict UTF-8, matching every other read in this codebase
            // (Bundle/Validate/IndexGenerator/OkfCli): fail loudly with
            // DecoderFallbackException on non-UTF-8 bytes rather than
            // File.ReadAllText's silent U+FFFD replacement.
            json = OkfEncodings.Strict.GetString(File.ReadAllBytes(_options.CatalogFilePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            snapshot = null;

            // Array.AsReadOnly() wraps the array in a genuine
            // ReadOnlyCollection<T> view -- otherwise a caller could
            // `(CatalogDiagnostic[])catalog.LastReloadDiagnostics` and mutate
            // published diagnostics.
            diagnostics = Array.AsReadOnly(new[]
            {
                new CatalogDiagnostic(
                    CatalogDiagnosticCode.ParseError,
                    $"Could not read catalog file '{_options.CatalogFilePath}': {e.Message}"),
            });
            return false;
        }

        if (!CatalogManifestParser.TryParse(json, _manifestDirectory, out snapshot, out diagnostics))
        {
            return false;
        }

        var pathDiagnostics = new List<CatalogDiagnostic>();
        foreach (var source in snapshot!.Sources)
        {
            if (!source.Enabled)
            {
                continue;
            }

            if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out _, out var pathDiagnostic))
            {
                pathDiagnostics.Add(pathDiagnostic!);
            }
        }

        if (pathDiagnostics.Count > 0)
        {
            snapshot = null;
            diagnostics = pathDiagnostics;
            return false;
        }

        return true;
    }

    private FileSystemWatcher CreateWatcher()
    {
        var fileName = Path.GetFileName(_options.CatalogFilePath);
        var watcher = new FileSystemWatcher(_manifestDirectory, fileName);

        // Guards the watcher itself: if any post-construction step below
        // throws, this disposes the partially-configured watcher rather than
        // leaking its native handle -- the constructor's own try/catch around
        // CreateWatcher() only ever sees a fully-disposed-or-fully-returned
        // watcher, never a half-built one.
        try
        {
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.Size;
            watcher.Changed += OnCatalogFileEvent;
            watcher.Created += OnCatalogFileEvent;
            watcher.Deleted += OnCatalogFileEvent;
            watcher.Renamed += OnCatalogFileEvent;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    private void OnCatalogFileEvent(object sender, FileSystemEventArgs e)
    {
        // Runs on a FileSystemWatcher ThreadPool thread with no caller to
        // observe or catch an exception; an unhandled exception here would
        // terminate the whole host process (.NET's default for unhandled
        // exceptions on a ThreadPool thread), which is exactly what this
        // best-effort, "never take the process down" watcher must not do.
        // Every path underneath is non-throwing today -- this is a
        // belt-and-suspenders guard against a future change that isn't.
        try
        {
            ScheduleDebouncedReload();
        }
        catch (Exception)
        {
            // Swallowed intentionally: see remarks above.
        }
    }

    private void ScheduleDebouncedReload()
    {
        lock (_gate)
        {
            if (_disposed || _debounceTimer is null)
            {
                return;
            }

            // Resetting the due time on every observed event -- rather than
            // letting an already-running timer fire -- is what coalesces a
            // burst of events into a single reload: only the last event in a
            // burst that is quieter than ReloadDebounce actually triggers one.
            _debounceTimer.Change(_options.ReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireDebouncedReload(object? state) => _ = ReloadAsync();

    private static string GetManifestDirectory(string catalogFilePath)
    {
        var full = Path.GetFullPath(catalogFilePath);
        return Path.GetDirectoryName(full) ?? full;
    }

    private static string FormatDiagnostics(IReadOnlyList<CatalogDiagnostic> diagnostics) =>
        "Invalid catalog manifest:" + string.Concat(diagnostics.Select(d => $"{Environment.NewLine}  [{d.Code}] {d.Message}"));
}

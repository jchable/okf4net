// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;
using OKF4net.Providers;

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
/// <para>
/// <b>The snapshot owns the handles</b> (spec §5.2, §9). Each published snapshot carries one
/// <see cref="BundleHandle"/> per enabled knowledge source (<see cref="KnowledgeCatalogSnapshot.Handles"/>),
/// built by the source's provider — the built-in filesystem one, confined to
/// <see cref="KnowledgeCatalogOptions.CatalogRoot"/> and re-checked against it before every
/// access, or the factory registered for the source's <c>provider</c> — under
/// <see cref="KnowledgeCatalogOptions.LoadOptions"/> and the source's refresh policy
/// (<see cref="KnowledgeCatalogOptions.RefreshPolicy"/>, <see cref="BundleRefreshPolicy.OnAccess"/>
/// by default). A reload reuses a source's handle, image included, only when its effective
/// configuration is identical in every point (provider type, resolved directory, settings,
/// refresh policy, load options); any difference, a changed <c>path</c> for instance, builds
/// a new handle and the old one is no longer served by the new snapshot. A handle that
/// leaves the catalog is disposed (which only ends its change subscription: a request still
/// holding the old snapshot keeps reading its image); <see cref="Dispose"/> disposes them all.
/// A rejected reload builds nothing that outlives it and keeps every current handle.
/// </para>
/// </remarks>
public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    private readonly KnowledgeCatalogOptions _options;
    private readonly string _manifestDirectory;
    private readonly Dictionary<string, IBundleProviderFactory> _factories;
    private readonly Lock _gate = new();

    /// <summary>The effective configuration each current handle was built for, by source id (guarded by <see cref="_gate"/>).</summary>
    private Dictionary<string, HandleConfiguration> _handleConfigurations = new(StringComparer.Ordinal);
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
    /// <exception cref="ArgumentException">
    /// <see cref="KnowledgeCatalogOptions.ProviderFactories"/> holds two factories for one
    /// provider type, or one for the built-in <c>filesystem</c> type: a host configuration
    /// error, refused before any manifest is read.
    /// </exception>
    public FileKnowledgeCatalog(KnowledgeCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _manifestDirectory = GetManifestDirectory(options.CatalogFilePath);
        _factories = IndexFactories(options.ProviderFactories);

        if (!TryLoadSnapshot(previous: null, out var snapshot, out var configurations, out var diagnostics))
        {
            throw new CatalogException(FormatDiagnostics(diagnostics));
        }

        _current = snapshot! with { Generation = 1 };
        _handleConfigurations = configurations!;

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

            if (!TryLoadSnapshot(_current, out var snapshot, out var configurations, out var diagnostics))
            {
                _lastReloadDiagnostics = diagnostics;
                return new ValueTask<KnowledgeCatalogSnapshot>(_current);
            }

            var previous = _current;
            _current = snapshot! with { Generation = _current.Generation + 1 };
            _handleConfigurations = configurations!;
            _lastReloadDiagnostics = [];
            DisposeHandlesLeft(previous, _current);
            return new ValueTask<KnowledgeCatalogSnapshot>(_current);
        }
    }

    /// <summary>
    /// Stops the watcher and debounce timer (if any), disposes the current snapshot's
    /// handles (their change subscriptions end; an image already handed out stays readable)
    /// and prevents any future reload from applying. Cancellation-safe (never leaves a torn
    /// snapshot) and idempotent -- safe to call more than once.
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
            foreach (var handle in _current.Handles.Values)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// The effective configuration a source's handle was built for (spec §9): a handle is
    /// reused across reloads only when every point is equal, so a changed <c>path</c> (its
    /// resolved directory), setting, policy or option rebuilds it. The load options are
    /// compared as the record they are (the catalog hands every handle its own instance).
    /// </summary>
    private sealed record HandleConfiguration(string ProviderType, string? ResolvedDirectory, ProviderSettings Settings, BundleRefreshPolicy RefreshPolicy, BundleLoadOptions LoadOptions);

    /// <summary>Disposes the handles of <paramref name="previous"/> that <paramref name="current"/> does not serve any more.</summary>
    private static void DisposeHandlesLeft(KnowledgeCatalogSnapshot previous, KnowledgeCatalogSnapshot current)
    {
        foreach (var (id, handle) in previous.Handles)
        {
            if (!current.Handles.TryGetValue(id, out var kept) || !ReferenceEquals(kept, handle))
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the handles of <paramref name="snapshot"/>'s enabled knowledge sources, reusing
    /// a handle of <paramref name="previous"/> whose effective configuration is unchanged.
    /// On failure nothing created here survives: the handles built so far are disposed and
    /// every current handle is untouched.
    /// </summary>
    private bool TryBuildHandles(
        KnowledgeCatalogSnapshot snapshot,
        KnowledgeCatalogSnapshot? previous,
        IReadOnlyDictionary<string, string> resolvedDirectories,
        out IReadOnlyDictionary<string, BundleHandle>? handles,
        out Dictionary<string, HandleConfiguration>? configurations,
        out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        var built = new Dictionary<string, BundleHandle>(StringComparer.Ordinal);
        var created = new List<BundleHandle>();
        var keys = new Dictionary<string, HandleConfiguration>(StringComparer.Ordinal);
        var problems = new List<CatalogDiagnostic>();

        foreach (var source in snapshot.Sources)
        {
            if (!source.Enabled || source.Role != SourceRole.Knowledge)
            {
                continue;
            }

            var policy = _options.RefreshPolicyFor?.Invoke(source) ?? _options.RefreshPolicy;
            var configuration = new HandleConfiguration(
                source.ProviderType,
                source.IsFileSystem ? resolvedDirectories[source.Id] : null,
                source.Settings,
                policy,
                _options.LoadOptions);

            if (previous is not null
                && previous.Handles.TryGetValue(source.Id, out var existing)
                && _handleConfigurations.TryGetValue(source.Id, out var existingConfiguration)
                && existingConfiguration == configuration)
            {
                built[source.Id] = existing;
                keys[source.Id] = configuration;
                continue;
            }

            try
            {
                var provider = source.IsFileSystem
                    ? new FileSystemBundleProvider(resolvedDirectories[source.Id], confinementBoundary: _options.CatalogRoot)
                    : _factories[source.ProviderType].Create(source);
                var handle = new BundleHandle(provider, _options.LoadOptions, policy);
                created.Add(handle);
                built[source.Id] = handle;
                keys[source.Id] = configuration;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Translated (spec §7): the factory's own text may name a bucket or an endpoint,
                // which stays with the host's log, never in a diagnostic a consumer may print.
                problems.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.ProviderUnavailable,
                    $"Source '{source.Id}' provider '{source.ProviderType}' could not be created ({e.GetType().Name})."));
            }
        }

        if (problems.Count > 0)
        {
            foreach (var handle in created)
            {
                handle.Dispose();
            }

            handles = null;
            configurations = null;
            diagnostics = problems.AsReadOnly();
            return false;
        }

        handles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, BundleHandle>(built);
        configurations = keys;
        diagnostics = [];
        return true;
    }

    private bool TryLoadSnapshot(
        KnowledgeCatalogSnapshot? previous,
        out KnowledgeCatalogSnapshot? snapshot,
        out Dictionary<string, HandleConfiguration>? configurations,
        out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        configurations = null;
        string json;
        try
        {
            // Strict UTF-8, matching every other read in this codebase
            // (Bundle/Validate/IndexGenerator/OkfCli): fail loudly with
            // DecoderFallbackException on non-UTF-8 bytes rather than
            // File.ReadAllText's silent U+FFFD replacement.
            //
            // Opened via FileStream (not File.ReadAllBytes) so the share mode
            // can be widened to FileShare.ReadWrite | FileShare.Delete: the
            // documented external-writer contract for catalog.json is an
            // atomic temp-file-then-File.Move(overwrite:true) replace, and on
            // Windows that replace requires FileShare.Delete on any
            // concurrent reader -- File.ReadAllBytes's default FileShare.Read
            // would otherwise let this watcher's own reload transiently block
            // a legitimate external atomic replace with a sharing-violation
            // IOException.
            byte[] rawBytes;
            using (var stream = new FileStream(
                _options.CatalogFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                rawBytes = new byte[stream.Length];
                stream.ReadExactly(rawBytes);
            }

            json = OkfEncodings.Strict.GetString(rawBytes);

            // Strict-UTF8 GetString decodes a leading U+FEFF byte-order mark
            // as a literal character rather than stripping it the way
            // File.ReadAllText used to -- so a BOM-prefixed catalog.json
            // (common from some editors/tools on Windows) would otherwise
            // fail JsonDocument.Parse on the leading U+FEFF even though the
            // manifest itself is perfectly valid. Strip a single leading BOM
            // character here, after the strict decode has already rejected
            // genuinely invalid UTF-8, to restore BOM tolerance without
            // loosening that validation (F9).
            if (json.Length > 0 && json[0] == '﻿')
            {
                json = json[1..];
            }
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
        var resolvedDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in snapshot!.Sources)
        {
            // A filesystem source's path is checked as before (an enabled source only); another
            // provider type needs a registered factory that accepts every setting the source
            // carries (spec §9). The settings allowlist depends on host configuration only, so
            // it is checked for a disabled source too whenever a factory exists for its type; a
            // disabled source naming a type with no factory stays accepted (the host may
            // register it, or enable the source, later).
            if (source.IsFileSystem)
            {
                if (source.Enabled)
                {
                    if (!CatalogPathResolver.TryResolve(_options.CatalogRoot, _manifestDirectory, source.Path, out var resolved, out var pathDiagnostic))
                    {
                        pathDiagnostics.Add(pathDiagnostic!);
                    }
                    else
                    {
                        resolvedDirectories[source.Id] = resolved!;
                    }
                }
            }
            else if (!_factories.TryGetValue(source.ProviderType, out var factory))
            {
                if (source.Enabled)
                {
                    pathDiagnostics.Add(new CatalogDiagnostic(
                        CatalogDiagnosticCode.UnknownProvider,
                        $"Source '{source.Id}' names the provider '{source.ProviderType}', for which no factory is registered (KnowledgeCatalogOptions.ProviderFactories)."));
                }
            }
            else
            {
                foreach (var name in source.Settings.Keys)
                {
                    if (!factory.AcceptedSettings.Contains(name))
                    {
                        pathDiagnostics.Add(new CatalogDiagnostic(
                            CatalogDiagnosticCode.IllegalSetting,
                            $"Source '{source.Id}' carries the setting '{name}', which the '{source.ProviderType}' provider does not accept."));
                    }
                }
            }
        }

        if (pathDiagnostics.Count > 0)
        {
            snapshot = null;

            // .AsReadOnly() wraps `pathDiagnostics` in a genuine
            // ReadOnlyCollection<T> view -- otherwise a caller could
            // downcast LastReloadDiagnostics back to List<CatalogDiagnostic>
            // and mutate published diagnostics (F4), matching the same
            // hardening the read-failure path above already applies via
            // Array.AsReadOnly().
            diagnostics = pathDiagnostics.AsReadOnly();
            return false;
        }

        if (!TryBuildHandles(snapshot, previous, resolvedDirectories, out var handles, out configurations, out diagnostics))
        {
            snapshot = null;
            return false;
        }

        snapshot = snapshot with { Handles = handles! };
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

    /// <summary>The factories by provider type; a duplicate type or the built-in <c>filesystem</c> type is a configuration error.</summary>
    private static Dictionary<string, IBundleProviderFactory> IndexFactories(IReadOnlyList<IBundleProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        var indexed = new Dictionary<string, IBundleProviderFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory, nameof(factories));
            if (string.IsNullOrEmpty(factory.ProviderType))
            {
                throw new ArgumentException("a provider factory needs a non-empty ProviderType", nameof(factories));
            }

            if (string.Equals(factory.ProviderType, KnowledgeCatalogSource.FileSystemProviderType, StringComparison.Ordinal))
            {
                throw new ArgumentException($"the provider type '{KnowledgeCatalogSource.FileSystemProviderType}' is built in and cannot be registered", nameof(factories));
            }

            if (!indexed.TryAdd(factory.ProviderType, factory))
            {
                throw new ArgumentException($"two provider factories claim the type '{factory.ProviderType}'", nameof(factories));
            }
        }

        return indexed;
    }

    private static string FormatDiagnostics(IReadOnlyList<CatalogDiagnostic> diagnostics) =>
        "Invalid catalog manifest:" + string.Concat(diagnostics.Select(d => $"{Environment.NewLine}  [{d.Code}] {d.Message}"));
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Configuration for a <see cref="FileKnowledgeCatalog"/>.
/// </summary>
public sealed class KnowledgeCatalogOptions
{
    /// <summary>The path to the catalog manifest file (<c>catalog.json</c>).</summary>
    public required string CatalogFilePath { get; init; }

    /// <summary>
    /// The catalog's root directory -- the containment boundary every enabled
    /// source's <c>path</c> is validated against (see
    /// <see cref="CatalogPathResolver.TryResolve"/>). Expected to already be
    /// canonicalized (<see cref="Path.GetFullPath(string)"/>) once by the
    /// caller at startup. The shared path components with
    /// <see cref="CatalogFilePath"/> must use the same exact spelling,
    /// including case: catalog containment is ordinal on every platform so a
    /// path cannot leave the configured root and re-enter through a
    /// case-variant spelling. A mismatch is rejected as
    /// <see cref="CatalogDiagnosticCode.OutsideRoot"/> even on a
    /// case-insensitive volume.
    /// </summary>
    public required string CatalogRoot { get; init; }

    /// <summary>
    /// How long to wait, after the most recent observed filesystem event on
    /// <see cref="CatalogFilePath"/>, before triggering a reload -- coalesces
    /// a burst of events (e.g. an editor's save-as-temp-then-rename, or a
    /// duplicate OS notification) into a single reload attempt. Defaults to
    /// 250 ms.
    /// </summary>
    public TimeSpan ReloadDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether to watch <see cref="CatalogFilePath"/> for changes and trigger
    /// a debounced reload automatically. When <see langword="false"/>,
    /// <see cref="IKnowledgeCatalog.ReloadAsync"/> is the only way to pick up
    /// changes. Defaults to <see langword="true"/>.
    /// </summary>
    public bool WatchForChanges { get; init; } = true;

    /// <summary>
    /// The provider factories the manifest may name by <c>provider</c> (spec §9): one per
    /// provider type, none for the built-in <c>filesystem</c>. A source naming a type with no
    /// factory here is rejected (<see cref="CatalogDiagnosticCode.UnknownProvider"/>); two
    /// factories with the same type, or one claiming <c>filesystem</c>, are a configuration
    /// error <see cref="FileKnowledgeCatalog"/>'s constructor refuses. Empty by default.
    /// </summary>
    public IReadOnlyList<IBundleProviderFactory> ProviderFactories { get; init; } = [];

    /// <summary>
    /// The refresh policy of every knowledge source's handle (spec §5.3): the bundle's token is
    /// compared before each use by default (<see cref="BundleRefreshPolicy.OnAccess"/>), so a
    /// source stays as fresh as when it was reloaded at every request, at the cost of a token
    /// read instead of a load. <see cref="RefreshPolicyFor"/> overrides it per source.
    /// </summary>
    public BundleRefreshPolicy RefreshPolicy { get; init; } = BundleRefreshPolicy.OnAccess;

    /// <summary>
    /// Chooses a source's refresh policy; <see langword="null"/> (the default), or a
    /// <see langword="null"/> answer for a source, means <see cref="RefreshPolicy"/>. A
    /// <see cref="BundleRefreshPolicy.Notify"/> answer needs a provider that implements
    /// <see cref="IBundleChangeNotifier"/>, else the source is rejected as
    /// <see cref="CatalogDiagnosticCode.ProviderUnavailable"/>.
    /// </summary>
    public Func<KnowledgeCatalogSource, BundleRefreshPolicy?>? RefreshPolicyFor { get; init; }

    /// <summary>The options every handle loads its bundle with (bounds, key policy); <see cref="BundleLoadOptions.Default"/> by default.</summary>
    public BundleLoadOptions LoadOptions { get; init; } = BundleLoadOptions.Default;
}

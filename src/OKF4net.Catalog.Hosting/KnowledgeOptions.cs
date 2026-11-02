// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Configures the single knowledge catalog registered by
/// <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>.
/// </summary>
/// <remarks>
/// V1 supports exactly one <c>catalog.json</c> per <c>AddKnowledge</c> call --
/// <c>AddBundle</c> is deliberately cut (YAGNI; a single-source
/// <c>catalog.json</c> already covers the "one bundle" case). Multiple
/// <see cref="AddCatalogFile"/> calls are rejected at registration time; see
/// <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>.
/// </remarks>
public sealed class KnowledgeOptions
{
    private int _catalogFileCallCount;
    private readonly List<IBundleProviderFactory> _providerFactories = [];

    /// <summary>
    /// The <see cref="KnowledgeResolverStrategy"/> used for searches whose
    /// query leaves <see cref="KnowledgeQuery.ResolverStrategy"/> unset.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="KnowledgeResolverStrategy.GroupedBySource"/>:
    /// the behaviour every existing deployment already has, so upgrading
    /// never silently reorders anyone's results. A host wanting one merged
    /// cross-source ranking -- typically to feed a consumer that truncates
    /// under a token budget -- opts in here.
    /// </remarks>
    public KnowledgeResolverStrategy DefaultResolverStrategy { get; set; } = KnowledgeResolverStrategy.GroupedBySource;

    /// <summary>
    /// The fairness quota the fused strategies apply when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset; <see langword="null"/>
    /// (the default) disables fairness reordering. Ignored by
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/>.
    /// </summary>
    public int? DefaultFairnessQuota { get; set; }

    /// <summary>
    /// The visibility policy every strategy applies when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction -- every
    /// enabled knowledge source stays visible to every caller, the
    /// behaviour every pre-existing deployment already has.
    /// </summary>
    public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? DefaultSourceVisibilityPolicy { get; set; }

    /// <summary>
    /// The resolved, full path to the catalog manifest last passed to
    /// <see cref="AddCatalogFile"/>; <see langword="null"/> until
    /// <see cref="AddCatalogFile"/> has been called at least once.
    /// </summary>
    internal string? CatalogFilePath { get; private set; }

    /// <summary>
    /// The catalog root derived from <see cref="CatalogFilePath"/>'s
    /// directory; <see langword="null"/> until <see cref="AddCatalogFile"/>
    /// has been called at least once.
    /// </summary>
    internal string? CatalogRoot { get; private set; }

    /// <summary>
    /// Registers the <c>catalog.json</c> manifest at <paramref name="path"/>
    /// as the catalog source. The catalog's root directory is derived from
    /// <paramref name="path"/>'s own directory
    /// (<see cref="Path.GetDirectoryName(string)"/> of
    /// <see cref="Path.GetFullPath(string)"/>) -- never from separate,
    /// independently configurable input -- so nothing else in the
    /// <c>configure</c> callback can point the catalog's containment root
    /// somewhere other than where the manifest itself lives.
    /// </summary>
    /// <param name="path">
    /// The path (relative or absolute) to the <c>catalog.json</c> manifest.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    public void AddCatalogFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _catalogFileCallCount++;

        var fullPath = Path.GetFullPath(path);
        CatalogFilePath = fullPath;
        CatalogRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
    }

    /// <summary>
    /// The refresh policy of every knowledge source's handle, handed to
    /// <see cref="KnowledgeCatalogOptions.RefreshPolicy"/>: the bundle's token is compared
    /// before each use by default (<see cref="OKF4net.Providers.BundleRefreshPolicy.OnAccess"/>),
    /// so a source stays as fresh as when it was reloaded at every request.
    /// </summary>
    public OKF4net.Providers.BundleRefreshPolicy RefreshPolicy { get; set; } = OKF4net.Providers.BundleRefreshPolicy.OnAccess;

    /// <summary>
    /// The provider factories registered by <see cref="AddProviderFactory"/>, in order,
    /// handed to <see cref="KnowledgeCatalogOptions.ProviderFactories"/>.
    /// </summary>
    internal IReadOnlyList<IBundleProviderFactory> ProviderFactories => _providerFactories;

    /// <summary>
    /// Registers a factory for the <c>provider</c> type it names
    /// (<see cref="IBundleProviderFactory.ProviderType"/>), so a <c>catalog.json</c> source may
    /// name that type. The credentials the provider needs are the factory's own (injected by
    /// the host that built it), never read from the manifest. The built-in <c>filesystem</c>
    /// type needs no factory and cannot be registered: <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>
    /// refuses it, as it refuses two factories for one type or an empty type, at registration time
    /// (<see cref="FileKnowledgeCatalog"/>'s constructor makes the same checks).
    /// </summary>
    /// <param name="factory">The factory to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void AddProviderFactory(IBundleProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _providerFactories.Add(factory);
    }

    /// <summary>
    /// Validates that exactly one <see cref="AddCatalogFile"/> call was made.
    /// Called by <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>
    /// immediately after running the <c>configure</c> callback, so a
    /// misconfiguration fails at registration time rather than on first
    /// resolve.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No <see cref="AddCatalogFile"/> call was made, or <see cref="DefaultFairnessQuota"/>
    /// is set but not greater than zero, or <see cref="RefreshPolicy"/> is <see langword="null"/>,
    /// or the registered provider factories include an empty
    /// type, the built-in <c>filesystem</c> type, or two factories for one type.
    /// </exception>
    /// <exception cref="InvalidOperationException">More than one <see cref="AddCatalogFile"/> call was made.</exception>
    internal void Validate()
    {
        if (_catalogFileCallCount == 0)
        {
            throw new ArgumentException(
                "AddKnowledge requires a catalog file: call KnowledgeOptions.AddCatalogFile(path) inside the configure callback.");
        }

        if (_catalogFileCallCount > 1)
        {
            throw new InvalidOperationException(
                "AddKnowledge supports exactly one AddCatalogFile call in V1 (AddBundle/multi-catalog composition is cut as YAGNI); "
                + "register every source in a single catalog.json instead.");
        }

        // Caught here, at registration time, rather than left to the router
        // constructor's own ArgumentOutOfRangeException on first resolve --
        // which would name "defaultFairnessQuota", a constructor parameter
        // the host never typed, rather than the KnowledgeOptions property it
        // actually set.
        if (DefaultFairnessQuota is <= 0)
        {
            throw new ArgumentException(
                $"KnowledgeOptions.DefaultFairnessQuota must be greater than zero (got {DefaultFairnessQuota}); use null to disable fairness reordering.");
        }

        // A config-bound host can leave the property null: caught here, at registration time,
        // naming the property the host set.
        if (RefreshPolicy is null)
        {
            throw new ArgumentException("KnowledgeOptions.RefreshPolicy must not be null.");
        }

        // The same three factory checks FileKnowledgeCatalog's constructor makes, run here so
        // the misconfiguration fails at registration time, naming AddProviderFactory.
        var providerTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var factory in _providerFactories)
        {
            if (string.IsNullOrEmpty(factory.ProviderType))
            {
                throw new ArgumentException("AddProviderFactory: a provider factory needs a non-empty ProviderType.");
            }

            if (string.Equals(factory.ProviderType, KnowledgeCatalogSource.FileSystemProviderType, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"AddProviderFactory: the provider type '{KnowledgeCatalogSource.FileSystemProviderType}' is built in and cannot be registered.");
            }

            if (!providerTypes.Add(factory.ProviderType))
            {
                throw new ArgumentException($"AddProviderFactory: two provider factories claim the type '{factory.ProviderType}'.");
            }
        }

        // Same reasoning as the quota check above: caught here, naming the
        // KnowledgeOptions property the host actually set, rather than left
        // to the router constructor's own ArgumentException on first resolve
        // (which would name "defaultStrategy", a parameter the host never
        // typed). Reachable only via an out-of-range value surviving a
        // config bind -- the enum type itself blocks this in ordinary C#
        // code -- but config-bound hosts are exactly the case this method
        // exists to fail fast for.
        if (!Enum.IsDefined(DefaultResolverStrategy))
        {
            throw new ArgumentException(
                $"KnowledgeOptions.DefaultResolverStrategy is not a defined KnowledgeResolverStrategy member (got {DefaultResolverStrategy}).");
        }
    }
}

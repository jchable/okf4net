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
    /// Validates that exactly one <see cref="AddCatalogFile"/> call was made.
    /// Called by <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/>
    /// immediately after running the <c>configure</c> callback, so a
    /// misconfiguration fails at registration time rather than on first
    /// resolve.
    /// </summary>
    /// <exception cref="ArgumentException">No <see cref="AddCatalogFile"/> call was made.</exception>
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
    }
}

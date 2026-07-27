// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Registers a singleton <see cref="IMemoryStore"/> built from the catalog's
/// <see cref="SourceRole.Memory"/> sources. Requires
/// <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/> to have
/// registered an <see cref="IKnowledgeCatalog"/>.
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IMemoryStore"/> (<see cref="FileMemoryStore"/>)
    /// whose per-tier roots are the catalog's currently-enabled
    /// <c>role:memory</c> sources, each resolved via
    /// <see cref="CatalogPathResolver.TryResolve"/>. This lot wires the user
    /// tier; a source that fails to resolve, or a tier not present in the
    /// manifest, is simply absent from the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V1 limitation: the memory-source set is frozen at first resolution.</b>
    /// The factory below reads <see cref="IKnowledgeCatalog.Current"/> and
    /// resolves every <c>role:memory</c> source's tier root (via
    /// <see cref="CatalogPathResolver.TryResolve"/>) exactly once -- the first
    /// time <see cref="IMemoryStore"/> is resolved from the container -- then
    /// freezes the result into the singleton <see cref="FileMemoryStore"/>'s
    /// read-only tier-root dictionary. Unlike <see cref="DefaultKnowledgeResolver"/>,
    /// which re-reads <see cref="IKnowledgeCatalog.Current"/> on every search
    /// to honor hot-reload, this factory will NOT reflect a <c>role:memory</c>
    /// source added, removed, or edited (e.g. its <c>path</c>, <c>tier</c>, or
    /// <c>enabled</c> flag changed) after the singleton has been built --
    /// including via <see cref="IKnowledgeCatalog.ReloadAsync"/>. Picking up
    /// such a change requires rebuilding the <see cref="IServiceCollection"/>/container.
    /// </para>
    /// <para>
    /// This is narrower than it may first appear: per-scope path resolution
    /// (tenant/user/session segments via <see cref="MemoryPath.For"/>) remains
    /// fully live on every <see cref="IMemoryStore"/> call -- only the fixed
    /// SET of memory sources and their already-resolved tier roots is
    /// captured once and never refreshed.
    /// </para>
    /// <para>
    /// <b>Fail-fast on overlapping roots.</b> A <c>role:memory</c> root that
    /// equals or nests within a <c>role:knowledge</c> root (or vice-versa)
    /// would be walked and searched by <see cref="DefaultKnowledgeResolver"/>
    /// as if it were shared knowledge, defeating scoped-memory isolation.
    /// Resolution therefore throws an <see cref="InvalidOperationException"/>
    /// (naming the offending source ids) rather than building a leaky store;
    /// the operator must reconfigure disjoint roots.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryStore>(sp =>
        {
            var catalog = sp.GetRequiredService<IKnowledgeCatalog>();
            var (tierRoots, memoryRoots, knowledgeRoots) = ResolveRoots(catalog);
            ThrowIfMemoryOverlapsKnowledge(memoryRoots, knowledgeRoots);
            return new FileMemoryStore(tierRoots);
        });

        return services;
    }

    /// <summary>
    /// Resolves every enabled source's root once (via
    /// <see cref="CatalogPathResolver.TryResolve"/>): the per-tier memory roots
    /// for the store, plus the memory and knowledge (id, root) pairs used by the
    /// disjointness check. A source that fails to resolve is simply omitted.
    /// </summary>
    private static (Dictionary<MemoryTier, string> TierRoots, List<(string Id, string Root)> MemoryRoots, List<(string Id, string Root)> KnowledgeRoots) ResolveRoots(IKnowledgeCatalog catalog)
    {
        var snapshot = catalog.Current;
        var tierRoots = new Dictionary<MemoryTier, string>();
        var memoryRoots = new List<(string Id, string Root)>();
        var knowledgeRoots = new List<(string Id, string Root)>();

        foreach (var source in snapshot.Sources)
        {
            if (!source.Enabled)
            {
                continue;
            }

            if (source.Role == SourceRole.Memory
                && source.Tier is { } tier
                && CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var memResolved, out _))
            {
                tierRoots[tier] = memResolved!;
                memoryRoots.Add((source.Id, memResolved!));
            }
            else if (source.Role == SourceRole.Knowledge
                && CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var knowResolved, out _))
            {
                knowledgeRoots.Add((source.Id, knowResolved!));
            }
        }

        return (tierRoots, memoryRoots, knowledgeRoots);
    }

    /// <summary>
    /// The comparison used to test memory/knowledge root containment: an
    /// OS-appropriate comparison mirroring <c>CatalogPathResolver</c> and
    /// <c>FileMemoryStore.PathComparison</c> (case-insensitive on
    /// Windows/macOS, ordinal on a case-sensitive filesystem). Both roots being
    /// compared are already <see cref="Path.GetFullPath(string)"/>-canonical
    /// (produced by <see cref="CatalogPathResolver.TryResolve"/>).
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Fail-fast: a memory root that equals or nests within a knowledge root
    /// (or vice-versa) would be walked and searched by
    /// <see cref="DefaultKnowledgeResolver"/> as if it were shared knowledge,
    /// defeating scoped-memory isolation. The operator must reconfigure disjoint
    /// roots, so this throws an <see cref="InvalidOperationException"/> naming
    /// the offending source ids rather than silently building a leaky store.
    /// </summary>
    private static void ThrowIfMemoryOverlapsKnowledge(
        IReadOnlyList<(string Id, string Root)> memoryRoots,
        IReadOnlyList<(string Id, string Root)> knowledgeRoots)
    {
        foreach (var (memId, memRoot) in memoryRoots)
        {
            foreach (var (knowId, knowRoot) in knowledgeRoots)
            {
                if (IsWithin(knowRoot, memRoot) || IsWithin(memRoot, knowRoot))
                {
                    throw new InvalidOperationException(
                        $"Memory source '{memId}' root '{memRoot}' overlaps knowledge source '{knowId}' root '{knowRoot}': a memory root must be "
                        + "disjoint from every knowledge root, otherwise the scoped-memory subtree would be walked and searched as shared knowledge "
                        + "by the resolver. Reconfigure the sources so their roots do not nest.");
                }
            }
        }
    }

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing full paths with
    /// <see cref="PathComparison"/>. Mirrors
    /// <c>OKF4net.Internal.ReparsePoints.IsWithin</c> (not visible to this
    /// assembly) rather than duplicating its containment convention loosely.
    /// </summary>
    private static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, PathComparison);
    }
}

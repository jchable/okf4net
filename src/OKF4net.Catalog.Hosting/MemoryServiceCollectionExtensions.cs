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
    /// </remarks>
    public static IServiceCollection AddMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryStore>(sp =>
        {
            var catalog = sp.GetRequiredService<IKnowledgeCatalog>();
            var snapshot = catalog.Current;
            var tierRoots = new Dictionary<MemoryTier, string>();

            foreach (var source in snapshot.Sources)
            {
                if (!source.Enabled || source.Role != SourceRole.Memory || source.Tier is not { } tier)
                {
                    continue;
                }

                if (CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var resolved, out _))
                {
                    tierRoots[tier] = resolved!;
                }
            }

            return new FileMemoryStore(tierRoots);
        });

        return services;
    }
}

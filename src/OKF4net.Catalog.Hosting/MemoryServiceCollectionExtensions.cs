// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Registers a scoped <see cref="IMemoryStore"/> built from the catalog's
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

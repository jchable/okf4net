// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Wires a single OKF knowledge catalog into an
/// <see cref="IServiceCollection"/>: <see cref="AddKnowledge"/> is the one
/// entry point a host application needs.
/// </summary>
public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>
    /// Configures a <see cref="KnowledgeOptions"/> via <paramref name="configure"/>
    /// and registers a <see cref="FileKnowledgeCatalog"/> (as
    /// <see cref="IKnowledgeCatalog"/>) and a <see cref="KnowledgeResolverRouter"/>
    /// (as <see cref="IKnowledgeResolver"/>) built from it. The router
    /// dispatches each search to the strategy named by the query, or to
    /// <see cref="KnowledgeOptions.DefaultResolverStrategy"/> when the query
    /// names none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lifetimes.</b> Both <see cref="IKnowledgeCatalog"/> and
    /// <see cref="IKnowledgeResolver"/> are registered as singletons:
    /// <see cref="FileKnowledgeCatalog"/> owns a <see cref="FileSystemWatcher"/>
    /// and an in-memory snapshot that must be shared, not duplicated, across
    /// a host's lifetime, and <see cref="KnowledgeResolverRouter"/>
    /// (with the three strategy instances it owns) is stateless over that same singleton catalog. The
    /// <see cref="OKF4net.Catalog.KnowledgeCatalogOptions"/> built here is
    /// also registered (as an immutable singleton), for callers that want to
    /// inspect the resolved catalog file path/root directly.
    /// </para>
    /// <para>
    /// <b>Registration-time validation.</b> <paramref name="configure"/> runs
    /// immediately and <see cref="KnowledgeOptions"/> is validated
    /// (<see cref="KnowledgeOptions.Validate"/>) before this method returns:
    /// no <see cref="KnowledgeOptions.AddCatalogFile"/> call throws
    /// <see cref="ArgumentException"/>; more than one call throws
    /// <see cref="InvalidOperationException"/> (V1 supports exactly one
    /// catalog file -- <c>AddBundle</c> is cut as YAGNI).
    /// </para>
    /// <para>
    /// <b>Fail-fast on an invalid catalog.</b> The registrations use lazy
    /// singleton factories, so no catalog file is actually read here.
    /// <see cref="FileKnowledgeCatalog"/>'s constructor -- which parses and
    /// path-validates the manifest -- only runs the first time
    /// <see cref="IKnowledgeCatalog"/> (or <see cref="IKnowledgeResolver"/>,
    /// which depends on it) is resolved from the container. An invalid
    /// initial <c>catalog.json</c> therefore surfaces as a
    /// <see cref="OKF4net.Catalog.CatalogException"/> thrown out of that
    /// first <c>GetRequiredService&lt;IKnowledgeCatalog&gt;()</c> (or
    /// <c>IKnowledgeResolver</c>) call, not out of <see cref="AddKnowledge"/>
    /// itself.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Callback that configures the single catalog file via <see cref="KnowledgeOptions.AddCatalogFile"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentException">No <see cref="KnowledgeOptions.AddCatalogFile"/> call was made inside <paramref name="configure"/>.</exception>
    /// <exception cref="InvalidOperationException">More than one <see cref="KnowledgeOptions.AddCatalogFile"/> call was made inside <paramref name="configure"/>.</exception>
    public static IServiceCollection AddKnowledge(this IServiceCollection services, Action<KnowledgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KnowledgeOptions();
        configure(options);
        options.Validate();

        var catalogOptions = new KnowledgeCatalogOptions
        {
            CatalogFilePath = options.CatalogFilePath!,
            CatalogRoot = options.CatalogRoot!,
        };

        services.TryAddSingleton(catalogOptions);
        services.TryAddSingleton<IKnowledgeCatalog>(_ => new FileKnowledgeCatalog(catalogOptions));
        var defaultStrategy = options.DefaultResolverStrategy;
        var defaultFairnessQuota = options.DefaultFairnessQuota;
        services.TryAddSingleton<IKnowledgeResolver>(sp => new KnowledgeResolverRouter(
            sp.GetRequiredService<IKnowledgeCatalog>(), defaultStrategy, defaultFairnessQuota));

        return services;
    }
}

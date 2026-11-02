// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// An <see cref="IBundleProviderFactory"/> for the catalog tests: serves one provider type,
/// accepts the settings it is given, and creates providers through a callback (by default an
/// <see cref="InMemoryBundleProvider"/> named after the source's id), recording every source
/// it was asked to create a provider for.
/// </summary>
public sealed class TestProviderFactory(string providerType, Func<KnowledgeCatalogSource, IBundleProvider>? create = null, params string[] acceptedSettings) : IBundleProviderFactory
{
    private readonly List<KnowledgeCatalogSource> _created = [];

    public string ProviderType { get; } = providerType;

    public IReadOnlySet<string> AcceptedSettings { get; } = new HashSet<string>(acceptedSettings, StringComparer.Ordinal);

    /// <summary>Every source this factory created a provider for, in order.</summary>
    public IReadOnlyList<KnowledgeCatalogSource> Created => _created;

    public IBundleProvider Create(KnowledgeCatalogSource source)
    {
        _created.Add(source);
        return create is null ? new Tests.Providers.InMemoryBundleProvider(id: source.Id) : create(source);
    }
}

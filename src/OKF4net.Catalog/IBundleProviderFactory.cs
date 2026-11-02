// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Builds the <see cref="IBundleProvider"/> of a <c>catalog.json</c> source whose
/// <c>provider</c> names this factory's <see cref="ProviderType"/> (spec §9, "nommer un
/// provider dans catalog.json"). A host registers one factory per provider type it offers
/// (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>); the type <c>filesystem</c> is
/// built in and cannot be registered. Each factory declares the settings it accepts, so a
/// manifest cannot smuggle a credential in: a setting the factory does not list is rejected
/// with <see cref="CatalogDiagnosticCode.IllegalSetting"/> before anything is created, and the
/// credentials a provider needs (a client, a key) are given to the factory by the host, never
/// read from the manifest (spec §7).
/// </summary>
public interface IBundleProviderFactory
{
    /// <summary>The <c>provider</c> value this factory serves; never <c>filesystem</c>.</summary>
    string ProviderType { get; }

    /// <summary>The names a source's <c>settings</c> may carry for this provider type; any other name is rejected.</summary>
    IReadOnlySet<string> AcceptedSettings { get; }

    /// <summary>
    /// Creates the provider of <paramref name="source"/>, whose
    /// <see cref="KnowledgeCatalogSource.Settings"/> carry only accepted names. Called when
    /// the catalog builds the source's handle (at construction, and at a reload that changed
    /// the source's effective configuration); what it throws rejects that load as
    /// <see cref="CatalogDiagnosticCode.ProviderUnavailable"/>.
    /// </summary>
    /// <param name="source">The validated source.</param>
    IBundleProvider Create(KnowledgeCatalogSource source);
}

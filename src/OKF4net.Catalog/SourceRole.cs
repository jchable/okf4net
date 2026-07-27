// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The role a <see cref="KnowledgeCatalogSource"/> plays in the catalog.
/// </summary>
/// <remarks>
/// Manifests that request any other role string are rejected with
/// <see cref="CatalogDiagnosticCode.IllegalRole"/>.
/// </remarks>
public enum SourceRole
{
    /// <summary>An ordinary read-only knowledge bundle source.</summary>
    Knowledge,

    /// <summary>
    /// A scoped read+write memory source. Requires a <see cref="MemoryTier"/>
    /// (<c>tier</c> in the manifest); not searched by <see cref="IKnowledgeResolver"/>;
    /// fed to <c>IMemoryStore</c> instead (a later lot).
    /// </summary>
    Memory,
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The role a <see cref="KnowledgeCatalogSource"/> plays in the catalog.
/// </summary>
/// <remarks>
/// V1 recognizes only <see cref="Knowledge"/>; a future <c>Memory</c> role is
/// reserved for V2 and is intentionally not defined here yet. Manifests that
/// request any other role string are rejected with
/// <see cref="CatalogDiagnosticCode.IllegalRole"/>.
/// </remarks>
public enum SourceRole
{
    /// <summary>An ordinary read-only knowledge bundle source.</summary>
    Knowledge,
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A single validated entry from a <c>catalog.json</c> manifest's
/// <c>sources</c> array.
/// </summary>
/// <param name="Id">
/// The source's unique identifier; validated as a single OKF concept-id
/// segment (<see cref="OKF4net.ConceptId.ValidateSegment"/>) and unique
/// within the owning <see cref="KnowledgeCatalogSnapshot"/>.
/// </param>
/// <param name="Path">The non-empty, manifest-relative path to the source bundle.</param>
/// <param name="Priority">
/// Ordering priority among sources; defaults to <c>0</c> when omitted from the manifest.
/// </param>
/// <param name="Enabled">Whether the source is active; defaults to <c>true</c> when omitted.</param>
/// <param name="Role">
/// The source's role; defaults to <see cref="SourceRole.Knowledge"/>, the only legal
/// value in V1.
/// </param>
public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role);

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
/// <param name="Path">
/// The non-empty, manifest-relative path to the source bundle, for a
/// <see cref="FileSystemProviderType"/> source; the empty string for any other
/// <see cref="ProviderType"/>, whose storage is named by <see cref="Settings"/>.
/// </param>
/// <param name="Priority">
/// Ordering priority among sources; defaults to <c>0</c> when omitted from the manifest.
/// </param>
/// <param name="Enabled">Whether the source is active; defaults to <c>true</c> when omitted.</param>
/// <param name="Role">
/// The source's role; defaults to <see cref="SourceRole.Knowledge"/> when omitted from
/// the manifest.
/// </param>
/// <param name="Tier">
/// The memory tier, for a <see cref="SourceRole.Memory"/> source; <see langword="null"/>
/// for a <see cref="SourceRole.Knowledge"/> source.
/// </param>
public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role, MemoryTier? Tier = null)
{
    /// <summary>The built-in provider type: a bundle directory under the catalog root, named by <see cref="Path"/>.</summary>
    public const string FileSystemProviderType = "filesystem";

    /// <summary>
    /// The source's <c>provider</c> (spec §9): <see cref="FileSystemProviderType"/> when the
    /// manifest names none, else the type of a factory the host registered
    /// (<see cref="IBundleProviderFactory.ProviderType"/>). A <see cref="SourceRole.Memory"/>
    /// source is always a filesystem one.
    /// </summary>
    public string ProviderType { get; init; } = FileSystemProviderType;

    /// <summary>The source's <c>settings</c>, accepted by its provider's factory; empty for a filesystem source.</summary>
    public ProviderSettings Settings { get; init; } = ProviderSettings.Empty;

    /// <summary><see langword="true"/> for a <see cref="FileSystemProviderType"/> source.</summary>
    public bool IsFileSystem => string.Equals(ProviderType, FileSystemProviderType, StringComparison.Ordinal);
}

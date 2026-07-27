// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// An immutable, validated catalog manifest snapshot (no filesystem access yet).
/// </summary>
/// <param name="Version">The manifest schema version; always <c>1</c> for a successfully parsed snapshot.</param>
/// <param name="Sources">
/// The manifest's sources, in the order they appeared in the manifest (ordinal-stable).
/// A genuine read-only view (<see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>
/// as produced by the parser): it cannot be downcast to a mutable list and modified, which
/// matters because published snapshots are shared across concurrent readers.
/// </param>
/// <param name="ManifestDirectory">
/// The directory the manifest was loaded from, as supplied by the caller; recorded
/// verbatim and not touched or validated by the parser.
/// </param>
/// <param name="Generation">
/// A monotonic counter assigned by the catalog on each successful publish (see
/// Task 4); the parser produces snapshots with <c>Generation 0</c> and the
/// catalog stamps the real value on publish.
/// </param>
public sealed record KnowledgeCatalogSnapshot(
    int Version, IReadOnlyList<KnowledgeCatalogSource> Sources, string ManifestDirectory, long Generation);

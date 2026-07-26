// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// An <see cref="IKnowledgeSource"/> over a single, already-resolved OKF
/// bundle directory on disk.
/// </summary>
/// <remarks>
/// <b>Stateless by design.</b> Each <see cref="SearchAsync"/> call performs
/// its own <see cref="Bundle.Load(string)"/> (permissive, §9) and searches
/// via the core <see cref="ConceptSearch.Search"/> -- the same scorer
/// <c>okf search</c>/<c>OkfBundleTools.Search</c> use, so ranking and scoring
/// are identical by construction rather than by parallel re-implementation.
/// Loading per call is simple and correct but not free; a bundle cache with
/// its own invalidation policy is an explicit V2 concern and is deliberately
/// not attempted here (mirrors <see cref="IKnowledgeCatalog.ReloadAsync"/>
/// being "the reliable path" rather than relying on implicit caching).
/// </remarks>
public sealed class OkfBundleKnowledgeSource : IKnowledgeSource
{
    private readonly string _bundleDirectory;

    /// <summary>
    /// Creates a source over <paramref name="bundleDirectory"/>, an already
    /// safety-validated, resolved absolute directory (see
    /// <see cref="CatalogPathResolver.TryResolve"/>) -- this type does not
    /// itself re-derive or re-validate a path from a manifest-relative one.
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="bundleDirectory">The resolved, absolute bundle root directory to load and search.</param>
    public OkfBundleKnowledgeSource(string id, string bundleDirectory)
    {
        Id = id;
        _bundleDirectory = bundleDirectory;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/> is handled defensively here
    /// too (<see cref="ConceptSearch.Search"/> already returns an empty list
    /// for zero query terms) even though <see cref="DefaultKnowledgeResolver"/>
    /// rejects it earlier -- this type has no other caller-contract to lean
    /// on.
    /// </remarks>
    public ValueTask<KnowledgeSearchResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        Bundle bundle;
        try
        {
            bundle = Bundle.Load(_bundleDirectory);
        }
        catch (OkfException e)
        {
            return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(
                [],
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{_bundleDirectory}' could not be loaded: {e.Message}")));
        }

        var scored = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);

        var passages = scored
            .Select(hit => new KnowledgePassage(
                SourceId: Id,
                ConceptId: hit.Concept.Id.ToString(),
                Title: hit.Concept.Document.Frontmatter.Title,
                Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                Score: hit.Score,
                BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path)))
            .ToList();

        return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(passages, null));
    }
}

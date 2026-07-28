// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// An <see cref="IKnowledgeSource"/> over a single, already-resolved OKF
/// bundle directory on disk.
/// </summary>
/// <remarks>
/// <b>Stateless by design.</b> Each <see cref="SearchAsync"/> call performs
/// its own <see cref="Bundle.Load(string)"/> (permissive, §11) and searches
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
    /// A null/blank <see cref="KnowledgeQuery.Text"/> is handled defensively
    /// here, before it ever reaches <see cref="ConceptSearch.Search"/>: the
    /// nullable-reference annotation on <see cref="KnowledgeQuery.Text"/> is
    /// only a compile-time hint, not a runtime guarantee (a
    /// deserialized/reflected/`!`-suppressed <see langword="null"/> reaches
    /// this method just fine), and <c>string.Split</c> on a
    /// <see langword="null"/> receiver would otherwise throw a
    /// <see cref="NullReferenceException"/> -- violating this type's own
    /// documented never-throw contract. Returning an empty result (no
    /// diagnostic) here, rather than throwing, mirrors
    /// <see cref="ConceptSearch.Search"/>'s own "zero query terms -&gt; empty
    /// list" behaviour for a merely-blank (non-null) query: a blank query is
    /// not a *source* error, even though <see cref="DefaultKnowledgeResolver"/>
    /// treats it as a caller error one layer up (this type has no such
    /// caller-contract to lean on).
    /// </remarks>
    public ValueTask<KnowledgeSearchResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(Array.Empty<KnowledgePassage>(), null));
        }

        Bundle bundle;
        try
        {
            bundle = Bundle.Load(_bundleDirectory);
        }
        catch (OkfException e)
        {
            return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(
                Array.Empty<KnowledgePassage>(),
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{_bundleDirectory}' could not be loaded: {e.Message}")));
        }

        var scored = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);

        var passages = scored
            .Select(hit =>
            {
                var fm = hit.Concept.Document.Frontmatter;
                var lc = fm.Lifecycle;
                return new KnowledgePassage(
                    SourceId: Id,
                    ConceptId: hit.Concept.Id.ToString(),
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    // Normalized to '/' regardless of OS -- matches ConceptId's
                    // '/' segment convention and travels correctly for a future
                    // <okf-context> adapter, rather than leaking a Windows
                    // backslash-separated path into cross-platform output.
                    BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path).Replace(Path.DirectorySeparatorChar, '/'),
                    TrustTier: fm.TrustTier,
                    Lifecycle: lc);
            })
            .ToList();

        // .AsReadOnly() wraps the mutable List<T> in a genuine
        // ReadOnlyCollection<T> view -- otherwise a caller could
        // `(List<KnowledgePassage>)result.Passages` and mutate a published
        // KnowledgeSearchResult (same reasoning as KnowledgeContext's own
        // passages/diagnostics; see DefaultKnowledgeResolver).
        return new ValueTask<KnowledgeSearchResult>(new KnowledgeSearchResult(passages.AsReadOnly(), null));
    }
}

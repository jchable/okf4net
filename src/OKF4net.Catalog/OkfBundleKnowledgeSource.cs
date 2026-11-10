// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// An <see cref="IKnowledgeSource"/> over one OKF bundle, served from a
/// <see cref="BundleHandle"/> (spec §5.2, §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>One image, refreshed under the handle's policy.</b> Each <see cref="SearchAsync"/>
/// call reads <see cref="BundleHandle.GetCurrentAsync"/> and searches it with the core
/// <see cref="ConceptSearch.Search"/> -- the same scorer <c>okf search</c>/<c>OkfBundleTools.Search</c>
/// use, so ranking and scoring are identical by construction rather than by parallel
/// re-implementation. The handle decides when the image is reloaded: a catalog source
/// is served from the handle its catalog keeps across requests (the snapshot's handles,
/// <see cref="BundleRefreshPolicy.OnAccess"/> by default; a hand-built snapshot without handles
/// falls back to a fresh <c>OnAccess</c> handle per request, with no subscription and nothing to
/// dispose), so an unchanged bundle is
/// loaded once and a changed one is seen at the next search. A reload that fails keeps
/// the previous image served (<see cref="BundleHandle.IsStale"/>, which the catalog's
/// resolvers report as <see cref="KnowledgeDiagnosticCode.SourceStale"/>); only a bundle
/// that never loaded is reported as <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>. A raw
/// exception from a host provider (neither a provider exception, an I/O error nor an access error)
/// is a load failure like those: the handle wraps it in a <see cref="BundleLoadException"/> that
/// names its type only, so a source that had loaded is served stale and a first load that fails is
/// unavailable.
/// </para>
/// <para>
/// <b>The sharing is explicit.</b> The directory constructor builds a handle of its own
/// over a <see cref="FileSystemBundleProvider"/>, under <see cref="BundleRefreshPolicy.OnAccess"/>
/// (the policy of a catalog source: the bundle's token is compared at every search, so an
/// external change is still seen, where the source used to reload at every call). The
/// filesystem token is weak, though (<c>FileSystemBundleProvider.GetVersion</c>: name, length
/// and modification time): two edits of the same size within one modification-time tick can
/// be missed until the next change, so a host that must see every edit calls
/// <c>Handle.Refresh()</c> or <c>Handle.Invalidate()</c>. Two
/// sources built that way over one directory have two images; a host that wants one image
/// per source passes the same <see cref="BundleHandle"/> to both.
/// </para>
/// </remarks>
public sealed class OkfBundleKnowledgeSource : IKnowledgeSource
{
    /// <summary>
    /// Creates a source over <paramref name="bundleDirectory"/>, an already
    /// safety-validated, resolved absolute directory (see
    /// <see cref="CatalogPathResolver.TryResolve"/>) -- this type does not
    /// itself re-derive or re-validate a path from a manifest-relative one.
    /// The source owns a handle of its own over that directory, refreshed on
    /// access (see the type remarks).
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="bundleDirectory">The resolved, absolute bundle root directory to load and search.</param>
    public OkfBundleKnowledgeSource(string id, string bundleDirectory)
        : this(id, new BundleHandle(new FileSystemBundleProvider(bundleDirectory), refreshPolicy: BundleRefreshPolicy.OnAccess))
    {
    }

    /// <summary>
    /// Creates a source served from <paramref name="handle"/>, whose image, options and
    /// refresh policy it shares with every other consumer of that handle. The source never
    /// disposes the handle: the caller owns it (a handle under a notify policy keeps its
    /// subscription alive until it is disposed).
    /// </summary>
    /// <param name="id">The source's id, echoed onto every produced <see cref="KnowledgePassage.SourceId"/>.</param>
    /// <param name="handle">The handle the bundle is read from.</param>
    public OkfBundleKnowledgeSource(string id, BundleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(handle);
        Id = id;
        Handle = handle;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <summary>The handle the bundle is read from.</summary>
    public BundleHandle Handle { get; }

    /// <summary>
    /// The handle's location for a diagnostic: a host provider's getter that throws (it is documented stable and
    /// non-throwing, but is a host getter read at every search) must not turn a diagnostic into an exception.
    /// </summary>
    internal string LocationForDiagnostic
    {
        get
        {
            try
            {
                return Handle.Location;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return "(location unavailable)";
            }
        }
    }

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
    /// not a *source* error, even though <see cref="GroupedKnowledgeResolver"/>
    /// treats it as a caller error one layer up (this type has no such
    /// caller-contract to lean on).
    /// </remarks>
    public async ValueTask<KnowledgeSearchResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return new KnowledgeSearchResult(Array.Empty<KnowledgePassage>(), null);
        }

        Bundle bundle;
        try
        {
            bundle = await Handle.GetCurrentAsync(ct).ConfigureAwait(false);
        }
        catch (OkfException e)
        {
            return new KnowledgeSearchResult(
                Array.Empty<KnowledgePassage>(),
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{LocationForDiagnostic}' could not be loaded: {e.Message}"));
        }
        catch (Exception e) when (e is not OutOfMemoryException && !(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A guard, not the usual path: the handle wraps a host provider's raw exception into a BundleLoadException
            // (caught above), so what reaches here is an exception the handle could not wrap (a host TimeProvider
            // that throws, say). This source is unavailable, by type only -- a host's message can name a bucket or
            // a path (spec §7) -- and the sources around it are still searched. A cancellation raised here that is
            // not the caller's (the handle's load holds no caller token) is reported the same way;
            // only a cancellation of the CALLER's token still propagates.
            return new KnowledgeSearchResult(
                Array.Empty<KnowledgePassage>(),
                new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    Id,
                    $"Source '{Id}' bundle at '{LocationForDiagnostic}' could not be loaded ({e.GetType().Name})."));
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
                    BundleRelativePath: RelativePathOf(bundle, hit.Concept),
                    TrustTier: fm.TrustTier,
                    Lifecycle: lc);
            })
            .ToList();

        // .AsReadOnly() wraps the mutable List<T> in a genuine
        // ReadOnlyCollection<T> view -- otherwise a caller could
        // `(List<KnowledgePassage>)result.Passages` and mutate a published
        // KnowledgeSearchResult (same reasoning as KnowledgeContext's own
        // passages/diagnostics; see GroupedKnowledgeResolver).
        return new KnowledgeSearchResult(passages.AsReadOnly(), null);
    }

    /// <summary>
    /// The passage's bundle-relative path: the concept's key, '/'-separated on every OS --
    /// the same text <c>Path.GetRelativePath(root, concept.Path)</c> gave with the separator
    /// normalised, which stays the fallback for a concept built by hand (no key).
    /// </summary>
    internal static string RelativePathOf(Bundle bundle, Concept concept) =>
        concept.Key is { } key
            ? key.ToString()
            : Path.GetRelativePath(bundle.Location, concept.Path).Replace(Path.DirectorySeparatorChar, '/');
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The grouped-by-source <see cref="IKnowledgeResolver"/> strategy: fans a
/// query out across every currently enabled <see cref="SourceRole.Knowledge"/>
/// source of an <see cref="IKnowledgeCatalog"/> and concatenates the results
/// **grouped by source, in priority order** -- no cross-source fusion,
/// deduplication, or merged ranking. <see cref="SourceRole.Memory"/> sources
/// are never searched here; they feed <c>IMemoryStore</c> instead (spec §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Each enabled source is searched through the <see cref="Providers.BundleHandle"/> the
/// catalog keeps for it (<see cref="KnowledgeCatalogSnapshot.Handles"/>, spec §9): the
/// image survives from one search to the next and is refreshed under the source's policy
/// (<see cref="KnowledgeCatalogOptions.RefreshPolicy"/>), and the catalog root's confinement
/// is re-checked by the provider before every access. A source whose bundle never loaded
/// (its first load failed) yields a <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>
/// diagnostic for that source alone; the remaining sources are still searched. A source
/// that loaded once and whose later reload fails (its directory deleted, or replaced by a
/// link, after the first search) is not unavailable: its last good image is served, with a
/// <see cref="KnowledgeDiagnosticCode.SourceStale"/> diagnostic naming the failure by its
/// exception type (spec §5.2, §7).
/// </para>
/// <para>
/// A snapshot that carries no handle for a source (one built by hand, or by
/// <see cref="CatalogManifestParser.TryParse"/>) is served as before the seam: the source's
/// manifest-relative <see cref="KnowledgeCatalogSource.Path"/> is re-resolved at every
/// search via <see cref="CatalogPathResolver.TryResolve"/>, with the same containment check,
/// and a fresh <see cref="OkfBundleKnowledgeSource"/> loads the bundle for that search
/// (<see cref="CatalogSourceOpener"/>).
/// </para>
/// </remarks>
public sealed class GroupedKnowledgeResolver : IKnowledgeResolver
{
    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? _defaultSourceVisibilityPolicy;

    /// <summary>Creates a resolver over <paramref name="catalog"/>; <paramref name="clock"/> supplies "now" for stale-policy filtering (defaults to the system clock).</summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "now" for stale-policy filtering; defaults to the system clock.</param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy applied when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction -- every
    /// enabled source stays visible to every caller.
    /// </param>
    public GroupedKnowledgeResolver(
        IKnowledgeCatalog catalog,
        IOkfClock? clock = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null)
    {
        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultSourceVisibilityPolicy = defaultSourceVisibilityPolicy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/> throws
    /// <see cref="ArgumentException"/> rather than being reported as a
    /// diagnostic: unlike <see cref="KnowledgeDiagnosticCode.NoMatches"/> (a
    /// legitimate zero-result outcome for a well-formed query) or
    /// <see cref="KnowledgeDiagnosticCode.NoEnabledSources"/> (a legitimate
    /// catalog state), a blank query text is a caller/programming error --
    /// there is no sensible search to even attempt. A non-positive
    /// <see cref="KnowledgeQuery.FairnessQuota"/> or an undefined
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> throw the same way even
    /// though this strategy ignores both otherwise, so a malformed query
    /// fails identically regardless of which strategy runs it.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        // Deliberately NOT declared async: validation must throw
        // SYNCHRONOUSLY, at the call site, the same way every other
        // IKnowledgeResolver implementation in this codebase does it. An
        // async method defers even its very first statement's exceptions
        // into the returned ValueTask instead of throwing them directly --
        // invisible to a caller that awaits immediately, but a real
        // divergence for one that doesn't. SearchCoreAsync carries the
        // actual (async) work; this method's only job is the validate-then-
        // delegate split that keeps the throw synchronous.
        ResolverGuards.ValidateQuery(query);
        return SearchCoreAsync(query, ct);
    }

    private async ValueTask<KnowledgeContext> SearchCoreAsync(KnowledgeQuery query, CancellationToken ct)
    {
        var snapshot = _catalog.Current;
        var enabledSources = snapshot.Sources
            .Where(s => s.Enabled && s.Role == SourceRole.Knowledge)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var preFilterCount = enabledSources.Count;
        enabledSources = SourceVisibility.Filter(enabledSources, query, _defaultSourceVisibilityPolicy);

        if (enabledSources.Count == 0)
        {
            // Same diagnostic code either way (the plan deliberately reuses
            // NoEnabledSources rather than minting a new code for a
            // visibility-narrowed-to-zero result) but a different message:
            // "genuinely no enabled sources" and "sources are enabled but
            // none are visible to this caller" are different operator
            // problems (catalog.json vs. the visibility policy/
            // PermittedSourceIds), so conflating them under one message
            // would misdirect debugging.
            var message = preFilterCount > 0
                ? $"No knowledge sources are visible to this caller ({preFilterCount} enabled source(s) were excluded by source-visibility filtering)."
                : "No enabled knowledge sources are configured.";

            // Array.AsReadOnly() wraps the array in a genuine
            // ReadOnlyCollection<T> view -- otherwise a caller could
            // `(KnowledgeDiagnostic[])context.Diagnostics` and mutate a
            // published KnowledgeContext (same reasoning as the main path's
            // .AsReadOnly() below).
            return new KnowledgeContext(
                query,
                snapshot.Generation,
                Array.Empty<KnowledgePassage>(),
                Array.AsReadOnly(new[] { new KnowledgeDiagnostic(KnowledgeDiagnosticCode.NoEnabledSources, null, message) }));
        }

        var passages = new List<KnowledgePassage>();
        var diagnostics = new List<KnowledgeDiagnostic>();
        var anySourceSearchedSuccessfully = false;

        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogSourceOpener.TryOpen(_catalog, snapshot, source, out var bundleSource, out _, out _, out var unavailable))
            {
                diagnostics.Add(unavailable!);
                continue;
            }

            var result = await bundleSource!.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
                diagnostics.Add(result.Diagnostic);
                continue;
            }

            anySourceSearchedSuccessfully = true;
            if (CatalogSourceOpener.StaleDiagnostic(bundleSource) is { } stale)
            {
                diagnostics.Add(stale);
            }

            passages.AddRange(result.Passages);
        }

        var now = _clock.Now;
        var admitted = passages
            .Where(p => query.StalePolicy.Admits(p.Lifecycle, now))
            .ToList();

        if (admitted.Count == 0 && anySourceSearchedSuccessfully)
        {
            diagnostics.Add(new KnowledgeDiagnostic(
                KnowledgeDiagnosticCode.NoMatches, null, $"No passages matched query '{query.Text}'."));
        }

        // .AsReadOnly() wraps each list in a genuine ReadOnlyCollection<T>
        // view rather than exposing the mutable List<T> behind IReadOnlyList<T>
        // -- otherwise a caller could `(List<T>)context.Passages` and mutate a
        // published KnowledgeContext (same containment reasoning as
        // KnowledgeCatalogSnapshot.Sources; see CatalogManifestParser).
        return new KnowledgeContext(query, snapshot.Generation, admitted.AsReadOnly(), diagnostics.AsReadOnly());
    }
}

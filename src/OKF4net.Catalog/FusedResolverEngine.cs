// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// A passage paired with the <see cref="KnowledgeCatalogSource.Priority"/> of
/// the source it came from. <see cref="KnowledgePassage"/> deliberately does
/// not carry priority (it is a catalog-configuration concern, not a property
/// of the matched concept), but the fused strategies' comparers need it, so
/// it rides alongside through ranking and is dropped again before the
/// <see cref="KnowledgeContext"/> is built.
/// </summary>
internal readonly record struct RankedPassage(KnowledgePassage Passage, int Priority);

/// <summary>
/// The shared pipeline behind every fusing <see cref="IKnowledgeResolver"/>
/// strategy: resolve and dedup the enabled sources, fan out, apply the
/// query's <see cref="StalePolicy"/>, then rank with a caller-supplied
/// comparer. <see cref="MergedKnowledgeResolver"/> and
/// <c>PriorityWeightedKnowledgeResolver</c> differ ONLY in that
/// comparer -- keeping the rest here means dedup semantics, diagnostics, and
/// the never-throw contract cannot drift between the two.
/// </summary>
/// <remarks>
/// Deliberately NOT used by <see cref="GroupedKnowledgeResolver"/>, whose
/// grouped output has no cross-source ranking step to share and whose
/// behaviour is frozen.
/// </remarks>
internal static class FusedResolverEngine
{
    /// <summary>
    /// Runs <paramref name="query"/> across <paramref name="catalog"/>'s
    /// enabled knowledge sources and returns one fused, ranked
    /// <see cref="KnowledgeContext"/>.
    /// </summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "today" for stale-policy filtering.</param>
    /// <param name="query">The query to run across every enabled knowledge source.</param>
    /// <param name="comparer">
    /// The ranking order. Must impose a TOTAL order (no ties left to
    /// <see cref="List{T}.Sort(IComparer{T})"/>'s unstable tie-breaking), so
    /// the same catalog and query always produce the same sequence.
    /// </param>
    /// <param name="fairnessQuota">
    /// The maximum number of CONSECUTIVE passages one source may contribute
    /// before another source's next-best passage is pulled ahead of it;
    /// <see langword="null"/> disables the reorder entirely. Already
    /// validated by <see cref="ResolverGuards"/> at the public boundary. See
    /// <see cref="ApplyFairness"/>.
    /// </param>
    /// <param name="ct">A cancellation token observed between sources.</param>
    /// <exception cref="ArgumentException"><paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null, empty, or whitespace.</exception>
    internal static async ValueTask<KnowledgeContext> SearchAsync(
        IKnowledgeCatalog catalog,
        IOkfClock clock,
        KnowledgeQuery query,
        IComparer<RankedPassage> comparer,
        int? fairnessQuota,
        CancellationToken ct)
    {
        ResolverGuards.ValidateQuery(query);

        var snapshot = catalog.Current;
        var enabledSources = snapshot.Sources
            .Where(s => s.Enabled && s.Role == SourceRole.Knowledge)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        if (enabledSources.Count == 0)
        {
            return new KnowledgeContext(
                query,
                snapshot.Generation,
                Array.Empty<KnowledgePassage>(),
                Array.AsReadOnly(new[]
                {
                    new KnowledgeDiagnostic(KnowledgeDiagnosticCode.NoEnabledSources, null, "No enabled knowledge sources are configured."),
                }));
        }

        var diagnostics = new List<KnowledgeDiagnostic>();

        // Resolve + dedup BEFORE searching. Two manifest entries pointing at
        // the same directory are the same bundle: searching both would load
        // and score it twice only to discard half the results. enabledSources
        // is already in priority-then-id order, so the first entry reaching a
        // given directory is the survivor by construction.
        var seenDirectories = new HashSet<string>(StringComparer.FromComparison(CatalogPathResolver.PathComparison));
        var resolved = new List<(KnowledgeCatalogSource Source, string Directory)>();

        foreach (var source in enabledSources)
        {
            ct.ThrowIfCancellationRequested();

            if (!CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var directory, out var pathDiagnostic))
            {
                diagnostics.Add(new KnowledgeDiagnostic(
                    KnowledgeDiagnosticCode.SourceUnavailable,
                    source.Id,
                    $"Source '{source.Id}' path could not be re-resolved: {pathDiagnostic!.Message}"));
                continue;
            }

            if (seenDirectories.Add(directory!))
            {
                resolved.Add((source, directory!));
            }
        }

        var ranked = new List<RankedPassage>();
        var anySourceSearchedSuccessfully = false;
        var today = clock.Today;

        foreach (var (source, directory) in resolved)
        {
            ct.ThrowIfCancellationRequested();

            var bundleSource = new OkfBundleKnowledgeSource(source.Id, directory);
            var result = await bundleSource.SearchAsync(query, ct).ConfigureAwait(false);

            if (result.Diagnostic is not null)
            {
                diagnostics.Add(result.Diagnostic);
                continue;
            }

            anySourceSearchedSuccessfully = true;
            foreach (var passage in result.Passages)
            {
                if (query.StalePolicy.Admits(passage.Lifecycle, today))
                {
                    ranked.Add(new RankedPassage(passage, source.Priority));
                }
            }
        }

        ranked.Sort(comparer);

        if (fairnessQuota is { } quota && ranked.Count > 1)
        {
            ranked = ApplyFairness(ranked, quota);
        }

        if (ranked.Count == 0 && anySourceSearchedSuccessfully)
        {
            diagnostics.Add(new KnowledgeDiagnostic(
                KnowledgeDiagnosticCode.NoMatches, null, $"No passages matched query '{query.Text}'."));
        }

        var passages = ranked.Select(r => r.Passage).ToList();

        // .AsReadOnly() wraps each list in a genuine ReadOnlyCollection<T>
        // view rather than exposing the mutable List<T> behind
        // IReadOnlyList<T> -- otherwise a caller could cast a published
        // KnowledgeContext's collections back and mutate them.
        return new KnowledgeContext(query, snapshot.Generation, passages.AsReadOnly(), diagnostics.AsReadOnly());
    }

    /// <summary>
    /// Reorders an already-ranked list so no source contributes more than
    /// <paramref name="quota"/> CONSECUTIVE passages while another source
    /// still has passages left. Returns a new list containing exactly the
    /// same passages -- nothing is dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely a reordering, because the problem it solves is early
    /// truncation, not result size: a consumer spending a token budget
    /// top-down (an agent context provider, say) stops partway down the list,
    /// and without this a single prolific source's whole run can consume the
    /// budget before any other source is reached. A consumer that reads the
    /// entire list is unaffected by design.
    /// </para>
    /// <para>
    /// When every remaining passage belongs to one source there is nothing to
    /// pull forward, so the quota simply stops applying and the rest of that
    /// source drains in ranked order. The quota is a fairness goal for
    /// interleavable results, not a hard guarantee obtainable from a
    /// single-source result set.
    /// </para>
    /// <para>
    /// Quadratic in the worst case (each pick may scan the remainder for a
    /// different source), which is deliberate: a search result is tens to low
    /// hundreds of passages, and a linear-time bucketed variant would cost
    /// more in complexity than it saves in time at that size.
    /// </para>
    /// </remarks>
    internal static List<RankedPassage> ApplyFairness(List<RankedPassage> ranked, int quota)
    {
        var remaining = new LinkedList<RankedPassage>(ranked);
        var result = new List<RankedPassage>(ranked.Count);

        string? runSource = null;
        var runLength = 0;

        while (remaining.First is { } head)
        {
            var pick = head;

            // The head would extend the current run past the quota: look for
            // the best-ranked passage from any OTHER source to interleave. If
            // there is none, keep the head -- draining is the only option.
            if (runLength >= quota && string.Equals(head.Value.Passage.SourceId, runSource, StringComparison.Ordinal))
            {
                var candidate = head.Next;
                while (candidate is not null && string.Equals(candidate.Value.Passage.SourceId, runSource, StringComparison.Ordinal))
                {
                    candidate = candidate.Next;
                }

                pick = candidate ?? head;
            }

            var chosen = pick.Value;
            remaining.Remove(pick);
            result.Add(chosen);

            if (string.Equals(chosen.Passage.SourceId, runSource, StringComparison.Ordinal))
            {
                runLength++;
            }
            else
            {
                runSource = chosen.Passage.SourceId;
                runLength = 1;
            }
        }

        return result;
    }
}

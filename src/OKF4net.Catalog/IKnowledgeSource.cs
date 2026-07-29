// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A single searchable knowledge source -- one bundle, in V1
/// (<see cref="OkfBundleKnowledgeSource"/>). A <see cref="GroupedKnowledgeResolver"/>
/// fans a query out across every enabled <see cref="KnowledgeCatalogSource"/>
/// as one of these.
/// </summary>
public interface IKnowledgeSource
{
    /// <summary>The source's id (matches its owning <see cref="KnowledgeCatalogSource.Id"/>).</summary>
    string Id { get; }

    /// <summary>
    /// Searches this source for <paramref name="query"/>. Never throws for a
    /// <em>data</em> condition -- any failure (e.g. the source's bundle could
    /// not be loaded) is reported via <see cref="KnowledgeSearchResult.Diagnostic"/>
    /// with an empty <see cref="KnowledgeSearchResult.Passages"/> instead. The
    /// only exceptions this method throws are <see cref="ArgumentNullException"/>
    /// for a <see langword="null"/> <paramref name="query"/> and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is
    /// cancelled -- both caller/programming errors or explicit cancellation,
    /// not source-data failures.
    /// </summary>
    ValueTask<KnowledgeSearchResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
}

/// <summary>
/// The result of searching a single <see cref="IKnowledgeSource"/>.
/// </summary>
/// <param name="Passages">
/// The matching passages, in the source's own relevance order (for
/// <see cref="OkfBundleKnowledgeSource"/>: the core scorer's
/// descending-score, then ascending concept-id order). Empty when the
/// source found nothing, or when <paramref name="Diagnostic"/> reports a
/// failure.
/// </param>
/// <param name="Diagnostic">
/// Non-<see langword="null"/> only when the source itself could not be
/// searched at all (e.g. <see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>);
/// <see langword="null"/> for a normal search, including one that matched
/// nothing.
/// </param>
public sealed record KnowledgeSearchResult(IReadOnlyList<KnowledgePassage> Passages, KnowledgeDiagnostic? Diagnostic);

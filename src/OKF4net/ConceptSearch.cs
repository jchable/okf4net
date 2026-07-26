// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>A concept matched by <see cref="ConceptSearch"/>, with its score.</summary>
public sealed record ScoredConcept(Concept Concept, int Score);

/// <summary>
/// Full-text scoring of OKF concepts by query terms. Weights: title x3,
/// tags/description x2, body x1, summed over the query's whitespace-separated
/// terms (case-insensitive substring). This is the single shared scorer used
/// by <c>OkfBundleTools</c> (okf_search / context provider) and any other
/// consumer of a bundle's concepts.
/// </summary>
public static class ConceptSearch
{
    /// <summary>
    /// Scores <paramref name="concepts"/> against <paramref name="query"/>,
    /// optionally pre-filtered to those carrying <paramref name="tag"/>
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/>). The query is split
    /// into terms on whitespace; each term is matched as an
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> substring. A
    /// concept's score is the sum, over all terms, of the weights of every
    /// field the term is found in: title x3, tags/description x2, body x1.
    /// Returns matches (<c>Score &gt; 0</c>) ordered by descending score then
    /// ascending <see cref="ConceptId"/>. An empty/whitespace query (or one
    /// that splits into zero terms) yields an empty list rather than
    /// throwing.
    /// </summary>
    /// <param name="concepts">The candidate concepts to score.</param>
    /// <param name="query">The search query (whitespace-separated, case-insensitive substring terms).</param>
    /// <param name="tag">Optional tag filter: only concepts carrying this tag (OrdinalIgnoreCase).</param>
    public static IReadOnlyList<ScoredConcept> Search(IEnumerable<Concept> concepts, string query, string? tag = null)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var effectiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag;

        var candidates = effectiveTag is null
            ? concepts
            : concepts.Where(c => c.Document.Frontmatter.Tags.Any(t => string.Equals(t, effectiveTag, StringComparison.OrdinalIgnoreCase)));

        return candidates
            .Select(c => new ScoredConcept(c, ScoreConcept(c, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Concept.Id)
            .ToList();
    }

    /// <summary>
    /// The single shared excerpt: the first non-blank, trimmed body line
    /// containing any of the query's whitespace-separated terms
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/> substring match), or
    /// <c>null</c> if none does (including when the query splits into zero
    /// terms).
    /// </summary>
    /// <param name="body">The concept body to search for an excerpt line.</param>
    /// <param name="query">The search query (whitespace-separated, case-insensitive substring terms).</param>
    public static string? Excerpt(string body, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return null;
        }

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// A concept's relevance score for <paramref name="terms"/>: the sum,
    /// over all terms, of the weights of every field the term is found in
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/> substring match):
    /// title x3, tags/description x2, body x1.
    /// </summary>
    private static int ScoreConcept(Concept concept, IReadOnlyList<string> terms)
    {
        var frontmatter = concept.Document.Frontmatter;
        var title = frontmatter.Title ?? string.Empty;
        var tagsAndDescription = string.Join(' ', frontmatter.Tags) + ' ' + (frontmatter.Description ?? string.Empty);
        var body = concept.Document.Body;

        var score = 0;
        foreach (var term in terms)
        {
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (tagsAndDescription.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (body.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }
}

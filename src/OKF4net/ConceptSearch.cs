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

    /// <summary>
    /// Picks the top <paramref name="count"/> of an already-scored list while
    /// spreading scarce slots across top-level id segments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The problem this solves, measured rather than supposed. On a 395-concept
    /// bundle shaped like a generated producer output, the curated concepts took
    /// <b>1 of 55</b> top-5 slots and 5 of 11 broad queries returned none at all
    /// in the top 20. Two mechanisms compound: <see cref="ScoreConcept"/> awards
    /// presence rather than frequency and caps at 6 per term, so ties are the
    /// common case; and <see cref="Search"/> breaks ties with
    /// <see cref="ConceptId"/> order, which is ordinal by segment, putting every
    /// <c>code/…</c> concept ahead of every <c>docs/…</c>, <c>overview</c> and
    /// <c>packages/…</c> one.
    /// </para>
    /// <para>
    /// Diversifying only <em>within</em> a score band was tried first and
    /// measured insufficient: a generated member literally named <c>Bundle</c>
    /// matches the query in its title, description and body and scores the
    /// maximum, while the curated concept that actually answers "bundle"
    /// mentions it only in its description and scores 2. They are in different
    /// bands, so no amount of within-band rotation reaches the curated one.
    /// </para>
    /// <para>
    /// So selection rotates across families outright: each top-level id segment
    /// is a family, families are visited in order of their best-scoring member
    /// (ties broken by segment, ordinal), and each contributes its next-best
    /// concept per rotation. Every family present in the results is therefore
    /// represented before any family takes a second slot. Within a family the
    /// input order — score, then id — is preserved exactly, and the
    /// highest-scoring concept overall is always first. With a single family
    /// this degrades to a plain truncation.
    /// </para>
    /// <para>
    /// The trade is deliberate: a high-scoring concept can be displaced by a
    /// lower-scoring one from an unrepresented family. That is the point. A
    /// window that shows twenty members of one type answers a narrower question
    /// than one that also shows the package and the document describing them.
    /// </para>
    /// <para>
    /// SCOPE. This is the <c>OKF4net.Agents</c>-side answer to a crowded
    /// window, not a global property of the scorer: <see cref="Search"/> is
    /// unchanged, and nothing applies this for a caller automatically. The
    /// catalog surfaces (<c>OkfBundleKnowledgeSource</c>,
    /// <c>FileMemoryStore</c>) return COMPLETE ranked lists and are therefore
    /// unaffected either way — but any caller that truncates one of those
    /// lists, or spends a budget down it, inherits the same starvation and
    /// must apply this itself. <see cref="TopDiversifiedBy"/> is the variant
    /// for lists that are not simply score-ordered.
    /// </para>
    /// </remarks>
    /// <param name="scored">Results from <see cref="Search"/>, in descending score order.</param>
    /// <param name="count">Maximum number of results to return.</param>
    /// <returns>At most <paramref name="count"/> results, in the order they should be shown.</returns>
    public static IReadOnlyList<ScoredConcept> TopDiversified(IReadOnlyList<ScoredConcept> scored, int count)
    {
        if (count <= 0 || scored.Count == 0)
        {
            return [];
        }

        // Group by top-level id segment, preserving the input order (score
        // descending, then id) inside each family.
        var families = GroupIntoFamilies(
            scored,
            static entry => entry.Concept.Id.Segments is { Count: > 0 } segments ? segments[0] : string.Empty);

        // Visit families best-first, so the highest-scoring concept overall is
        // always picked first. Ordinal on the segment breaks ties, keeping the
        // rotation order deterministic.
        families.Sort((a, b) =>
        {
            var byScore = b.Queue.Peek().Score.CompareTo(a.Queue.Peek().Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Family, b.Family);
        });

        return RoundRobin(families, count, scored.Count);
    }

    /// <summary>
    /// The order-preserving sibling of <see cref="TopDiversified"/>: picks the
    /// top <paramref name="count"/> of <paramref name="items"/> while spreading
    /// scarce slots across the families named by <paramref name="familyOf"/>,
    /// visiting families in the order they FIRST APPEAR in
    /// <paramref name="items"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same rotation, different family order, and the difference is the whole
    /// point of having two. <see cref="TopDiversified"/> re-derives the family
    /// order from the scores, which is right when the input is exactly
    /// <see cref="Search"/>'s output and wrong the moment a caller has already
    /// ordered the list by something else — a catalog resolver's
    /// source-fairness interleave, a priority weighting — because re-sorting by
    /// score silently undoes that work. Ordering families by first appearance
    /// instead keeps every upstream decision: within a family the incoming
    /// order is preserved exactly, and a passage its caller deliberately pulled
    /// to the head of its family keeps that head position and so is reached in
    /// the first rotation.
    /// </para>
    /// <para>
    /// For a list that IS score-ordered the two agree, since
    /// <see cref="ConceptId"/> compares segment by segment (ordinal): first
    /// appearance is then exactly "best score first, ties by first segment".
    /// </para>
    /// <para>
    /// Pass <c>items.Count</c> as <paramref name="count"/> to get a full
    /// diversified reordering rather than a truncation — the shape a caller
    /// wants when what bounds the list is a token budget spent top-down rather
    /// than a fixed slot count.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The item type; anything that can name a family.</typeparam>
    /// <param name="items">The already-ordered items to select from.</param>
    /// <param name="familyOf">Names the family an item belongs to (compared with <see cref="StringComparer.Ordinal"/>).</param>
    /// <param name="count">Maximum number of items to return.</param>
    /// <returns>At most <paramref name="count"/> items, in the order they should be shown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> or <paramref name="familyOf"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<T> TopDiversifiedBy<T>(IReadOnlyList<T> items, Func<T, string> familyOf, int count)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(familyOf);

        if (count <= 0 || items.Count == 0)
        {
            return [];
        }

        return RoundRobin(GroupIntoFamilies(items, familyOf), count, items.Count);
    }

    /// <summary>
    /// Buckets <paramref name="items"/> by family, preserving the input order
    /// inside each bucket and returning the buckets in order of first
    /// appearance.
    /// </summary>
    private static List<(string Family, Queue<T> Queue)> GroupIntoFamilies<T>(IReadOnlyList<T> items, Func<T, string> familyOf)
    {
        var families = new List<(string Family, Queue<T> Queue)>();
        var byFamily = new Dictionary<string, Queue<T>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var family = familyOf(item) ?? string.Empty;
            if (!byFamily.TryGetValue(family, out var queue))
            {
                queue = new Queue<T>();
                byFamily[family] = queue;
                families.Add((family, queue));
            }

            queue.Enqueue(item);
        }

        return families;
    }

    /// <summary>
    /// Drains <paramref name="families"/> one item per family per rotation, in
    /// the given family order, until <paramref name="count"/> items are picked
    /// or every family is empty.
    /// </summary>
    private static List<T> RoundRobin<T>(List<(string Family, Queue<T> Queue)> families, int count, int total)
    {
        var picked = new List<T>(Math.Min(count, total));
        var drained = false;
        while (!drained && picked.Count < count)
        {
            drained = true;
            foreach (var (_, queue) in families)
            {
                if (queue.Count == 0)
                {
                    continue;
                }

                drained = false;
                picked.Add(queue.Dequeue());
                if (picked.Count == count)
                {
                    break;
                }
            }
        }

        return picked;
    }
}

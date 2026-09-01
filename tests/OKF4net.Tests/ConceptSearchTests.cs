// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for the core <see cref="ConceptSearch"/> scorer/excerpt: title vs.
/// tag/description vs. body weighting, multi-term OR/additive scoring, tie
/// ordering by ascending <see cref="ConceptId"/>, the tag filter, the
/// empty-query no-op, and <see cref="ConceptSearch.Excerpt"/>'s
/// first-matching-line behaviour, plus <see cref="ConceptSearch.TopDiversified"/>'s
/// within-band rotation across top-level id families. Mirrors the scoring cases already covered
/// end-to-end (through <c>OkfBundleTools.Search</c>'s formatted output) by
/// <c>OkfSearchTests</c>, but exercises <see cref="ConceptSearch"/> directly
/// against in-memory <see cref="Concept"/> values, without a bundle on disk.
/// </summary>
public class ConceptSearchTests
{
    private static Concept MakeConcept(string id, string? title = null, string[]? tags = null, string? description = null, string body = "")
    {
        var frontmatterLines = new List<string> { "type: Widget" };
        if (title is not null)
        {
            frontmatterLines.Add($"title: {title}");
        }

        if (description is not null)
        {
            frontmatterLines.Add($"description: {description}");
        }

        if (tags is { Length: > 0 })
        {
            frontmatterLines.Add($"tags: [{string.Join(", ", tags)}]");
        }

        var text = "---\n" + string.Join('\n', frontmatterLines) + "\n---\n\n" + body;
        var document = OkfDocument.Parse(text);
        return new Concept(ConceptId.Parse(id), $"{id}.md", document);
    }

    [Fact]
    public void Search_scores_title_match_higher_than_tag_or_body()
    {
        var titleMatch = MakeConcept("a", title: "Orders");
        var tagMatch = MakeConcept("b", tags: ["orders"]);
        var bodyMatch = MakeConcept("c", body: "mentions orders in passing");

        var results = ConceptSearch.Search([titleMatch, tagMatch, bodyMatch], "orders");

        Assert.Equal(3, results.Count);
        Assert.Equal(3, results.Single(r => r.Concept.Id == titleMatch.Id).Score);
        Assert.Equal(2, results.Single(r => r.Concept.Id == tagMatch.Id).Score);
        Assert.Equal(1, results.Single(r => r.Concept.Id == bodyMatch.Id).Score);
    }

    [Fact]
    public void Search_weighs_description_the_same_as_tags()
    {
        var descriptionMatch = MakeConcept("a", description: "all about orders");

        var results = ConceptSearch.Search([descriptionMatch], "orders");

        Assert.Equal(2, Assert.Single(results).Score);
    }

    [Fact]
    public void Search_sums_weights_across_zones_for_a_single_term()
    {
        // Title (x3) + tags (x2) = 5, matching OkfSearchTests'
        // tables/orders fixture case exactly.
        var concept = MakeConcept("tables/orders", title: "Orders", tags: ["sales", "orders"]);

        var results = ConceptSearch.Search([concept], "orders");

        Assert.Equal(5, Assert.Single(results).Score);
    }

    [Fact]
    public void Search_multi_term_query_uses_OR_semantics_and_additive_scoring()
    {
        var matchesBoth = MakeConcept("both", title: "Sales Orders");
        var matchesOnlyFirst = MakeConcept("first", title: "Sales Report");
        var matchesOnlySecond = MakeConcept("second", title: "Purchase Orders");
        var matchesNeither = MakeConcept("neither", title: "Unrelated");

        var results = ConceptSearch.Search([matchesBoth, matchesOnlyFirst, matchesOnlySecond, matchesNeither], "sales orders");

        Assert.Equal(3, results.Count);
        Assert.Equal(6, results.Single(r => r.Concept.Id == matchesBoth.Id).Score);
        Assert.Equal(3, results.Single(r => r.Concept.Id == matchesOnlyFirst.Id).Score);
        Assert.Equal(3, results.Single(r => r.Concept.Id == matchesOnlySecond.Id).Score);
        Assert.DoesNotContain(results, r => r.Concept.Id == matchesNeither.Id);
    }

    [Fact]
    public void Search_orders_ties_by_ascending_concept_id()
    {
        var z = MakeConcept("z", title: "Widget");
        var a = MakeConcept("a", title: "Widget");
        var m = MakeConcept("m", title: "Widget");

        var results = ConceptSearch.Search([z, a, m], "widget");

        Assert.Equal(["a", "m", "z"], results.Select(r => r.Concept.Id.ToString()));
    }

    [Fact]
    public void Search_drops_concepts_scoring_zero()
    {
        var match = MakeConcept("a", title: "Orders");
        var noMatch = MakeConcept("b", title: "Something else entirely");

        var results = ConceptSearch.Search([match, noMatch], "orders");

        Assert.Single(results);
        Assert.Equal(match.Id, results[0].Concept.Id);
    }

    [Fact]
    public void Search_empty_query_returns_empty_list()
    {
        var concept = MakeConcept("a", title: "Orders");

        Assert.Empty(ConceptSearch.Search([concept], ""));
        Assert.Empty(ConceptSearch.Search([concept], "   "));
    }

    [Fact]
    public void Search_tag_filter_is_ordinal_ignore_case_and_excludes_untagged()
    {
        var tagged = MakeConcept("a", title: "Orders", tags: ["Sales"]);
        var untagged = MakeConcept("b", title: "Orders");

        var results = ConceptSearch.Search([tagged, untagged], "orders", "sales");

        Assert.Single(results);
        Assert.Equal(tagged.Id, results[0].Concept.Id);
    }

    [Fact]
    public void Excerpt_returns_first_matching_non_blank_line()
    {
        var body = "\n   \nIrrelevant line.\nThis line mentions orders.\nAnother line mentions orders too.\n";

        var excerpt = ConceptSearch.Excerpt(body, "orders");

        Assert.Equal("This line mentions orders.", excerpt);
    }

    [Fact]
    public void Excerpt_skips_blank_and_whitespace_only_lines()
    {
        var body = "\n\n   \n\t\nThe real content line has the term.\n";

        var excerpt = ConceptSearch.Excerpt(body, "term");

        Assert.Equal("The real content line has the term.", excerpt);
    }

    [Fact]
    public void Excerpt_returns_null_when_no_line_matches()
    {
        var body = "First line.\nSecond line.\n";

        Assert.Null(ConceptSearch.Excerpt(body, "absent"));
    }

    [Fact]
    public void Excerpt_returns_null_for_empty_query()
    {
        var body = "Some content here.\n";

        Assert.Null(ConceptSearch.Excerpt(body, ""));
        Assert.Null(ConceptSearch.Excerpt(body, "   "));
    }

    [Fact]
    public void Excerpt_matches_case_insensitively()
    {
        var body = "This Line Mentions ORDERS in caps.\n";

        Assert.Equal("This Line Mentions ORDERS in caps.", ConceptSearch.Excerpt(body, "orders"));
    }

    [Fact]
    public void Excerpt_trims_the_returned_line()
    {
        var body = "   Leading and trailing whitespace around orders.   \n";

        Assert.Equal("Leading and trailing whitespace around orders.", ConceptSearch.Excerpt(body, "orders"));
    }

    // ---- TopDiversified: scarce slots across top-level id families ---------

    /// <summary>
    /// Builds one score band without touching the filesystem: ids only, all at
    /// the same score, in the order <see cref="ConceptSearch.Search"/> would
    /// return them (ascending ordinal by id).
    /// </summary>
    private static IReadOnlyList<ScoredConcept> Band(int score, params string[] ids) =>
        [.. ids.Select(id => new ScoredConcept(MakeConcept(id), score))];

    [Fact]
    public void TopDiversified_rotates_slots_across_top_level_segments_within_a_band()
    {
        var scored = Band(6,
            "code/csharp/a", "code/csharp/b", "code/csharp/c", "code/csharp/d",
            "docs/readme",
            "overview",
            "packages/okf4net");

        var top = ConceptSearch.TopDiversified(scored, 4);

        Assert.Equal(
            ["code/csharp/a", "docs/readme", "overview", "packages/okf4net"],
            top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void TopDiversified_puts_the_best_scoring_concept_first()
    {
        var scored = new List<ScoredConcept>();
        scored.AddRange(Band(6, "code/csharp/a", "code/csharp/b"));
        scored.AddRange(Band(3, "docs/readme"));

        Assert.Equal("code/csharp/a", ConceptSearch.TopDiversified(scored, 3)[0].Concept.Id.ToString());
    }

    [Fact]
    public void TopDiversified_reaches_a_lower_scoring_family_rather_than_filling_up_with_the_best_one()
    {
        // The measured failure this whole method exists for: a generated member
        // literally named "Bundle" scores the maximum while the curated concept
        // that answers the question only mentions it in its description. They
        // are in different score bands, so the curated one is reachable only by
        // giving each family a turn.
        var scored = new List<ScoredConcept>();
        scored.AddRange(Band(6, "code/csharp/a", "code/csharp/b", "code/csharp/c"));
        scored.AddRange(Band(2, "packages/okf4net"));

        var top = ConceptSearch.TopDiversified(scored, 2);

        Assert.Equal(["code/csharp/a", "packages/okf4net"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void TopDiversified_preserves_input_order_inside_a_family()
    {
        var scored = new List<ScoredConcept>();
        scored.AddRange(Band(6, "code/a"));
        scored.AddRange(Band(4, "code/b"));
        scored.AddRange(Band(2, "code/c"));

        var top = ConceptSearch.TopDiversified(scored, 3);

        Assert.Equal(["code/a", "code/b", "code/c"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void TopDiversified_returns_everything_when_fewer_results_than_requested()
        => Assert.Equal(2, ConceptSearch.TopDiversified(Band(6, "code/csharp/a", "docs/readme"), 20).Count);

    [Fact]
    public void TopDiversified_degrades_to_plain_score_order_for_a_single_family()
    {
        var scored = Band(6, "code/csharp/a", "code/csharp/b", "code/csharp/c");

        var top = ConceptSearch.TopDiversified(scored, 2);

        Assert.Equal(["code/csharp/a", "code/csharp/b"], top.Select(s => s.Concept.Id.ToString()));
    }

    /// <summary>
    /// The rotation order is pinned to an EXPECTED sequence, not to a second
    /// call's output: comparing two calls in one process on one input is
    /// satisfied by any stable implementation, a plain <c>Take</c> included,
    /// and cannot see a family order that depends on hash iteration. Here the
    /// input is deliberately NOT in id order, so only the documented
    /// (score, then segment ordinal) family order produces this sequence.
    /// </summary>
    [Fact]
    public void TopDiversified_orders_tied_families_by_segment_ordinal_not_by_input_order()
    {
        var scored = Band(6, "packages/x", "code/a", "docs/y", "code/b", "overview");

        var top = ConceptSearch.TopDiversified(scored, 3);

        Assert.Equal(["code/a", "docs/y", "overview"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void TopDiversified_visits_families_best_scoring_first()
    {
        // `packages` holds the single best result, so its family leads the
        // rotation even though `code` sorts before it ordinally.
        var scored = new List<ScoredConcept>();
        scored.AddRange(Band(6, "packages/x"));
        scored.AddRange(Band(4, "code/a", "code/b"));

        var top = ConceptSearch.TopDiversified(scored, 3);

        Assert.Equal(["packages/x", "code/a", "code/b"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void TopDiversified_returns_empty_for_an_empty_input()
        => Assert.Empty(ConceptSearch.TopDiversified([], 5));

    [Fact]
    public void TopDiversified_returns_empty_for_a_non_positive_count()
        => Assert.Empty(ConceptSearch.TopDiversified(Band(6, "code/a"), 0));

    // ---- TopDiversifiedBy: the same rotation over a caller-ordered list ----

    private static string FamilyOf(string id)
    {
        var slash = id.IndexOf('/');
        return slash < 0 ? id : id[..slash];
    }

    /// <summary>
    /// The one behaviour that distinguishes it from <see cref="ConceptSearch.TopDiversified"/>,
    /// and the reason both exist: the caller's list is already ordered by
    /// something the scores cannot express (a catalog resolver's source
    /// interleave), so families are visited as they first appear rather than
    /// re-sorted by score. Feeding this same input to
    /// <see cref="ConceptSearch.TopDiversified"/> would lead with <c>docs/a</c>
    /// and undo the caller's ordering.
    /// </summary>
    [Fact]
    public void TopDiversifiedBy_visits_families_in_first_appearance_order()
    {
        string[] ids = ["code/a", "docs/a", "code/b", "code/c", "docs/b"];

        var top = ConceptSearch.TopDiversifiedBy(ids, FamilyOf, 4);

        Assert.Equal(["code/a", "docs/a", "code/b", "docs/b"], top);
    }

    [Fact]
    public void TopDiversifiedBy_preserves_the_incoming_order_inside_a_family()
    {
        string[] ids = ["code/c", "code/a", "code/b"];

        Assert.Equal(ids, ConceptSearch.TopDiversifiedBy(ids, FamilyOf, 3));
    }

    /// <summary>
    /// The shape the scoped context provider uses: <c>count = items.Count</c>
    /// is a full reordering, not a truncation, because what bounds its list is
    /// a token budget spent top-down rather than a slot count.
    /// </summary>
    [Fact]
    public void TopDiversifiedBy_reorders_the_whole_list_when_count_covers_it()
    {
        string[] ids = ["code/a", "code/b", "code/c", "docs/a"];

        Assert.Equal(
            ["code/a", "docs/a", "code/b", "code/c"],
            ConceptSearch.TopDiversifiedBy(ids, FamilyOf, ids.Length));
    }

    [Fact]
    public void TopDiversifiedBy_returns_empty_for_an_empty_input_or_a_non_positive_count()
    {
        Assert.Empty(ConceptSearch.TopDiversifiedBy(Array.Empty<string>(), FamilyOf, 5));
        Assert.Empty(ConceptSearch.TopDiversifiedBy(["code/a"], FamilyOf, 0));
    }

    [Fact]
    public void TopDiversifiedBy_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ConceptSearch.TopDiversifiedBy<string>(null!, FamilyOf, 1));
        Assert.Throws<ArgumentNullException>(() => ConceptSearch.TopDiversifiedBy(["code/a"], null!, 1));
    }
}

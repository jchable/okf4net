// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for the core <see cref="ConceptSearch"/> scorer/excerpt: title vs.
/// tag/description vs. body weighting, multi-term OR/additive scoring, tie
/// ordering by ascending <see cref="ConceptId"/>, the tag filter, the
/// empty-query no-op, and <see cref="ConceptSearch.Excerpt"/>'s
/// first-matching-line behaviour. Mirrors the scoring cases already covered
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
}

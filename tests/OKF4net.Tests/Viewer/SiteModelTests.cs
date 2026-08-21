// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the viewer's pure Bundle -> display-model projection.</summary>
public class SiteModelTests
{
    [Fact]
    public void RelativeHref_between_two_root_concepts_is_a_bare_filename()
        => Assert.Equal("b.html",
            SiteModel.RelativeHref(ConceptId.Parse("a"), ConceptId.Parse("b")));

    [Fact]
    public void RelativeHref_from_nested_to_root_walks_up()
        => Assert.Equal("../b.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("b")));

    [Fact]
    public void RelativeHref_from_root_to_nested_walks_down()
        => Assert.Equal("tables/users.html",
            SiteModel.RelativeHref(ConceptId.Parse("a"), ConceptId.Parse("tables/users")));

    [Fact]
    public void RelativeHref_within_the_same_directory_is_a_bare_filename()
        => Assert.Equal("orders.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("tables/orders")));

    [Fact]
    public void RelativeHref_across_sibling_directories_walks_up_then_down()
        => Assert.Equal("../glossary/term.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("glossary/term")));

    [Fact]
    public void RelativeHref_from_deeply_nested_walks_up_once_per_level()
        => Assert.Equal("../../b.html",
            SiteModel.RelativeHref(ConceptId.Parse("a/b/c"), ConceptId.Parse("b")));

    [Fact]
    public void RelativeHref_when_an_ancestor_segment_collides_with_a_shallower_concept_name()
    {
        // from = a/b/c lives in directory a/b. to = a/b is itself a concept,
        // but "a/b" is also a *directory prefix* of `from` -- the common-prefix
        // walk only compares fromDir against to's directory segments (all but
        // to's last), so common stops at 1 ("a") rather than matching "b" too:
        // toPath.Count - 1 = 1, so the loop bound never lets common reach 2.
        // That leaves one level to walk up (fromDir.Count - common = 2 - 1 = 1)
        // and "b" (to's own last segment) to walk back down to.
        Assert.Equal("../b.html",
            SiteModel.RelativeHref(ConceptId.Parse("a/b/c"), ConceptId.Parse("a/b")));
    }

    [Fact]
    public void RelativeHref_reverse_when_the_target_nests_under_a_same_named_concept()
    {
        // from = a/b sits in directory a (fromDir = ["a"]). to = a/b/c's
        // directory prefix is ["a","b"]; the common-prefix walk matches only
        // "a" (common = 1) since fromDir has just one segment to offer, so no
        // "../" is needed (fromDir.Count - common = 0) and the remaining
        // "b/c" of to's path is walked straight down to.
        Assert.Equal("b/c.html",
            SiteModel.RelativeHref(ConceptId.Parse("a/b"), ConceptId.Parse("a/b/c")));
    }

    private static Bundle LoadBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root index\nokf_version: \"0.2\"\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: The users table\ntags:\n  - core\n---\nBody line about users.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Build_emits_one_page_per_concept()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        var page = Assert.Single(site.Pages);
        Assert.Equal("tables/users", page.Id.ToString());
    }

    [Fact]
    public void Build_uses_the_frontmatter_title_as_the_page_title()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal("Users", site.Pages[0].Title);
    }

    [Fact]
    public void Build_falls_back_to_the_concept_id_when_the_title_is_missing()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("untitled.md", "---\ntype: note\ntitle: \"\"\ndescription: d\n---\nBody\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Equal("untitled", Assert.Single(site.Pages).Title);
    }

    [Fact]
    public void Build_maps_the_concept_id_to_an_html_path()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal("tables/users.html", site.Pages[0].RelativeHtmlPath);
    }

    [Fact]
    public void Build_carries_the_raw_markdown_body_unmodified()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        // OkfDocument.Parse strips the trailing newline (see DocumentTests'
        // "trailing newline is dropped on parse"), so the projected body
        // does not carry one back either.
        Assert.Equal("Body line about users.", site.Pages[0].Body);
    }

    [Fact]
    public void Build_preserves_frontmatter_key_order()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal(
            ["type", "title", "description", "tags"],
            site.Pages[0].Frontmatter.Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Build_renders_a_non_scalar_frontmatter_value_rather_than_dropping_it()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        // `tags` is a sequence: AsDisplayString() returns null for non-scalars,
        // so the projection must fall back rather than emit an empty cell.
        var tags = site.Pages[0].Frontmatter.Single(e => e.Key == "tags");
        Assert.Contains("core", tags.Value);
    }

    [Fact]
    public void Build_records_the_bundle_root()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal(tmp.Path, site.BundleRoot);
    }

    private static Bundle LoadLinkedBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: d\n---\n"
            + "See [term](../glossary/term.md) and [gone](../glossary/missing.md).\n"
            + "External [site](https://example.com) and [anchor](#section).\n");
        tmp.Write("glossary/term.md", "---\ntype: term\ntitle: Term\ndescription: d\n---\nA term.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Build_resolves_an_internal_link_to_the_target_pages_relative_href()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        var link = users.Links.Single(l => l.RawTarget == "../glossary/term.md");
        Assert.Equal("../glossary/term.html", link.Href);
        Assert.True(link.Exists);
    }

    [Fact]
    public void Build_uses_the_title_stripped_link_destination_as_the_table_key()
    {
        // RawTarget is ResolvedLink.Raw, which Links.cs's StripTitle has
        // already trimmed and stripped of an optional "title" suffix -- not
        // the literal markdown source text. That is the right key: a
        // CommonMark renderer (including marked, client-side) puts exactly
        // this title-stripped string in the rendered anchor's href, so it is
        // what viewer.js looks the link up by at render time.
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("a/b.md", "---\ntype: note\ntitle: B\ndescription: d\n---\nB.\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: d\n---\n"
            + "See [x](../a/b.md \"Title\").\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        var link = Assert.Single(users.Links);
        Assert.Equal("../a/b.md", link.RawTarget);
    }

    [Fact]
    public void Build_marks_a_link_to_a_missing_concept_as_broken()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        var link = users.Links.Single(l => l.RawTarget == "../glossary/missing.md");
        Assert.False(link.Exists);
    }

    [Fact]
    public void Build_leaves_external_and_anchor_links_out_of_the_rewiring_table()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        Assert.DoesNotContain(users.Links, l => l.RawTarget.StartsWith("https://", StringComparison.Ordinal));
        Assert.DoesNotContain(users.Links, l => l.RawTarget.StartsWith("#", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_records_backlinks_pointing_at_a_concept()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var term = site.Pages.Single(p => p.Id.ToString() == "glossary/term");
        var backlink = Assert.Single(term.Backlinks);
        Assert.Equal("../tables/users.html", backlink.Href);
        Assert.True(backlink.Exists);
    }

    [Fact]
    public void Build_generates_index_markdown_linking_to_generated_pages()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Contains("[Users](tables/users.html)", site.IndexMarkdown);
    }

    [Fact]
    public void Build_groups_index_entries_by_concept_type()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Contains("# table", site.IndexMarkdown);
    }

    [Fact]
    public void Build_surfaces_parse_errors_rather_than_dropping_them()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("good.md", "---\ntype: note\ntitle: Good\ndescription: d\n---\nBody\n");
        // Missing `type` alone would NOT reach Bundle.ParseErrors: neither
        // OkfDocument.Parse nor ConceptId.FromPath require it (§11 `type`
        // conformance is a *validation* concern, checked by
        // OkfDocument.ValidateConformance, not a *parse* one) -- such a file
        // loads as an ordinary concept. An unterminated frontmatter block is
        // a genuine parse failure (OkfDocument.Parse throws
        // DocumentParseException("Unterminated YAML frontmatter block")),
        // which Bundle.Load does collect into ParseErrors.
        tmp.Write("broken.md", "---\ntitle: No closing delimiter\nBody\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        var error = Assert.Single(site.ParseErrors);
        Assert.Contains("broken.md", error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
    }

    [Fact]
    public void Build_on_an_empty_bundle_yields_an_empty_site_not_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Empty(site.Pages);
        Assert.Empty(site.ParseErrors);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the viewer's pure Bundle -> display-model projection.</summary>
public class SiteModelTests
{
    [Fact]
    public void Viewer_assembly_is_referenced()
        => Assert.Equal("OKF4net.Viewer", ViewerAssemblyMarker.Name);

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
}

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
}

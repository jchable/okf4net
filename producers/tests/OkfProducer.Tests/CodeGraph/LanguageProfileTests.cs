// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Tests.CodeGraph;

public class LanguageProfileTests
{
    private static readonly LanguageProfile CSharp =
        new("csharp", "c-sharp", "", "", "///", [".cs"]);

    [Fact]
    public void A_csharp_container_splits_on_dots()
        => Assert.Equal(["N", "Outer", "Inner"], CSharp.SplitContainer("N.Outer.Inner"));

    [Fact]
    public void An_empty_container_splits_to_an_empty_list()
        => Assert.Empty(CSharp.SplitContainer(string.Empty));

    [Fact]
    public void A_single_segment_container_splits_to_one_segment()
        => Assert.Equal(["N"], CSharp.SplitContainer("N"));

    [Theory]
    [InlineData("public")]
    public void Public_maps_to_public(string modifiers)
        => Assert.Equal(SymbolVisibility.Public, CSharp.VisibilityOf(modifiers, SymbolKind.Member));

    [Theory]
    [InlineData("protected internal")]
    [InlineData("internal protected")]
    public void Protected_internal_maps_to_public_because_it_crosses_the_assembly_boundary(string modifiers)
        => Assert.Equal(SymbolVisibility.Public, CSharp.VisibilityOf(modifiers, SymbolKind.Member));

    [Fact]
    public void Internal_maps_to_internal()
        => Assert.Equal(SymbolVisibility.Internal, CSharp.VisibilityOf("internal", SymbolKind.Member));

    [Fact]
    public void Plain_protected_maps_to_internal_as_the_nearer_of_the_two_remaining_tiers()
        => Assert.Equal(SymbolVisibility.Internal, CSharp.VisibilityOf("protected", SymbolKind.Member));

    [Theory]
    [InlineData("private protected")]
    [InlineData("protected private")]
    public void Private_protected_maps_to_private_as_the_intersection(string modifiers)
        => Assert.Equal(SymbolVisibility.Private, CSharp.VisibilityOf(modifiers, SymbolKind.Member));

    [Fact]
    public void Private_maps_to_private()
        => Assert.Equal(SymbolVisibility.Private, CSharp.VisibilityOf("private", SymbolKind.Member));

    [Fact]
    public void A_type_with_no_modifier_defaults_to_internal()
        => Assert.Equal(SymbolVisibility.Internal, CSharp.VisibilityOf(string.Empty, SymbolKind.Type));

    [Fact]
    public void A_member_with_no_modifier_defaults_to_private()
        => Assert.Equal(SymbolVisibility.Private, CSharp.VisibilityOf(string.Empty, SymbolKind.Member));

    [Fact]
    public void Non_access_modifiers_do_not_affect_the_default()
        => Assert.Equal(SymbolVisibility.Private, CSharp.VisibilityOf("static readonly", SymbolKind.Member));

    [Fact]
    public void Public_static_still_maps_to_public()
        => Assert.Equal(SymbolVisibility.Public, CSharp.VisibilityOf("public static", SymbolKind.Member));
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Tests.CodeGraph;

public class NameMatchResolverTests
{
    [Fact]
    public void A_unique_name_resolves_ByName()
    {
        var edges = Resolve(sites: [Site("Caller", "Scan")], symbols: [Member("Scanner", "Scan")]);

        var edge = Assert.Single(edges);
        Assert.Equal(EdgeConfidence.ByName, edge.Confidence);
        Assert.Equal("Scanner", edge.TargetContainer);
    }

    [Fact]
    public void An_ambiguous_name_stays_Unresolved_rather_than_guessing()
    {
        // The spike measured 38-39% of internal edges as inter-type ambiguous
        // (`Equals` across 7 types). Picking one would be a silent wrong answer;
        // §4.5 puts these in `## Calls (unresolved)` as text instead.
        var edges = Resolve(
            sites: [Site("Caller", "Equals")],
            symbols: [Member("A", "Equals"), Member("B", "Equals")]);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(edges).Confidence);
    }

    [Fact]
    public void A_name_with_no_declaration_in_the_repo_stays_Unresolved()
        => Assert.Equal(EdgeConfidence.Unresolved,
            Assert.Single(Resolve([Site("Caller", "Substring")], [Member("T", "Scan")])).Confidence);

    [Fact]
    public void An_unresolved_edge_carries_no_target()
    {
        var edges = Resolve(
            sites: [Site("Caller", "Equals")],
            symbols: [Member("A", "Equals"), Member("B", "Equals")]);

        var edge = Assert.Single(edges);
        Assert.Null(edge.TargetContainer);
        Assert.Null(edge.TargetName);
    }

    [Fact]
    public void Owns_returns_true_for_every_path()
    {
        var resolver = new NameMatchResolver();

        Assert.True(resolver.Owns("A.cs"));
        Assert.True(resolver.Owns("some/deep/path/File.ts"));
        Assert.True(resolver.Owns(string.Empty));
    }

    [Fact]
    public void Edges_are_returned_in_the_order_the_sites_were_given()
    {
        // The resolver must not silently reorder sites via internal dictionary/lookup structures --
        // the output order is a straight pass over the input list.
        var sites = new[] { Site("Caller", "Second"), Site("Caller", "First") };
        var symbols = new[] { Member("T", "First"), Member("T", "Second") };

        var edges = Resolve(sites, symbols);

        Assert.Equal(["Second", "First"], edges.Select(e => e.Site.CalledName));
    }

    private static IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols) =>
        new NameMatchResolver().Resolve(sites, symbols);

    private static CallSite Site(string callerName, string calledName, string callerContainer = "T", string path = "A.cs", int offset = 0) =>
        new(callerContainer, callerName, calledName, path, offset);

    private static SymbolFact Member(string container, string name, string path = "A.cs") =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            SymbolVisibility.Public, path, 0, 10, 1, 1, null);
}

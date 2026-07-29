// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="SourceVisibility.Filter"/>: the shared resolution algorithm
/// both <see cref="GroupedKnowledgeResolver"/> and
/// <see cref="FusedResolverEngine"/> apply before searching. Exercised
/// directly against hand-built source lists -- pure list filtering, no
/// catalog or bundle needed.
/// </summary>
public class SourceVisibilityTests
{
    private static KnowledgeCatalogSource Source(string id) =>
        new(id, $"./{id}", 0, true, SourceRole.Knowledge);

    [Fact]
    public void No_restriction_returns_every_source_unchanged()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x");

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(sources, result);
    }

    [Fact]
    public void PermittedSourceIds_keeps_only_the_named_sources()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b"), Source("c") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "a", "c" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(new[] { "a", "c" }, result.Select(s => s.Id));
    }

    [Fact]
    public void PermittedSourceIds_wins_over_a_configured_default_policy()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "a" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, _) => false);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Query_level_policy_receives_the_query_Scope_and_each_source()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        var query = new KnowledgeQuery("x")
        {
            Scope = scope,
            SourceVisibilityPolicy = (s, source) => s == scope && source.Id == "b",
        };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Equal(new[] { "b" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Query_level_policy_overrides_the_host_default()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x") { SourceVisibilityPolicy = (_, source) => source.Id == "a" };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, _) => true);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id));
    }

    [Fact]
    public void Host_default_policy_applies_when_the_query_sets_neither_field()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a"), Source("b") };
        var query = new KnowledgeQuery("x");

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: (_, source) => source.Id == "b");

        Assert.Equal(new[] { "b" }, result.Select(s => s.Id));
    }

    [Fact]
    public void An_unmatched_permitted_id_yields_an_empty_result_not_an_error()
    {
        var sources = new List<KnowledgeCatalogSource> { Source("a") };
        var query = new KnowledgeQuery("x") { PermittedSourceIds = new HashSet<string> { "typo-id" } };

        var result = SourceVisibility.Filter(sources, query, defaultPolicy: null);

        Assert.Empty(result);
    }
}

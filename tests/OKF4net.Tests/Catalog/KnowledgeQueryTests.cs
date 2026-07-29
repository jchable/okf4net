// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="KnowledgeQuery"/>'s per-query resolver-selection fields: both
/// default to <see langword="null"/> ("defer to the host default") and both
/// survive a <c>with</c>-expression, so a caller can override one without
/// disturbing the other or the query's pre-existing fields.
/// </summary>
public class KnowledgeQueryTests
{
    [Fact]
    public void Resolver_selection_fields_default_to_null()
    {
        var query = new KnowledgeQuery("orders");

        Assert.Null(query.ResolverStrategy);
        Assert.Null(query.FairnessQuota);
    }

    [Fact]
    public void Resolver_selection_fields_round_trip_through_an_initializer()
    {
        var query = new KnowledgeQuery("orders", "sales")
        {
            StalePolicy = StalePolicy.Strict,
            ResolverStrategy = KnowledgeResolverStrategy.Merged,
            FairnessQuota = 2,
        };

        Assert.Equal(KnowledgeResolverStrategy.Merged, query.ResolverStrategy);
        Assert.Equal(2, query.FairnessQuota);
        Assert.Equal(StalePolicy.Strict, query.StalePolicy);
        Assert.Equal("sales", query.Tag);
    }

    [Fact]
    public void Overriding_one_selection_field_leaves_the_others_intact()
    {
        var original = new KnowledgeQuery("orders")
        {
            ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted,
            FairnessQuota = 3,
        };

        var narrowed = original with { FairnessQuota = 1 };

        Assert.Equal(KnowledgeResolverStrategy.PriorityWeighted, narrowed.ResolverStrategy);
        Assert.Equal(1, narrowed.FairnessQuota);
        Assert.Equal(3, original.FairnessQuota);
    }

    [Fact]
    public void Visibility_fields_default_to_unrestricted()
    {
        var query = new KnowledgeQuery("orders");

        Assert.Equal(KnowledgeAccessScope.Local, query.Scope);
        Assert.Null(query.PermittedSourceIds);
        Assert.Null(query.SourceVisibilityPolicy);
    }

    [Fact]
    public void Visibility_fields_round_trip_through_an_initializer()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        var permitted = new HashSet<string> { "a", "b" };
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool> policy = (_, source) => source.Id == "a";

        var query = new KnowledgeQuery("orders")
        {
            Scope = scope,
            PermittedSourceIds = permitted,
            SourceVisibilityPolicy = policy,
        };

        Assert.Equal(scope, query.Scope);
        Assert.Same(permitted, query.PermittedSourceIds);
        Assert.Same(policy, query.SourceVisibilityPolicy);
    }

    [Fact]
    public void Overriding_PermittedSourceIds_leaves_Scope_and_the_policy_intact()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme");
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool> policy = (_, source) => source.Id == "a";
        var original = new KnowledgeQuery("orders")
        {
            Scope = scope,
            SourceVisibilityPolicy = policy,
        };

        var narrowed = original with { PermittedSourceIds = new HashSet<string> { "a" } };

        Assert.Equal(scope, narrowed.Scope);
        Assert.Same(policy, narrowed.SourceVisibilityPolicy);
        Assert.Null(original.PermittedSourceIds);
    }

    [Fact]
    public void KnowledgeAccessScope_Local_is_all_null()
    {
        var a = new KnowledgeQuery("orders");

        Assert.True(a.Scope.IsLocal);
    }
}

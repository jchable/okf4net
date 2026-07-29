// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;

namespace OKF4net.Tests.Agents;

/// <summary>
/// The scenario the resolver-strategy work exists for:
/// <see cref="OkfContextProvider"/> renders a resolver's passages top-down
/// until its token budget runs out, so the resolver's ORDER decides what an
/// agent actually gets to see. Over a deliberately lopsided catalog, grouped
/// order spends the whole budget on one source while a merged ranking with a
/// fairness quota surfaces both.
/// </summary>
public class OkfContextProviderFusionTests
{
    private sealed class TestAgentSession : AgentSession { }

    /// <summary>
    /// "big" holds 6 concepts that all match strongly; "small" holds 1 that
    /// matches weakly. Both sources share a priority, so the two strategies
    /// differ purely in how they interleave.
    /// </summary>
    private static FileKnowledgeCatalog SetUpLopsidedCatalog(TempDir root)
    {
        for (var i = 0; i < 6; i++)
        {
            root.Write(Path.Combine("big", $"b{i}.md"),
                $"---\ntype: Note\ntitle: Orders orders {i}\ndescription: orders\n---\nOrders orders body {i}.\n");
        }

        root.Write(Path.Combine("small", "s0.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: d\n---\nOne mention of orders here.\n");

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "big", "path": "./big", "priority": 1, "role": "knowledge" },
                { "id": "small", "path": "./small", "priority": 1, "role": "knowledge" }
              ]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    private static FileMemoryStore EmptyMemoryStore(TempDir root)
    {
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        return new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.User] = Path.Combine(root.Path, "mem"),
        });
    }

    private static AIContextProvider.InvokingContext Invoking(AgentSession? session, string userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session, ai);
#pragma warning restore MAAI001
    }

    /// <summary>
    /// A budget tight enough that only the first few passages fit -- the
    /// whole point being that what fits depends on the resolver's ordering.
    /// Memory is unused here, so knowledge gets the entire budget.
    /// </summary>
    private static OkfContextProviderOptions TightBudget(int? fairnessQuota) => new()
    {
        TokenBudget = 120,
        KnowledgeBudgetShare = 1.0,
        MemoryBudgetShare = 0.0,
        MemoryCapture = MemoryCaptureMode.Disabled,
        ScopeAccessor = _ => new KnowledgeAccessScope(userId: "alice"),
        KnowledgeQueryFairnessQuota = fairnessQuota,
    };

    [Fact]
    public async Task Grouped_order_spends_the_whole_budget_on_one_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var provider = new OkfContextProvider(
            new GroupedKnowledgeResolver(catalog), EmptyMemoryStore(root), TightBudget(fairnessQuota: null));

        var result = await provider.ProvideForTest(Invoking(new TestAgentSession(), "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        // Grouped emits all of "big" before "small" is reached, and the
        // budget runs out first -- this is the defect the lot addresses.
        Assert.Contains("knowledge:big:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledge:small:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merged_ranking_with_a_fairness_quota_surfaces_both_sources()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var provider = new OkfContextProvider(
            new MergedKnowledgeResolver(catalog), EmptyMemoryStore(root), TightBudget(fairnessQuota: 1));

        var result = await provider.ProvideForTest(Invoking(new TestAgentSession(), "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        // Same catalog, same budget, same query: interleaving puts "small"
        // second, so it now fits.
        Assert.Contains("knowledge:big:", text, StringComparison.Ordinal);
        Assert.Contains("knowledge:small:", text, StringComparison.Ordinal);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// The acceptance gate for §8.7 of the producer code-graph design
/// (<c>docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md</c>):
/// a bundle dominated by generated code concepts must not starve the curated
/// ones out of the windows consumers actually read.
///
/// Measured before <see cref="ConceptSearch.TopDiversified"/> existed, on a
/// 395-concept corpus built from this repo's real symbols: curated concepts took
/// <b>1 of 55</b> top-5 slots, and 5 of 11 broad queries returned none at all in
/// the top 20. The two windows that matter are
/// <c>OkfBundleTools.Search</c>'s 20 results and
/// <c>OkfContextProviderOptions.MaxConceptsInjected</c>'s default of 5.
/// </summary>
public class SearchScaleTests
{
    private const int CodeConceptCount = 380;

    /// <summary>
    /// Broad terms a user or an agent would plausibly ask about, each of which
    /// a curated concept genuinely answers better than any single member does.
    /// </summary>
    private static readonly string[] Queries =
        ["validation", "bundle", "concept", "yaml", "catalog", "search", "index"];

    /// <summary>
    /// Writes a bundle shaped like a real producer output: a handful of curated
    /// concepts and a large generated <c>code/</c> subtree whose titles collide
    /// with the curated vocabulary on purpose — that collision is what produces
    /// the score ties the ordering has to survive.
    /// </summary>
    private static TempDir BuildCorpus()
    {
        var tmp = new TempDir();

        tmp.Write("overview.md",
            "---\ntype: Repository\ntitle: okf\ndescription: OKF4net implements the Open Knowledge Format, with bundle loading, validation, indexing and search.\n---\nRepository overview.\n");
        tmp.Write("packages/okf4net.md",
            "---\ntype: Package\ntitle: OKF4net\ndescription: The core library - bundle loading, validation, yaml parsing and concept search.\n---\nCore package.\n");
        tmp.Write("packages/okf4net-catalog.md",
            "---\ntype: Package\ntitle: OKF4net.Catalog\ndescription: Knowledge catalog model - sources, resolvers, the index and the memory store.\n---\nCatalog package.\n");
        tmp.Write("docs/readme.md",
            "---\ntype: Documentation\ntitle: README\ndescription: How to install the okf CLI, run bundle validation, browse the yaml frontmatter and search a bundle.\n---\nReadme.\n");

        var names = new[] { "Validate", "Bundle", "Concept", "Yaml", "Catalog", "Search", "Index", "Graph" };
        for (var i = 0; i < CodeConceptCount; i++)
        {
            var name = names[i % names.Length];
            tmp.Write(
                $"code/csharp/okf4net/type{i:D3}/{name.ToLowerInvariant()}.md",
                $"---\ntype: C# Member\ntitle: Type{i:D3}.{name}\ndescription: Member {name} on Type{i:D3}.\ntags: [csharp, method, public]\n---\n## Signatures\n\n- `public void {name}()`\n");
        }

        return tmp;
    }

    private static bool IsCurated(ScoredConcept s) =>
        !s.Concept.Id.ToString().StartsWith("code/", StringComparison.Ordinal);

    [Fact]
    public void Curated_concepts_reach_the_agent_injection_window_on_every_broad_query()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        foreach (var query in Queries)
        {
            var top5 = ConceptSearch.TopDiversified(ConceptSearch.Search(bundle.Concepts, query), 5);

            Assert.True(
                top5.Any(IsCurated),
                $"query '{query}': no curated concept in the top 5 — the agent would be injected only generated code.");
        }
    }

    [Fact]
    public void Curated_concepts_are_well_represented_in_the_search_window()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        foreach (var query in Queries)
        {
            var scored = ConceptSearch.Search(bundle.Concepts, query);
            var curatedAvailable = scored.Count(IsCurated);
            if (curatedAvailable == 0)
            {
                continue;   // the query genuinely matches no curated concept
            }

            var top20 = ConceptSearch.TopDiversified(scored, 20);

            Assert.True(
                top20.Count(IsCurated) >= Math.Min(curatedAvailable, 2),
                $"query '{query}': only {top20.Count(IsCurated)} curated concepts in the top 20 of {scored.Count} hits.");
        }
    }

    /// <summary>
    /// Locks the defect itself. Without this, a future change that quietly drops
    /// diversification would leave the two tests above passing for the wrong
    /// reason — or worse, someone would "simplify" TopDiversified back into a
    /// plain Take and see green.
    /// </summary>
    [Fact]
    public void The_undiversified_ordering_still_starves_curated_concepts_on_the_same_corpus()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        var plainTop5 = ConceptSearch.Search(bundle.Concepts, "bundle").Take(5);

        Assert.DoesNotContain(plainTop5, IsCurated);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;
using OKF4net.Tests.Agents;

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
/// the top 20.
///
/// There are THREE windows, not two, and they are exercised here THROUGH THE
/// PRODUCTION TYPES — <c>OkfBundleTools.Search</c>'s 20 results, the V1
/// <c>OkfContextProvider</c>'s <c>MaxConceptsInjected</c> concepts, and the
/// scoped (V2) <c>OkfContextProvider</c>'s token-budget prefix over resolver
/// passages. Calling <see cref="ConceptSearch.TopDiversified"/> directly proves
/// only that the function works; it stays green if either call site is reverted
/// to a plain <c>Take</c>, which is the whole failure this gate exists to catch.
/// The two selector-level tests below are kept, but named for what they are.
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
    /// Writes a bundle shaped like a real producer output into
    /// <paramref name="prefix"/> (empty for the temp root): a handful of curated
    /// concepts and a large generated <c>code/</c> subtree whose titles collide
    /// with the curated vocabulary on purpose — that collision is what produces
    /// the score ties the ordering has to survive.
    /// </summary>
    /// <param name="tmp">The temp directory to write into.</param>
    /// <param name="prefix">Subdirectory for the bundle root, or the empty string for the temp root.</param>
    /// <param name="sharedVocabulary">
    /// When <see langword="true"/>, every generated member's description and
    /// signature line also carry the curated vocabulary, as okfgen's
    /// doc-comment-derived descriptions do on a real repository. That makes
    /// ALL 380 members match every broad query instead of one name in eight,
    /// which is what pushes the hit list past what a token budget can render —
    /// the condition the scoped path is measured under (design §8.7).
    /// </param>
    private static void WriteCorpus(TempDir tmp, string prefix, bool sharedVocabulary)
    {
        var root = prefix.Length == 0 ? string.Empty : prefix + "/";

        tmp.Write($"{root}overview.md",
            "---\ntype: Repository\ntitle: okf\ndescription: OKF4net implements the Open Knowledge Format, with bundle loading, validation, indexing and search.\n---\nRepository overview.\n");
        tmp.Write($"{root}packages/okf4net.md",
            "---\ntype: Package\ntitle: OKF4net\ndescription: The core library - bundle loading, validation, yaml parsing and concept search.\n---\nCore package.\n");
        tmp.Write($"{root}packages/okf4net-catalog.md",
            "---\ntype: Package\ntitle: OKF4net.Catalog\ndescription: Knowledge catalog model - sources, resolvers, the index and the memory store.\n---\nCatalog package.\n");
        tmp.Write($"{root}docs/readme.md",
            "---\ntype: Documentation\ntitle: README\ndescription: How to install the okf CLI, run bundle validation, browse the yaml frontmatter and search a bundle.\n---\nReadme.\n");

        var names = new[] { "Validate", "Bundle", "Concept", "Yaml", "Catalog", "Search", "Index", "Graph" };
        for (var i = 0; i < CodeConceptCount; i++)
        {
            var name = names[i % names.Length];
            var description = sharedVocabulary
                ? $"Member {name} on Type{i:D3}, part of the okf4net bundle concept catalog yaml validation search surface."
                : $"Member {name} on Type{i:D3}.";
            var signature = sharedVocabulary
                ? $"- `public void {name}()` — loads a bundle concept from the catalog, validating its yaml frontmatter for search."
                : $"- `public void {name}()`";

            tmp.Write(
                $"{root}code/csharp/okf4net/type{i:D3}/{name.ToLowerInvariant()}.md",
                $"---\ntype: C# Member\ntitle: Type{i:D3}.{name}\ndescription: {description}\ntags: [csharp, method, public]\n---\n## Signatures\n\n{signature}\n");
        }
    }

    private static TempDir BuildCorpus()
    {
        var tmp = new TempDir();
        WriteCorpus(tmp, prefix: string.Empty, sharedVocabulary: false);
        return tmp;
    }

    private static bool IsCurated(ScoredConcept s) => IsCurated(s.Concept.Id.ToString());

    private static bool IsCurated(string conceptId) => !conceptId.StartsWith("code/", StringComparison.Ordinal);

    // ---- The selector itself ------------------------------------------------

    [Fact]
    public void The_selector_puts_a_curated_concept_in_the_first_five_on_every_broad_query()
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
    public void The_selector_represents_curated_concepts_in_the_first_twenty_on_every_broad_query()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        foreach (var query in Queries)
        {
            var scored = ConceptSearch.Search(bundle.Concepts, query);
            var curatedAvailable = scored.Count(IsCurated);
            var top20 = ConceptSearch.TopDiversified(scored, 20);

            Assert.True(
                top20.Count(IsCurated) >= Math.Min(curatedAvailable, 2),
                $"query '{query}': only {top20.Count(IsCurated)} curated concepts in the top 20 of {scored.Count} hits.");
        }
    }

    // ---- The three production windows ---------------------------------------

    /// <summary>
    /// The <c>okf_search</c> window (<c>OkfBundleTools.Search</c>, 20 results),
    /// through the tool rather than through the selector it happens to call —
    /// so reverting that call site to <c>scored.Take(MaxResults)</c> fails here.
    /// </summary>
    [Fact]
    public void The_search_tool_returns_curated_concepts_on_every_broad_query()
    {
        using var tmp = BuildCorpus();
        var tools = new OkfBundleTools(tmp.Path);

        foreach (var query in Queries)
        {
            var shown = SearchResultIds(tools.Search(query));

            Assert.True(
                shown.Any(IsCurated),
                $"query '{query}': okf_search returned no curated concept in {shown.Count} shown result(s).");
        }
    }

    /// <summary>
    /// The V1 agent-injection window (<c>OkfContextProvider(OkfBundleTools)</c>,
    /// <c>MaxConceptsInjected</c> concepts), through the provider — so reverting
    /// that call site to <c>admitted.Take(...)</c> fails here.
    /// </summary>
    [Fact]
    public async Task The_agent_injection_window_reaches_curated_concepts_on_every_broad_query()
    {
        using var tmp = BuildCorpus();
        var provider = new OkfContextProvider(new OkfBundleTools(tmp.Path));

        foreach (var query in Queries)
        {
            var injected = InjectedConceptIds(await provider.ProvideForTest(Invoking(query), CancellationToken.None));

            Assert.True(
                injected.Any(IsCurated),
                $"query '{query}': the provider injected no curated concept among {injected.Count} block(s).");
        }
    }

    /// <summary>
    /// The scoped (V2) injection window — the path hosts are steered towards,
    /// since the V1 provider's <c>MemoryDirectory</c> is <c>[Obsolete]</c>. It
    /// truncates by TOKEN BUDGET rather than by slot count, rendering a
    /// contiguous prefix of the resolver's passages, which is a truncation all
    /// the same. Measured on this corpus before it was diversified: 38 of 336
    /// passages rendered and ZERO curated concepts injected on 6 of the 7
    /// queries, the first curated passage sitting at rank #333.
    /// </summary>
    [Fact]
    public async Task The_scoped_injection_window_reaches_curated_concepts_on_every_broad_query()
    {
        using var tmp = new TempDir();
        using var catalog = BuildScopedCatalog(tmp);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.User] = Path.Combine(tmp.Path, "mem"),
        });

        // Default options: the budget bites here on its own, without a
        // contrived tight budget.
        var provider = new OkfContextProvider(new GroupedKnowledgeResolver(catalog), store, new OkfContextProviderOptions());

        foreach (var query in Queries)
        {
            var injected = InjectedPassageConceptIds(await provider.ProvideForTest(Invoking(query), CancellationToken.None));

            Assert.True(
                injected.Any(IsCurated),
                $"query '{query}': the scoped provider injected no curated concept among {injected.Count} passage(s).");
        }
    }

    // ---- The control --------------------------------------------------------

    /// <summary>
    /// Locks the CORPUS, not the code: it says that on this corpus a plain
    /// <c>Take</c> and a diversified selection genuinely differ, so the tests
    /// above are green because of the ordering and not because any ordering
    /// would do. (It does not guard against someone reducing
    /// <see cref="ConceptSearch.TopDiversified"/> to a <c>Take</c> — that
    /// mutation turns the tests above red, which is their job. This one turns
    /// red instead when the corpus stops discriminating, e.g. if the generated
    /// subtree shrank or stopped colliding with the curated vocabulary.)
    /// </summary>
    [Fact]
    public void The_undiversified_ordering_still_starves_curated_concepts_on_the_same_corpus()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        var plainTop5 = ConceptSearch.Search(bundle.Concepts, "bundle").Take(5);

        Assert.DoesNotContain(plainTop5, IsCurated);
    }

    // ---- Harness ------------------------------------------------------------

    /// <summary>Concept ids from <c>FormatSearchResults</c>' <c>* &lt;id&gt; — &lt;title&gt; (&lt;score&gt;)</c> lines.</summary>
    private static List<string> SearchResultIds(string rendered) =>
        [.. rendered.Split('\n')
            .Where(line => line.StartsWith("* ", StringComparison.Ordinal))
            .Select(line => line[2..].Split(' ')[0])];

    /// <summary>Concept ids of the V1 provider's injected blocks (the root index block excluded).</summary>
    private static List<string> InjectedConceptIds(AIContext context) =>
        [.. BlockIds(context).Where(id => id != "index")];

    /// <summary>Concept ids of the scoped provider's injected knowledge blocks (<c>knowledge:&lt;source&gt;:&lt;id&gt;</c>).</summary>
    private static List<string> InjectedPassageConceptIds(AIContext context) =>
        [.. BlockIds(context)
            .Where(id => id.StartsWith("knowledge:", StringComparison.Ordinal))
            .Select(id => id[(id.IndexOf(':', "knowledge:".Length) + 1)..])];

    private static IEnumerable<string> BlockIds(AIContext context)
    {
        var text = context.Messages is null ? string.Empty : string.Join("\n", context.Messages.Select(m => m.Text));
        return Regex.Matches(text, "<okf-context id=\"([^\"]*)\">").Select(m => m.Groups[1].Value);
    }

    private static AIContextProvider.InvokingContext Invoking(string userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session: null, ai);
#pragma warning restore MAAI001
    }

    /// <summary>
    /// A one-source catalog over the shared-vocabulary corpus, plus the empty
    /// memory-tier root the scoped provider's store needs.
    /// </summary>
    private static FileKnowledgeCatalog BuildScopedCatalog(TempDir tmp)
    {
        WriteCorpus(tmp, prefix: "kb", sharedVocabulary: true);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "mem"));
        tmp.Write("catalog.json", """
            { "version": 1, "sources": [ { "id": "kb", "path": "./kb", "role": "knowledge" } ] }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(tmp.Path, "catalog.json"),
            CatalogRoot = tmp.Path,
            WatchForChanges = false,
        });
    }
}

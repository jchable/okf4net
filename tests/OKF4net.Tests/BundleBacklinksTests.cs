// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace OKF4net.Tests;

/// <summary>
/// Pins the semantics of <see cref="Bundle.Backlinks"/> (#148): each citer
/// appears once per target, however many times or in whatever form it links
/// to it, and citers appear in first-citation order, which is the order
/// <see cref="Bundle.Concepts"/> lists them in (the graph pass visits concepts
/// in that order). The viewer's "Cited by" list and the agent tools render
/// backlinks in exactly this order, so it is observable output.
/// </summary>
public class BundleBacklinksTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Receives xunit's per-test output sink.</summary>
    public BundleBacklinksTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_concept_citing_the_same_target_twice_is_one_backlink()
    {
        using var tmp = new TempDir();
        tmp.Write("hub.md", Doc(""));
        // Two spellings of the same target (§6.2): absolute, and relative with
        // an anchor. Both resolve to `hub`; the citer is credited once.
        tmp.Write("a.md", Doc("[one](/hub.md) and [two](./hub.md#top) and [three](/hub.md)."));

        var bundle = Bundle.Load(tmp.Path);

        Assert.Equal([Id("a")], bundle.Backlinks(Id("hub")));
        // Outbound links are not de-duplicated: every occurrence stays.
        Assert.Equal(3, bundle.LinksFrom(Id("a")).Count);
    }

    [Fact]
    public void Interleaved_links_A_B_A_credit_the_citer_once_per_target()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", Doc(""));
        tmp.Write("y.md", Doc(""));
        tmp.Write("a.md", Doc("[x](/x.md), [y](/y.md), then [x again](/x.md)."));

        var bundle = Bundle.Load(tmp.Path);

        Assert.Equal([Id("a")], bundle.Backlinks(Id("x")));
        Assert.Equal([Id("a")], bundle.Backlinks(Id("y")));
    }

    [Fact]
    public void Citers_appear_in_first_citation_order_with_a_self_link()
    {
        using var tmp = new TempDir();
        tmp.Write("hub.md", Doc("I cite [myself](/hub.md) and [a](/a.md)."));
        tmp.Write("a.md", Doc("[hub](/hub.md)"));
        tmp.Write("b.md", Doc("[hub](/hub.md) [hub](/hub.md)"));
        tmp.Write("c.md", Doc("[elsewhere](/a.md) then [hub](/hub.md)"));
        tmp.Write("z/deep.md", Doc("[hub](../hub.md)"));

        var bundle = Bundle.Load(tmp.Path);

        // First-citation order is concept order: the backlinks of a target are
        // exactly its citers, filtered from Bundle.Concepts without reordering.
        var expected = bundle.Concepts
            .Select(c => c.Id)
            .Where(id => bundle.LinksFrom(id).Any(l => l.Target.Equals(Id("hub"))))
            .ToList();
        Assert.Equal(expected, bundle.Backlinks(Id("hub")));
        Assert.Equal(
            [Id("a"), Id("b"), Id("c"), Id("hub"), Id("z/deep")],
            bundle.Backlinks(Id("hub")));

        Assert.Equal([Id("c"), Id("hub")], bundle.Backlinks(Id("a")));
    }

    /// <summary>
    /// A SMOKE CHECK, not a timing guard: it asserts nothing about time. One
    /// hub cited by 100,000 distinct concepts, each citing it twice. Before
    /// #148 the backlink de-duplication scanned the hub's whole citer list per
    /// link (~10^10 <see cref="ConceptId"/> comparisons here, minutes of CPU);
    /// with a set of seen (target, source) edges it is linear. A regression to
    /// a quadratic scan would make this test conspicuously slow rather than
    /// fail it. It calls the internal graph pass directly on in-memory
    /// concepts, so no file is written and the disk plays no part. The elapsed
    /// time is printed for comparison only.
    /// </summary>
    [Fact]
    public void A_hub_cited_by_a_hundred_thousand_concepts_builds_its_backlinks()
    {
        const int Citers = 100_000;
        var hub = Id("hub");
        var concepts = new List<Concept> { new(hub, "hub.md", OkfDocument.Parse(Doc(""))) };
        var citerDocument = OkfDocument.Parse(Doc("[hub](/hub.md) and [hub again](/hub.md#top)"));
        for (var i = 0; i < Citers; i++)
        {
            var id = Id(string.Create(CultureInfo.InvariantCulture, $"c/{i:D6}"));
            concepts.Add(new Concept(id, id + ".md", citerDocument));
        }

        var index = new Dictionary<ConceptId, int>();
        for (var i = 0; i < concepts.Count; i++)
        {
            index[concepts[i].Id] = i;
        }

        var watch = Stopwatch.StartNew();
        var (outbound, backlinks) = Bundle.BuildGraph(concepts, index);
        watch.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"BuildGraph, {Citers:N0} citers x 2 links: {watch.Elapsed.TotalMilliseconds:0.0} ms"));

        Assert.Equal(concepts.Skip(1).Select(c => c.Id), backlinks[hub]);
        Assert.Single(backlinks);
        Assert.All(concepts.Skip(1), c => Assert.Equal(2, outbound[c.Id].Count));
    }

    private static ConceptId Id(string id) => ConceptId.Parse(id);

    private static string Doc(string body) => "---\ntype: Note\n---\n" + body + "\n";
}

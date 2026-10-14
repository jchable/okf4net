// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace OKF4net.Tests;

/// <summary>
/// Pins the semantics of <see cref="Bundle.Backlinks"/> (#148): each citer
/// appears once per target, however many times or in whatever form it links
/// to it, and citers appear in first-citation order, which is the order the
/// concepts are loaded in (the graph pass visits them in that order). The
/// viewer's "Cited by" list and the agent tools render backlinks in exactly
/// this order, so it is observable output.
/// </summary>
public class BundleBacklinksTests
{
    [Fact]
    public void A_concept_citing_the_same_target_three_times_is_one_backlink()
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

        // Load (path) order, each citer once, the self-link included.
        Assert.Equal(
            [Id("a"), Id("b"), Id("c"), Id("hub"), Id("z/deep")],
            bundle.Backlinks(Id("hub")));
        Assert.Equal([Id("c"), Id("hub")], bundle.Backlinks(Id("a")));
    }

    internal static ConceptId Id(string id) => ConceptId.Parse(id);

    internal static string Doc(string body) => "---\ntype: Note\n---\n" + body + "\n";
}

/// <summary>
/// The scale guard for #148: the backlink de-duplication must stay linear in a
/// target's citer count.
///
/// <para><b>What it proves.</b> That building the graph for one hub cited by
/// <see cref="Citers"/> distinct concepts finishes under
/// <see cref="PathologicalCeiling"/>. Measured on the machine this was written
/// on (Windows 11, Release): with the set of seen edges, 200,000 citers took
/// about 1.8 s; with the former <c>List.Contains</c> scan this test failed,
/// abandoned at the 60 s ceiling (100,000 citers alone took 170 s to finish, so
/// 200,000 would take about 11 minutes, the scan growing with the square of the
/// count). A runner would have to be about ten times faster than that machine
/// for a quadratic scan to slip under the ceiling, or over thirty times slower
/// for the linear code to trip it.</para>
///
/// <para><b>What it does not prove.</b> It is not a performance target and
/// says nothing about any duration below the ceiling: a constant-factor
/// slowdown, or a mildly superlinear one, passes. It exercises only the graph
/// pass (<see cref="Bundle.BuildGraph"/>, called directly on in-memory
/// concepts, so the disk plays no part), not the walk or the parse of
/// <see cref="Bundle.Load"/>, which <see cref="BundleLoadPerformanceTests"/>
/// covers.</para>
///
/// <para><b>Runs alone.</b> It sits in
/// <see cref="BundleLoadPerformanceCollection"/> (no parallel neighbour) for the
/// reason that collection exists: a CPU burst run alongside the suite pushed a
/// wall-clock-bounded test elsewhere over its bound on a Windows CI runner. The
/// graph pass runs on a worker task the test waits on for at most the ceiling,
/// so a quadratic regression fails after the ceiling instead of holding the
/// run for many minutes (the abandoned task keeps a core busy until the test
/// host exits). The <c>Performance</c> category is not filtered out by CI, so
/// it runs there.</para>
/// </summary>
[Collection(BundleLoadPerformanceCollection.Name)]
public class BundleBacklinksScaleTests
{
    private const int Citers = 200_000;

    /// <summary>
    /// NOT a performance target: a pathology tripwire, as in
    /// <see cref="BundleLoadPerformanceTests"/>, sitting over 30x above the
    /// linear time measured locally and about 11x below the quadratic one.
    /// </summary>
    private static readonly TimeSpan PathologicalCeiling = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    /// <summary>Receives xunit's per-test output sink.</summary>
    public BundleBacklinksScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task A_hub_cited_by_two_hundred_thousand_concepts_builds_its_backlinks_under_the_ceiling()
    {
        var hub = BundleBacklinksTests.Id("hub");
        var concepts = new List<Concept> { new(hub, "hub.md", OkfDocument.Parse(BundleBacklinksTests.Doc(""))) };
        // Each citer cites the hub twice, so the de-duplication runs on every link.
        var citerDocument = OkfDocument.Parse(BundleBacklinksTests.Doc("[hub](/hub.md) and [hub again](/hub.md#top)"));
        for (var i = 0; i < Citers; i++)
        {
            var id = BundleBacklinksTests.Id(string.Create(CultureInfo.InvariantCulture, $"c/{i:D6}"));
            concepts.Add(new Concept(id, id + ".md", citerDocument));
        }

        var index = new Dictionary<ConceptId, int>();
        for (var i = 0; i < concepts.Count; i++)
        {
            index[concepts[i].Id] = i;
        }

        var watch = Stopwatch.StartNew();
        var build = Task.Run(() => Bundle.BuildGraph(concepts, index));
        var finished = await Task.WhenAny(build, Task.Delay(PathologicalCeiling)) == build;
        watch.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"BuildGraph, {Citers:N0} citers x 2 links: {watch.Elapsed.TotalMilliseconds:0.0} ms{(finished ? "" : " (abandoned at the ceiling)")}"));

        Assert.True(
            finished,
            string.Create(CultureInfo.InvariantCulture, $"BuildGraph did not finish within the {PathologicalCeiling.TotalSeconds:0} s pathology ceiling for one hub cited by {Citers:N0} concepts: the backlink de-duplication is no longer linear (#148)."));

        var (outbound, backlinks) = await build;
        Assert.Equal(concepts.Skip(1).Select(c => c.Id), backlinks[hub]);
        Assert.Single(backlinks);
        Assert.All(concepts.Skip(1), c => Assert.Equal(2, outbound[c.Id].Count));
    }
}

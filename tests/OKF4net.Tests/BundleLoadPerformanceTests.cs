// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace OKF4net.Tests;

/// <summary>
/// A performance BASELINE for <see cref="Bundle.Load"/> on a large synthetic
/// bundle (#6). It reports numbers; its only timing gate is a pathology
/// ceiling, never a target.
///
/// <para><b>Bundle shape.</b> 2,001 concepts, generated into a
/// <see cref="TempDir"/> at test time (nothing is checked in): one hub,
/// <c>overview.md</c>, at the root, plus 4 domains x 5 sections x 100
/// concepts at <c>{domain}/s{n}/c{nnnn}.md</c>, and a root <c>index.md</c>
/// carrying <c>okf_version</c> (§12). Every concept has producer-grade v0.2
/// frontmatter (<c>type</c>, <c>title</c>, <c>description</c>,
/// <c>resource</c>, <c>tags</c> and a <c>generated</c> stamp, §4.1/§5.2 —
/// <see cref="BundleValidator"/> reports no error or warning on it, only the
/// planted broken links as info). The hub links to the first concept of each
/// of the 20 sections. Each of the 2,000 others has a short body with a
/// heading, a fenced code block (holding link-shaped text that must not
/// count) and six links: four internal ones that resolve to an existing
/// concept (an absolute link, a <c>./</c> sibling, a <c>../../</c>
/// cross-domain link with an anchor, and the hub), an external URL and a pure
/// anchor (§6.1). Every hundredth of them also carries one absolute link to a
/// concept that does not exist, so both resolution outcomes run. Total:
/// 8,040 resolved links, 20 of them broken.</para>
///
/// <para><b>What is measured.</b> Wall-clock time of <see cref="Bundle.Load"/>
/// alone: the directory walk, reading and UTF-8-decoding every file, parsing
/// frontmatter and scanning bodies for links, building the id index and the
/// link/backlink graph. Generating the files is timed separately and is NOT
/// part of the reported load numbers. Two loads are reported: the first and
/// the second measured load of this bundle. Whether the first one pays for
/// JIT compilation of the load path depends on what ran before it: run alone
/// (the filter below) it usually does, but in the full suite other tests have
/// typically called <see cref="Bundle.Load"/> already and xunit gives no
/// ordering guarantee, so it is not a JIT-cold figure. The second runs right
/// after the first, on this same bundle. Neither is a cold-disk measurement:
/// the files were just written, so the OS file cache holds them for both. Not measured: validation, index generation, search,
/// memory use.</para>
///
/// <para><b>How to see the numbers.</b>
/// <c>dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleLoadPerformanceTests" --logger "console;verbosity=detailed"</c>
/// prints them under "Standard Output Messages". The numbers are only
/// comparable between runs on the same machine (same disk, same antivirus,
/// same load): they move with hardware, file-system and on-access scanning,
/// so a figure from a CI runner says nothing about a figure from a laptop.
/// Compare a before/after pair on one machine, several runs each.</para>
///
/// <para><b>What is asserted.</b> Only shape: the concept count, no parse
/// errors, the planted links resolving or dangling exactly as planted (which
/// proves the link pass ran, not just the file walk), the bundle staying
/// producer-grade under <see cref="BundleValidator"/>, and one deliberately
/// generous time ceiling — see <see cref="PathologicalCeiling"/>.</para>
///
/// <para><b>Runs alone.</b> The class sits in a collection with
/// parallelization disabled (<see cref="BundleLoadPerformanceCollection"/>),
/// which xunit runs by itself after every parallel collection has finished.
/// Writing and loading 2,001 files is a burst of disk and CPU work (and of
/// on-access scanning on Windows); run alongside the rest of the suite, it
/// pushed a wall-clock-bounded test in <c>CliContainerEngineRunTests</c> over
/// its bound on a Windows CI runner, and the suite's own load would in turn
/// leak into these numbers.</para>
/// </summary>
[Collection(BundleLoadPerformanceCollection.Name)]
public class BundleLoadPerformanceTests
{
    private static readonly string[] Domains = ["datasets", "tables", "metrics", "guides"];
    private const int SectionsPerDomain = 5;
    private const int ConceptsPerSection = 100;
    private static readonly int GeneratedConcepts = Domains.Length * SectionsPerDomain * ConceptsPerSection;
    private const int BrokenEvery = 100;

    /// <summary>
    /// NOT a performance target. On the machine this baseline was written on
    /// (Windows 11, NTFS, a Debug build), a load took 0.35–0.75 s; this
    /// bound exists only so that a pathological regression (a quadratic pass
    /// over concepts or links, a per-file rescan of the tree) fails the suite
    /// instead of silently turning a sub-second load into minutes. It sits
    /// roughly two orders of magnitude above the observed time so that a slow
    /// or contended CI runner on any of the three platforms never trips it.
    /// </summary>
    private static readonly TimeSpan PathologicalCeiling = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    /// <summary>Receives xunit's per-test output sink.</summary>
    public BundleLoadPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public void Load_baseline_on_a_two_thousand_concept_bundle()
    {
        using var tmp = new TempDir();

        var generation = Stopwatch.StartNew();
        var expected = Generate(tmp);
        generation.Stop();

        var first = Stopwatch.StartNew();
        var bundle = Bundle.Load(tmp.Path);
        first.Stop();

        var second = Stopwatch.StartNew();
        var repeat = Bundle.Load(tmp.Path);
        second.Stop();

        var resolvedLinks = bundle.Concepts.Sum(c => bundle.LinksFrom(c.Id).Count);
        var broken = bundle.BrokenLinks();

        _output.WriteLine($"bundle: {bundle.Count} concepts, {resolvedLinks} resolved links ({broken.Count} broken), {bundle.IndexFiles.Count} index file(s)");
        _output.WriteLine($"generate: {Ms(generation.Elapsed)} (not part of the load figures)");
        _output.WriteLine($"load #1 (first measured): {Ms(first.Elapsed)}, {Rate(bundle.Count, first.Elapsed)} concepts/s, {Rate(resolvedLinks, first.Elapsed)} links/s");
        _output.WriteLine($"load #2 (repeat):         {Ms(second.Elapsed)}, {Rate(repeat.Count, second.Elapsed)} concepts/s, {Rate(resolvedLinks, second.Elapsed)} links/s");

        // ---- Shape: the load did all of its work --------------------------

        Assert.Empty(bundle.ParseErrors);
        Assert.Equal(GeneratedConcepts + 1, bundle.Count);
        Assert.Equal(bundle.Count, repeat.Count);
        Assert.Single(bundle.IndexFiles);
        Assert.Equal("0.2", bundle.OkfVersion);

        // Every planted internal link, and nothing else (the external URL, the
        // pure anchor and the link-shaped text inside the fenced code block
        // must not surface as edges).
        Assert.Equal(expected.Resolved, resolvedLinks);

        // Exactly the planted broken links dangle, and they are the ones planted.
        Assert.Equal(
            expected.Broken.OrderBy(t => t, StringComparer.Ordinal),
            broken.Select(b => b.RawTarget).OrderBy(t => t, StringComparer.Ordinal));

        // Spot-check one concept's resolution end to end: absolute, ./ sibling,
        // ../../ cross-domain with an anchor stripped, the hub, and its broken link.
        var c0100 = ConceptId.Parse("datasets/s1/c0100");
        Assert.Equal(
            [
                (ConceptId.Parse("datasets/s1/c0101"), true),
                (ConceptId.Parse("datasets/s1/c0199"), true),
                (ConceptId.Parse("tables/s1/c0600"), true),
                (ConceptId.Parse("overview"), true),
                (ConceptId.Parse("missing/ghost-0100"), false),
            ],
            bundle.LinksFrom(c0100).Select(l => (l.Target, l.Exists)));

        // The backlink side of the graph was built too: every generated
        // concept links to the hub.
        Assert.Equal(GeneratedConcepts, bundle.Backlinks(ConceptId.Parse("overview")).Count);

        // The synthetic bundle is producer-grade, so the baseline measures the
        // load of a bundle a real producer would write, not of a degraded one:
        // nothing above info, and the only infos are the planted broken links.
        // (Outside the timed region; validation is not part of the baseline.)
        var diagnostics = BundleValidator.Validate(bundle).Diagnostics;
        Assert.All(diagnostics, d => Assert.Equal((Severity.Info, DiagnosticCode.BrokenLink), (d.Severity, d.Code)));
        Assert.Equal(expected.Broken.Count, diagnostics.Count);

        // ---- The one timing assertion: a pathology tripwire, not a target --

        Assert.True(
            first.Elapsed < PathologicalCeiling,
            $"Bundle.Load took {Ms(first.Elapsed)} on {bundle.Count} concepts, above the {PathologicalCeiling.TotalSeconds:0} s pathology ceiling.");
        Assert.True(
            second.Elapsed < PathologicalCeiling,
            $"the repeat Bundle.Load took {Ms(second.Elapsed)} on {repeat.Count} concepts, above the {PathologicalCeiling.TotalSeconds:0} s pathology ceiling.");
    }

    /// <summary>Writes the synthetic bundle and returns what its links should resolve to.</summary>
    private static (int Resolved, List<string> Broken) Generate(TempDir tmp)
    {
        var resolved = 0;
        var broken = new List<string>();

        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Overview\n\n* [Overview](/overview.md) - Entry point of the synthetic load-baseline bundle.\n");

        var hub = new StringBuilder();
        hub.Append(Frontmatter("Overview", "Overview", "Entry point of the synthetic load-baseline bundle.", "overview", "https://example.com/okf/overview"));
        hub.Append("# Sections\n\n");
        for (var d = 0; d < Domains.Length; d++)
        {
            for (var s = 0; s < SectionsPerDomain; s++)
            {
                var firstInSection = ((d * SectionsPerDomain) + s) * ConceptsPerSection;
                hub.Append(CultureInfo.InvariantCulture, $"- [{Domains[d]} s{s}](/{PathOf(firstInSection)}.md)\n");
                resolved++;
            }
        }

        tmp.Write("overview.md", hub.ToString());

        var body = new StringBuilder();
        for (var i = 0; i < GeneratedConcepts; i++)
        {
            var (domain, section, local) = Place(i);
            var sectionBase = i - local;
            var sibling = sectionBase + ((local + ConceptsPerSection - 1) % ConceptsPerSection);
            var crossDomain = (i + (SectionsPerDomain * ConceptsPerSection)) % GeneratedConcepts;

            body.Clear();
            body.Append(Frontmatter(
                "BigQuery Table",
                $"Concept {i:D4}",
                $"Synthetic concept {i} in {domain}/s{section}, generated for the Bundle.Load baseline.",
                $"synthetic, {domain}, s{section}",
                $"https://example.com/okf/{PathOf(i)}"));
            body.Append(CultureInfo.InvariantCulture, $"# Concept {i:D4}\n\n");
            body.Append(CultureInfo.InvariantCulture, $"Follows into [the next concept](/{PathOf((i + 1) % GeneratedConcepts)}.md) and\n");
            body.Append(CultureInfo.InvariantCulture, $"back to [its sibling](./c{sibling:D4}.md). Its counterpart in another domain is\n");
            body.Append(CultureInfo.InvariantCulture, $"[here](../../{PathOf(crossDomain)}.md#schema); everything starts at the [overview](/overview.md).\n");
            body.Append(CultureInfo.InvariantCulture, $"See also [the upstream docs](https://example.com/docs/{i}) and [notes](#notes).\n\n");
            body.Append("```sql\n-- [not a link](/inside/a/fence.md)\nSELECT 1;\n```\n\n");
            body.Append("## Notes\n\nNothing else to say.\n");
            resolved += 4;

            if (i % BrokenEvery == 0)
            {
                var ghost = $"/missing/ghost-{i:D4}.md";
                body.Append(CultureInfo.InvariantCulture, $"\nOnce linked to [a retired concept]({ghost}).\n");
                broken.Add(ghost);
                resolved++;
            }

            tmp.Write(PathOf(i) + ".md", body.ToString());
        }

        return (resolved, broken);
    }

    private static string Frontmatter(string type, string title, string description, string tags, string resource) =>
        "---\n" +
        $"type: {type}\n" +
        $"title: {title}\n" +
        $"description: {description}\n" +
        $"resource: {resource}\n" +
        $"tags: [{tags}]\n" +
        "generated: { by: process:okf4net-load-baseline, at: 2026-10-05T00:00:00Z }\n" +
        "---\n\n";

    private static (string Domain, int Section, int Local) Place(int i) =>
        (Domains[i / (SectionsPerDomain * ConceptsPerSection)], (i / ConceptsPerSection) % SectionsPerDomain, i % ConceptsPerSection);

    private static string PathOf(int i)
    {
        var (domain, section, _) = Place(i);
        return string.Create(CultureInfo.InvariantCulture, $"{domain}/s{section}/c{i:D4}");
    }

    private static string Ms(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0.0} ms");

    private static string Rate(int count, TimeSpan t) =>
        t.TotalSeconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"{count / t.TotalSeconds:N0}") : "n/a";
}

/// <summary>
/// Runs <see cref="BundleLoadPerformanceTests"/> with no other test alongside it:
/// xunit executes a collection that disables parallelization on its own, after
/// all the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BundleLoadPerformanceCollection
{
    /// <summary>The collection's name, shared by the definition and the test class.</summary>
    public const string Name = "Bundle.Load performance baseline";
}

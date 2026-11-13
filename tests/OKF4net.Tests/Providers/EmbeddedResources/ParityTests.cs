// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>
/// #141 acceptance (spec §6.3): each bundle, loaded through FileSystemBundleProvider and through the
/// embedded provider, gives the same objects, load facts, concepts, links, diagnostics, audit, search,
/// executable-resource survey and generated indexes. Display paths (Concept.Path, Diagnostic.Path,
/// AuditFinding.Path) and version tokens (SHA-256 vs MVID) necessarily differ and are left out; a message
/// that embeds the display root is compared with each side's Location replaced by "&lt;root&gt;".
/// </summary>
public class ParityTests
{
    private static readonly IOkfClock Clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string> Bundles => new()
    {
        { "fixture-okf_v02", "tests/fixtures/okf_v02" },
        { "fixture-okf_v02_computation", "tests/fixtures/okf_v02_computation" },
        { "fixture-okf_v02_reserved", "tests/fixtures/okf_v02_reserved" },
        { "fixture-appendix_a", "tests/fixtures/appendix_a" },
        { "acme_retail", "bundles/acme_retail" },
    };

    private static (IBundleProvider Fs, IBundleProvider Emb) Providers(string prefix, string relative) => (
        new FileSystemBundleProvider(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar))),
        new EmbeddedResourceBundleProvider(typeof(ParityTests).Assembly, prefix));

    private static async Task<SortedDictionary<string, byte[]>> Objects(IBundleProvider p)
    {
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var listing in p.EnumerateAsync(null, BundleLimits.Default, CancellationToken.None))
        {
            using var obj = await p.ReadAsync(BundleKey.Parse(listing.RawName), null, CancellationToken.None);
            using var copy = new MemoryStream();
            await obj.Content.CopyToAsync(copy);
            result.Add(listing.RawName, copy.ToArray());
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public async Task Same_keys_and_bytes(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = await Objects(fs);
        var b = await Objects(emb);
        Assert.Equal(a.Keys, b.Keys);
        foreach (var (key, bytes) in a)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(b[key]), key);
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_load_facts_concepts_and_links(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);
        Assert.Equal(a.OkfVersion, b.OkfVersion);
        Assert.Equal(a.IndexKeys, b.IndexKeys);
        Assert.Equal(a.LogKeys, b.LogKeys);
        Assert.Equal(a.ParseProblems.Select(p => (p.RawName, p.Key, p.Error)), b.ParseProblems.Select(p => (p.RawName, p.Key, p.Error)));
        Assert.Equal(a.PortabilityIssues, b.PortabilityIssues);
        Assert.Equal(a.RejectedKeys.Order(), b.RejectedKeys.Order());
        Assert.Equal(a.Concepts.Select(c => (c.Key, c.Id, c.Document)), b.Concepts.Select(c => (c.Key, c.Id, c.Document)));
        foreach (var c in a.Concepts)
        {
            Assert.Equal(a.LinksFrom(c.Id), b.LinksFrom(c.Id));
            Assert.Equal(a.Backlinks(c.Id), b.Backlinks(c.Id));
        }

        Assert.Equal(a.BrokenLinks(), b.BrokenLinks());
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_diagnostics_audit_and_search(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);

        static object Diag(Diagnostic d, string location) =>
            (d.Severity, d.Key, d.Concept, d.Field, d.Code, d.Message.Replace(location, "<root>", StringComparison.Ordinal));
        Assert.Equal(
            BundleValidator.Validate(a, Clock).Diagnostics.Select(d => Diag(d, fs.Location)),
            BundleValidator.Validate(b, Clock).Diagnostics.Select(d => Diag(d, emb.Location)));

        var auditA = ConceptAudit.Run(a, default, Clock);
        var auditB = ConceptAudit.Run(b, default, Clock);
        Assert.Equal(auditA.EvaluatedAt, auditB.EvaluatedAt);
        Assert.Equal(auditA.AsOf, auditB.AsOf);
        Assert.Equal(auditA.ConceptCount, auditB.ConceptCount);
        Assert.Equal(auditA.TrustCounts.OrderBy(k => k.Key), auditB.TrustCounts.OrderBy(k => k.Key));
        Assert.Equal(auditA.StatusCounts.OrderBy(k => k.Key), auditB.StatusCounts.OrderBy(k => k.Key));
        Assert.Equal(auditA.StaleCount, auditB.StaleCount);
        Assert.Equal(
            auditA.Findings.Select(f => (f with { Path = string.Empty }, f.Key)),
            auditB.Findings.Select(f => (f with { Path = string.Empty }, f.Key)));

        foreach (var query in new[] { "data", "customer", "order", "revenue", "the" })
        {
            Assert.Equal(
                ConceptSearch.Search(a.Concepts, query).Select(s => (s.Concept.Id, s.Score)),
                ConceptSearch.Search(b.Concepts, query).Select(s => (s.Concept.Id, s.Score)));
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_executable_resources_read_pinned_to_each_sides_version(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);

        static IEnumerable<(ConceptId, ExecutableResource)> Flat(Bundle bundle) => bundle.ExecutableResources
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .SelectMany(kv => new[] { (kv.Key, kv.Value.Computation), (kv.Key, kv.Value.Attester) });

        Assert.Equal(
            Flat(a).Select(x => (x.Item1, x.Item2 with { Version = null })),
            Flat(b).Select(x => (x.Item1, x.Item2 with { Version = null })));

        foreach (var ((_, ra), (_, rb)) in Flat(a).Zip(Flat(b)))
        {
            if (ra.State == ExecutableResourceState.Pinned)
            {
                Assert.Equal(
                    a.ReadResourceText(ra.Key!, ra.Version, out _),
                    b.ReadResourceText(rb.Key!, rb.Version, out _));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_generated_indexes(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = new CapturingProvider(fs);
        var b = new CapturingProvider(emb);
        var reportA = IndexGenerator.RegenerateIndexes(a);
        var reportB = IndexGenerator.RegenerateIndexes(b);

        // Two empty results must not pass as parity: every regeneration writes at least the root index.md.
        Assert.Empty(reportA.Failed);
        Assert.Empty(reportB.Failed);
        Assert.NotEmpty(reportA.Written);
        Assert.Contains(BundleKey.Parse("index.md"), reportA.Written);
        Assert.Equal(reportA.Written.Select(k => k.ToString()).Order(StringComparer.Ordinal), a.Written.Keys);
        Assert.Equal(reportA.Written, reportB.Written);
        Assert.Equal(a.Written.Keys, b.Written.Keys);
        foreach (var (key, bytes) in a.Written)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(b.Written[key]), key);
        }
    }
}

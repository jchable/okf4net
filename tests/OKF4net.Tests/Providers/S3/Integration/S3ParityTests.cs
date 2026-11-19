// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// Spec T2 "Parité": each bundle, uploaded as is to a real RustFS and loaded through FileSystemBundleProvider and
/// through S3BundleProvider, asynchronously, gives the same objects, load facts, concepts, links, diagnostics, audit,
/// search, executable-resource survey (versions included: both sides are the SHA-256 of the bytes) and generated
/// indexes. A copy of the embedded provider's ParityTests, deliberately not shared with it. Display paths are left out;
/// a message that embeds the display root is compared with each side's Location replaced by "&lt;root&gt;".
/// </summary>
[Trait("Category", "S3Integration")]
[Collection(StoreCollection.Name)]
public class S3ParityTests(StoreFixture fx) : IAsyncLifetime
{
    private static readonly IOkfClock Clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> Uploads = new(StringComparer.Ordinal);

    public static TheoryData<string, string> Bundles => new()
    {
        { "fixture-okf_v02", "tests/fixtures/okf_v02" },
        { "fixture-okf_v02_computation", "tests/fixtures/okf_v02_computation" },
        { "fixture-okf_v02_reserved", "tests/fixtures/okf_v02_reserved" },
        { "fixture-appendix_a", "tests/fixtures/appendix_a" },
        { "acme_retail", "bundles/acme_retail" },
    };

    /// <summary>Every bundle uploaded once per fixture (per store), awaited before any theory of this class runs.</summary>
    public async Task InitializeAsync()
    {
        if (fx.SkipReason is not null)
        {
            return;
        }

        foreach (var row in Bundles)
        {
            var (prefix, relative) = ((string)row[0], (string)row[1]);
            await Uploads.GetOrAdd(fx.Endpoint + "|" + prefix, _ => new Lazy<Task>(() => fx.UploadDirectoryAsync(Local(relative), prefix))).Value;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Local(string relative) => Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private (IBundleProvider Fs, S3BundleProvider S3) Providers(string prefix, string relative) => (
        new FileSystemBundleProvider(Local(relative)),
        new S3BundleProvider(fx.Credentials, new S3BundleSource { Bucket = StoreFixture.Bucket, Prefix = prefix, ServiceUrl = fx.Endpoint, ForcePathStyle = true, AllowInsecureHttp = true, RequestTimeout = TimeSpan.FromSeconds(60) }));

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

    [SkippableTheory]
    [MemberData(nameof(Bundles))]
    public async Task Same_keys_and_bytes(string prefix, string relative)
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (fs, s3) = Providers(prefix, relative);
        using var _ = s3;
        var a = await Objects(fs);
        var b = await Objects(s3);
        Assert.NotEmpty(a);
        Assert.Equal(a.Keys, b.Keys);
        foreach (var (key, bytes) in a)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(b[key]), key);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Bundles))]
    public async Task Same_load_facts_concepts_and_links(string prefix, string relative)
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (fs, s3) = Providers(prefix, relative);
        using var _ = s3;
        var a = await Bundle.LoadAsync(fs);
        var b = await Bundle.LoadAsync(s3);
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

    [SkippableTheory]
    [MemberData(nameof(Bundles))]
    public async Task Same_diagnostics_audit_and_search(string prefix, string relative)
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (fs, s3) = Providers(prefix, relative);
        using var _ = s3;
        var a = await Bundle.LoadAsync(fs);
        var b = await Bundle.LoadAsync(s3);

        static object Diag(Diagnostic d, string location) =>
            (d.Severity, d.Key, d.Concept, d.Field, d.Code, d.Message.Replace(location, "<root>", StringComparison.Ordinal));
        Assert.Equal(
            (await BundleValidator.ValidateAsync(a, Clock)).Diagnostics.Select(d => Diag(d, fs.Location)),
            (await BundleValidator.ValidateAsync(b, Clock)).Diagnostics.Select(d => Diag(d, s3.Location)));

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

    [SkippableTheory]
    [MemberData(nameof(Bundles))]
    public async Task Same_executable_resources_and_versions_read_pinned_on_each_side(string prefix, string relative)
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (fs, s3) = Providers(prefix, relative);
        using var _ = s3;
        var a = await Bundle.LoadAsync(fs);
        var b = await Bundle.LoadAsync(s3);

        static IEnumerable<(ConceptId, ExecutableResource)> Flat(Bundle bundle) => bundle.ExecutableResources
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .SelectMany(kv => new[] { (kv.Key, kv.Value.Computation), (kv.Key, kv.Value.Attester) });

        // Versions included: the S3 provider's object version is the filesystem provider's format (spec D8).
        Assert.Equal(Flat(a), Flat(b));

        foreach (var ((_, ra), (_, rb)) in Flat(a).Zip(Flat(b)))
        {
            if (ra.State == ExecutableResourceState.Pinned)
            {
                var (textA, versionA) = await a.ReadResourceTextAsync(ra.Key!, ra.Version);
                var (textB, versionB) = await b.ReadResourceTextAsync(rb.Key!, rb.Version);
                Assert.Equal(textA, textB);
                Assert.Equal(ra.Version, versionA);
                Assert.Equal(rb.Version, versionB);
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Bundles))]
    public async Task Same_generated_indexes(string prefix, string relative)
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (fs, s3) = Providers(prefix, relative);
        using var _ = s3;
        var a = new CapturingProvider(fs);
        var b = new CapturingProvider(s3);
        var reportA = await IndexGenerator.RegenerateIndexesAsync(a);
        var reportB = await IndexGenerator.RegenerateIndexesAsync(b);

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

    /// <summary>Delegates reads to <paramref name="inner"/> and captures writes in memory: no fixture may be written, and the S3 provider is read-only.</summary>
    private sealed class CapturingProvider(IBundleProvider inner) : IBundleProvider
    {
        private readonly SortedDictionary<string, byte[]> _written = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, byte[]> Written => _written;

        public string Id => "capture:" + inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => new(CanWrite: true, CompletesSynchronously: inner.Capabilities.CompletesSynchronously);

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
        {
            lock (_written)
            {
                _written[key.ToString()] = content.ToArray();
                return ValueTask.FromResult(new BundleObjectVersion("captured:" + _written.Count));
            }
        }

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
        {
            lock (_written)
            {
                _written[key.ToString()] = [];
            }

            return ValueTask.CompletedTask;
        }
    }
}

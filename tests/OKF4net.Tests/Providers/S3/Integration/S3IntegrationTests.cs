// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using Amazon.S3;
using OKF4net.Attestation;
using OKF4net.Providers;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;
using Xunit.Abstractions;

namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// Spec T2 against a real RustFS (no fake below the provider): server refusals, hostile names against an independent
/// oracle, the lowered key bound, pinned reads, freshness, bounds, the load races and an outage. Outside CI by decision:
/// run <c>dotnet test tests/OKF4net.Tests --filter Category=S3Integration</c> with a container engine and check the run
/// reports 0 skipped. Each test works under its own key prefix.
/// </summary>
[Trait("Category", "S3Integration")]
[Collection(RustFsCollection.Name)]
public class S3IntegrationTests(RustFsFixture fx, ITestOutputHelper output)
{
    private const string Note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n";

    private S3BundleProvider Provider(string prefix) => Provider(fx, prefix);

    private static S3BundleProvider Provider(RustFsFixture store, string prefix, TimeSpan? timeout = null) =>
        new(store.Credentials, new S3BundleSource { Bucket = RustFsFixture.Bucket, Prefix = prefix, ServiceUrl = store.Endpoint, ForcePathStyle = true, AllowInsecureHttp = true, RequestTimeout = timeout ?? TimeSpan.FromSeconds(60) });

    [SkippableFact]
    public async Task The_server_refuses_dot_segments_and_double_slashes()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        foreach (var key in new[] { "t-dots/a/../b.md", "t-dots/a//b.md", "t-dots/a/./b.md" })
        {
            var e = await Assert.ThrowsAsync<AmazonS3Exception>(() => fx.PutAsync(key, "x"u8.ToArray()));
            Assert.Equal(400, (int)e.StatusCode);
            output.WriteLine($"PUT '{key}': HTTP {(int)e.StatusCode} {e.ErrorCode}");
        }

        Assert.Empty(await fx.ListAsync("t-dots/")); // the oracle agrees nothing was stored

        using var p = Provider("t-dots");
        await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            Assert.Fail("nothing may be listed");
        }
    }

    [SkippableFact]
    public async Task Accepted_hostile_names_serve_their_own_distinctive_content_and_match_an_independent_listing()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        string[] names = ["%2F.md", "%2e%2e.md", "%25.md", "%252e.md", "a+b.md", "a b.md", "a\\b.md", "a?b.md", "a#b.md", "caf\u00e9.md", "cafe\u0301.md", "dir/", ".md", "\U00010000.md", "\uE000.md"];
        foreach (var n in names)
        {
            await fx.PutAsync("t-names/" + n, Encoding.UTF8.GetBytes("CONTENT-OF:" + n));
        }

        await fx.PutAsync("t-names2/sibling.md", "x"u8.ToArray());

        // The path /okf//t-names/lead.md, signed as sent: RustFS normalizes the leading '/' of the key (spec M4). The
        // SDK cannot send it (measured: 403 SignatureDoesNotMatch), hence the fixture's own SigV4 PUT.
        Assert.Equal(200, await fx.PutRawPathAsync("/t-names/lead.md", Encoding.UTF8.GetBytes("CONTENT-OF:lead.md")));

        // Oracle: the admin client's listing WITHOUT EncodingType (keys as XML text: an independent decoding path).
        var plain = await fx.ListAsync("t-names/");
        var oracle = plain.Select(o => o.Key["t-names/".Length..]).ToArray();

        // Measurement only: what RustFS sends with EncodingType=url (the SDK does not decode it, gate G12).
        foreach (var o in await fx.ListAsync("t-names/", EncodingType.Url))
        {
            output.WriteLine($"listed (url): {o.Key}  ETag {o.ETag}  Size {o.Size}");
        }

        using var p = Provider("t-names");
        var listed = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            listed.Add(l.RawName);
        }

        Assert.Equal(oracle.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.Equal(names.Append("lead.md").Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal)); // every name, as written
        Assert.DoesNotContain("sibling.md", listed);
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(listed, listed.Order(Comparer<string>.Create(S3KeyCodec.CompareByCodePoint))); // UTF-8 order, spec §4.5 precondition
        Assert.Equal(oracle, listed); // and in the oracle's own order

        var read = new List<string>();
        foreach (var raw in listed.Where(r => BundleKey.TryParse(r, out _, out _)))
        {
            using var o = await p.ReadAsync(BundleKey.Parse(raw), null, default);
            using var ms = new MemoryStream();
            await o.Content.CopyToAsync(ms);
            Assert.Equal("CONTENT-OF:" + raw, Encoding.UTF8.GetString(ms.ToArray()));
            read.Add(raw);
        }

        foreach (var mustRead in new[] { "a b.md", "a+b.md", "%2F.md", "%2e%2e.md", "%25.md", "%252e.md", "a?b.md", "a#b.md", "caf\u00e9.md", "cafe\u0301.md", "lead.md" })
        {
            Assert.Contains(mustRead, read);
        }

        var bundle = await Bundle.LoadAsync(p); // Native policy: what the core rejects follows its base rules, the rest loads
        output.WriteLine($"load: {bundle.Concepts.Count} concepts, {bundle.ParseProblems.Count} parse problems, {bundle.RejectedKeys.Count} rejected");
        foreach (var problem in bundle.ParseProblems)
        {
            output.WriteLine($"  problem '{problem.RawName}': {problem.Error}");
        }

        Assert.NotEmpty(bundle.Concepts);

        // Every Markdown name listed (the core's rule: ".md" alone is not one) is accounted for by the core, exactly once:
        // a concept, or a local problem under its own raw name.
        var markdown = listed.Where(r => r.EndsWith(".md", StringComparison.Ordinal) && r != ".md").ToArray();
        Assert.Equal(14, markdown.Length);
        foreach (var raw in markdown)
        {
            Assert.True(
                bundle.Concepts.Any(c => c.Key is { } k && k.ToString() == raw) ^ bundle.ParseProblems.Any(x => x.RawName == raw),
                string.Join(' ', raw.Select(ch => ((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
        }

        Assert.Contains(bundle.ParseProblems, x => x.RawName == "a\\b.md"); // the core's base rule, under Native
    }

    [SkippableFact]
    public async Task A_lowered_key_bound_is_a_local_problem()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-long/" + new string('a', 60) + ".md", Encoding.UTF8.GetBytes(Note));
        await fx.PutAsync("t-long/ok.md", Encoding.UTF8.GetBytes(Note));
        using var p = Provider("t-long");
        var bundle = await Bundle.LoadAsync(p, new BundleLoadOptions { Limits = BundleLimits.Default with { MaxKeyBytes = 32 } });
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "ok");
        Assert.Equal(new string('a', 60) + ".md", Assert.Single(bundle.ParseProblems).RawName);
    }

    [SkippableFact]
    public async Task A_pinned_read_and_a_capture_refuse_changed_code()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.UploadDirectoryAsync(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02_computation"), "t-pin");
        using var p = Provider("t-pin");
        var bundle = await Bundle.LoadAsync(p);
        var (concept, survey) = bundle.ExecutableResources.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .Select(kv => (bundle.Concepts.Single(c => c.Id == kv.Key), kv.Value))
            .First(x => x.Value.Computation.State == ExecutableResourceState.Pinned);
        var key = survey.Computation.Key!;
        var (before, pinned) = await bundle.ReadResourceTextAsync(key, survey.Computation.Version); // a positive witness: served at its version
        Assert.Equal(survey.Computation.Version, pinned);
        Assert.NotEqual("CHANGED\n", before);

        await fx.PutAsync("t-pin/" + key, "CHANGED\n"u8.ToArray());

        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await bundle.ReadResourceTextAsync(key, survey.Computation.Version));
        var capture = await AttestationOrchestrator.CaptureAsync(bundle, concept);
        Assert.Null(capture.Capture); // refused: nothing to run, so no runtime stage can start
        Assert.NotNull(capture.Failure);
        Assert.True(capture.ImageOutdated); // the stored bytes moved past the image's pinned version
        Assert.Contains(capture.Failure!.Reasons, r => r.Contains("changed since the bundle was loaded", StringComparison.Ordinal));
        Assert.Null(capture.Failure.Receipt); // no execution stage started
    }

    [SkippableFact]
    public async Task OnAccess_sees_a_change_and_Every_only_after_its_interval()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-fresh/a.md", Encoding.UTF8.GetBytes(Note));
        using var p1 = Provider("t-fresh");
        using var p2 = Provider("t-fresh");
        using var onAccess = new BundleHandle(p1, refreshPolicy: BundleRefreshPolicy.OnAccess);
        // The interval runs on a clock the test moves, so a loaded machine can neither pass nor fail it by accident; the
        // store, its listing and its token are real.
        var clock = new ManualClock();
        using var every = new BundleHandle(p2, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromSeconds(3)), time: clock);
        Assert.DoesNotContain((await onAccess.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b");
        Assert.DoesNotContain((await every.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b");
        await fx.PutAsync("t-fresh/b.md", Encoding.UTF8.GetBytes(Note));
        Assert.Contains((await onAccess.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain((await every.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b"); // within the interval
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains((await every.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b"); // after it
    }

    [SkippableFact]
    public async Task Bounds_an_object_is_local_a_listing_fails_the_load()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-bounds/big.md", Encoding.UTF8.GetBytes(Note + new string('x', 5000)));
        await fx.PutAsync("t-bounds/ok.md", Encoding.UTF8.GetBytes(Note));
        using var p1 = Provider("t-bounds");
        using var p2 = Provider("t-bounds");
        var bundle = await Bundle.LoadAsync(p1, new BundleLoadOptions { Limits = BundleLimits.Default with { MaxObjectBytes = 1000 } });
        Assert.Contains(bundle.ParseProblems, x => x.RawName == "big.md");
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "ok");
        await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p2, new BundleLoadOptions { Limits = BundleLimits.Default with { MaxEntries = 1 } }));
    }

    [SkippableFact]
    public async Task A_concept_deleted_between_the_listing_and_its_read_fails_the_reload_and_keeps_the_previous_image()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-race-concept/a.md", Encoding.UTF8.GetBytes(Note));
        await fx.PutAsync("t-race-concept/c.md", Encoding.UTF8.GetBytes(Note));
        using var racing = new DeletingProvider(Provider("t-race-concept"), "c.md", () => fx.DeleteAsync("t-race-concept/c.md"));
        using var handle = new BundleHandle(racing, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();
        Assert.Contains(first.Concepts, c => c.Id.ToString() == "c");

        await fx.PutAsync("t-race-concept/z.md", Encoding.UTF8.GetBytes(Note)); // changes the token: the next access reloads
        racing.Arm();
        var second = await handle.GetCurrentAsync();

        Assert.True(racing.Deleted);
        Assert.Same(first, second);
        Assert.True(handle.IsStale);
        // The cause, not only the type (measured: the load failure carries the not-found read of c.md, in the closed
        // vocabulary — the key, never the bucket nor the endpoint).
        var error = Assert.IsType<BundleLoadException>(handle.LastLoadError);
        Assert.Equal("no object under 'c.md'", error.Message);
        var cause = Assert.IsType<BundleObjectNotFoundException>(error.InnerException);
        Assert.Equal(BundleKey.Parse("c.md"), cause.Key);
        Assert.Equal("no object under 'c.md'", cause.Message);
        Assert.Null(cause.InnerException); // nothing else (no server text, no endpoint) rides along
    }

    [SkippableFact]
    public async Task A_surveyed_resource_deleted_between_the_listing_and_its_read_is_Missing()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        const string concept = "---\ntype: Attested Computation\ntitle: C\ndescription: D.\nruntime: bigquery\ncomputation: q.sql\n---\n";
        await fx.PutAsync("t-race-resource/c.md", Encoding.UTF8.GetBytes(concept));
        await fx.PutAsync("t-race-resource/q.sql", "SELECT 1\n"u8.ToArray());
        using var racing = new DeletingProvider(Provider("t-race-resource"), "q.sql", () => fx.DeleteAsync("t-race-resource/q.sql"));
        var control = await Bundle.LoadAsync(racing); // a positive witness: before the race, the file is pinned
        Assert.Equal(ExecutableResourceState.Pinned, control.ExecutableResources[ConceptId.Parse("c")].Computation.State);

        racing.Arm();
        var bundle = await Bundle.LoadAsync(racing);

        Assert.True(racing.Deleted);
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "c"); // the load succeeds
        Assert.Equal(ExecutableResourceState.Missing, bundle.ExecutableResources[ConceptId.Parse("c")].Computation.State);
    }

    [SkippableFact]
    public async Task A_root_index_deleted_between_the_listing_and_its_read_leaves_no_okf_version()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-race-index/index.md", "---\nokf_version: \"0.2\"\n---\n\n# Index\n"u8.ToArray());
        await fx.PutAsync("t-race-index/a.md", Encoding.UTF8.GetBytes(Note));
        using var racing = new DeletingProvider(Provider("t-race-index"), "index.md", () => fx.DeleteAsync("t-race-index/index.md"));
        var control = await Bundle.LoadAsync(racing); // a positive witness: before the race, the version is read
        Assert.Equal("0.2", control.OkfVersion);

        racing.Arm();
        var bundle = await Bundle.LoadAsync(racing);

        Assert.True(racing.Deleted);
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "a"); // the load succeeds
        Assert.Null(bundle.OkfVersion);
    }

    [SkippableFact]
    public async Task A_stopped_store_keeps_the_previous_image_marked_stale()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);

        // A store of its own: stopping the shared one would fail every other test of the collection.
        await using var own = new RustFsFixture();
        await own.InitializeAsync();
        Assert.Null(own.SkipReason);
        await own.PutAsync("t-down/a.md", Encoding.UTF8.GetBytes(Note));
        using var p = Provider(own, "t-down", TimeSpan.FromSeconds(10));
        using var handle = new BundleHandle(p, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();
        Assert.False(handle.IsStale);

        await own.StopAsync();
        var second = await handle.GetCurrentAsync();

        Assert.Same(first, second);
        Assert.True(handle.IsStale);
        Assert.NotNull(handle.LastLoadError);
        output.WriteLine($"outage: {handle.LastLoadError!.GetType().Name}: {handle.LastLoadError.Message}");
    }

    /// <summary>
    /// The real provider, with one scripted race (spec §4.3 table): once armed, the first <see cref="ReadAsync"/> that
    /// starts after the listing has yielded <paramref name="target"/> first deletes it through the admin client, so the
    /// load lists it and then cannot read it (every read of it starts after its listing, hence after the deletion).
    /// RustFS cannot produce this ordering by itself.
    /// </summary>
    private sealed class DeletingProvider(S3BundleProvider inner, string target, Func<Task> delete) : IBundleProvider, IDisposable
    {
        private readonly SemaphoreSlim _once = new(1, 1);
        private volatile bool _armed;
        private volatile bool _listed;

        public bool Deleted { get; private set; }

        public void Arm() => _armed = true;

        public string Id => inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _listed = false; // this walk's own listing of the target, not an earlier load's
            await foreach (var listing in inner.EnumerateAsync(prefix, limits, ct))
            {
                if (listing.RawName == target)
                {
                    _listed = true;
                }

                yield return listing;
            }
        }

        public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            if (_armed && _listed && !Deleted)
            {
                await _once.WaitAsync(ct);
                try
                {
                    if (!Deleted)
                    {
                        await delete();
                        Deleted = true;
                    }
                }
                finally
                {
                    _once.Release();
                }
            }

            return await inner.ReadAsync(key, expected, ct);
        }

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);

        public void Dispose() => inner.Dispose();
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Agents;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Providers.S3;
using OKF4net.Tests.Catalog;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec T1: consumers, bridge, D7 of #141, no residual file I/O, messages.</summary>
#pragma warning disable CS0618 // the [Obsolete] path members and string tool bridges are exercised on purpose
[Collection(CurrentDirectoryCollection.Name)]
public class ConsumersTests
{
    private const string Fm = "type: Note\ntitle: New\ndescription: A new note.";
    private const string Note = "---\ntype: Note\ntitle: First\ndescription: The first note.\nsources:\n  - refs/q.sql\n---\n\nLinks to [b](b.md).\n";

    private static S3BundleProvider Provider(FakeS3Handler? fake = null) =>
        S3Test.Provider(fake ?? S3Test.Serving(S3Test.Objects(("kb/index.md", "# kb\n"), ("kb/a.md", Note), ("kb/b.md", Note.Replace("First", "Second", StringComparison.Ordinal)), ("kb/refs/q.sql", "SELECT 1\n"))));

    [Fact]
    public async Task Every_write_path_refuses_and_sends_nothing()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", Note)));
        using var p = Provider(fake);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.WriteAsync(BundleKey.Parse("x.md"), "x"u8.ToArray(), BundleWriteCondition.None, default));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.DeleteAsync(BundleKey.Parse("a.md"), BundleWriteCondition.None, default));
        var writer = new BundleConceptWriter(p);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await writer.WriteConceptAsync("notes/c", Fm, "C\n"));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await writer.AppendLogAsync("Update", "x"));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await IndexGenerator.RegenerateIndexesAsync(p));
        Assert.Empty(fake.Requests); // every write path refuses before any request, reads included
    }

    [Fact]
    public void The_Notify_refresh_policy_is_refused_up_front_and_the_polling_policies_are_not()
    {
        // #290: examined. This provider has no change signal (S3 sends none to a client that polls), so it is not an
        // IBundleChangeNotifier, and BundleHandle refuses Notify at construction (spec §5.3): an explicit configuration
        // error, never a handle that silently never refreshes. Polling is OnAccess or Every.
        using var p = Provider();
        Assert.IsNotAssignableFrom<IBundleChangeNotifier>(p);
        var e = Assert.Throws<ArgumentException>(() => new BundleHandle(p, refreshPolicy: BundleRefreshPolicy.Notify));
        Assert.Equal("refreshPolicy", e.ParamName);

        // Positive controls: the same provider takes the polling policies.
        using var onAccess = new BundleHandle(p, refreshPolicy: BundleRefreshPolicy.OnAccess);
        using var every = new BundleHandle(p, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_tool_set_offers_no_write_tool()
    {
        using var handle = new BundleHandle(Provider());
        var names = new OkfBundleTools(handle).GetTools(OkfToolMode.ReadWrite).Select(t => t.Name).ToArray();
        Assert.Contains("okf_read_concept", names);
        Assert.DoesNotContain("okf_write_concept", names);
    }

    [Fact]
    public async Task A_synchronous_read_over_a_really_pending_request_is_refused_and_cancelled()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeS3Handler
        {
            Respond = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var p = Provider(fake);
        var observed = new Observed(p);

        // BundleLimits.Default's 30 s RequestTimeout would end the operation by its deadline (classified as the
        // caller's cancellation, since the caller had cancelled) within the 60 s bound below: a limit longer than the
        // source's 5 minutes never wins, so only the cancellation itself can end the operation in time.
        var options = new BundleLoadOptions { Limits = BundleLimits.Default with { RequestTimeout = TimeSpan.FromMinutes(10) } };
        var e = Assert.Throws<InvalidOperationException>(() => Bundle.Load(observed, options));
        Assert.Equal(SyncBridge.IncompleteMessage, e.Message); // the core's own constant (internal, visible to this assembly)

        // The bridge never waits, so where the abandoned operation stands when it is cancelled (permit, credentials,
        // request) is a race. Its END is not: the fake never answers, so the operation ends only by honouring the
        // cancellation, wherever it was; and if its request had reached the fake, the fake saw it cancelled first.
        var end = await observed.FirstEnded.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsAssignableFrom<OperationCanceledException>(end);
        Assert.Equal(1, observed.Started);
        // Deterministic once the operation has ended, not a conditional that could skip its check: FakeS3Handler records a
        // request and enters Respond in one synchronous step, and the operation awaits that very call, so it cannot have
        // ended between the two. The fake never answers: it was entered if and only if it saw the cancellation. (The race
        // above decides which of the two worlds a run is in, and both satisfy this equality.)
        Assert.Equal(fake.Requests.Count > 0, cancelled.Task.IsCompleted);
    }

    /// <summary>Forwards to a provider and records how its first operation ended (with <see langword="null"/> for a success).</summary>
    private sealed class Observed(IBundleProvider inner) : IBundleProvider
    {
        private int _started;

        public TaskCompletionSource<Exception?> FirstEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started => Volatile.Read(ref _started);

        public string Id => inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => Watch(() => inner.GetVersionAsync(limits, ct));

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            var items = inner.EnumerateAsync(prefix, limits, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await items.MoveNextAsync();
                    }
                    catch (Exception x)
                    {
                        FirstEnded.TrySetResult(x);
                        throw;
                    }

                    if (!more)
                    {
                        break;
                    }

                    yield return items.Current;
                }
            }
            finally
            {
                await items.DisposeAsync();
            }

            FirstEnded.TrySetResult(null);
        }

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => Watch(() => inner.ReadAsync(key, expected, ct));

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => Watch(() => inner.ExistsAsync(key, ct));

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);

        private ValueTask<T> Watch<T>(Func<ValueTask<T>> start)
        {
            Interlocked.Increment(ref _started);
            return new(Body());

            async Task<T> Body()
            {
                try
                {
                    var result = await start();
                    FirstEnded.TrySetResult(null);
                    return result;
                }
                catch (Exception x)
                {
                    FirstEnded.TrySetResult(x);
                    throw;
                }
            }
        }
    }

    [Fact]
    public void A_synchronous_write_is_refused_before_it_starts()
    {
        var fake = new FakeS3Handler();
        using var p = Provider(fake);
        // The writer's capability guard runs first (BundleConceptWriter.GuardWritable), the synchronous-provider guard
        // right behind it: over this provider the refusal is the read-only one, before any request either way.
        Assert.Throws<BundleReadOnlyException>(() => new BundleConceptWriter(p).WriteConcept("notes/c", Fm, "C\n"));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task The_path_based_obsolete_members_refuse_this_provider()
    {
        using var p = Provider();
        var bundle = await Bundle.LoadAsync(p);
        var a = bundle.Concepts.Single(c => c.Id.ToString() == "a");
        Assert.Throws<NotSupportedException>(() => bundle.TryResolveResource(a, "refs/q.sql", out _, out _));
        Assert.Throws<NotSupportedException>(() => bundle.ReadResourceText("refs/q.sql"));
    }

    [Fact]
    public async Task Every_read_entry_point_leaves_an_empty_current_directory_empty()
    {
        using var empty = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(empty.Path);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
            using var p = Provider();
            var bundle = await Bundle.LoadAsync(p);
            _ = await BundleValidator.ValidateAsync(bundle, clock);
            _ = ConceptAudit.Run(bundle, default, clock);
            Assert.NotEmpty(ConceptSearch.Search(bundle.Concepts, "first"));
            var (key, _) = await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql");
            Assert.Equal("SELECT 1\n", (await bundle.ReadResourceTextAsync(key!)).Text);
            using var handle = new BundleHandle(Provider());
            var tools = new OkfBundleTools(handle);
            Assert.Contains("Links to", await tools.ReadConceptAsync("a"), StringComparison.Ordinal);
            Assert.Contains("a", await tools.SearchAsync("first"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Path));
    }

    [Fact]
    public async Task Neither_validation_paths_nor_tool_output_name_the_bucket_or_the_endpoint()
    {
        using var handle = new BundleHandle(Provider());
        var bundle = await handle.GetCurrentAsync();
        var report = await BundleValidator.ValidateAsync(bundle);
        Assert.All(report.Diagnostics, d => Assert.DoesNotContain("okf", d.Path ?? "", StringComparison.Ordinal));
        Assert.All(bundle.Concepts, c => Assert.StartsWith("s3:", c.Path, StringComparison.Ordinal));
        var text = await new OkfBundleTools(handle).ValidateBundleAsync();
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        // Case-sensitive on purpose (measured: an ignore-case check fails on the tool's own "conformant with OKF v0.2"):
        // the bucket is "okf", and an S3 bucket name is lowercase by rule, so a leak of it would be lowercase too, while
        // the uppercase product name is legitimate output.
        Assert.DoesNotContain("okf", text, StringComparison.Ordinal);

        // Not vacuous: the notes draw warnings, each displayed under the provider's Location.
        Assert.NotEmpty(report.Diagnostics);
        Assert.All(report.Diagnostics, d => Assert.StartsWith("s3:", d.Path, StringComparison.Ordinal));
        Assert.Contains("s3:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_segment_like_a_drive_keeps_its_exact_key()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/c:x.md", Note))));
        var bundle = await Bundle.LoadAsync(p);

        // ':' is not a concept id character (spec §2), so the object is a parse problem, not a concept: its key is
        // what must survive exactly. Path is display only (spec §4.3); on Windows Path.Combine may drop the location.
        Assert.Empty(bundle.Concepts);
        Assert.Equal("c:x.md", Assert.Single(bundle.ParseProblems).Key!.ToString());
        Assert.Equal("c:x.md", Assert.Single(bundle.PortabilityIssues).Key.ToString());
    }

    [Fact]
    public async Task A_failing_catalog_source_reports_neither_the_bucket_the_endpoint_nor_the_servers_code()
    {
        using var root = new TempDir();
        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "remote", "provider": "s3-test" } ] }""");
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.ServiceUnavailable, FakeS3Handler.Error("SlowDown"))) };
        using var p = Provider(fake);
        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [new TestProviderFactory("s3-test", _ => p)],
        });
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("first")).AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotEmpty(fake.Requests); // the source really failed on the 503, not before any request
        var unavailable = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.SourceUnavailable);
        Assert.Equal("remote", unavailable.SourceId);
        Assert.All(context.Diagnostics, d =>
        {
            Assert.DoesNotContain("okf", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SlowDown", d.Message, StringComparison.Ordinal);
        });

        // POSITIVE CONTROL of the "SlowDown" assertion above: the sentinel really reaches the exception the provider
        // raises (as the server's error code on the SDK exception it keeps for the host's log), so its absence from the
        // diagnostics is the catalog's doing, not a sentinel that never arrived.
        var raised = await Assert.ThrowsAsync<S3RequestException>(async () => await p.GetVersionAsync(BundleLimits.Default, default));
        Assert.Equal("SlowDown", Assert.IsAssignableFrom<Amazon.Runtime.AmazonServiceException>(raised.InnerException).ErrorCode);
    }
}
#pragma warning restore CS0618

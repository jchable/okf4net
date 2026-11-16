// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="FileMemoryStore"/> over a host provider that completes asynchronously, that
/// fails with its own exceptions, or that ignores the bounds it is given (Copilot's review of
/// #233 and #235): every operation awaits the provider, reports a failure as data by its
/// type only, and bounds the listing itself; a scope's view (<see cref="PrefixedBundleProvider"/>)
/// never walks the other scopes of its tier.
/// </summary>
public class FileMemoryStoreAsyncProviderTests
{
    private const string Frontmatter =
        "type: AgentMemory\n"
        + "title: Agent memory 2026-07-27\n"
        + "description: Captured exchanges.\n";

    private static readonly MemoryTier[] AllTiers = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    private static readonly KnowledgeAccessScope Alice = new(tenantId: "acme", userId: "alice", sessionId: "s1");

    private static readonly KnowledgeAccessScope Bob = new(tenantId: "globex", userId: "bob", sessionId: "s2");

    private static MemoryEntry Entry(string name, string body) =>
        new(name, Frontmatter, $"## 10:00:00 UTC\n\n{body}\n");

    private static Dictionary<MemoryTier, IBundleProvider> EveryTier(IBundleProvider provider) =>
        AllTiers.ToDictionary(t => t, _ => provider);

    private static string UserScope(KnowledgeAccessScope scope) => MemoryPath.For(MemoryTier.User, scope);

    [Fact]
    public async Task Write_read_enumerate_and_delete_round_trip_over_a_provider_that_completes_asynchronously()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var provider = new YieldingProvider(storage);
        var store = new FileMemoryStore(EveryTier(provider));

        foreach (var tier in AllTiers)
        {
            Assert.Equal(new MemoryWriteResult(true, null), await store.WriteAsync(Alice, Entry("2026-07-27", $"alice {tier} refunds"), tier));
        }

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        Assert.Empty(read.Diagnostics);
        Assert.Equal(3, read.Passages.Count);
        Assert.Equal(3, (await store.EnumerateAsync(Alice)).Count);

        // Before the store awaited the provider, this threw "provider completed asynchronously"
        // AFTER the deletion had been started, and left it running unawaited.
        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        Assert.Null(deletion.Error);
        Assert.Equal([$"{UserScope(Alice)}/2026-07-27.md"], Assert.Single(deletion.Tiers).Report.Deleted.Select(k => k.ToString()).ToList());
        Assert.Empty(storage.Enumerate(BundleKey.Parse(UserScope(Alice)), BundleLimits.Default));
        Assert.True(provider.Yields > 0, "the provider never completed asynchronously, so the test proved nothing");
    }

    [Fact]
    public async Task A_host_provider_timeout_is_reported_as_data_by_its_type_on_every_operation()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var seeding = new FileMemoryStore(EveryTier(storage));
        Assert.True((await seeding.WriteAsync(Alice, Entry("2026-07-27", "refunds"), MemoryTier.User)).Written);

        // A storage timeout surfacing as a TaskCanceledException, while the caller never cancelled.
        var failing = new YieldingProvider(storage) { Fail = () => new TaskCanceledException("secret host detail") };
        var store = new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = failing });

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        var diagnostic = Assert.Single(read.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Contains(nameof(TaskCanceledException), diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic.Message, StringComparison.Ordinal);

        Assert.Empty(await store.EnumerateAsync(Alice));

        var write = await store.WriteAsync(Alice, Entry("2026-07-28", "more"), MemoryTier.User);
        Assert.False(write.Written);
        Assert.DoesNotContain("secret", write.Error, StringComparison.Ordinal);

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        Assert.NotNull(deletion.Error);
        Assert.Contains(nameof(TaskCanceledException), deletion.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", deletion.Error, StringComparison.Ordinal);
        Assert.NotEmpty(storage.Enumerate(BundleKey.Parse(UserScope(Alice)), BundleLimits.Default));
    }

    [Fact]
    public async Task A_cancellation_of_the_callers_own_token_still_propagates()
    {
        // The token is cancelled by the provider's first operation, so each call below gets past
        // its own entry check and is cancelled from inside the provider: only the store's
        // exception filter decides whether that cancellation propagates or becomes data.
        static (FileMemoryStore Store, CancellationTokenSource Cts) Fresh(InMemoryBundleProvider storage)
        {
            var cts = new CancellationTokenSource();
            var provider = new YieldingProvider(storage) { BeforeEach = () => cts.Cancel() };
            return (new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = provider }), cts);
        }

        var storage = new InMemoryBundleProvider(canWrite: true);
        Assert.True((await new FileMemoryStore(EveryTier(storage)).WriteAsync(Alice, Entry("2026-07-27", "x"), MemoryTier.User)).Written);

        var (reader, readCts) = Fresh(storage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(Alice, new KnowledgeQuery("x"), readCts.Token));
        var (writer, writeCts) = Fresh(storage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.WriteAsync(Alice, Entry("2026-07-28", "y"), MemoryTier.User, writeCts.Token));
        var (deleter, deleteCts) = Fresh(storage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await deleter.DeleteScopeAsync(Alice, MemoryTier.User, deleteCts.Token));
        Assert.NotEmpty(storage.Enumerate(BundleKey.Parse(UserScope(Alice)), BundleLimits.Default));

        readCts.Dispose();
        writeCts.Dispose();
        deleteCts.Dispose();
    }

    [Fact]
    public async Task A_provider_that_ignores_the_entry_bound_is_stopped_by_the_store_and_the_scope_is_unavailable()
    {
        var limits = new BundleLimits { MaxEntries = 50 };
        var runaway = new RunawayListingProvider(UserScope(Alice), 100_000);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = runaway })
        {
            ProbeLimitsForTest = limits,
        };

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("x"));
        var diagnostic = Assert.Single(read.Diagnostics);
        Assert.Contains(nameof(BundleLimitExceededException), diagnostic.Message, StringComparison.Ordinal);
        Assert.True(runaway.Produced <= limits.MaxEntries + 1, $"the store consumed {runaway.Produced} names past a bound of {limits.MaxEntries}");

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        Assert.Contains(nameof(BundleLimitExceededException), deletion.Error, StringComparison.Ordinal);
        Assert.Equal(0, runaway.Deletes);
    }

    [Fact]
    public async Task A_scope_reads_while_another_scope_of_its_tier_cannot_be_listed()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var seeding = new FileMemoryStore(EveryTier(storage));
        Assert.True((await seeding.WriteAsync(Alice, Entry("2026-07-27", "alice refunds"), MemoryTier.User)).Written);
        Assert.True((await seeding.WriteAsync(Bob, Entry("2026-07-27", "bob refunds"), MemoryTier.User)).Written);

        // Bob's scope cannot be listed, and the tier's whole-store token cannot be computed.
        var provider = new YieldingProvider(storage)
        {
            FailEnumerateUnder = UserScope(Bob),
            FailVersion = true,
        };
        var store = new FileMemoryStore(EveryTier(provider));

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        Assert.Empty(read.Diagnostics);
        Assert.Contains("alice refunds", Assert.Single(read.Passages).Excerpt, StringComparison.Ordinal);
        Assert.Equal(0, provider.VersionRequests);
        Assert.DoesNotContain(provider.EnumeratedPrefixes, p => p is null);

        // Bob's own scope is the one reported unavailable.
        Assert.NotEmpty((await store.ReadAsync(Bob, new KnowledgeQuery("refunds"))).Diagnostics);
    }

    [Fact]
    public async Task A_name_too_long_for_a_key_once_prefixed_does_not_fail_the_scope_and_is_reported_by_a_deletion()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var seeding = new FileMemoryStore(EveryTier(storage));
        Assert.True((await seeding.WriteAsync(Alice, Entry("2026-07-27", "alice refunds"), MemoryTier.User)).Written);

        // Short enough to be a key of the scope's view, too long once the scope's prefix is added.
        var scope = UserScope(Alice);
        var relative = new string('a', BundleKey.MaxUtf8Bytes - 3 - 10) + ".md";
        Assert.True(BundleKey.TryParse(relative, out _, out _));
        var full = $"{scope}/{relative}";
        Assert.True(Encoding.UTF8.GetByteCount(full) > BundleKey.MaxUtf8Bytes);
        storage.Add(full, $"---\n{Frontmatter}---\n\nhidden\n");
        var store = new FileMemoryStore(EveryTier(storage));

        var read = await store.ReadAsync(Alice, new KnowledgeQuery("refunds"));
        Assert.Empty(read.Diagnostics);
        Assert.Single(read.Passages);

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        var report = Assert.Single(deletion.Tiers).Report;
        Assert.Equal([$"{scope}/2026-07-27.md"], report.Deleted.Select(k => k.ToString()).ToList());
        Assert.Contains(report.Failed, f => f.Reason.Contains("is not a key", StringComparison.Ordinal));
        Assert.NotNull(deletion.Error);
    }

    [Fact]
    public async Task The_view_token_changes_on_a_same_length_edit_of_its_scope_and_not_on_another_scope()
    {
        var storage = new InMemoryBundleProvider(canWrite: true)
            .Add("memory-user/a/x.md", "aaaa")
            .Add("memory-user/b/y.md", "bbbb");
        var view = new PrefixedBundleProvider(storage, BundleKey.Parse("memory-user/a"));
        var before = await view.GetVersionAsync(BundleLimits.Default, CancellationToken.None);

        storage.Add("memory-user/b/y.md", "cccc");
        Assert.Equal(before, await view.GetVersionAsync(BundleLimits.Default, CancellationToken.None));

        storage.Add("memory-user/a/x.md", "abcd");
        Assert.NotEqual(before, await view.GetVersionAsync(BundleLimits.Default, CancellationToken.None));
    }

    [Fact]
    public async Task A_host_provider_failure_inside_the_write_or_the_deletion_reaches_the_result_by_its_type_only()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        Assert.True((await new FileMemoryStore(EveryTier(storage)).WriteAsync(Alice, Entry("2026-07-27", "x"), MemoryTier.User)).Written);

        // The reads before the write succeed: the failure happens in the write the writer makes,
        // which the writer reports by message for an IOException, and in each key's deletion,
        // which the store reports by message for a BundleProviderException.
        var failing = new YieldingProvider(storage)
        {
            FailWrite = () => new IOException("secret host detail"),
            FailDelete = () => new HostDefinedProviderException("secret host detail"),
        };
        var store = new FileMemoryStore(new Dictionary<MemoryTier, IBundleProvider> { [MemoryTier.User] = failing });

        var write = await store.WriteAsync(Alice, Entry("2026-07-27", "more"), MemoryTier.User);
        Assert.False(write.Written);
        Assert.Contains(nameof(IOException), write.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", write.Error, StringComparison.Ordinal);

        var deletion = await store.DeleteScopeAsync(Alice, MemoryTier.User);
        Assert.Contains(nameof(HostDefinedProviderException), deletion.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", deletion.Error, StringComparison.Ordinal);
        Assert.All(Assert.Single(deletion.Tiers).Report.Failed, f => Assert.DoesNotContain("secret", f.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_view_token_cannot_be_spelled_by_a_version_that_holds_its_separators()
    {
        // Under plain NUL and newline separators, one entry whose opaque version spells a second
        // entry hashed exactly like the two entries themselves.
        var nul = (char)0;
        var one = new PrefixedBundleProvider(new FixedListingProvider(("s/a.md", 4, $"aa\nb.md{nul}4{nul}bb")), BundleKey.Parse("s"));
        var two = new PrefixedBundleProvider(new FixedListingProvider(("s/a.md", 4, "aa"), ("s/b.md", 4, "bb")), BundleKey.Parse("s"));

        Assert.NotEqual(
            await one.GetVersionAsync(BundleLimits.Default, CancellationToken.None),
            await two.GetVersionAsync(BundleLimits.Default, CancellationToken.None));
    }

    [Fact]
    public async Task The_view_counts_every_raw_entry_of_its_inner_listing_against_the_bound()
    {
        // The inner provider ignores both the bound and the prefix: everything it lists lies
        // outside the view, so nothing reaches a consumer that could count it.
        var inner = new RunawayListingProvider("elsewhere", 100_000);
        var view = new PrefixedBundleProvider(inner, BundleKey.Parse("scope"));
        var limits = new BundleLimits { MaxEntries = 10 };

        await Assert.ThrowsAsync<BundleLimitExceededException>(async () =>
        {
            await foreach (var _ in view.EnumerateAsync(null, limits, CancellationToken.None))
            {
            }
        });
        Assert.True(inner.Produced <= limits.MaxEntries + 1, $"the view consumed {inner.Produced} raw entries past a bound of {limits.MaxEntries}");
    }

    [Fact]
    public async Task A_name_too_long_once_prefixed_is_over_long_in_the_view_and_still_condemns_its_case_insensitive_siblings()
    {
        // The core's invariant (BundleKeyValidatorTests.A_sibling_implied_only_by_a_name_over_the_key_bound_still_collides),
        // through the view: Docs/<long>.md is a valid key of the view but not of the inner
        // provider once prefixed, so the load must count it as over-long, never read it, and
        // still see its Docs/ ancestor, which collides with docs/ on this storage.
        const string scope = "memory-user/s";
        var doc = $"---\n{Frontmatter}---\n\nbody\n";
        var relative = "Docs/" + new string('x', BundleKey.MaxUtf8Bytes - 20) + ".md";
        Assert.True(BundleKey.TryParse(relative, out _, out _));
        Assert.True(Encoding.UTF8.GetByteCount($"{scope}/{relative}") > BundleKey.MaxUtf8Bytes);
        var inner = new InMemoryBundleProvider(BundleKeyConstraints.CaseInsensitiveCollisions)
            .Add($"{scope}/docs/a.md", doc)
            .Add($"{scope}/{relative}", doc)
            .Add($"{scope}/ok.md", doc);

        var bundle = await Bundle.LoadAsync(new PrefixedBundleProvider(inner, BundleKey.Parse(scope)));

        Assert.Equal(["ok"], bundle.Concepts.Select(c => c.Id.ToString()).ToList());
        Assert.Contains(bundle.ParseProblems, p => p.RawName == relative && p.Error.Contains("longer", StringComparison.Ordinal));
        Assert.DoesNotContain(inner.Calls, call => call.StartsWith($"read {scope}/Docs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The inner provider, with every operation completing after a <see cref="Task.Yield"/> (so
    /// nothing completes synchronously), and optional failures: <see cref="Fail"/> thrown by every
    /// operation after its yield, <see cref="FailVersion"/> by the token, and
    /// <see cref="FailEnumerateUnder"/> by any listing that would cover that prefix.
    /// </summary>
    private sealed class YieldingProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        private int _yields;
        private int _versions;

        public Func<Exception>? Fail { get; init; }

        public Action? BeforeEach { get; init; }

        public bool FailVersion { get; init; }

        public string? FailEnumerateUnder { get; init; }

        public int Yields => Volatile.Read(ref _yields);

        public int VersionRequests => Volatile.Read(ref _versions);

        public List<string?> EnumeratedPrefixes { get; } = [];

        public string Id => "yielding";

        public string Location => "memory://yielding";

        public BundleProviderCapabilities Capabilities => new(inner.CanWrite, CompletesSynchronously: false);

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        private async Task StepAsync(CancellationToken ct)
        {
            BeforeEach?.Invoke();
            Interlocked.Increment(ref _yields);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (Fail is { } fail)
            {
                throw fail();
            }
        }

        public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
        {
            Interlocked.Increment(ref _versions);
            await StepAsync(ct);
            if (FailVersion)
            {
                throw new IOException("the whole store cannot be walked");
            }

            return await inner.GetVersionAsync(limits, ct);
        }

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (EnumeratedPrefixes)
            {
                EnumeratedPrefixes.Add(prefix?.ToString());
            }

            await StepAsync(ct);
            if (FailEnumerateUnder is { } blocked && (prefix is null || blocked == prefix.ToString() || blocked.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                throw new UnauthorizedAccessException("a directory denies listing");
            }

            await foreach (var listing in inner.EnumerateAsync(prefix, limits, ct))
            {
                yield return listing;
            }
        }

        public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
        {
            await StepAsync(ct);
            return await inner.ReadAsync(key, expected, ct);
        }

        public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
        {
            await StepAsync(ct);
            return await inner.ExistsAsync(key, ct);
        }

        public async ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
        {
            await StepAsync(ct);
            if (FailWrite is { } fail)
            {
                throw fail();
            }

            return await inner.WriteAsync(key, content, condition, ct);
        }

        public async ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
        {
            await StepAsync(ct);
            if (FailDelete is { } fail)
            {
                throw fail();
            }

            await inner.DeleteAsync(key, condition, ct);
        }

        /// <summary>Thrown by every write, after its yield (the reads before it succeed).</summary>
        public Func<Exception>? FailWrite { get; init; }

        /// <summary>Thrown by every deletion, after its yield (the listing before it succeeds).</summary>
        public Func<Exception>? FailDelete { get; init; }
    }

    /// <summary>A provider exception a host defines itself, whose message the library did not write.</summary>
    private sealed class HostDefinedProviderException(string message) : BundleProviderException(message);

    /// <summary>A provider that lists fixed entries, each with the version its listing gives for free.</summary>
    private sealed class FixedListingProvider(params (string Name, long Length, string Version)[] entries) : IBundleProvider
    {
        public string Id => "fixed";

        public string Location => "memory://fixed";

        public BundleProviderCapabilities Capabilities => new(CanWrite: false, CompletesSynchronously: true);

        public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.None;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => ValueTask.FromResult(new BundleVersion("whole"));

        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) =>
            entries.Select(e => new BundleListing(e.Name, e.Length, new BundleObjectVersion(e.Version))).ToAsyncEnumerable();

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) =>
            ValueTask.FromException<BundleObject>(new BundleObjectNotFoundException(key));

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => ValueTask.FromResult(false);

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
            ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
            ValueTask.FromException(new BundleReadOnlyException(Id));
    }

    /// <summary>A provider whose listing under <c>prefix</c> yields <c>count</c> names, whatever bound it is given.</summary>
    private sealed class RunawayListingProvider(string prefix, int count) : IBundleProvider
    {
        private int _produced;
        private int _deletes;

        public int Produced => Volatile.Read(ref _produced);

        public int Deletes => Volatile.Read(ref _deletes);

        public string Id => "runaway";

        public string Location => "memory://runaway";

        public BundleProviderCapabilities Capabilities => new(CanWrite: true, CompletesSynchronously: false);

        public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.None;

        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => ValueTask.FromResult(new BundleVersion("v"));

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? under, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            for (var i = 0; i < count; i++)
            {
                Interlocked.Increment(ref _produced);
                yield return new BundleListing($"{prefix}/n{i}.md", 1, null);
            }
        }

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) =>
            ValueTask.FromResult(new BundleObject(new MemoryStream("x"u8.ToArray()), new BundleObjectVersion("o"), 1));

        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => ValueTask.FromResult(false);

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
            ValueTask.FromResult(new BundleObjectVersion("w"));

        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
        {
            Interlocked.Increment(ref _deletes);
            return ValueTask.CompletedTask;
        }
    }
}

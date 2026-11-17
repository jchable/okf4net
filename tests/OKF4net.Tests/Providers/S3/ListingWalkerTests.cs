// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using Amazon.Runtime;
using OKF4net.Providers;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec §4.3, D9: the walker's invariants, projections and bounds — through the real SDK's ListObjectsV2, the real
/// guarded client and the provider's own parse, against the scripted fake.
/// </summary>
public class ListingWalkerTests
{
    private const string Note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nB\n";

    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static async Task<List<string>> Names(S3BundleProvider p, BundleKey? prefix = null, BundleLimits? limits = null, CancellationToken ct = default)
    {
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(prefix, limits ?? BundleLimits.Default, ct))
        {
            names.Add(l.RawName);
        }

        return names;
    }

    /// <summary>The query parameters of a request, by name (each sent once: the guard refuses a repeat).</summary>
    private static Dictionary<string, string> Query(HttpRequestMessage r)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            Assert.True(S3KeyCodec.TryDecode(eq < 0 ? "" : pair[(eq + 1)..], out var value));
            result.Add(eq < 0 ? pair : pair[..eq], value);
        }

        return result;
    }

    [Fact]
    public async Task The_prefix_is_removed_and_kb2_is_never_requested()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", Note), ("kb2/x.md", Note), ("other.md", Note)));
        using var p = S3Test.Provider(fake);
        Assert.Equal(["a.md"], await Names(p));
        Assert.All(fake.Requests, r => Assert.Contains("prefix=kb%2F", r.RequestUri!.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_page_sends_exactly_the_admitted_parameters()
    {
        var objects = Enumerable.Range(0, 20).ToDictionary(i => $"kb/n{i:D2}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects, pageSize: 7);
        using var p = S3Test.Provider(fake);
        Assert.Equal(20, (await Names(p)).Count);
        Assert.Equal(3, fake.Requests.Count);
        string[] tokens = [null!, "7", "14"];
        for (var i = 0; i < 3; i++)
        {
            var q = Query(fake.Requests[i]);
            Assert.Equal(i == 0 ? ["encoding-type", "list-type", "max-keys", "prefix"] : ["continuation-token", "encoding-type", "list-type", "max-keys", "prefix"], q.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("2", q["list-type"]);
            Assert.Equal("url", q["encoding-type"]);
            Assert.Equal("1000", q["max-keys"]);
            Assert.Equal("kb/", q["prefix"]);
            Assert.Equal(tokens[i], q.GetValueOrDefault("continuation-token"));
            Assert.Equal(HttpMethod.Get, fake.Requests[i].Method);
            Assert.Equal("/okf/", fake.Requests[i].RequestUri!.AbsolutePath);
        }
    }

    [Fact]
    public async Task Many_small_valid_pages_are_accepted()
    {
        // 43 pages: one operation context per page, or the guard's 3-send cap would stop the walk at the fourth.
        var objects = Enumerable.Range(0, 300).ToDictionary(i => $"kb/n{i:D4}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects, pageSize: 7);
        using var p = S3Test.Provider(fake);
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default with { MaxEntries = 1000 }, default))
        {
            names.Add(l.RawName);
        }

        Assert.Equal(300, names.Count);
        Assert.Equal(43, fake.Requests.Count);
        Assert.Equal(objects.Keys.Select(k => k["kb/".Length..]).Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task Pages_of_one_key_are_accepted_up_to_the_entry_bound()
    {
        var objects = Enumerable.Range(0, 30).ToDictionary(i => $"kb/n{i:D2}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        using var p = S3Test.Provider(S3Test.Serving(objects, pageSize: 1));
        Assert.Equal(30, (await Names(p, limits: BundleLimits.Default with { MaxEntries = 30 })).Count);
    }

    [Fact]
    public async Task More_than_a_thousand_objects_span_pages_and_the_entry_bound_stops_and_disposes()
    {
        var objects = Enumerable.Range(0, 2500).ToDictionary(i => $"kb/n{i:D4}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects);
        using var p = S3Test.Provider(fake);
        Assert.Equal(2500, (await Names(p)).Count);
        Assert.Equal(3, fake.Requests.Count);
        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p, new BundleLoadOptions { Limits = BundleLimits.Default with { MaxEntries = 1500 } }));
        Assert.IsType<BundleLimitExceededException>(e.InnerException);
        Assert.Equal(5, fake.Requests.Count); // the version walk stopped inside its second page; nothing else was fetched
    }

    [Fact]
    public async Task The_core_entry_bound_stops_the_listing_and_fetches_no_further_page()
    {
        var objects = Enumerable.Range(0, 2500).ToDictionary(i => $"kb/n{i:D4}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects);
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<BundleLimitExceededException>(async () => await Bundle.ListNamesAsync(p, BundleLimits.Default with { MaxEntries = 1500 }, default));
        Assert.Equal(BundleBound.Entries, e.Bound);
        Assert.Equal(2, fake.Requests.Count);
    }

    public static TheoryData<string> Inconsistencies => new()
    {
        "outside-prefix", "not-increasing", "duplicate", "repeated-token", "truncated-empty", "oversized-page",
        "not-increasing-across-pages", "duplicate-across-pages", "utf16-order", "token-too-long", "token-empty",
        "undecodable-key", "encoding-type-missing", "kb2-sibling",
    };

    /// <summary>The page a scripted store answers; <paramref name="first"/>: the request carries no continuation token (each walk starts over).</summary>
    private static string InconsistentPage(string scenario, int call, bool first) => scenario switch
    {
        "outside-prefix" => ListPageParserTests.Page([("other/a.md", "\"e\"", 1)], false, null),
        "kb2-sibling" => ListPageParserTests.Page([("kb2/a.md", "\"e\"", 1)], false, null),
        "not-increasing" => ListPageParserTests.Page([("kb/b.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
        "duplicate" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
        "repeated-token" => ListPageParserTests.Page([("kb/a" + call + ".md", "\"e\"", 1)], true, "same"),
        "truncated-empty" => ListPageParserTests.Page([], true, "t" + call),
        "oversized-page" => ListPageParserTests.Page(Enumerable.Range(0, 1001).Select(i => ($"kb/n{i:D4}.md", "\"e\"", 1L)), false, null),
        "not-increasing-across-pages" => first ? ListPageParserTests.Page([("kb/b.md", "\"e\"", 1)], true, "t1") : ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], false, null),
        "duplicate-across-pages" => first ? ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], true, "t1") : ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], false, null),
        "utf16-order" => ListPageParserTests.Page([(S3Test.Encode("kb/" + U(0x10000) + ".md"), "\"e\"", 1), (S3Test.Encode("kb/" + U(0xE000) + ".md"), "\"e\"", 1)], false, null),
        "token-too-long" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], true, new string('t', 1025)),
        "token-empty" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], true, ""),
        "undecodable-key" => ListPageParserTests.Page([("kb/%FF.md", "\"e\"", 1)], false, null),
        "encoding-type-missing" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], false, null, encodingType: null),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    [Theory]
    [MemberData(nameof(Inconsistencies))]
    public async Task A_transport_inconsistency_fails_the_listing_and_the_load(string scenario)
    {
        var calls = 0;
        var fake = new FakeS3Handler { Respond = (req, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, InconsistentPage(scenario, Interlocked.Increment(ref calls), !req.RequestUri!.Query.Contains("continuation-token=", StringComparison.Ordinal)))) };
        using var p = S3Test.Provider(fake);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a regression that walks on fails, never hangs
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p, ct: deadline.Token));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.Equal(S3Operation.List, e.Operation);
        Assert.Equal("S3 list failed: InvalidResponse", e.Message);
        Assert.True(calls <= 2, "an inconsistency is detected on the page that shows it");
        Assert.Null(OperationContext.Current.Value);
        await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p, null, deadline.Token));
        await Assert.ThrowsAsync<S3RequestException>(async () => await p.GetVersionAsync(BundleLimits.Default, deadline.Token));
    }

    public static TheoryData<string, string> HostileXml => new()
    {
        { "not xml", "<not-xml" },
        { "a DTD", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"x\">]><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><Contents><Key>kb/&e;.md</Key><ETag>e</ETag><Size>1</Size></Contents></ListBucketResult>" },
        { "a foreign root namespace", ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], false, null).Replace(ListPageParserTests.S3Namespace, "urn:evil", StringComparison.Ordinal) },
        { "another root", "<?xml version=\"1.0\"?><Other><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated></Other>" },
        { "an empty body", "" },
        { "an undeclared entity", ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], false, null).Replace("<Key>kb/a.md</Key>", "<Key>kb/&undeclared;</Key>", StringComparison.Ordinal) },
        { "billion laughs", ListPageParserTests.BillionLaughs },
    };

    [Theory]
    [MemberData(nameof(HostileXml))]
    public async Task A_hostile_document_through_the_real_SDK_is_InvalidResponse(string name, string xml)
    {
        _ = name;
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, xml)) };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task The_page_budget_stops_a_store_that_never_ends()
    {
        // Every page is truncated and makes progress (one new, increasing key, a fresh token): only MaxEntries + 1 pages are fetched.
        var calls = 0;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, ListPageParserTests.Page([($"kb/n{n:D6}.md", "\"e\"", 1)], true, "t" + n)));
            },
        };
        using var p = S3Test.Provider(fake);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a regression that walks on fails, never hangs
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p, limits: BundleLimits.Default with { MaxEntries = 5 }, ct: deadline.Token));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Code_point_order_is_accepted_and_the_token_hashes_the_listing_order()
    {
        var objects = S3Test.Objects(("kb/" + U(0x10000) + ".md", "b"), ("kb/" + U(0xE000) + ".md", "a"), ("kb/z.md", "z"));
        using var p = S3Test.Provider(S3Test.Serving(objects));
        Assert.Equal(["z.md", U(0xE000) + ".md", U(0x10000) + ".md"], await Names(p));
        var expected = BundleTokenHasher.Hash(new[] { "kb/z.md", "kb/" + U(0xE000) + ".md", "kb/" + U(0x10000) + ".md" }.Select(k => (k[3..], S3Test.ETag(objects[k]), (long)objects[k].Length)));
        Assert.Equal(expected, (await p.GetVersionAsync(BundleLimits.Default, default)).Token);
    }

    [Fact]
    public async Task The_token_does_not_depend_on_the_page_split()
    {
        var objects = Enumerable.Range(0, 300).ToDictionary(i => $"kb/n{i:D4}.md", i => System.Text.Encoding.UTF8.GetBytes("x" + i), StringComparer.Ordinal);
        using var small = S3Test.Provider(S3Test.Serving(objects, pageSize: 7));
        using var large = S3Test.Provider(S3Test.Serving(objects, pageSize: 1000));
        var a = await small.GetVersionAsync(BundleLimits.Default, default);
        var b = await large.GetVersionAsync(BundleLimits.Default, default);
        Assert.Equal(a, b);
        Assert.Equal(BundleTokenHasher.Hash(objects.Keys.Order(StringComparer.Ordinal).Select(k => (k[3..], S3Test.ETag(objects[k]), (long)objects[k].Length))), a.Token);
    }

    [Fact]
    public async Task The_token_changes_with_a_name_an_etag_or_a_size()
    {
        async Task<string> Token(params (string, string)[] items)
        {
            using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(items)));
            return (await p.GetVersionAsync(BundleLimits.Default, default)).Token;
        }

        var baseline = await Token(("kb/a.md", "x"), ("kb/b.md", "y"));
        Assert.Equal(baseline, await Token(("kb/a.md", "x"), ("kb/b.md", "y"), ("kb2/c.md", "outside")));
        Assert.NotEqual(baseline, await Token(("kb/a.md", "x"), ("kb/c.md", "y")));
        Assert.NotEqual(baseline, await Token(("kb/a.md", "x"), ("kb/b.md", "z")));
        Assert.NotEqual(baseline, await Token(("kb/a.md", "x"), ("kb/b.md", "yy")));
        Assert.NotEqual(baseline, await Token(("kb/a.md", "x")));
    }

    [Fact]
    public async Task The_empty_bundle_has_the_empty_listing_token()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects()));
        Assert.Equal(BundleTokenHasher.Hash([]), (await p.GetVersionAsync(BundleLimits.Default, default)).Token);
        Assert.Empty(await Names(p));
    }

    [Fact]
    public async Task The_prefix_itself_and_folder_markers_are_passed_raw_for_the_core_to_judge()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/", ""), ("kb/dir/", ""), ("kb/x.txt", "x"))));
        Assert.Equal(["", "dir/", "x.txt"], await Names(p));
        // A console "folder" (the key "kb/" itself, "kb/dir/") is no transport inconsistency: the core rejects the
        // names, and diagnoses only .md names (Bundle.cs, spec §2 enumeration scope), so the load succeeds.
        var bundle = await Bundle.LoadAsync(p);
        Assert.Empty(bundle.Concepts);
        Assert.Empty(bundle.ParseProblems);
    }

    [Fact]
    public async Task A_key_prefix_lists_under_it_at_a_segment_boundary()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/d/x.md", "x"), ("kb/dx.md", "x"), ("kb/e.md", "x")));
        using var p = S3Test.Provider(fake);
        Assert.Equal(["d/x.md"], await Names(p, BundleKey.Parse("d")));
        Assert.Equal("kb/d/", Query(Assert.Single(fake.Requests))["prefix"]);
    }

    [Fact]
    public async Task An_empty_bundle_prefix_lists_the_whole_bucket()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("a.md", "x"), ("kb/b.md", "x"))), S3Test.Local(prefix: ""));
        Assert.Equal(["a.md", "kb/b.md"], await Names(p));
    }

    [Fact]
    public async Task Stopping_the_enumeration_fetches_no_further_page_and_returns_the_permit()
    {
        var objects = Enumerable.Range(0, 30).ToDictionary(i => $"kb/n{i:D2}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects, pageSize: 10);
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 1 });
        await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            break;
        }

        Assert.Single(fake.Requests);
        Assert.True(await p.ExistsAsync(BundleKey.Parse("n00.md"), default)); // the one permit was returned: no Timeout
    }

    [Fact]
    public async Task No_permit_is_held_while_the_consumer_works_between_entries()
    {
        // The core reads objects while it lists: a permit held across a yield would deadlock MaxConcurrentRequests = 1.
        var objects = Enumerable.Range(0, 5).ToDictionary(i => $"kb/n{i}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        using var p = S3Test.Provider(S3Test.Serving(objects, pageSize: 2), S3Test.Local() with { MaxConcurrentRequests = 1 });
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            Assert.True(await p.ExistsAsync(BundleKey.Parse(l.RawName), default));
        }
    }

    [Fact]
    public async Task A_caller_cancellation_stays_a_cancellation()
    {
        var objects = Enumerable.Range(0, 30).ToDictionary(i => $"kb/n{i:D2}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects, pageSize: 10);
        using var p = S3Test.Provider(fake);
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        });
        Assert.Equal(1, seen); // honoured between entries of one page, not only between pages
        Assert.Single(fake.Requests);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await p.GetVersionAsync(BundleLimits.Default, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), cts.Token));
        Assert.Single(fake.Requests);
    }

    /// <summary>Records the operation context its resolution runs under.</summary>
    private sealed class ContextProbeCredentials : AWSCredentials
    {
        public readonly List<OperationContext?> Seen = [];

        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("the provider resolves asynchronously, isolated");

        public override Task<ImmutableCredentials> GetCredentialsAsync()
        {
            lock (Seen)
            {
                Seen.Add(OperationContext.Current.Value);
            }

            return Task.FromResult(new ImmutableCredentials("AKIDEXAMPLE", "secret", null));
        }
    }

    [Fact]
    public async Task Credentials_are_resolved_once_per_page_outside_any_operation_context()
    {
        var objects = Enumerable.Range(0, 20).ToDictionary(i => $"kb/n{i:D2}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var creds = new ContextProbeCredentials();
        using var p = S3Test.Provider(S3Test.Serving(objects, pageSize: 7), creds: creds);
        Assert.Equal(20, (await Names(p)).Count);
        Assert.Equal(3, creds.Seen.Count);
        Assert.All(creds.Seen, Assert.Null); // the host's code never runs inside the guard's context
        Assert.Null(OperationContext.Current.Value);
    }

    [Fact]
    public async Task A_badly_named_but_well_transported_key_is_listed_raw()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/bad" + U(1) + ".md", Note), ("kb/a//b.md", Note), ("kb/ok.md", Note))));
        Assert.Equal(["a//b.md", "bad" + U(1) + ".md", "ok.md"], await Names(p));
    }

    [Fact(Skip = "Task 8: ReadAsync")]
    public async Task A_badly_named_but_well_transported_key_is_a_local_problem()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/bad" + U(1) + ".md", Note), ("kb/a//b.md", Note), ("kb/ok.md", Note))));
        var bundle = await Bundle.LoadAsync(p);
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "ok");
        Assert.Contains(bundle.ParseProblems, x => x.RawName == "bad" + U(1) + ".md");
    }

    [Fact]
    public async Task Exists_matches_the_key_exactly()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md.bak", "x"), ("kb/b.md", "x"), ("kb/b.md.bak", "x")));
        using var p = S3Test.Provider(fake);
        Assert.False(await p.ExistsAsync(BundleKey.Parse("a.md"), default));
        Assert.True(await p.ExistsAsync(BundleKey.Parse("b.md"), default)); // a truncated first page: one key, one request
        Assert.False(await p.ExistsAsync(BundleKey.Parse("c.md"), default));
        Assert.Equal(3, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal("1", Query(r)["max-keys"]));
        Assert.Equal("kb/b.md", Query(fake.Requests[1])["prefix"]);
    }

    [Fact]
    public async Task Exists_on_a_refusal_never_answers_false()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.Forbidden, FakeS3Handler.Error("AccessDenied"))) };
        using var p = S3Test.Provider(fake);
        await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), default));
    }

    [Fact]
    public async Task Exists_refuses_an_inconsistent_page()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, ListPageParserTests.Page([("kb/a.md", "\"e\"", 1), ("kb/b.md", "\"e\"", 1)], false, null))) };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), default));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind); // two keys for MaxKeys = 1
        Assert.Equal(S3Operation.Exists, e.Operation);
        Assert.Equal("S3 exists failed: InvalidResponse for 'a.md'", e.Message); // the key, never the bucket or the S3 key
    }

    [Fact]
    public async Task GetVersion_bounds_its_own_walk()
    {
        var objects = Enumerable.Range(0, 20).ToDictionary(i => $"kb/d/{i}/x.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        using var p = S3Test.Provider(S3Test.Serving(objects));
        Assert.Equal(BundleBound.Entries, (await Assert.ThrowsAsync<BundleLimitExceededException>(async () => await p.GetVersionAsync(BundleLimits.Default with { MaxEntries = 10 }, default))).Bound);
        Assert.Equal(BundleBound.Depth, (await Assert.ThrowsAsync<BundleLimitExceededException>(async () => await p.GetVersionAsync(BundleLimits.Default with { MaxDepth = 2 }, default))).Bound);
        await p.GetVersionAsync(BundleLimits.Default with { MaxEntries = 20, MaxDepth = 3 }, default);
    }

    [Fact]
    public async Task A_disposed_provider_fails_every_listing_member_as_Disposed()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", "x")));
        var p = S3Test.Provider(fake);
        p.Dispose();
        p.Dispose(); // once
        Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p))).Kind);
        Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(async () => await p.GetVersionAsync(BundleLimits.Default, default))).Kind);
        Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), default))).Kind);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task The_provider_is_read_only_and_says_so()
    {
        using var p = S3Test.Provider(new FakeS3Handler());
        Assert.False(p.Capabilities.CanWrite);
        Assert.False(p.Capabilities.CompletesSynchronously);
        Assert.Equal(BundleKeyConstraints.WellFormedUnicode, p.NativeConstraints);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.WriteAsync(BundleKey.Parse("a.md"), "x"u8.ToArray(), BundleWriteCondition.None, default));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.DeleteAsync(BundleKey.Parse("a.md"), BundleWriteCondition.None, default));
    }
}

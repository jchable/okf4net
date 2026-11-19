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
        "outside-prefix", "not-increasing", "duplicate", "repeated-token", "repeated-token-on-empty-pages", "oversized-page",
        "not-increasing-across-pages", "duplicate-across-pages", "utf16-order", "token-too-long", "token-empty",
        "truncated-without-token", "truncated-empty-without-token", "undecodable-key", "encoding-type-missing", "kb2-sibling",
    };

    /// <summary>The page a scripted store answers; <paramref name="first"/>: the request carries no continuation token (each walk starts over).</summary>
    private static string InconsistentPage(string scenario, int call, bool first) => scenario switch
    {
        "outside-prefix" => ListPageParserTests.Page([("other/a.md", "\"e\"", 1)], false, null),
        "kb2-sibling" => ListPageParserTests.Page([("kb2/a.md", "\"e\"", 1)], false, null),
        "not-increasing" => ListPageParserTests.Page([("kb/b.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
        "duplicate" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
        "repeated-token" => ListPageParserTests.Page([("kb/a" + call + ".md", "\"e\"", 1)], true, "same"),
        "repeated-token-on-empty-pages" => ListPageParserTests.Page([], true, "same"), // an empty truncated page is accepted (spec C10), a repeated token never
        "truncated-without-token" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1)], true, null),
        "truncated-empty-without-token" => ListPageParserTests.Page([], true, null),
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

    /// <summary>
    /// A store that answers, for every walk, <paramref name="empty"/> truncated pages without a key (tokens e1, e2, …)
    /// before one final page listing <paramref name="finalKeys"/> (the keys under the requested prefix); reads serve a note.
    /// </summary>
    private static FakeS3Handler EmptyPagesThen(int empty, params string[] finalKeys) => new()
    {
        Respond = (req, _) =>
        {
            var uri = req.RequestUri!;
            if (!uri.Query.Contains("list-type=2", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Note) });
            }

            S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "prefix", out var prefix);
            S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "continuation-token", out var token);
            var n = token is null ? 0 : int.Parse(token[1..], System.Globalization.CultureInfo.InvariantCulture);
            var page = n < empty
                ? ListPageParserTests.Page([], true, "e" + (n + 1))
                : ListPageParserTests.Page(finalKeys.Where(k => k.StartsWith(prefix ?? "", StringComparison.Ordinal)).Select(k => (k, S3Test.ETag(System.Text.Encoding.UTF8.GetBytes(Note)), (long)System.Text.Encoding.UTF8.GetByteCount(Note))), false, null);
            return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, page));
        },
    };

    [Fact]
    public async Task Empty_truncated_pages_are_followed_and_a_later_page_with_keys_loads()
    {
        // Spec Annexe C10 (maintainer's decision 2026-11-19): a truncated page without a key — Amazon S3 may send one
        // on a versioned bucket, under a long run of delete markers — is accepted and counted against the page budget.
        var fake = EmptyPagesThen(40, "kb/a.md", "kb/b.md");
        using var p = S3Test.Provider(fake);
        Assert.Equal(["a.md", "b.md"], await Names(p));
        Assert.Equal(41, fake.Requests.Count); // one operation per page: the guard's 3-send cap never spans pages
        var bundle = await Bundle.LoadAsync(p);
        Assert.Equal(["a", "b"], bundle.Concepts.Select(c => c.Id.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(BundleTokenHasher.Hash(new[] { "a.md", "b.md" }.Select(k => (k, S3Test.ETag(System.Text.Encoding.UTF8.GetBytes(Note)), (long)System.Text.Encoding.UTF8.GetByteCount(Note)))), (await p.GetVersionAsync(BundleLimits.Default, default)).Token);
    }

    [Fact]
    public async Task Empty_truncated_pages_count_against_the_page_budget()
    {
        // An endless run of empty truncated pages, each with a fresh token: exactly MaxEntries + 1 pages are fetched, then
        // InvalidResponse. Past 10 000 calls the store ends the listing, so a walk without the budget fails the
        // assertion instead of hanging.
        var calls = 0;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, n > 10_000 ? ListPageParserTests.Page([], false, null) : ListPageParserTests.Page([], true, "t" + n)));
            },
        };
        using var p = S3Test.Provider(fake);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // a regression that walks on fails, never hangs
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p, limits: BundleLimits.Default with { MaxEntries = 5 }, ct: deadline.Token));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.Equal("S3 list failed: InvalidResponse", e.Message);
        Assert.Equal(6, calls);
        Assert.Null(OperationContext.Current.Value);
        Volatile.Write(ref calls, 0);
        Assert.Equal(S3FailureKind.InvalidResponse, (await Assert.ThrowsAsync<S3RequestException>(async () => await p.GetVersionAsync(BundleLimits.Default with { MaxEntries = 5 }, deadline.Token))).Kind);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Exists_follows_empty_truncated_pages()
    {
        var fake = EmptyPagesThen(30, "kb/a.md", "kb/b.md.bak");
        using var p = S3Test.Provider(fake);
        Assert.True(await p.ExistsAsync(BundleKey.Parse("a.md"), default));
        Assert.Equal(31, fake.Requests.Count);
        Assert.False(await p.ExistsAsync(BundleKey.Parse("b.md"), default)); // the first key listed is another one
        Assert.False(await p.ExistsAsync(BundleKey.Parse("c.md"), default)); // nothing listed
        Assert.Equal(93, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal("1", Query(r)["max-keys"]));
    }

    [Fact]
    public async Task Exists_stops_an_endless_run_of_empty_pages_at_its_own_budget()
    {
        var calls = 0;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, n > 10_000 ? ListPageParserTests.Page([], false, null) : ListPageParserTests.Page([], true, "t" + n)));
            },
        };
        using var p = S3Test.Provider(fake);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), deadline.Token));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind); // never false: the store has not said the key is absent
        Assert.Equal(S3Operation.Exists, e.Operation);
        Assert.Equal(S3BundleProvider.ExistsMaxPages, calls);
    }

    [Fact]
    public async Task Exists_refuses_a_repeated_token_among_empty_pages()
    {
        var calls = 0;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, ListPageParserTests.Page([], true, n == 3 ? "t1" : "t" + n)));
            },
        };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), default));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.Equal(3, calls);
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

    [Theory]
    [InlineData(true)]  // Amazon S3, MinIO: a space listed as a raw '+', a '+' as %2B
    [InlineData(false)] // RustFS: a space listed as %20
    public async Task A_space_and_a_plus_come_back_under_their_own_names_whatever_the_store_encoding(bool awsStyle)
    {
        var objects = S3Test.Objects(("kb/a b.md", "space"), ("kb/a+b.md", "plus"), ("kb/a  b.md", "two spaces"), ("kb/c.md", "c"));
        var fake = S3Test.Serving(objects, awsStyle: awsStyle);
        using var p = S3Test.Provider(fake);
        Assert.Equal(["a  b.md", "a b.md", "a+b.md", "c.md"], await Names(p));
        Assert.Equal(awsStyle ? "kb/a+b.md" : "kb/a%20b.md", S3Test.Encode("kb/a b.md", awsStyle)); // what the fake really lists
        Assert.Equal("kb/a%2Bb.md", S3Test.Encode("kb/a+b.md", awsStyle));

        var expected = BundleTokenHasher.Hash(new[] { "kb/a  b.md", "kb/a b.md", "kb/a+b.md", "kb/c.md" }.Select(k => (k[3..], S3Test.ETag(objects[k]), (long)objects[k].Length)));
        Assert.Equal(expected, (await p.GetVersionAsync(BundleLimits.Default, default)).Token);
        Assert.True(await p.ExistsAsync(BundleKey.Parse("a b.md"), default));
        Assert.True(await p.ExistsAsync(BundleKey.Parse("a+b.md"), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bundle_prefix_with_a_space_lists_under_it(bool awsStyle)
    {
        var fake = S3Test.Serving(S3Test.Objects(("my kb/x.md", "x"), ("my kb/y z.md", "y"), ("my kbx/no.md", "n"), ("my+kb/no.md", "n")), awsStyle: awsStyle);
        using var p = S3Test.Provider(fake, S3Test.Local(prefix: "my kb"));
        Assert.Equal(["x.md", "y z.md"], await Names(p));
        Assert.Equal("my kb/", Query(Assert.Single(fake.Requests))["prefix"]); // sent as %20 by the SDK: a raw '+' is refused by the guard
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

    [Fact]
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
    public async Task A_null_key_is_refused_by_ExistsAsync_as_by_ReadAsync_before_any_request()
    {
        // Minors A1: a null key used to ask, silently, about the bundle prefix's own folder marker (KeyPrefix + null).
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", "x")));
        using var p = S3Test.Provider(fake);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await p.ExistsAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await p.ReadAsync(null!, null, default)); // the sibling this one follows
        Assert.Empty(fake.Requests);
    }

    private static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(int.MaxValue);

    public static TheoryData<TimeSpan, TimeSpan?, TimeSpan> Timeouts => new()
    {
        { TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(5) },     // an infinite limit never wins
        { TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(5) },                // nor a zero one
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-2), TimeSpan.FromSeconds(5) },     // nor a negative one
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) },      // the shorter positive one wins
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(5) },
        { TimeSpan.FromSeconds(5), TimeSpan.MaxValue, TimeSpan.FromSeconds(5) },
        { TimeSpan.FromSeconds(5), null, TimeSpan.FromSeconds(5) },                         // ExistsAsync: the source's alone
        { TimeSpan.FromDays(100), Timeout.InfiniteTimeSpan, Cap },                         // capped where a deadline can be set
        { TimeSpan.FromDays(100), TimeSpan.MaxValue, Cap },
        { TimeSpan.FromDays(100), null, Cap },
        { TimeSpan.FromDays(100), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) },
        { Cap, null, Cap },
    };

    [Theory]
    [MemberData(nameof(Timeouts))]
    public void The_operation_deadline_is_the_shorter_positive_timeout_capped(TimeSpan source, TimeSpan? limit, TimeSpan expected) =>
        Assert.Equal(expected, S3BundleProvider.OperationTimeout(source, limit));

    /// <summary>A store that answers nothing until the request is cancelled; <c>Arrived</c> completes at the first request.</summary>
    private static (FakeS3Handler Fake, TaskCompletionSource Arrived) Stalling()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeS3Handler
        {
            Respond = async (_, ct) =>
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        return (fake, arrived);
    }

    public static TheoryData<string, TimeSpan, TimeSpan, TimeSpan> Deadlines => new()
    {
        { "list", TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(5) },
        { "list", TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(5) },
        { "list", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-2), TimeSpan.FromSeconds(5) },
        { "version", TimeSpan.FromDays(100), TimeSpan.MaxValue, Cap },
        { "version", TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(5) },
        { "exists", TimeSpan.FromDays(100), BundleLimits.Default.RequestTimeout, Cap },
    };

    [Theory]
    [MemberData(nameof(Deadlines))]
    public async Task The_deadline_observed_is_the_effective_timeout(string member, TimeSpan sourceTimeout, TimeSpan limitTimeout, TimeSpan effective)
    {
        var clock = new ManualClock();
        var (fake, arrived) = Stalling();
        using var p = new S3BundleProvider(S3Test.Creds, S3Test.Local() with { RequestTimeout = sourceTimeout }, fake, clock);
        var limits = BundleLimits.Default with { RequestTimeout = limitTimeout };
        Task call = member switch
        {
            "list" => Names(p, limits: limits),
            "version" => p.GetVersionAsync(limits, default).AsTask(),
            _ => p.ExistsAsync(BundleKey.Parse("a.md"), default).AsTask(),
        };
        await Task.WhenAny(arrived.Task, call).WaitAsync(TimeSpan.FromSeconds(60)); // the request went out: no deadline already past, no invalid one
        Assert.True(arrived.Task.IsCompleted, call.Exception?.InnerException?.GetType().Name ?? "the call ended before sending");
        clock.Advance(effective);
        var e = await Assert.ThrowsAsync<S3RequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(S3FailureKind.Timeout, e.Kind);
    }

    [Fact]
    public async Task A_parse_that_fails_unexpectedly_is_the_closed_InvalidResponse()
    {
        // Seam: a parser that throws a type nothing expects. Its text never reaches the message, and nothing escapes Fail.
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", "x")));
        var source = SourceRules.Normalize(S3Test.Local());
        using var client = ClientBuilder.Build(new SnapshotCredentials(), source, fake, out var http);
        using (http)
        {
            var walker = new ListingWalker(client, source.Bucket, new OperationGate(4, TimeProvider.System), new CredentialResolver(S3Test.Creds), (_, _) => throw new InvalidCastException("PARSER-CANARY"));
            var e = await Assert.ThrowsAsync<S3RequestException>(async () =>
            {
                await foreach (var _ in walker.WalkAsync("kb/", 1000, 10, TimeSpan.FromSeconds(5), S3Operation.List, null, default))
                {
                }
            });
            Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
            Assert.DoesNotContain("PARSER-CANARY", e.Message, StringComparison.Ordinal);
            Assert.Single(fake.Requests);
        }
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

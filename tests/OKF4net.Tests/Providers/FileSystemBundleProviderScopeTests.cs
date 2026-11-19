// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using System.Globalization;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The listing scope of <see cref="FileSystemBundleProvider"/> (issue #205): inside one load or
/// one validation each directory is listed once; nothing learned there is used after it.
/// The exact-name path — the one that lists directories per key — runs on every OS, Linux
/// included, so the default provider is the one under test.
/// </summary>
public class FileSystemBundleProviderScopeTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static FileSystemBundleProvider Exact(string root, BundleLimits? limits = null) =>
        new(root, limits: limits);

    private static string Concept(string title, string? resource = null) =>
        "---\ntype: guide\ntitle: " + title + "\ndescription: d\n"
        + (resource is null ? string.Empty : "resource: " + resource + "\n")
        + "---\n\nbody\n";

    private static string Name(int i) => "c" + i.ToString("D3", CultureInfo.InvariantCulture) + ".md";

    private static TempDir Flat(int count)
    {
        var tmp = new TempDir();
        for (var i = 0; i < count; i++)
        {
            tmp.Write(Name(i), Concept("C" + i.ToString(CultureInfo.InvariantCulture)));
        }

        return tmp;
    }

    private static List<string> Ids(Bundle bundle) => bundle.Concepts.Select(c => c.Id.ToString()).ToList();

    [Fact]
    public void Without_a_scope_every_exact_lookup_lists_its_directory()
    {
        using var tmp = Flat(20);
        var p = Exact(tmp.Path);
        for (var i = 0; i < 20; i++)
        {
            p.Read(K(Name(i))).Dispose();
        }

        Assert.Equal(20, p.DirectoryListings);
    }

    [Fact]
    public void A_load_of_a_flat_directory_lists_it_once()
    {
        using var tmp = Flat(50);
        var p = Exact(tmp.Path);
        var bundle = Bundle.Load(p);
        Assert.Equal(50, bundle.Count);
        Assert.Empty(bundle.ParseErrors);
        // The listing walk lists the root; the version walk and the 50 exact-case reads reuse it.
        Assert.Equal(1, p.DirectoryListings);
    }

    [Fact]
    public void A_read_only_view_forwards_the_scope_to_the_provider_it_wraps()
    {
        using var tmp = Flat(10);
        var p = Exact(tmp.Path);
        Assert.Equal(10, Bundle.Load(p.AsReadOnly()).Count);
        Assert.Equal(1, p.DirectoryListings);
    }

    [Fact]
    public void A_validation_lists_each_directory_once_for_all_its_path_checks_and_keeps_nothing()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 30; i++)
        {
            tmp.Write(Name(i), Concept("C" + i.ToString(CultureInfo.InvariantCulture), "/" + Name((i + 1) % 30)));
        }

        tmp.Write("orphan.md", Concept("Orphan", "/missing.md"));
        var p = Exact(tmp.Path);
        var bundle = Bundle.Load(p);
        var afterLoad = p.DirectoryListings;

        var report = BundleValidator.Validate(bundle);
        Assert.Equal(1, p.DirectoryListings - afterLoad); // 31 path checks plus the hint lookup: one listing
        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);

        // Outside the validation's scope, a question lists again.
        bundle.TryResolveResourceKey(bundle.Concepts[0], "/" + Name(1), out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(2, p.DirectoryListings - afterLoad);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_created_or_deleted_after_a_load_is_seen_by_the_bundle_and_by_the_next_load(bool foldingFlag)
    {
        using var tmp = Flat(5);
        var p = foldingFlag ? Exact(tmp.Path) : new FileSystemBundleProvider(tmp.Path);
        var bundle = Bundle.Load(p);
        Assert.Equal(5, bundle.Count);

        tmp.Write("later.md", Concept("Later"));
        File.Delete(Path.Combine(tmp.Path, Name(4)));

        var concept = bundle.Concepts[0];
        bundle.TryResolveResourceKey(concept, "/later.md", out var key, out var status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(Concept("Later"), bundle.ReadResourceText(key!));
        bundle.TryResolveResourceKey(concept, "/" + Name(4), out _, out var deleted);
        Assert.Equal(ResourceResolutionStatus.Missing, deleted);
        Assert.True(p.Exists(K("later.md")));
        Assert.False(p.Exists(K(Name(4))));

        var next = Bundle.Load(p);
        Assert.Equal(5, next.Count);
        Assert.Contains("later", Ids(next));
        Assert.DoesNotContain("c004", Ids(next));
    }

    [Fact]
    public void A_case_only_rename_between_two_loads_is_seen_by_the_second()
    {
        using var tmp = new TempDir();
        tmp.Write("Topic.md", Concept("Topic"));
        tmp.Write("other.md", Concept("Other"));
        var p = Exact(tmp.Path);

        var first = Bundle.Load(p);
        Assert.Equal(["Topic", "other"], Ids(first).Order(StringComparer.Ordinal));

        File.Move(Path.Combine(tmp.Path, "Topic.md"), Path.Combine(tmp.Path, "topic.md"));

        var second = Bundle.Load(p);
        Assert.Equal(["other", "topic"], Ids(second).Order(StringComparer.Ordinal));
        Assert.True(p.Exists(K("topic.md")));
        Assert.False(p.Exists(K("Topic.md")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("Topic.md")));

        var other = first.Concepts.Single(c => c.Id.ToString() == "other");
        first.TryResolveResourceKey(other, "/topic.md", out _, out var renamed);
        Assert.Equal(ResourceResolutionStatus.Resolved, renamed);
        first.TryResolveResourceKey(other, "/Topic.md", out _, out var former);
        Assert.Equal(ResourceResolutionStatus.Missing, former);
    }

    [Fact]
    public void Inside_a_scope_every_answer_matches_the_unscoped_one()
    {
        using var tmp = new TempDir();
        tmp.Write("a/B.md", "B");
        tmp.Write("a/b2.md", "b2");
        tmp.Write("a/sub/x.md", "x");
        tmp.Write("top.md", "top");
        tmp.Write("Up/x.sql", "sql");
        string[] keys = ["a/B.md", "a/b.md", "A/B.md", "a/b2.md", "a/sub/x.md", "a/sub", "a", "nope/x.md", "top.md", "TOP.md", "Up/x.sql", "up/x.sql", "a/B.md/x", "a/sub/x.md/y"];
        var p = Exact(tmp.Path);

        static string Answer(FileSystemBundleProvider provider, string text)
        {
            var key = K(text);
            string exists;
            try
            {
                exists = provider.Exists(key).ToString();
            }
            catch (BundleProviderException e)
            {
                exists = e.GetType().Name;
            }

            string read;
            try
            {
                using var obj = provider.Read(key);
                read = new StreamReader(obj.Content).ReadToEnd();
            }
            catch (BundleProviderException e)
            {
                read = e.GetType().Name;
            }

            var listed = string.Join(",", provider.Enumerate(key, BundleLimits.Default).Select(l => l.RawName));
            return $"{text}: exists={exists} read={read} listed=[{listed}]";
        }

        var expected = keys.Select(k => Answer(p, k)).ToList();
        List<string> first;
        List<string> second;
        int listingsInScope;
        using (((IProviderScope)p).BeginScope())
        {
            var before = p.DirectoryListings;
            first = keys.Select(k => Answer(p, k)).ToList();
            second = keys.Select(k => Answer(p, k)).ToList(); // from the memo only
            listingsInScope = p.DirectoryListings - before;
        }

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        // root, a, a/sub and Up are each listed once for 14 keys asked twice; the prefixed walks
        // of a and a/sub reuse the lookups' listings. The two files a key asks to list as
        // directories (a/B.md, a/sub/x.md: "no such entry") fail to list, and a failed listing is
        // not kept: each is tried again by the Exists, Read and Enumerate of each of the two
        // rounds (2 files x 3 calls x 2 rounds = 12).
        Assert.Equal(4 + 12, listingsInScope);
    }

    [Fact]
    public void A_flow_that_inherited_a_scope_stops_using_its_memo_when_the_scope_ends()
    {
        using var tmp = Flat(3);
        var p = Exact(tmp.Path);
        ExecutionContext inherited;
        using (((IProviderScope)p).BeginScope())
        {
            Assert.False(p.Exists(K("late.md"))); // memoises the root's names
            tmp.Write("late.md", "x");
            // The accepted window: inside the scope, the root's names are the ones first listed.
            Assert.False(p.Exists(K("late.md")));
            inherited = ExecutionContext.Capture()!;
        }

        Assert.True(p.Exists(K("late.md"))); // this flow: the scope is gone
        var seen = false;
        ExecutionContext.Run(inherited, _ => seen = p.Exists(K("late.md")), null);
        Assert.True(seen); // a flow that captured the scope: its memo is closed
    }

    [Fact]
    public void A_nested_scope_joins_the_open_one_and_does_not_end_it()
    {
        using var tmp = Flat(3);
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            using (((IProviderScope)p).BeginScope())
            {
                Assert.True(p.Exists(K(Name(0))));
            }

            Assert.True(p.Exists(K(Name(1))));
            Assert.Equal(1, p.DirectoryListings);
        }
    }

    [Fact]
    public void Past_its_bound_the_scope_stops_memoising_and_never_throws()
    {
        using var tmp = Flat(10);
        // The provider's own MaxEntries bounds the memo; the load walks under the default bounds.
        var p = Exact(tmp.Path, new BundleLimits { MaxEntries = 5 });
        var bundle = Bundle.Load(p);
        Assert.Equal(10, bundle.Count);
        Assert.Equal(12, p.DirectoryListings); // two walks and ten lookups, as without a scope
    }

    [Fact]
    public void A_stopped_memo_still_resolves_every_lookup_as_the_unscoped_provider_does()
    {
        using var tmp = Flat(10);
        tmp.Write("d/e.md", "e");
        var p = Exact(tmp.Path, new BundleLimits { MaxEntries = 5 });
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K(Name(0)))); // lists the root: 11 names and a directory pass 5, the memo stops
            var before = p.DirectoryListings;
            Assert.True(p.Exists(K(Name(9))));
            Assert.False(p.Exists(K("C009.md"))); // exact case, as without a scope
            Assert.False(p.Exists(K("nope.md")));
            Assert.True(p.Exists(K("d/e.md")));
            Assert.False(p.Exists(K("d/E.md")));
            Assert.Equal("e", new StreamReader(p.Read(K("d/e.md")).Content).ReadToEnd());
            Assert.Equal(1 + 1 + 1 + 2 + 2 + 2, p.DirectoryListings - before); // nothing is kept: each lookup lists
        }
    }

    [Fact]
    public void A_listing_that_fails_is_not_kept_for_the_scope()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "file");
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.False(p.Exists(K("x.md/y"))); // x.md is a file: its listing fails, "no such entry"
            File.Delete(Path.Combine(tmp.Path, "x.md"));
            tmp.Write("x.md/y", "now a directory");
            Assert.True(p.Exists(K("x.md/y"))); // the failure was not memoised: the next lookup lists again
        }
    }

    [SkippableFact]
    public void A_directory_turned_into_a_junction_after_it_was_memoised_is_still_refused_inside_the_scope()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        tmp.Write("a/B.md", "inside");
        external.Write("B.md", "outside");
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("a/B.md"))); // memoises the root and a
            Assert.Single(p.Enumerate(K("a"), BundleLimits.Default));
            var before = p.DirectoryListings;

            Directory.Delete(Path.Combine(tmp.Path, "a"), recursive: true);
            Skip.IfNot(tmp.TryCreateJunctionToExternalDir("a", external.Path), "no junction/symlink privilege on this machine");

            Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/B.md")));
            Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/B.md")));
            Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/nope.md")));
            Assert.ThrowsAny<BundleProviderException>(() => p.Enumerate(K("a"), BundleLimits.Default).ToList());
            Assert.DoesNotContain("a/B.md", p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
            // Every refusal came from a guard that ran at access time, none from a fresh listing.
            Assert.Equal(before, p.DirectoryListings);
        }
    }

    private static TempDir Tree()
    {
        var tmp = new TempDir();
        tmp.Write("top.md", "top");
        tmp.Write("a/one.md", "one");
        tmp.Write("a/two.md", "two!");
        tmp.Write("a/sub/x.md", "x");
        tmp.Write("b/y.sql", "select 1");
        return tmp;
    }

    private static List<string> Listed(FileSystemBundleProvider p) =>
        p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName + "=" + l.Length?.ToString(CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public void A_walk_examines_each_entry_once_and_a_later_walk_of_the_scope_only_its_directories()
    {
        using var tmp = Tree();
        var p = Exact(tmp.Path);
        var listed = Listed(p);
        var version = p.GetVersion(BundleLimits.Default);
        // Outside a scope every walk examines every entry: 5 files and 3 directories.
        Assert.Equal(8 + 8, p.EntryExaminations);

        using (((IProviderScope)p).BeginScope())
        {
            var before = p.EntryExaminations;
            Assert.Equal(listed, Listed(p));
            Assert.Equal(8, p.EntryExaminations - before);

            // The files are not asked about again; the 3 directories are, by every walk.
            Assert.Equal(version, p.GetVersion(BundleLimits.Default));
            Assert.Equal(listed, Listed(p));
            Assert.Equal(8 + 3 + 3, p.EntryExaminations - before);
        }

        // Nothing is kept past the scope.
        var after = p.EntryExaminations;
        Assert.Equal(version, p.GetVersion(BundleLimits.Default));
        Assert.Equal(8, p.EntryExaminations - after);
    }

    [Fact]
    public void A_load_lists_each_directory_once_and_examines_each_file_once_and_each_directory_once_per_walk()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Index\n");
        tmp.Write("top.md", Concept("Top"));
        tmp.Write("a/one.md", Concept("One", "./two.md"));
        tmp.Write("a/two.md", Concept("Two"));
        tmp.Write("a/sub/x.md", Concept("X"));
        tmp.Write("b/y.sql", "select 1");
        var p = Exact(tmp.Path);

        var bundle = Bundle.Load(p);

        Assert.Equal(4, bundle.Count);
        Assert.Empty(bundle.ParseErrors);
        // The root, a, a/sub and b: each listed once for the listing walk, the version walk and every read.
        Assert.Equal(4, p.DirectoryListings);
        // 6 files and 3 directories by the listing walk; by the version walk only the 3 directories again.
        Assert.Equal(6 + 3 + 3, p.EntryExaminations);
    }

    [Fact]
    public void A_walk_reuses_a_listing_a_lookup_made_and_still_examines_its_entries()
    {
        using var tmp = Tree();
        var p = Exact(tmp.Path);
        var listed = Listed(p);
        var version = p.GetVersion(BundleLimits.Default);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("a/sub/x.md"))); // memoises the names of the root, a and a/sub: no walk yet
            var listings = p.DirectoryListings;
            var before = p.EntryExaminations;
            Assert.Equal(version, p.GetVersion(BundleLimits.Default));
            Assert.Equal(8, p.EntryExaminations - before);
            Assert.Equal(1, p.DirectoryListings - listings); // only b was not listed yet
            Assert.Equal(listed, Listed(p));
            Assert.Equal(8 + 3, p.EntryExaminations - before);
        }
    }

    [Fact]
    public void A_write_inside_a_scope_is_seen_by_the_scopes_next_walk()
    {
        using var tmp = Tree();
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            var version = p.GetVersion(BundleLimits.Default);
            Assert.Contains("a/one.md=3", Listed(p));

            p.Write(K("a/one.md"), "longer"u8.ToArray(), BundleWriteCondition.None);
            Assert.Contains("a/one.md=6", Listed(p));
            var rewritten = p.GetVersion(BundleLimits.Default);
            Assert.NotEqual(version, rewritten);

            p.Write(K("a/sub/new.md"), "new"u8.ToArray(), BundleWriteCondition.MustNotExist);
            Assert.Contains("a/sub/new.md=3", Listed(p));
            var created = p.GetVersion(BundleLimits.Default);
            Assert.NotEqual(rewritten, created);

            p.Delete(K("a/sub/new.md"), BundleWriteCondition.None);
            Assert.DoesNotContain("a/sub/new.md=3", Listed(p));
            Assert.Equal(rewritten, p.GetVersion(BundleLimits.Default));
        }

        // The token computed inside the scope is the one an unscoped walk computes.
        using (((IProviderScope)p).BeginScope())
        {
            var inScope = p.GetVersion(BundleLimits.Default);
            Assert.Equal(inScope, p.GetVersion(BundleLimits.Default));
            Assert.Equal(inScope, new FileSystemBundleProvider(tmp.Path).GetVersion(BundleLimits.Default));
        }
    }

    /// <summary>
    /// The accepted window of what a walk keeps: a file rewritten in place by another process
    /// during the scope keeps the length and last-write time the scope's first walk read, until
    /// the scope ends. A read is not concerned: it opens the object and returns its bytes.
    /// </summary>
    [Fact]
    public void A_file_rewritten_behind_the_scope_keeps_its_first_listing_until_the_scope_ends_and_reads_its_new_bytes()
    {
        using var tmp = Tree();
        var p = Exact(tmp.Path);
        BundleVersion inScope;
        using (((IProviderScope)p).BeginScope())
        {
            inScope = p.GetVersion(BundleLimits.Default);
            File.WriteAllText(Path.Combine(tmp.Path, "a", "one.md"), "rewritten behind the scope");
            Assert.Equal(inScope, p.GetVersion(BundleLimits.Default));
            Assert.Contains("a/one.md=3", Listed(p));
            using var obj = p.Read(K("a/one.md"));
            Assert.Equal(26, obj.Length);
        }

        Assert.NotEqual(inScope, p.GetVersion(BundleLimits.Default));
        Assert.Contains("a/one.md=26", Listed(p));
    }

    [SkippableFact]
    public void A_directory_turned_into_a_junction_after_a_walk_of_the_scope_is_skipped_by_its_next_walk()
    {
        using var tmp = Tree();
        using var external = new TempDir();
        external.Write("outside.md", "outside");
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.Contains("a/one.md=3", Listed(p)); // the walk keeps what it examined of the root and of a
            var listings = p.DirectoryListings;

            Directory.Delete(Path.Combine(tmp.Path, "a"), recursive: true);
            Skip.IfNot(tmp.TryCreateJunctionToExternalDir("a", external.Path), "no junction/symlink privilege on this machine");
            try
            {
                // As outside a scope: the link is skipped, never followed, and nothing under it is listed.
                var names = p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName).ToList();
                Assert.Equal(["b/y.sql", "top.md"], names);
                p.GetVersion(BundleLimits.Default);
                Assert.Equal(listings, p.DirectoryListings);
                Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/one.md")));
            }
            finally
            {
                Directory.Delete(Path.Combine(tmp.Path, "a"));
            }
        }
    }

    [SkippableFact]
    public void A_file_turned_into_a_link_after_a_walk_of_the_scope_is_never_read_through()
    {
        using var tmp = Tree();
        using var external = new TempDir();
        var outside = external.Write("outside.md", "outside, and much longer than the file it replaces");
        var p = Exact(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            var listed = Listed(p);
            var version = p.GetVersion(BundleLimits.Default);

            File.Delete(Path.Combine(tmp.Path, "a", "one.md"));
            Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile(Path.Combine("a", "one.md"), outside), "no symlink privilege on this machine");

            // The scope's later walks answer from what the first one read of the file that was
            // there: nothing of the link's target (its length, its time) reaches them.
            Assert.Equal(listed, Listed(p));
            Assert.Equal(version, p.GetVersion(BundleLimits.Default));
            // The read gate inspects the entry at access time, as always.
            Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/one.md")));
            Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/one.md")));
        }

        // Past the scope the walk examines the entry again and skips the link.
        Assert.DoesNotContain(Listed(p), l => l.StartsWith("a/one.md", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_that_names_a_directory_is_no_object_and_a_vanished_one_is_absent_inside_a_scope_as_outside()
    {
        using var tmp = Tree();
        var p = Exact(tmp.Path);
        Assert.False(p.Exists(K("a/sub")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("a/sub")));
        using (((IProviderScope)p).BeginScope())
        {
            Assert.False(p.Exists(K("a/sub")));
            Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("a/sub")));
            Assert.True(p.Exists(K("a/one.md"))); // memoises a's names

            // Deleted behind the scope: the name is still in the memo, the leaf's inspection
            // finds no entry, and the existence check and the open answer as they always did.
            File.Delete(Path.Combine(tmp.Path, "a", "one.md"));
            Assert.False(p.Exists(K("a/one.md")));
            Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("a/one.md")));
        }
    }

    [Fact]
    public void Parallel_loads_and_validations_of_one_provider_return_equal_results()
    {
        using var tmp = new TempDir();
        for (var d = 0; d < 3; d++)
        {
            for (var i = 0; i < 30; i++)
            {
                tmp.Write($"d{d}/{Name(i)}", Concept("C", $"./{Name((i + 1) % 30)}"));
            }
        }

        for (var i = 0; i < 10; i++)
        {
            tmp.Write(Name(i), Concept("Top", "/d0/" + Name(i)));
        }

        var p = Exact(tmp.Path);
        var reference = Bundle.Load(p);
        var referenceReport = BundleValidator.Validate(reference);
        Assert.Equal(100, reference.Count);

        var bundles = new Bundle[8];
        var reports = new ValidationReport[8];
        Parallel.For(0, 8, i =>
        {
            bundles[i] = Bundle.Load(p);
            reports[i] = BundleValidator.Validate(bundles[i]);
        });

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(Ids(reference), Ids(bundles[i]));
            Assert.Equal(reference.Version, bundles[i].Version);
            Assert.Equal(
                referenceReport.Diagnostics.Select(d => d.ToString()),
                reports[i].Diagnostics.Select(d => d.ToString()));
        }
    }
}

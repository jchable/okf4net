// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleProviderLoadTests
{
    // 79 UTF-8 bytes; the "big" fixtures below are 115 and 227 — the bound used with them is 100.
    private const string Orders = "---\ntype: Table\ntitle: Orders\ndescription: One row per order.\n---\n\nSee [customers](/tables/customers.md).\n";
    private const string Customers = "---\ntype: Table\ntitle: Customers\ndescription: One row per customer.\n---\n\nBody.\n";
    private const string Broken = "---\ntype: Note\nk: *a\n---\nbody\n"; // a YAML alias: the subset rejects it → ParseErrors

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static readonly BundleLoadOptions StrictOptions = new() { KeyPolicy = BundleKeyPolicy.Strict };

    [Fact]
    public void Loading_through_the_filesystem_provider_equals_loading_by_path()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n");
        tmp.Write("tables/orders.md", Orders);
        tmp.Write("tables/customers.md", Customers);
        tmp.Write("tables/log.md", "# Log\n\n## 2026-01-01\n\n- x\n");
        tmp.Write("tables/broken.md", Broken);
        var byPath = Bundle.Load(tmp.Path);
        var provider = new FileSystemBundleProvider(tmp.Path);
        var byProvider = Bundle.Load(provider);

        Assert.Equal(byPath.Concepts.Select(c => c.Id), byProvider.Concepts.Select(c => c.Id));
        Assert.Equal(byPath.Concepts.Select(c => c.Path), byProvider.Concepts.Select(c => c.Path));
        Assert.Equal(byPath.Concepts.Select(c => c.Document.Body), byProvider.Concepts.Select(c => c.Document.Body));
        Assert.Equal(byPath.IndexFiles, byProvider.IndexFiles);
        Assert.Equal(byPath.LogFiles, byProvider.LogFiles);
        Assert.Equal(byPath.ParseErrors, byProvider.ParseErrors);
        Assert.Single(byPath.ParseErrors);
        Assert.Equal(byPath.OkfVersion, byProvider.OkfVersion);
        Assert.Equal(byPath.Root, byProvider.Root);
        var orders = ConceptId.Parse("tables/orders");
        Assert.Equal(byPath.LinksFrom(orders), byProvider.LinksFrom(orders));
        Assert.Equal(byPath.Backlinks(ConceptId.Parse("tables/customers")), byProvider.Backlinks(ConceptId.Parse("tables/customers")));

        Assert.Same(provider, byProvider.Provider);
        Assert.Equal(tmp.Path, byProvider.Location);
        Assert.Equal(provider.GetVersion(BundleLimits.Default), byProvider.Version);
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byProvider.Get(orders)!.Key);
        Assert.Equal(new BundleObjectVersion(Sha(Orders)), byProvider.ObjectVersions[BundleKey.Parse("tables/orders.md")]);
        Assert.Equal([BundleKey.Parse("index.md")], byProvider.IndexKeys);
        Assert.Equal([BundleKey.Parse("tables/log.md")], byProvider.LogKeys);
        Assert.NotNull(byPath.Provider); // Load(string) sets the new members too
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byPath.Get(orders)!.Key);
    }

    [Fact]
    public void Concept_keeps_its_three_positional_parameters()
    {
        var doc = OkfDocument.Parse(Customers);
        var concept = new Concept(ConceptId.Parse("a"), "a.md", doc);
        var (id, path, document) = concept; // Deconstruct(3) still exists
        Assert.Equal(ConceptId.Parse("a"), id);
        Assert.Equal("a.md", path);
        Assert.Same(doc, document);
        Assert.Null(concept.Key);
        Assert.Equal(BundleKey.Parse("a.md"), (concept with { Key = BundleKey.Parse("a.md") }).Key);
    }

    /// <summary>
    /// Record equality, hash and <c>ToString</c> stay over the three positional members, as the
    /// compiler synthesised them on <c>dev</c> before <see cref="Concept.Key"/> existed: a loaded
    /// concept (which carries a key) equals a hand-built one with the same id, path and document.
    /// </summary>
    [Fact]
    public void A_loaded_concept_equals_a_hand_built_one_with_the_same_three_members()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/customers.md", Customers);
        var loaded = Bundle.Load(tmp.Path).Concepts.Single();
        Assert.NotNull(loaded.Key);

        var handBuilt = new Concept(loaded.Id, loaded.Path, loaded.Document);
        Assert.Null(handBuilt.Key);
        Assert.Equal(handBuilt, loaded);
        Assert.True(loaded == handBuilt);
        Assert.True(handBuilt.Equals((object)loaded));
        Assert.Equal(handBuilt.GetHashCode(), loaded.GetHashCode());
        Assert.Equal(handBuilt.ToString(), loaded.ToString());
        Assert.Equal($"Concept {{ Id = {loaded.Id}, Path = {loaded.Path}, Document = {loaded.Document} }}", loaded.ToString());

        var copy = loaded with { };
        Assert.Equal(loaded.Key, copy.Key); // `with` still copies the key
        Assert.Equal(loaded, loaded with { Key = BundleKey.Parse("other.md") });
        Assert.NotEqual(loaded, loaded with { Path = loaded.Path + "x" });
        Assert.NotEqual(loaded, loaded with { Id = ConceptId.Parse("other") });
        Assert.NotEqual(loaded, loaded with { Document = OkfDocument.Parse(Orders) });
        Assert.False(loaded.Equals(null));
    }

    [Fact]
    public void Strict_rejections_land_in_ParseProblems_with_the_raw_name_and_no_key()
    {
        var p = new InMemoryBundleProvider()
            .Add("con.md", Customers)
            .Add("ok.md", Customers)
            .Add("a:b.txt", "not a concept, silently skipped");
        var bundle = Bundle.Load(p, StrictOptions);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("con.md", problem.RawName);
        Assert.Null(problem.Key);
        Assert.Contains("reserved", problem.Error);
        var projected = Assert.Single(bundle.ParseErrors);
        Assert.Contains("con.md", projected.Path);
        Assert.Equal(problem.Error, projected.Error);
        Assert.Contains(BundleKey.Parse("a:b.txt"), bundle.RejectedKeys); // remembered even though not diagnosed
    }

    [Fact]
    public void Native_policy_keeps_what_strict_rejects()
    {
        var p = new InMemoryBundleProvider().Add("A/x.md", Customers).Add("a/y.md", Customers);
        var strict = Bundle.Load(p, StrictOptions);
        Assert.Empty(strict.Concepts);
        Assert.Equal(2, strict.ParseProblems.Count);
        Assert.Contains(BundleKey.Parse("A/x.md"), strict.RejectedKeys);
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("a")], strict.RejectedPrefixes);
        var native = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal(2, native.Concepts.Count);
        Assert.Empty(native.ParseProblems);
        Assert.Empty(native.RejectedKeys);
        Assert.Empty(native.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejected_objects_stay_rejected_for_resolution_direct_reads_the_root_index_and_later_descendants()
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("A/x.sql", "outside tree x")
            .Add("a/y.sql", "y")
            .Add("con.sql", "reserved")
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n")
            .Add("INDEX.md", "# shadow\n");
        var bundle = Bundle.Load(p, StrictOptions);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;

        // Diagnostics follow the enumeration scope (§2): only the two .md names.
        Assert.Equal(2, bundle.ParseProblems.Count);
        Assert.Equal(5, bundle.RejectedKeys.Count); // A/x.sql, a/y.sql, con.sql, index.md, INDEX.md
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("INDEX.md"), BundleKey.Parse("a"), BundleKey.Parse("index.md")], bundle.RejectedPrefixes);

        bundle.TryResolveResourceKey(concept, "/A/x.sql", out var key, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status); // Exists would say yes; the tree said no
        Assert.Null(key);

        // Direct reads go through the same guard.
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("A/x.sql")));
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("con.sql")));

        // The root index was rejected: it declares nothing.
        Assert.Empty(bundle.IndexKeys);
        Assert.Null(bundle.OkfVersion);

        // An object created later under a condemned prefix is not rehabilitated.
        p.Add("A/new.sql", "late");
        bundle.TryResolveResourceKey(concept, "/A/new.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("A/new.sql")));

        // One created later under a clean prefix is checked live, as today.
        p.Add("c/late.sql", "fine");
        bundle.TryResolveResourceKey(concept, "./late.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
    }

    [Fact]
    public void A_third_spelling_of_a_condemned_prefix_is_still_condemned()
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("Ab/x.sql", "x")
            .Add("aB/y.sql", "y");
        var bundle = Bundle.Load(p, StrictOptions);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.Equal([BundleKey.Parse("Ab"), BundleKey.Parse("aB")], bundle.RejectedPrefixes);

        p.Add("AB/new.sql", "third spelling");
        bundle.TryResolveResourceKey(concept, "/AB/new.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("AB/new.sql")));
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("ab/z.sql"), out var reason));
        Assert.Contains("rejected when the bundle was loaded", reason);
    }

    [Theory]
    [InlineData("Ab/x.sql", "aB/y.sql", "AB/new.sql", true)]        // every offender duplicated: the collision is structural only
    [InlineData("x", "x/y.sql", "x/new.sql", true)]                 // same for the file/directory conflict
    [InlineData("x.md/A.sql", "x.md/a.sql", "x.md/new.sql", false)] // case-doomed children: the parent `x.md` stays an object-and-prefix
    [InlineData("caf\u00e9/x.sql", "cafe\u0301/y.sql", "caf\u00e9/new.sql", false)] // canonically equivalent prefixes: the NFC spelling is condemned with the refused NFD one (decision 17)
    [InlineData("\u212b/x.sql", "\u00c5/y.sql", "\u00c5/new.sql", false)]            // ANGSTROM SIGN beside Å
    public void Prefixes_condemned_only_structurally_still_guard_late_objects(string first, string second, string late, bool duplicated)
    {
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("x.md", "---\ntype: T\n---\n")
            .Add(first, "1")
            .Add(second, "2");
        if (duplicated)
        {
            p.ListTwice(first).ListTwice(second);
        }
        var bundle = Bundle.Load(p, StrictOptions);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.NotEmpty(bundle.RejectedPrefixes);

        p.Add(late, "late");
        bundle.TryResolveResourceKey(concept, "/" + late, out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse(late)));
    }

    [Fact]
    public void A_host_validator_that_refuses_a_parent_does_not_rehabilitate_its_children()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux) // native: FileDirectoryConflict
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("x.md", "---\ntype: T\n---\n")
            .Add("x.md/ok.sql", "ok");
        var options = new BundleLoadOptions
        {
            KeyPolicy = BundleKeyPolicy.Native,
            ExtraKeyValidators = [k => k.ToString() == "x.md" ? "refused by the host" : null],
        };
        var bundle = Bundle.Load(p, options);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.DoesNotContain(bundle.Concepts, c => c.Id == ConceptId.Parse("x"));
        Assert.Equal([BundleKey.Parse("x.md")], bundle.RejectedPrefixes);

        bundle.TryResolveResourceKey(concept, "/x.md/ok.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("x.md/ok.sql")));

        p.Add("x.md/new.sql", "late");
        bundle.TryResolveResourceKey(concept, "/x.md/new.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
    }

    [Fact]
    public void A_directory_whose_name_is_no_valid_key_is_a_diagnostic_not_a_failure()
    {
        var p = new InMemoryBundleProvider()
            .Add("bad\u0085dir/x.md", Customers) // U+0085: layer-1 control character
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("bad\u0085dir/x.md", problem.RawName);
        Assert.Null(problem.Key);
        Assert.Contains("control", problem.Error);
    }

    [Fact]
    public void Two_names_a_UTF8_store_would_merge_neither_load_under_a_native_linux_set()
    {
        // `\uD800.md` and `\uD801.md` are two keys, but a UTF-8 store writes both as U+FFFD.md.
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux)
            .Add("\uD800.md", Customers)
            .Add("\uD801.md", Customers)
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal(2, bundle.ParseProblems.Count);
        Assert.All(bundle.ParseProblems, e => Assert.Contains("unpaired surrogate", e.Error));
    }

    [Fact]
    public void A_concept_over_the_object_bound_becomes_a_parse_problem_and_the_load_goes_on()
    {
        var big = "---\ntype: T\n---\n" + new string('x', 100); // 115 bytes
        var p = new InMemoryBundleProvider().Add("big.md", big).Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 100 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(BundleKey.Parse("big.md"), problem.Key);
        Assert.Contains("100", problem.Error);
    }

    [Fact]
    public void A_root_index_over_the_bound_gives_no_declared_version_but_a_global_failure_reading_it_aborts()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n" + new string('#', 200)) // 227 bytes
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 100 } });
        Assert.Null(bundle.OkfVersion);
        Assert.Single(bundle.IndexKeys);

        var failing = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n")
            .Add("ok.md", Customers)
            .FailReadOf("index.md", new BundleLimitExceededException(BundleBound.Entries, 1));
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(failing));
        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public void Parse_problems_are_in_the_component_wise_path_order_whatever_stage_found_them()
    {
        // Two YAML failures (read stage), a file/directory conflict and a backslash (tree
        // stage), a name over the key bound (listing stage): one path order, the one the
        // loader before the seam sorted its files by (`/` and `\` both separate components).
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux) { ObeyLimits = false }
            .Add("z.md", Broken)
            .Add("m\\n.md", Customers)
            .Add("c.md", Customers)
            .Add("c.md/x.md", Customers)
            .Add("a.md", Broken)
            .Add("b/" + new string('l', 20) + ".md", Customers)
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 16 } });
        string[] expected = ["a.md", "b/" + new string('l', 20) + ".md", "c.md", "c.md/x.md", "m\\n.md", "z.md"];
        Assert.Equal(expected, bundle.ParseProblems.Select(e => e.RawName));
        Assert.Equal(expected.Length, bundle.ParseErrors.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.EndsWith(expected[i].Replace('/', Path.DirectorySeparatorChar), bundle.ParseErrors[i].Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_20_MiB_concept_and_a_70_deep_tree_load_under_the_default_bounds()
    {
        using var tmp = new TempDir();
        var big = Customers + new string('x', 20 * 1024 * 1024);
        tmp.Write("big.md", big);
        var deep = string.Join('/', Enumerable.Repeat("d", 69)) + "/deep.md"; // 70 segments
        tmp.Write(deep, Customers);

        var bundle = Bundle.Load(tmp.Path);
        Assert.Empty(bundle.ParseProblems);
        Assert.True(bundle.Get(ConceptId.Parse("big"))!.Document.Body.Length > 20 * 1024 * 1024);
        Assert.NotNull(bundle.Get(ConceptId.Parse(deep[..^3])));
    }

    [Fact]
    public void The_core_enforces_entries_depth_and_key_bytes_even_when_the_provider_does_not()
    {
        var entries = new InMemoryBundleProvider { ObeyLimits = false }.Add("a.md", Customers).Add("b.md", Customers);
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(entries, new BundleLoadOptions { Limits = new BundleLimits { MaxEntries = 1 } }));
        Assert.Contains("entries", ex.Message);

        var depth = new InMemoryBundleProvider { ObeyLimits = false }.Add("a/b/c.md", Customers);
        var dex = Assert.Throws<BundleLoadException>(() => Bundle.Load(depth, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2 } }));
        Assert.Contains("depth", dex.Message);

        // Depth is measured on the raw name, before any other rejection: a too-deep name that
        // Strict would also reject (reserved name), or that is no valid key at all (backslash,
        // over 1 024 bytes), still aborts the load.
        var deepReserved = new InMemoryBundleProvider { ObeyLimits = false }.Add("a/b/con.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepReserved, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2 } })).Message);
        var deepInvalid = new InMemoryBundleProvider { ObeyLimits = false }.Add("bad\\name/a/b.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepInvalid, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 1 } })).Message);
        var deepLong = new InMemoryBundleProvider { ObeyLimits = false }.Add(new string('x', 1030) + "/a/b.md", Customers);
        Assert.Contains("depth", Assert.Throws<BundleLoadException>(() => Bundle.Load(deepLong, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 1 } })).Message);

        var longKey = new InMemoryBundleProvider { ObeyLimits = false }.Add("longname.md", Customers).Add("ok.md", Customers);
        var bundle = Bundle.Load(longKey, new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 6 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("longname.md", problem.RawName);
        Assert.Contains("6", problem.Error);
        Assert.Contains(BundleKey.Parse("longname.md"), bundle.RejectedKeys);
    }

    [Fact]
    public void A_child_refused_for_its_length_still_condemns_its_parent_object()
    {
        // `x.md/longname.sql` (17 bytes) is over the 12-byte bound: refused, but physically under `x.md`.
        var p = new InMemoryBundleProvider()
            .Add("c/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("x.md", "---\ntype: T\n---\n")
            .Add("x.md/longname.sql", "long");
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Strict, Limits = new BundleLimits { MaxKeyBytes = 12 } });
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        Assert.Equal([ConceptId.Parse("c/rev")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal([BundleKey.Parse("x.md")], bundle.RejectedPrefixes);
        Assert.Contains(bundle.ParseProblems, e => e.RawName == "x.md" && e.Error.Contains("both an object and a prefix"));
        Assert.Contains(BundleKey.Parse("x.md/longname.sql"), bundle.RejectedKeys); // the length diagnostics are kept

        p.Add("x.md/new.sql", "late"); // 12 bytes: within the bound, refused for its condemned parent
        bundle.TryResolveResourceKey(concept, "/x.md/new.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("x.md/new.sql"), out var reason));
        Assert.Contains("under 'x.md'", reason);
    }

    [Fact]
    public void A_name_refused_for_its_length_still_collides_with_its_sibling_as_an_object()
    {
        // NFC `café.md` is 8 UTF-8 bytes, its NFD twin 9: under an 8-byte bound only the NFD
        // name is refused for its length, yet it is still THERE beside the NFC one.
        var nfc = "caf" + (char)0x00E9 + ".md";
        var nfd = "cafe" + (char)0x0301 + ".md";
        var p = new InMemoryBundleProvider().Add(nfc, Customers).Add(nfd, Customers).Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Strict, Limits = new BundleLimits { MaxKeyBytes = 8 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        Assert.Contains(bundle.ParseProblems, e => e.RawName == nfc && e.Error.Contains("collides with a sibling"));
        Assert.Contains(bundle.ParseProblems, e => e.RawName == nfd && e.Error.Contains("8-byte bound"));
        Assert.Equal(2, bundle.RejectedPrefixes.Count);
        Assert.Contains(BundleKey.Parse(nfc), bundle.RejectedPrefixes);
        Assert.Contains(BundleKey.Parse(nfd), bundle.RejectedPrefixes);
    }

    [Theory]
    [InlineData(BundleKey.MaxUtf8Bytes)] // refused by the core bound: an unadmitted name
    [InlineData(4096)]                   // past the bound, refused by BundleKey.TryParse: no key at all
    public void A_directory_implied_only_by_a_name_over_the_key_limit_still_collides_with_its_sibling(int maxKeyBytes)
    {
        var tooLong = "docs/" + new string('x', BundleKey.MaxUtf8Bytes) + ".md"; // no key at all
        var p = new InMemoryBundleProvider { ObeyLimits = false }
            .Add("Docs/a.md", Customers)
            .Add(tooLong, Customers)
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Strict, Limits = new BundleLimits { MaxKeyBytes = maxKeyBytes } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal([BundleKey.Parse("Docs"), BundleKey.Parse("docs")], bundle.RejectedPrefixes);
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("DOCS/new.md"), out _));
    }

    [Fact]
    public void Bounds_also_apply_to_keys_that_appear_after_the_load()
    {
        var p = new InMemoryBundleProvider().Add("c/rev.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxDepth = 2, MaxKeyBytes = 12 } });
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        p.Add("long/path/q.sql", "late and too deep");
        bundle.TryResolveResourceKey(concept, "/long/path/q.sql", out _, out var status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("long/path/q.sql")));
        Assert.False(bundle.IsKeyAllowed(BundleKey.Parse("long/path/q.sql"), out var reason));
        Assert.Contains("depth", reason);
    }

    [Fact]
    public void Provider_failures_are_translated_from_their_type_never_from_their_message()
    {
        var raw = new InMemoryBundleProvider().Add("a.md", Customers);
        raw.FailReadsWith = new IOException("secret-token=123 host=C:/private signed-url=https://private/?sig=secret");
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(raw));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Contains("'a.md'", ex.Message);
        Assert.Contains(nameof(IOException), ex.Message);
        Assert.Contains("secret-token", ex.InnerException!.Message);

        var typed = new InMemoryBundleProvider().Add("c.md", Customers);
        typed.FailReadsWith = new BundleAccessRefusedException(BundleKey.Parse("c.md"), "secret-token=123");
        var tex = Assert.Throws<BundleLoadException>(() => Bundle.Load(typed));
        Assert.DoesNotContain("secret", tex.Message);
        Assert.Contains("'c.md'", tex.Message);
        Assert.Contains("secret-token", ((BundleAccessRefusedException)tex.InnerException!).Detail);

        var readOnly = Assert.Throws<BundleReadOnlyException>(() => raw.Write(BundleKey.Parse("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.DoesNotContain(raw.Id, readOnly.Message);
    }

    [Fact]
    public void An_unlistable_storage_or_a_failing_version_is_translated_at_the_boundary()
    {
        var unlistable = new InMemoryBundleProvider().Add("a.md", Customers);
        var denied = new UnauthorizedAccessException("secret-token=123 host=C:/private");
        unlistable.FailEnumerateWith = denied;
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(unlistable));
        Assert.Same(denied, ex.InnerException);
        Assert.Contains("listing", ex.Message);
        Assert.Contains(nameof(UnauthorizedAccessException), ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("private", ex.Message);

        var noVersion = new InMemoryBundleProvider().Add("a.md", Customers);
        var broken = new IOException("signed-url=https://private/?sig=secret");
        noVersion.FailVersionWith = broken;
        var vex = Assert.Throws<BundleLoadException>(() => Bundle.Load(noVersion));
        Assert.Same(broken, vex.InnerException);
        Assert.Contains("version", vex.Message);
        Assert.Contains(nameof(IOException), vex.Message);
        Assert.DoesNotContain("secret", vex.Message);
    }

    [Fact]
    public void Reserved_filenames_are_classified_by_the_last_segment_only()
    {
        var p = new InMemoryBundleProvider()
            .Add("tables/index.md/x.md", Customers)
            .Add("tables/index.md", "# Tables\n")
            .Add("log.md", "# Log\n");
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal([ConceptId.Parse("tables/index.md/x")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal([BundleKey.Parse("tables/index.md")], bundle.IndexKeys);
        Assert.Equal([BundleKey.Parse("log.md")], bundle.LogKeys);
    }

    [Fact]
    public void Resource_keys_resolve_through_the_provider_and_the_policy()
    {
        var p = new InMemoryBundleProvider()
            .Add("computations/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("references/q.sql", "select 1")
            .Add("computations/sibling.sql", "select 2");
        var bundle = Bundle.Load(p, StrictOptions);
        var concept = bundle.Get(ConceptId.Parse("computations/rev"))!;

        Assert.True(bundle.TryResolveResourceKey(concept, "/references/q.sql", out var key, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("references/q.sql"), key);

        bundle.TryResolveResourceKey(concept, "./sibling.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("computations/sibling.sql"), key);

        bundle.TryResolveResourceKey(concept, "references/missing.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Missing, status);
        Assert.Null(key); // the key is non-null only for Resolved

        bundle.TryResolveResourceKey(concept, ".", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Missing, status); // the prefix `computations` is no object
        Assert.Null(key);

        bundle.TryResolveResourceKey(concept, "../../q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(key);

        bundle.TryResolveResourceKey(concept, "references\\q.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "/con.sql", out _, out status); // rejected by Strict
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "https://x/q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Url, status);
        Assert.Null(key);

        Assert.Equal("select 1", bundle.ReadResourceText(BundleKey.Parse("references/q.sql")));
        Assert.Throws<FileNotFoundException>(() => bundle.ReadResourceText(BundleKey.Parse("references/missing.sql")));
    }

    [Fact]
    public void A_provider_refusing_Exists_reads_as_Unsafe_and_a_direct_read_is_unauthorized()
    {
        var p = new InMemoryBundleProvider()
            .Add("computations/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("linked/secret.sql", "select 1")
            .RefuseExistsOf("linked/secret.sql");
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("computations/rev"))!;

        Assert.True(bundle.TryResolveResourceKey(concept, "../linked/secret.sql", out var key, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(key);

        p.FailReadsWith = new BundleAccessRefusedException(BundleKey.Parse("linked/secret.sql"), "secret-token=123");
        var ex = Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(BundleKey.Parse("linked/secret.sql")));
        Assert.DoesNotContain("secret-token", ex.Message);
        Assert.IsType<BundleAccessRefusedException>(ex.InnerException);
    }

    [Fact]
    public void ConceptId_round_trips_through_keys()
    {
        var id = ConceptId.Parse("tables/orders");
        Assert.Equal(BundleKey.Parse("tables/orders.md"), id.ToKey());
        Assert.Equal(id, ConceptId.FromKey(BundleKey.Parse("tables/orders.md")));
        Assert.Throws<ConceptIdException>(() => ConceptId.FromKey(BundleKey.Parse("tables/orders.txt")));
    }

    [Fact]
    public void The_validator_reads_reserved_files_and_resources_through_the_provider()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n\n- [Orders](tables/orders.md) — One row per order.\n")
            .Add("tables/index.md", "---\nstray: frontmatter\n---\n# Tables\n")
            .Add("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: q.sql\n---\n")
            .Add("tables/q.sql", "select 1")
            .Add("log.md", "# Log\n\n## not-a-date\n\n- x\n");
        var bundle = Bundle.Load(p);
        var report = BundleValidator.Validate(bundle);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.IndexHasFrontmatter);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid);
        var missing = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.Contains("a file exists at tables/q.sql", missing.Message); // the concept-relative hint, now through the provider
    }
}

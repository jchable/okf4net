// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class FileSystemBundleProviderTests
{
    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static BundleKey K(string s) => BundleKey.Parse(s);

    [Fact]
    public void Enumerate_lists_every_file_relative_with_slashes_in_ordinal_order_depth_first_and_is_eager()
    {
        using var tmp = new TempDir();
        tmp.Write("b.md", "b");
        tmp.Write("a/z.txt", "z");
        tmp.Write("a/B.md", "B");
        tmp.Write("a/a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        var listing = p.Enumerate(null, BundleLimits.Default);
        Assert.IsAssignableFrom<IReadOnlyList<BundleListing>>(listing); // materialised, not deferred
        Assert.Equal(["a/B.md", "a/a.md", "a/z.txt", "b.md"], listing.Select(l => l.RawName)); // 'B' < 'a' ordinally; non-.md files are listed too
        Assert.All(listing, l => Assert.Null(l.Version)); // the filesystem gives no cheap version
        Assert.Equal(1, listing.Single(l => l.RawName == "b.md").Length);
    }

    [Fact]
    public void Enumerate_prefix_is_segment_bounded()
    {
        using var tmp = new TempDir();
        tmp.Write("a/x.md", "x");
        tmp.Write("ab/y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["a/x.md"], p.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName));
        Assert.Empty(p.Enumerate(K("nope"), BundleLimits.Default));
    }

    [Fact]
    public void Enumerate_counts_every_entry_against_the_bound_and_enforces_depth()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        tmp.Write("y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        // Entries inspected: d (directory), d/x.md, y.md = 3.
        var ex = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));
        Assert.Equal(BundleBound.Entries, ex.Bound);
        Assert.Equal(2, p.Enumerate(null, new BundleLimits { MaxEntries = 3 }).Count()); // 2 files listed, 3 entries counted
        tmp.Write("l1/l2/l3/deep.md", "d");
        var depth = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxDepth = 3 })); // l1/l2/l3/deep.md has 4 segments
        Assert.Equal(BundleBound.Depth, depth.Bound);
    }

    [Fact]
    public void A_directory_whose_name_is_no_valid_key_is_walked_not_fatal()
    {
        using var tmp = new TempDir();
        tmp.Write("bad\u0085dir/x.md", "x"); // U+0085 is a layer-1 control character: the KEY is invalid, the DIRECTORY exists
        tmp.Write("ok.md", "o");
        var p = new FileSystemBundleProvider(tmp.Path);
        var names = p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName).ToList();
        Assert.Equal(["bad\u0085dir/x.md", "ok.md"], names); // raw names, not keys: the loader rejects the first with a diagnostic (Task 6)
    }

    [SkippableFact]
    public void Enumerate_counts_skipped_reparse_points_too()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateFileSymlink("l1.md", "real.md") && tmp.TryCreateFileSymlink("l2.md", "real.md"), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));
    }

    [SkippableFact]
    public void Enumerate_skips_a_symlinked_entry_but_read_refuses_it_strictly()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateFileSymlink("link.md", "real.md"), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("link.md")));
        Assert.False(p.Exists(K("link.md")));
    }

    [SkippableFact]
    public void A_junction_prefix_is_refused_and_a_file_under_it_is_unreadable()
    {
        using var external = new TempDir();
        external.Write("secret.md", "s");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("jump", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("jump"), BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("jump/secret.md")));
        // Present and absent names under the junction are indistinguishable: both refused
        // before the junction is ever listed (fourth review).
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("jump/absent.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("jump/absent"), BundleLimits.Default));
        Assert.False(p.Exists(K("jump/secret.md")));
        Assert.False(p.Exists(K("jump/absent.md")));
        Assert.Empty(p.Enumerate(null, BundleLimits.Default)); // the walk skips the junction entry and lists nothing under it
    }

    [SkippableFact]
    public void An_enumeration_taken_before_a_prefix_or_root_is_replaced_never_sees_the_replacement()
    {
        using var external = new TempDir();
        external.Write("secret.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("src/p/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);

        var byPrefix = p.Enumerate(K("p"), BundleLimits.Default); // eager: already complete
        Directory.Delete(Path.Combine(root, "p"), recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("src", "p"), external.Path), "junctions unavailable"); // the helper hands the path to mklink as is: native separator
        Assert.Equal(["p/a.md"], byPrefix.Select(l => l.RawName)); // what was there, not what is there now
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("p"), BundleLimits.Default)); // a fresh call sees the junction and refuses

        var whole = p.Enumerate(null, BundleLimits.Default);
        Directory.Delete(Path.Combine(root, "p")); // the junction first, non-recursively: a recursive delete refuses to traverse it
        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.DoesNotContain(whole, l => l.RawName.Contains("secret"));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("secret.md")));
    }

    [Fact]
    public void Read_returns_the_bytes_and_a_sha256_version_and_honours_expected()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "hello");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var obj = p.Read(K("a.md"));
        Assert.Equal(new BundleObjectVersion(Sha256Hex("hello")), obj.Version);
        Assert.Equal(5, obj.Length);
        using var reader = new StreamReader(obj.Content, Encoding.UTF8);
        Assert.Equal("hello", reader.ReadToEnd());
        Assert.Throws<BundleVersionMismatchException>(() => p.Read(K("a.md"), new BundleObjectVersion("other")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("missing.md")));
        using var pinned = p.Read(K("a.md"), new BundleObjectVersion(Sha256Hex("hello")));
        Assert.Equal(5, pinned.Length);
    }

    [Fact]
    public void Read_stops_at_the_object_bound_before_hashing()
    {
        using var tmp = new TempDir();
        tmp.Write("big.md", new string('x', 11));
        var p = new FileSystemBundleProvider(tmp.Path, limits: new BundleLimits { MaxObjectBytes = 10 });
        var ex = Assert.Throws<BundleObjectTooLargeException>(() => p.Read(K("big.md")));
        Assert.IsAssignableFrom<IOException>(ex);
        tmp.Write("ok.md", new string('x', 10));
        using var ok = p.Read(K("ok.md"));
        Assert.Equal(10, ok.Length);
    }

    [SkippableFact]
    public void Keys_match_exactly_in_case_on_a_case_insensitive_volume()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "needs a case-insensitive default volume");
        using var tmp = new TempDir();
        tmp.Write("References/A.sql", "select 1");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.MatchesCaseExactly);
        Assert.True(p.Exists(K("References/A.sql")));
        Assert.False(p.Exists(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("References/a.sql")));
        using var exact = p.Read(K("References/A.sql"));
        Assert.Equal(8, exact.Length);
        Assert.Empty(p.Enumerate(K("references"), BundleLimits.Default));
    }

    [Fact]
    public void Constraints_can_be_added_never_removed_and_a_folding_flag_turns_exact_case_on_anywhere()
    {
        using var tmp = new TempDir();
        tmp.Write("a/UP.sql", "x");
        var p = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.None);
        Assert.Equal(BundleKeyConstraintSets.ForCurrentOperatingSystem(), p.NativeConstraints);
        var folding = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.CaseInsensitiveCollisions);
        Assert.True(folding.MatchesCaseExactly); // on Linux too (a case-insensitive mount, decision 15)
        Assert.True(folding.NativeConstraints.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions));
        Assert.True(folding.NativeConstraints.HasFlag(BundleKeyConstraintSets.ForCurrentOperatingSystem()));
        Assert.True(folding.Exists(K("a/UP.sql")));
        Assert.False(folding.Exists(K("a/up.sql")));
    }

    [Fact]
    public void GetVersion_changes_with_content_and_is_stable_otherwise()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.md", "one");
        var p = new FileSystemBundleProvider(tmp.Path);
        var v1 = p.GetVersion(BundleLimits.Default);
        Assert.Equal(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "two!"); // different length
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "one");
        File.SetLastWriteTimeUtc(file, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default)); // same bytes, different mtime: a listing digest, not a content hash
        Assert.Throws<BundleLimitExceededException>(() => p.GetVersion(new BundleLimits { MaxEntries = 0 }));
    }

    [Fact]
    public void Writes_are_refused_in_this_lot_and_the_message_carries_no_host_path()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.False(p.Capabilities.CanWrite);
        var ex = Assert.Throws<BundleReadOnlyException>(() => p.Write(K("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(p.Id, ex.ProviderId);
        Assert.Throws<BundleReadOnlyException>(() => p.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "a.md")));
    }

    [Fact]
    public void Id_is_the_canonical_root_and_location_is_the_root_as_given()
    {
        using var tmp = new TempDir();
        var withSlash = tmp.Path + Path.DirectorySeparatorChar;
        var a = new FileSystemBundleProvider(tmp.Path);
        var b = new FileSystemBundleProvider(withSlash);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(withSlash, b.Location);
        Assert.Equal(Path.Combine(tmp.Path, "tables", "x.md"), BundleDisplayPath.Compose(tmp.Path, K("tables/x.md")));
    }

    [Fact]
    public void A_boundary_outside_the_root_refuses_every_access()
    {
        using var tmp = new TempDir();
        tmp.Write("inside/a.md", "a");
        tmp.Write("elsewhere/b.md", "b");
        var p = new FileSystemBundleProvider(Path.Combine(tmp.Path, "elsewhere"), confinementBoundary: Path.Combine(tmp.Path, "inside"));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("b.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("b.md")));
    }

    [SkippableFact]
    public void A_root_turned_into_a_junction_after_construction_is_refused_on_the_next_access_when_a_boundary_is_given()
    {
        using var external = new TempDir();
        external.Write("a.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("src/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);
        using (var before = p.Read(K("a.md")))
        {
            Assert.Equal(6, before.Length);
        }

        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a.md")));
    }

    [SkippableFact]
    public void A_symlinked_root_loads_without_a_boundary_but_not_with_one()
    {
        using var tmp = new TempDir();
        tmp.Write("target/a.md", "a");
        Skip.IfNot(tmp.TryCreateDirectorySymlink("link", "target"), "directory symlinks unavailable");
        var link = Path.Combine(tmp.Path, "link");
        var free = new FileSystemBundleProvider(link);
        Assert.Equal(["a.md"], free.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        using var obj = free.Read(K("a.md"));
        Assert.Equal(1, obj.Length);
        var bounded = new FileSystemBundleProvider(link, confinementBoundary: tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => bounded.Read(K("a.md")));
    }

    [SkippableTheory]
    [InlineData("a:b.md")]
    [InlineData("C:")]
    [InlineData("C:x")]
    [InlineData("a:b/x.md")]
    public void On_Windows_a_segment_no_name_can_carry_is_no_such_entry_not_an_exception(string keyText)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a Windows name rule: ':' makes a segment a drive or stream, which .NET rejects as a search pattern");
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        var key = K(keyText); // layer 1 forbids no colon: the key parses
        Assert.False(p.Exists(key));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(key));
        Assert.Empty(p.Enumerate(key, BundleLimits.Default));
        Assert.Equal(["a.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName)); // the real entry is unaffected
    }

    [SkippableFact]
    public void On_Windows_a_wildcard_segment_is_no_such_entry()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "no Windows name can carry '*' or '?'");
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.False(p.Exists(K("*")));
        Assert.False(p.Exists(K("?.md")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("*")));
        Assert.Empty(p.Enumerate(K("?"), BundleLimits.Default));
    }

    [SkippableFact]
    public void On_Unix_a_wildcard_in_a_name_is_read_literally()
    {
        Skip.If(OperatingSystem.IsWindows(), "a Unix name rule: Windows names cannot carry '*' or '?'");
        using var tmp = new TempDir();
        tmp.Write("*.sql", "star");
        tmp.Write("a.sql", "a");
        var p = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.CaseInsensitiveCollisions); // exact-case matching on: ResolveExact runs
        Assert.True(p.MatchesCaseExactly);
        Assert.True(p.Exists(K("*.sql")));
        using var star = p.Read(K("*.sql"));
        Assert.Equal(4, star.Length);
        var anyChar = K("?.sql"); // as a pattern it would match a.sql
        Assert.False(p.Exists(anyChar));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(anyChar));
        Assert.True(p.Exists(K("a.sql")));
    }

    [SkippableFact]
    public void A_root_that_is_an_uninspectable_junction_is_refused_strictly_under_a_boundary()
    {
        // "x/y" is a junction to `external` whose attributes the current user cannot read (deny
        // ReadAttributes on it, deny listing on "x"); the OS still lets a read traverse it. Both
        // predicates answer differently here: the lenient IsReparsePoint says "not a link", the
        // STRICT one says "refuse". Both classify an ordinary junction as a link, which is why the
        // junction tests above cannot tell them apart.
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        using var external = new TempDir();
        external.Write("a.md", "OUTSIDE-THE-BUNDLE");
        external.Write("z/b.md", "OUTSIDE-THE-BUNDLE");
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var p = new FileSystemBundleProvider(Path.Combine(tmp.Path, "x", "y"), confinementBoundary: tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("z"), BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a.md")));
    }

    [SkippableFact]
    public void A_root_under_an_uninspectable_junction_is_refused_strictly_under_a_boundary()
    {
        // The root itself ("x/y/z", a real directory seen through the junction) is readable and
        // is no link; only the ancestor between the boundary and the root is uninspectable, so
        // this pins the ancestor half of the boundary check (HasReparsePointOrUninspectableAncestor).
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        using var external = new TempDir();
        external.Write("z/b.md", "OUTSIDE-THE-BUNDLE");
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var p = new FileSystemBundleProvider(Path.Combine(tmp.Path, "x", "y", "z"), confinementBoundary: tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("b.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("b.md")));
    }

    [SkippableFact]
    public void An_uninspectable_junction_below_the_root_reads_as_absent_and_the_walk_cannot_enter_it()
    {
        // Without a boundary the root is trusted; below it, a junction whose parent denies
        // listing is never read through: a read says "not found", a question says false, a
        // prefix lists nothing — and nothing outside the bundle is returned.
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        using var external = new TempDir();
        external.Write("z/b.md", "OUTSIDE-THE-BUNDLE");
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.False(p.Exists(K("x/y/z/b.md")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("x/y/z/b.md")));
        Assert.Empty(p.Enumerate(K("x/y/z"), BundleLimits.Default));
        Assert.Empty(p.Enumerate(K("x/y"), BundleLimits.Default));
    }
}

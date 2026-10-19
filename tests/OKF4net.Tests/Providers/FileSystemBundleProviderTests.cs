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
    public void Enumerate_counts_skipped_junctions_too()
    {
        // The junction twin of the test above: mklink /J needs no privilege, so this runs on an
        // unprivileged Windows account where the symlink variant skips.
        using var external = new TempDir();
        external.Write("outside.md", "o");
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j1", external.Path) && tmp.TryCreateJunctionToExternalDir("j2", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName)); // both junctions skipped, nothing under them listed
        var ex = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 })); // 3 entries inspected: j1, j2, real.md
        Assert.Equal(BundleBound.Entries, ex.Bound);
        Assert.Single(p.Enumerate(null, new BundleLimits { MaxEntries = 3 }));
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
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("link.md")));
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
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("jump/secret.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("jump/absent.md")));
        Assert.Empty(p.Enumerate(null, BundleLimits.Default)); // the walk skips the junction entry and lists nothing under it
    }

    [SkippableFact]
    public void A_junction_two_levels_down_is_refused_at_its_own_level_and_its_sibling_still_reads()
    {
        // The resolver inspects each traversed directory once, at its own level, and the read
        // gate then inspects only the leaf: "a" passes at level 2, the junction "a/j" must be
        // refused at level 3 before it is listed — so an absent name under it is refused too,
        // never "not found" (no present/absent leak) — and "a/j" as the leaf is refused by the
        // leaf check. TempDir's disposal removes the junction as a link, never through it.
        using var external = new TempDir();
        external.Write("x.md", "OUTSIDE-THE-BUNDLE");
        using var tmp = new TempDir();
        tmp.Write("a/ok.md", "ok");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("a", "j"), external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/j/x.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/j/x.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/j/absent.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/j/absent.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(K("a/j"), BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a/j")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a/j")));
        Assert.True(p.Exists(K("a/ok.md")));
        using var ok = p.Read(K("a/ok.md"));
        Assert.Equal(2, ok.Length);
        Assert.Equal(["a/ok.md"], p.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName)); // the walk skips the junction
    }

    [SkippableFact]
    public void A_file_symlink_as_the_leaf_under_a_plain_directory_is_refused()
    {
        // The leaf half of the read gate: "d" is a plain directory, so only the leaf's own
        // inspection can refuse "d/link.md". Skips on an account without the symlink privilege
        // (Windows without Developer Mode or elevation); a directory junction as the leaf is
        // pinned, unprivileged, by the junction test above (Read/Exists of "a/j").
        using var tmp = new TempDir();
        tmp.Write("d/real.md", "real");
        Skip.IfNot(tmp.TryCreateFileSymlink(Path.Combine("d", "link.md"), Path.Combine("d", "real.md")), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("d/link.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("d/link.md")));
        Assert.True(p.Exists(K("d/real.md")));
        using var real = p.Read(K("d/real.md"));
        Assert.Equal(4, real.Length);
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
        Assert.True(p.Exists(K("References/A.sql")));
        Assert.False(p.Exists(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("References/a.sql")));
        using var exact = p.Read(K("References/A.sql"));
        Assert.Equal(8, exact.Length);
        Assert.Empty(p.Enumerate(K("references"), BundleLimits.Default));
    }

    [Fact]
    public void The_default_provider_matches_the_stored_name_exactly_on_every_OS()
    {
        // No folding flag: the exact branch must run by itself, on Linux too, where a vfat,
        // NTFS, exFAT or CIFS mount (or a case-insensitive ext4 directory) folds case although
        // the OS does not. Such a mount cannot be created here; the listing count shows the
        // exact branch ran, and Windows and macOS (case-insensitive by default) show its answer.
        using var tmp = new TempDir();
        tmp.Write("a/stored.sql", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(BundleKeyConstraintSets.ForCurrentOperatingSystem(), p.NativeConstraints);
        var before = p.DirectoryListings;
        Assert.True(p.Exists(K("a/stored.sql")));
        Assert.Equal(2, p.DirectoryListings - before); // the root, then "a": one listing per segment
        Assert.False(p.Exists(K("a/STORED.sql")));
        Assert.False(p.Exists(K("A/stored.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("a/Stored.sql")));
        Assert.Empty(p.Enumerate(K("A"), BundleLimits.Default));
    }

    [SkippableFact]
    public void On_a_case_sensitive_volume_names_differing_only_by_case_each_resolve_to_themselves()
    {
        using var tmp = new TempDir();
        tmp.Write("a.sql", "lower");
        tmp.Write("A.sql", "UPPER!");
        Skip.If(Directory.GetFiles(tmp.Path).Length != 2, "needs a case-sensitive volume (Linux)");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var lower = p.Read(K("a.sql"));
        using var upper = p.Read(K("A.sql"));
        Assert.Equal(5, lower.Length);
        Assert.Equal(6, upper.Length);
        Assert.False(p.Exists(K("a.SQL")));
    }

    [Fact]
    public void Constraints_can_be_added_never_removed()
    {
        using var tmp = new TempDir();
        tmp.Write("a/UP.sql", "x");
        var p = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.None);
        Assert.Equal(BundleKeyConstraintSets.ForCurrentOperatingSystem(), p.NativeConstraints);
        var folding = new FileSystemBundleProvider(tmp.Path, additionalConstraints: BundleKeyConstraints.CaseInsensitiveCollisions);
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

    /// <summary>
    /// The lexical resolver leaves a drive-shaped first segment to the key policy, so on a real
    /// Windows run every drive form must still stay inside the root: the native set refuses
    /// the colon (<c>Unsafe</c>), and the provider, asked directly, answers "no such entry"
    /// without combining the segment into a path — even when the value names, through the
    /// bundle's own drive, a file that exists outside the root.
    /// </summary>
    [SkippableFact]
    public void On_Windows_a_drive_form_never_leaves_the_root_and_never_throws()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a Windows path rule: elsewhere a colon is a name character");
        using var tmp = new TempDir();
        tmp.Write("outside.sql", "secret");
        tmp.Write("b/computations/rev.md", "---\ntype: Attested Computation\n---\n");
        tmp.Write("b/x.sql", "inside");
        var root = Path.Combine(tmp.Path, "b");
        var outside = Path.Combine(tmp.Path, "outside.sql");
        Skip.IfNot(outside.Length > 2 && outside[1] == ':', "the temp directory is not on a drive letter");
        var drive = outside[..2];                          // C:
        var driveAbsolute = outside.Replace('\\', '/');   // C:/…/outside.sql, a file that exists
        var bundle = Bundle.Load(root);
        var concept = bundle.Get(ConceptId.Parse("computations/rev"))!;
        var provider = new FileSystemBundleProvider(root);

        foreach (var raw in new[] { driveAbsolute, "/" + driveAbsolute, drive + "x.sql", drive, drive + "/", "./" + drive + "x.sql", "C:/Windows/win.ini", "C:/x" })
        {
            Assert.True(bundle.TryResolveResourceKey(concept, raw, out var key, out var status));
            Assert.Equal(ResourceResolutionStatus.Unsafe, status);
            Assert.Null(key);
        }

        foreach (var keyText in new[] { driveAbsolute, drive + "x.sql", "C:/x" })
        {
            var key = K(keyText); // layer 1 forbids no colon: the key parses
            Assert.False(provider.Exists(key));
            Assert.Throws<BundleObjectNotFoundException>(() => provider.Read(key));
            Assert.Throws<UnauthorizedAccessException>(() => bundle.ReadResourceText(key));
        }
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
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.Exists(K("*.sql")));
        using var star = p.Read(K("*.sql"));
        Assert.Equal(4, star.Length);
        var anyChar = K("?.sql"); // as a pattern it would match a.sql
        Assert.False(p.Exists(anyChar));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(anyChar));
        Assert.True(p.Exists(K("a.sql")));
    }

    [SkippableTheory]
    [InlineData("a\"b.sql")]
    [InlineData("x<y.sql")]
    [InlineData("x>y.sql")]
    [InlineData("name.")]
    [InlineData("d\"<>./f.sql")]
    public void On_Unix_a_name_carrying_a_DOS_wildcard_or_a_trailing_dot_resolves_exactly(string name)
    {
        // '"', '<' and '>' are DOS wildcards under MatchType.Win32 and a trailing '.' is special
        // in that syntax: each such name must still resolve to itself, and only to itself.
        Skip.If(OperatingSystem.IsWindows(), "a Unix name rule: Windows names cannot carry these");
        using var tmp = new TempDir();
        tmp.Write(name, "exact");
        tmp.Write("decoy.sql", "decoy");
        var p = new FileSystemBundleProvider(tmp.Path);
        var key = K(name);
        Assert.True(p.Exists(key));
        using var obj = p.Read(key);
        Assert.Equal(5, obj.Length);
        Assert.Equal([name], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName).Where(n => n != "decoy.sql"));
        if (name.Contains('/'))
        {
            Assert.Equal([name], p.Enumerate(K(name[..name.IndexOf('/')]), BundleLimits.Default).Select(l => l.RawName));
        }
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
        // prefix lists nothing, the whole walk cannot list the parent and aborts — and nothing
        // outside the bundle is returned.
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
        // The whole-bundle walk reaches "x", a plain directory, and is denied its listing: the
        // OS error ends the walk before the junction is ever seen, and the load aborts on it.
        Assert.Throws<UnauthorizedAccessException>(() => p.Enumerate(null, BundleLimits.Default));
        var load = Assert.Throws<BundleLoadException>(() => Bundle.Load(tmp.Path));
        Assert.IsType<UnauthorizedAccessException>(load.InnerException);
    }

    /// <summary>
    /// The walk inspects each directory once, on its own, before listing it (its ancestors
    /// were inspected when the same walk entered them). A junction met as an entry is skipped
    /// at whatever depth it sits, and nothing under it is listed.
    /// </summary>
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void A_junction_is_skipped_by_the_walk_at_any_depth(int depth)
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        external.Write("secret.md", "OUTSIDE-THE-BUNDLE");
        var parent = string.Join('/', Enumerable.Repeat("d", depth - 1));
        var prefix = parent.Length == 0 ? string.Empty : parent + "/";
        tmp.Write(prefix + "real.md", "r");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir((prefix + "j").Replace('/', Path.DirectorySeparatorChar), external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal([prefix + "real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        _ = p.GetVersion(BundleLimits.Default); // the version walk skips it too, without refusing
        if (depth > 1)
        {
            Assert.Equal(["real.md"], p.Enumerate(K(parent), BundleLimits.Default).Select(l => l.RawName[(parent.Length + 1)..]));
        }
    }

    /// <summary>
    /// A directory swapped for a junction after the walk examined it as an entry of its parent,
    /// and before the walk enters it, is refused by the walk's own check on that directory —
    /// at depth 3, below two directories the walk entered before. The swap stands in for
    /// another process acting in that window (<c>BeforeWalkEntersDirectory</c>). With
    /// <paramref name="uninspectable"/>, the junction also denies reading its attributes and its
    /// parent denies listing, so only the STRICT predicate refuses it: a lenient check would
    /// list the junction's target, outside the bundle.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_directory_swapped_for_a_junction_before_the_walk_enters_it_is_refused(bool uninspectable)
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        external.Write("secret.md", "OUTSIDE-THE-BUNDLE");
        tmp.Write("a.md", "a");
        tmp.Write("x/y/z/in.md", "in");
        var target = Path.Combine(tmp.Path, "x", "y", "z");
        var p = new FileSystemBundleProvider(tmp.Path);
        UninspectableJunction? handle = null;
        var swapped = false;
        p.BeforeWalkEntersDirectory = full =>
        {
            if (swapped || !string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(Path.Combine(full, "in.md"));
            Directory.Delete(full);
            var link = Path.Combine("x", "y", "z");
            if (uninspectable)
            {
                handle = tmp.TryCreateUninspectableJunction(link, external.Path);
                swapped = handle is not null;
            }
            else
            {
                swapped = tmp.TryCreateJunctionToExternalDir(link, external.Path);
            }
        };

        try
        {
            var thrown = Record.Exception(() => p.Enumerate(null, BundleLimits.Default));
            Skip.IfNot(swapped, uninspectable ? "needs Windows (a junction plus deny ACEs)" : "junctions unavailable");
            Assert.IsType<BundleAccessRefusedException>(thrown);
            Assert.DoesNotContain("OUTSIDE", thrown.Message);
        }
        finally
        {
            handle?.Dispose();
        }
    }
}

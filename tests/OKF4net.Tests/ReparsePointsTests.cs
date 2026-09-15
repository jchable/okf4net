// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// Pins <see cref="ReparsePoints.IsWithin"/>'s comparison-sensitivity: the
/// method itself is platform-independent (it just compares two strings with
/// whatever <see cref="StringComparison"/> the caller supplies, using
/// <see cref="Path.DirectorySeparatorChar"/> to build the prefix it checks
/// against), so this test does not need any actual filesystem/platform-
/// specific behavior to exercise the F1 finding -- it only needs to show
/// that <c>Ordinal</c> correctly rejects a case-variant "escape" that
/// <c>OrdinalIgnoreCase</c> would wrongly accept. Paths are built with
/// <see cref="Path.DirectorySeparatorChar"/> (rather than a hardcoded '/')
/// so the test exercises the exact separator <see cref="ReparsePoints.IsWithin"/>
/// itself uses on every platform, not just Unix-style ones. On a real
/// case-sensitive filesystem (Linux, the CI/container target), a root and
/// its uppercase spelling are two different directories, so a path resolving
/// through the case-variant must NOT be treated as contained within the
/// original root.
/// </summary>
public class ReparsePointsTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    [Fact]
    public void IsWithin_ordinal_rejects_case_variant_of_root_as_escape()
    {
        var root = $"{Sep}srv{Sep}kb";
        var caseVariantChild = $"{Sep}srv{Sep}KB{Sep}x";

        Assert.False(ReparsePoints.IsWithin(root, caseVariantChild, StringComparison.Ordinal));
    }

    [Fact]
    public void IsWithin_ordinal_ignore_case_wrongly_accepts_case_variant_of_root()
    {
        // Documents exactly the unsafe behavior F1 is about: OrdinalIgnoreCase
        // treats a case-variant path as "within root" even though, on a
        // case-sensitive filesystem, it resolves to a completely different
        // directory. This is why untrusted-input callers (CatalogPathResolver)
        // must not use OrdinalIgnoreCase on such platforms.
        var root = $"{Sep}srv{Sep}kb";
        var caseVariantChild = $"{Sep}srv{Sep}KB{Sep}x";

        Assert.True(ReparsePoints.IsWithin(root, caseVariantChild, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsWithin_ordinal_still_accepts_exact_case_match()
    {
        var root = $"{Sep}srv{Sep}kb";
        var child = $"{Sep}srv{Sep}kb{Sep}x";

        Assert.True(ReparsePoints.IsWithin(root, child, StringComparison.Ordinal));
    }

    /// <summary>
    /// Pins the exact comparison boundary <see cref="OKF4net.Bundle"/>'s
    /// <c>PathComparison</c> field relies on for its §6.2 containment check
    /// (P1 finding): a bundle rooted at ".../Bundle" must not treat the
    /// sibling directory ".../bundle" as contained within it under
    /// <see cref="StringComparison.Ordinal"/> -- the comparison
    /// <see cref="OKF4net.Bundle.TryResolveResource"/> now uses on Linux --
    /// while an exact-case descendant of the same root must still be
    /// accepted. This test runs on any OS: <see cref="ReparsePoints.IsWithin"/>
    /// itself is a pure string comparison, independent of the actual
    /// filesystem's case sensitivity.
    /// </summary>
    [Fact]
    public void IsWithin_ordinal_rejects_Bundle_bundle_case_variant_but_accepts_exact_case_descendant()
    {
        var root = $"{Sep}tmp{Sep}Bundle";
        var caseVariantSibling = $"{Sep}tmp{Sep}bundle{Sep}secret";
        var exactCaseDescendant = $"{Sep}tmp{Sep}Bundle{Sep}secret";

        Assert.False(ReparsePoints.IsWithin(root, caseVariantSibling, StringComparison.Ordinal));
        Assert.True(ReparsePoints.IsWithin(root, exactCaseDescendant, StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression for the false-positive that broke <c>BundleConceptWriter</c>
    /// on macOS CI: <c>Path.GetFullPath</c> preserves a trailing separator if
    /// present, but <see cref="ReparsePoints.HasReparsePointAncestor(string, string, StringComparison)"/>'s
    /// walk stops via exact string equality against an ancestor produced by
    /// <see cref="Path.GetDirectoryName(string)"/>, which never carries one --
    /// an untrimmed root with a trailing separator therefore never matches,
    /// overshooting the walk past the intended root into whatever real
    /// filesystem sits above it. Reproduced portably (no dependency on
    /// macOS's <c>/var</c> symlink) by planting a junction/symlink strictly
    /// ABOVE the nominal root: without the bug, that ancestor is out of the
    /// walk's scope and must never be inspected; with it, the untrimmed-root
    /// call overshoots into it and wrongly reports a reparse point.
    /// </summary>
    [SkippableFact]
    public void HasReparsePointAncestor_gives_the_same_result_whether_or_not_the_root_has_a_trailing_separator()
    {
        using var outer = new TempDir();
        using var external = new TempDir();

        Skip.IfNot(outer.TryCreateJunctionToExternalDir("linked", external.Path), "no junction/symlink privilege on this machine");

        var root = Path.Combine(outer.Path, "linked", "bundle");
        Directory.CreateDirectory(root);
        var nested = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(nested);

        var withoutTrailingSeparator = ReparsePoints.HasReparsePointAncestor(root, nested);
        var withTrailingSeparator = ReparsePoints.HasReparsePointAncestor(root + Sep, nested);

        Assert.False(withoutTrailingSeparator);
        Assert.False(withTrailingSeparator);
    }

    /// <summary>Same regression as <see cref="HasReparsePointAncestor_gives_the_same_result_whether_or_not_the_root_has_a_trailing_separator"/>, for the sibling <see cref="ReparsePoints.IsWithinBundleRoot"/> convenience overload.</summary>
    [Fact]
    public void IsWithinBundleRoot_gives_the_same_result_whether_or_not_the_root_has_a_trailing_separator()
    {
        using var tmp = new TempDir();
        var root = tmp.Path;
        var nested = Path.Combine(root, "a", "b.md");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);

        Assert.True(ReparsePoints.IsWithinBundleRoot(root, nested));
        Assert.True(ReparsePoints.IsWithinBundleRoot(root + Sep, nested));
    }

    [Fact]
    public void IsWithinBundleRoot_ordinal_rejects_case_variant_sibling_but_accepts_exact_case_descendant()
    {
        var root = $"{Sep}tmp{Sep}Bundle";
        var caseVariantSibling = $"{Sep}tmp{Sep}bundle{Sep}secret.md";
        var exactCaseDescendant = $"{Sep}tmp{Sep}Bundle{Sep}secret.md";

        Assert.False(ReparsePoints.IsWithinBundleRoot(root, caseVariantSibling));
        Assert.True(ReparsePoints.IsWithinBundleRoot(root, exactCaseDescendant));
    }

    /// <summary>
    /// Pins <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>'s
    /// own root-comparison, independent of how today's callers happen to use
    /// it: a <c>root</c> argument that is a CASE-VARIANT of a real ancestor
    /// directory must not be treated as "root reached" -- the walk must keep
    /// going past it, so a genuine reparse point further up is still found.
    /// Constructed with a junction ABOVE the case-variant point (rather than
    /// relying on any specific caller's containment check running first) so
    /// this test exercises the helper's own contract in isolation, not a
    /// scenario that depends on other code.
    /// </summary>
    [SkippableFact]
    public void HasReparsePointAncestor_two_arg_ordinal_does_not_stop_early_on_a_case_variant_root()
    {
        using var outer = new TempDir();
        using var external = new TempDir();

        Skip.IfNot(outer.TryCreateJunctionToExternalDir("Linked", external.Path), "no junction/symlink privilege on this machine");

        var trueRoot = Path.Combine(outer.Path, "Linked", "Bundle");
        Directory.CreateDirectory(trueRoot);
        var nested = Path.Combine(trueRoot, "a");
        Directory.CreateDirectory(nested);
        var caseVariantRoot = Path.Combine(outer.Path, "Linked", "bundle");

        Assert.True(ReparsePoints.HasReparsePointAncestor(caseVariantRoot, nested));
    }

    /// <summary>
    /// Moved here from <c>OKF4net.Viewer</c>'s <c>HtmlWriter</c> (Task D3),
    /// which owned <see cref="ReparsePoints.ResolveThroughReparsePoints"/> as
    /// a private copy previously exercised only indirectly, through
    /// <c>HtmlWriterTests.Write_refuses_an_out_dir_that_is_a_junction_resolving_inside_the_bundle</c>.
    /// The common case -- no reparse point anywhere in the ancestry -- needs
    /// no filesystem privilege to exercise, so it runs unconditionally.
    /// </summary>
    [Fact]
    public void ResolveThroughReparsePoints_returns_the_path_unchanged_when_no_ancestor_is_a_reparse_point()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "a", "b.txt");

        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(path, out var resolved));

        Assert.Equal(path, resolved);
    }

    /// <summary>
    /// The reparse-point case: a path reached only through a junction
    /// resolves to the junction's real target, with the trailing segments
    /// past the junction re-attached.
    /// </summary>
    [SkippableFact]
    public void ResolveThroughReparsePoints_follows_a_junction_and_reattaches_the_trailing_segments()
    {
        using var linkHost = new TempDir();
        using var target = new TempDir();

        Skip.IfNot(linkHost.TryCreateJunctionToExternalDir("link", target.Path), "no junction/symlink privilege on this machine");

        var path = Path.Combine(linkHost.Path, "link", "nested", "file.txt");

        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(path, out var resolved));

        Assert.Equal(Path.Combine(target.Path, "nested", "file.txt"), resolved);
    }

    // ----------------------------------------------------------------
    // Task H1: the strict predicate for guards. A guard fails closed on an
    // entry whose link status cannot be inspected; a walk (the lenient
    // IsReparsePoint) fails open. Both must answer "not a link" for an entry
    // that does not exist, or no guard could allow a new file.
    // ----------------------------------------------------------------

    [Fact]
    public void Strict_predicate_is_false_for_a_missing_leaf()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "not-yet.md");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(path));
        Assert.False(ReparsePoints.IsReparsePoint(path));
    }

    [Fact]
    public void Strict_predicate_is_false_for_a_missing_parent()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "no-such-dir", "deeper", "not-yet.md");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(path));
        Assert.False(ReparsePoints.IsReparsePoint(path));
    }

    [Fact]
    public void Strict_predicate_is_false_below_a_parent_that_is_a_regular_file()
    {
        // Measured: DirectoryNotFoundException on Windows and Linux alike, so
        // a guard reports the write's own failure, not a refusal.
        using var tmp = new TempDir();
        var file = tmp.Write("plain.md", "x");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(Path.Combine(file, "child")));
    }

    [Fact]
    public void Strict_predicate_is_false_for_a_plain_file_and_a_plain_directory()
    {
        using var tmp = new TempDir();
        var file = tmp.Write(Path.Combine("dir", "plain.md"), "x");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(file));
        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(Path.GetDirectoryName(file)!));
    }

    [SkippableFact]
    public void Strict_predicate_is_true_for_a_junction_or_symlink()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("link", external.Path), "no junction/symlink privilege on this machine");

        var link = Path.Combine(tmp.Path, "link");

        try
        {
            Assert.True(ReparsePoints.IsReparsePointOrUninspectable(link));
            Assert.True(ReparsePoints.IsReparsePoint(link));
        }
        finally
        {
            // Removed explicitly: TempDir.Dispose's recursive delete does not
            // reliably remove a junction on Windows, and would leave the temp
            // directory behind.
            Directory.Delete(link);
        }
    }

    [SkippableFact]
    public void Strict_predicate_is_true_for_an_entry_whose_attributes_cannot_be_read_where_the_lenient_one_is_false()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        Assert.True(ReparsePoints.IsReparsePointOrUninspectable(junction!.LinkPath));
        Assert.False(ReparsePoints.IsReparsePoint(junction.LinkPath));
    }

    [SkippableFact]
    public void Strict_ancestor_walk_refuses_an_uninspectable_ancestor_but_not_the_missing_entries_below_it()
    {
        // "x/y" cannot be inspected; "z" and "file.md" below it do not exist.
        // The strict walk passes the two missing entries and stops at "x/y";
        // the lenient walk passes all of them.
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var path = Path.Combine(junction!.LinkPath, "z", "file.md");

        Assert.True(ReparsePoints.HasReparsePointOrUninspectableAncestor(tmp.Path, path));
        Assert.True(ReparsePoints.HasReparsePointOrUninspectableAncestor(tmp.Path, path, StringComparison.Ordinal));
        Assert.False(ReparsePoints.HasReparsePointAncestor(tmp.Path, path));
    }

    [Fact]
    public void Strict_ancestor_walk_allows_a_path_of_missing_directories_under_a_plain_root()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "brand", "new", "deep", "file.md");

        Assert.False(ReparsePoints.HasReparsePointOrUninspectableAncestor(tmp.Path, path));
    }

    [SkippableFact]
    public void TryResolveThroughReparsePoints_fails_when_an_entry_on_the_path_cannot_be_inspected()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var path = Path.Combine(junction!.LinkPath, "site");

        Assert.False(ReparsePoints.TryResolveThroughReparsePoints(path, out var resolved));
        Assert.Equal(path, resolved);
    }

    [SkippableFact]
    public void TryResolveThroughReparsePoints_fails_rather_than_throws_when_a_link_target_cannot_be_read()
    {
        // H1 fix round (M2): the junction's attributes are readable (its parent
        // is listable), so it is classified as a reparse point, but
        // Directory.ResolveLinkTarget on it throws UnauthorizedAccessException.
        // A Try method must answer false, not throw.
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path, denyParentListing: false);
        Skip.If(junction is null, "needs Windows (a junction plus a deny ACE)");
        var path = Path.Combine(junction!.LinkPath, "site");

        Assert.False(ReparsePoints.TryResolveThroughReparsePoints(path, out var resolved));
        Assert.Equal(path, resolved);
    }

    [SkippableFact]
    public void Strict_predicate_is_false_for_a_share_that_does_not_exist()
    {
        // H1 fix round (I1): Windows reports a missing share as a plain
        // IOException (ERROR_BAD_NET_NAME / ERROR_BAD_NETPATH), not a
        // not-found type. It is absence, not an entry anyone could redirect.
        //
        // The case is probed first: only a host that really answers with one
        // of those codes produces it, and another host skips (naming what it
        // answered) instead of failing on its network setup. Once the case is
        // produced, a wrong mapping fails.
        Skip.IfNot(OperatingSystem.IsWindows(), "a UNC share path is Windows-only");
        Assert.StartsWith(new string(Path.DirectorySeparatorChar, 2), AbsentPaths.MissingShareSite, StringComparison.Ordinal); // a UNC path, never a drive-rooted one
        Skip.IfNot(
            AbsentPaths.HostReportsAbsenceAsPlainIOException(AbsentPaths.MissingShareSite, out var observed),
            $"this host answers a missing share with {observed}, not ERROR_BAD_NET_NAME/ERROR_BAD_NETPATH on a plain IOException");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(AbsentPaths.MissingShareSite));
        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(AbsentPaths.MissingShareSite, out var resolved));
        Assert.Equal(AbsentPaths.MissingShareSite, resolved);
    }

    [SkippableFact]
    public void Strict_predicate_is_false_for_a_drive_that_holds_no_volume()
    {
        // H1 fix round (I1): an empty card reader or optical drive answers
        // ERROR_NOT_READY, a plain IOException. Absence, not uninspectable.
        // Probed first, like the share test above.
        var site = AbsentPaths.SiteOnADriveWithNoVolume();
        Skip.If(site is null, "no drive without a volume on this machine");
        Skip.IfNot(
            AbsentPaths.HostReportsAbsenceAsPlainIOException(site!, out var observed),
            $"this host answers the empty drive {site} with {observed}, not ERROR_NOT_READY on a plain IOException");

        Assert.False(ReparsePoints.IsReparsePointOrUninspectable(site!));
        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(site!, out _));
    }
}

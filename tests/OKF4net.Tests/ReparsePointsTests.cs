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
    [Fact]
    public void HasReparsePointAncestor_gives_the_same_result_whether_or_not_the_root_has_a_trailing_separator()
    {
        using var outer = new TempDir();
        using var external = new TempDir();

        if (!outer.TryCreateJunctionToExternalDir("linked", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

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
}

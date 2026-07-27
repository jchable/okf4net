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
}

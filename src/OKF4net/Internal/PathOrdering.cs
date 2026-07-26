// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Compares two absolute file paths component-by-component (splitting on
/// <c>\</c> and <c>/</c>), ordinal per segment, with a shorter segment list
/// sorting first when one path is a prefix of the other. Mirrors Rust's
/// <c>PathBuf</c>'s derived <c>Ord</c> (which compares via the
/// <c>Component</c> iterator, not raw bytes) — used for every deterministic
/// file-tree ordering in this port (<c>Bundle.Load</c>'s
/// <c>md_files.sort()</c> and <c>IndexGenerator</c>'s directory/child
/// orderings).
///
/// A flat ordinal string comparison of full paths is NOT equivalent: on
/// Windows, <c>'.'</c> (0x2E) sorts before <c>'\'</c> (0x5C), so a raw string
/// sort would place <c>orders.md</c> before <c>orders\extra.md</c> even
/// though the directory <c>orders</c> should sort before the sibling file
/// <c>orders.md</c> — inverting the DFS walk order the collectors already
/// produce.
///
/// Consolidated from the two previously byte-identical private copies in
/// <c>Bundle</c> and <c>IndexGenerator</c>.
/// </summary>
internal static class PathOrdering
{
    /// <summary>Compares <paramref name="a"/> and <paramref name="b"/> component-wise; see the type doc comment.</summary>
    internal static int CompareComponentWise(string a, string b)
    {
        var segmentsA = a.Split('\\', '/');
        var segmentsB = b.Split('\\', '/');
        var n = Math.Min(segmentsA.Length, segmentsB.Length);
        for (var i = 0; i < n; i++)
        {
            var cmp = string.CompareOrdinal(segmentsA[i], segmentsB[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return segmentsA.Length.CompareTo(segmentsB.Length);
    }
}

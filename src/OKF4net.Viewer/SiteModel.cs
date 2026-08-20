// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>
/// Projects a loaded <see cref="Bundle"/> into the display model the viewer
/// renders. Pure: performs no I/O, so it is fully testable without touching
/// the filesystem.
/// </summary>
public static class SiteModel
{
    /// <summary>
    /// The href of <paramref name="to"/>'s generated page, relative to
    /// <paramref name="from"/>'s. Always <c>/</c>-separated and suffixed
    /// <c>.html</c>, so the generated site is navigable straight off the
    /// filesystem (<c>file://</c>) at any nesting depth.
    /// </summary>
    /// <param name="from">The concept whose page contains the link.</param>
    /// <param name="to">The concept being linked to.</param>
    public static string RelativeHref(ConceptId from, ConceptId to)
    {
        // Only the directory part of `from` matters: a page at a/b/c.html
        // sits in directory a/b, so it is 2 levels deep.
        var fromDir = from.Segments.Take(from.Segments.Count - 1).ToList();
        var toPath = to.Segments;

        var common = 0;
        while (common < fromDir.Count
               && common < toPath.Count - 1
               && string.Equals(fromDir[common], toPath[common], StringComparison.Ordinal))
        {
            common++;
        }

        var up = Enumerable.Repeat("..", fromDir.Count - common);
        var down = toPath.Skip(common);
        return string.Join('/', up.Concat(down)) + ".html";
    }
}

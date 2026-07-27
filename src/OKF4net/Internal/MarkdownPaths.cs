// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Filename predicate for a real <c>.md</c> extension, shared by
/// <see cref="OKF4net.Bundle"/>'s and <see cref="OKF4net.IndexGenerator"/>'s
/// markdown-collecting sites.
///
/// A file named EXACTLY <c>.md</c> must NOT count: it is a dotfile (like
/// <c>.gitignore</c>), not a <c>"stem.ext"</c> split. Neither .NET's
/// <see cref="Path.GetExtension(string)"/> nor a naive <c>EndsWith(".md")</c>
/// check gets this right on its own (<c>Path.GetExtension(".md")</c> returns
/// <c>".md"</c>, not <c>""</c>), so the leading-dot-only name is excluded
/// explicitly below.
/// </summary>
internal static class MarkdownPaths
{
    /// <summary>True if <paramref name="path"/>'s file name has a real (non-dotfile) <c>.md</c> extension.</summary>
    internal static bool HasMarkdownExtension(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".md", StringComparison.Ordinal) && name != ".md";
    }
}

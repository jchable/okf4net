// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Filename predicate mirroring Rust's
/// <c>path.extension() == Some("md")</c> (bundle.rs:216, index.rs:130 and
/// 229), shared by <see cref="OKF4net.Bundle"/>'s and
/// <see cref="OKF4net.IndexGenerator"/>'s markdown-collecting sites.
///
/// Neither .NET's <see cref="Path.GetExtension"/> nor a naive
/// <c>EndsWith(".md")</c> check matches Rust here: both treat a file named
/// EXACTLY <c>.md</c> as having the extension <c>.md</c>
/// (<c>Path.GetExtension(".md")</c> returns <c>".md"</c>, not <c>""</c>).
/// Rust's <c>Path::extension()</c> instead treats a leading-dot-only file
/// name as having NO extension — it is a dotfile (like <c>.gitignore</c>),
/// not a <c>"stem.ext"</c> split — so <c>.md</c> alone must be excluded.
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

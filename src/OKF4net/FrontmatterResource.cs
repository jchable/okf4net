// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace OKF4net;

/// <summary>
/// How a §6.2 path-valued frontmatter field's raw string is shaped, which
/// determines how <see cref="Bundle.TryResolveResource"/> resolves it.
/// </summary>
public enum FrontmatterResourceKind
{
    /// <summary>An absolute URL (<c>scheme://...</c>), e.g. <c>https://example.com/x</c>. Never resolved to a local path.</summary>
    Url,

    /// <summary>A path rooted at the bundle root, starting with <c>/</c> or <c>\</c>, e.g. <c>/skills/run.md</c>.</summary>
    BundleRelative,

    /// <summary>A path relative to the concept's own directory, e.g. <c>./policy.md</c> or <c>../refs/revenue.sql</c>.</summary>
    Relative,
}

/// <summary>The outcome of resolving a §6.2 path-valued frontmatter field to a filesystem path via <see cref="Bundle.TryResolveResource"/>.</summary>
public enum ResourceResolutionStatus
{
    /// <summary>The raw value is a URL; it was never resolved to a local path (<c>absolutePath</c> is <c>null</c>).</summary>
    Url,

    /// <summary>The resolved path is within the bundle root and the file exists.</summary>
    Resolved,

    /// <summary>The resolved path is within the bundle root but the file does not exist.</summary>
    Missing,

    /// <summary>
    /// The resolved path would escape the bundle root, or the path (or one of
    /// its ancestor directories) is a filesystem reparse point (symlink,
    /// junction, mount point).
    /// </summary>
    Unsafe,
}

/// <summary>
/// One path-valued frontmatter field (§6.2), as enumerated by
/// <see cref="OkfDocument.FrontmatterResources"/>.
/// </summary>
/// <param name="Field">
/// The field's dotted/indexed label, e.g. <c>resource</c>, <c>sources[0].resource</c>,
/// <c>computation</c>, <c>executor.resource</c>, or <c>attester.resource</c>.
/// </param>
/// <param name="RawPath">The field's raw string value, exactly as written in the frontmatter.</param>
/// <param name="Kind">How <paramref name="RawPath"/> is shaped (§6.2).</param>
public readonly record struct FrontmatterResource(string Field, string RawPath, FrontmatterResourceKind Kind);

/// <summary>Classifies §6.2 path-valued frontmatter values.</summary>
internal static class FrontmatterResourceClassifier
{
    // scheme := ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) followed by "://" (RFC 3986 §3.1).
    private static readonly Regex UrlScheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.Compiled);

    /// <summary>Classifies a raw frontmatter path/URL value per §6.2: <see cref="FrontmatterResourceKind.Url"/> for a <c>scheme://</c> value, <see cref="FrontmatterResourceKind.BundleRelative"/> for a leading <c>/</c> or <c>\</c>, otherwise <see cref="FrontmatterResourceKind.Relative"/>.</summary>
    internal static FrontmatterResourceKind KindOf(string rawPath)
    {
        if (UrlScheme.IsMatch(rawPath))
        {
            return FrontmatterResourceKind.Url;
        }

        if (rawPath.Length > 0 && (rawPath[0] == '/' || rawPath[0] == '\\'))
        {
            return FrontmatterResourceKind.BundleRelative;
        }

        return FrontmatterResourceKind.Relative;
    }
}

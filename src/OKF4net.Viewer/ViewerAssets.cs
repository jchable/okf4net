// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>
/// The viewer's static assets, embedded in the assembly so the Native AOT
/// <c>okf-render</c> binary (<c>OKF4net.Render</c>) stays self-contained (no
/// files to ship alongside it). <c>okf</c> itself does not reference this
/// assembly at all -- static-site generation was split into its own binary
/// so the CI-facing validator does not carry this vendored viewer
/// JavaScript, which it never executes.
/// </summary>
/// <remarks>
/// Every file under <c>Assets/</c> (except <c>fonts/README.md</c>) is embedded
/// with its path below <c>Assets/</c> as its resource name, and
/// <c>HtmlWriter</c> writes every one of them (spec §12.0): a script, font or
/// licence text added there needs no change here.
/// </remarks>
public static class ViewerAssets
{
    private const string Prefix = "Assets/";

    /// <summary>The generated site's stylesheet.</summary>
    public static string Css { get; } = Text("viewer.css");

    /// <summary>The vendored marked bundle (MIT) used for client-side markdown rendering.</summary>
    public static string MarkedJs { get; } = Text("marked.min.js");

    /// <summary>The client bootstrap that renders a page's payload and rewires its links.</summary>
    public static string ViewerJs { get; } = Text("viewer.js");

    /// <summary>Applies the stored theme before first paint and adds the theme toggle (loaded in <c>&lt;head&gt;</c>).</summary>
    internal static string ThemeJs { get; } = Text("okf-theme.js");

    /// <summary>Side-effect-free helpers shared by the interactive scripts (<c>window.OkfSite</c>).</summary>
    internal static string SiteJs { get; } = Text("okf-site.js");

    /// <summary>The tree explorer.</summary>
    internal static string ExplorerJs { get; } = Text("okf-explorer.js");

    /// <summary>The "Jump to" palette.</summary>
    internal static string PaletteJs { get; } = Text("okf-palette.js");

    /// <summary>Heading anchors, the contents list and fragment resolution.</summary>
    internal static string TocJs { get; } = Text("okf-toc.js");

    /// <summary>
    /// Every embedded asset's path relative to <c>Assets/</c>, <c>/</c>-separated,
    /// in ordinal order -- the order <c>HtmlWriter</c> writes them in.
    /// </summary>
    internal static IReadOnlyList<string> Paths { get; } = typeof(ViewerAssets).Assembly
        .GetManifestResourceNames()
        .Select(Normalize)
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(name => name[Prefix.Length..])
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    /// <summary>An embedded text asset, by its path below <c>Assets/</c>.</summary>
    /// <param name="path">The asset's <c>/</c>-separated path, e.g. <c>viewer.css</c>.</param>
    internal static string Text(string path)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>An embedded asset's bytes, by its path below <c>Assets/</c>.</summary>
    /// <param name="path">The asset's <c>/</c>-separated path, e.g. <c>fonts/inter-latin.woff2</c>.</param>
    internal static byte[] Bytes(string path)
    {
        using var stream = Open(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Stream Open(string path)
    {
        var assembly = typeof(ViewerAssets).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (string.Equals(Normalize(name), Prefix + path, StringComparison.Ordinal))
            {
                return assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"embedded asset not readable: {path}");
            }
        }

        throw new InvalidOperationException($"embedded asset not found: {path}");
    }

    private static string Normalize(string name) => name.Replace('\\', '/');
}

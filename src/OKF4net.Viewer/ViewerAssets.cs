// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;

namespace OKF4net.Viewer;

/// <summary>
/// The viewer's static assets, embedded in the assembly so the Native AOT
/// <c>okf</c> binary stays self-contained (no files to ship alongside it).
/// </summary>
public static class ViewerAssets
{
    /// <summary>The generated site's stylesheet.</summary>
    public static string Css { get; } = Read("viewer.css");

    /// <summary>The vendored marked bundle (MIT) used for client-side markdown rendering.</summary>
    public static string MarkedJs { get; } = Read("marked.min.js");

    /// <summary>The client bootstrap that renders a page's payload and rewires its links.</summary>
    public static string ViewerJs { get; } = Read("viewer.js");

    private static string Read(string name)
    {
        var assembly = typeof(ViewerAssets).Assembly;
        var resource = $"OKF4net.Viewer.Assets.{name}";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded asset not found: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

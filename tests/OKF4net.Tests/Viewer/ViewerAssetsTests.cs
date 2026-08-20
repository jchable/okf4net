// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests that the viewer's embedded assets are present and carry the
/// guarantees the generated pages depend on.
/// </summary>
public class ViewerAssetsTests
{
    [Fact]
    public void Css_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.Css));

    [Fact]
    public void MarkedJs_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.MarkedJs));

    [Fact]
    public void MarkedJs_retains_its_MIT_copyright_banner()
        => Assert.Contains("marked", ViewerAssets.MarkedJs, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ViewerJs_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.ViewerJs));

    [Fact]
    public void ViewerJs_disables_raw_html_passthrough()
    {
        // marked renders raw HTML by default and has no `sanitize` option any
        // more, so suppression happens via the renderer's html hooks. If this
        // override is ever dropped, a concept body can inject script into the
        // generated page (design spec §8.2).
        Assert.Contains("html:", ViewerAssets.ViewerJs);
        Assert.Contains("renderer", ViewerAssets.ViewerJs);
    }

    [Fact]
    public void ViewerJs_also_disables_raw_html_via_the_text_renderer()
    {
        // The vendored build (marked v15.0.12) routes image/title alt-text
        // through a *separate* TextRenderer instance that marked.use({
        // renderer: ... }) does not touch. Left unpatched, a concept body
        // like `![<a href="x" onclick="...">t</a>](img.png)` still injects
        // raw markup into the generated page's alt attribute even with the
        // primary renderer.html override in place (verified against the
        // vendored build: this repo's audit for Task 7 reproduced the
        // injection with only the primary override applied). Guard against
        // this second override being dropped.
        Assert.Contains("TextRenderer", ViewerAssets.ViewerJs);
    }
}

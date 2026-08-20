// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests that the viewer's embedded assets are present and carry the
/// guarantees the generated pages depend on.
/// </summary>
/// <remarks>
/// <para>
/// The tests below that reference sanitization (their names start with
/// <c>ViewerJs_sanitizer_</c>) are SMOKE CHECKS, not behavioural proof. xunit
/// runs on .NET, not in a browser, so it cannot execute <c>viewer.js</c> or
/// observe what a real DOM sanitizer actually does with a hostile payload —
/// these tests only assert that specific source-text markers are present
/// (an allowlist name, a rejected scheme, etc.). A change that renamed or
/// gutted the sanitizer while keeping those markers as dead text would still
/// pass every test here.
/// </para>
/// <para>
/// The binding verification for the sanitizer is external to this suite:
/// <c>tools/viewer-security-check/</c> is a committed Node/jsdom harness
/// that loads the real vendored <c>marked.min.js</c> plus the shipped
/// <c>viewer.js</c> and exercises them together against a battery of
/// hostile payloads — including the attribute-breakout payload
/// <c>![foo" onerror="alert(1)](x.png)</c>, <c>javascript:</c>/<c>data:</c>
/// links (plain and mixed-case), raw <c>&lt;script&gt;</c>/<c>&lt;svg
/// onload&gt;</c>, and nested HTML in alt text — confirming no executable
/// output and no attribute breakout in any case, plus a battery of ordinary
/// markdown (headings, lists, bold, fenced code, relative links, plain
/// image alt-text) confirming the sanitizer isn't over-aggressive either.
/// Run it with <c>npm install &amp;&amp; npm test</c> from that directory;
/// see its README for why it is deliberately not wired into CI and when to
/// run it (whenever <c>marked.min.js</c> is bumped).
/// </para>
/// </remarks>
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
    public void ViewerJs_disables_marked_raw_html_passthrough()
    {
        // Layer 1 (defence in depth, not the control that holds on its own):
        // marked renders raw HTML by default and has no `sanitize` option any
        // more, so this neuters it at the renderer level. Smoke check only —
        // see the class remarks.
        Assert.Contains("html:", ViewerAssets.ViewerJs);
        Assert.Contains("renderer", ViewerAssets.ViewerJs);
    }

    [Fact]
    public void ViewerJs_also_patches_the_text_renderer()
    {
        // The vendored build (marked v15.0.12) routes image/title alt-text
        // through a *separate* TextRenderer instance that marked.use({
        // renderer: ... }) does not touch. Smoke check only — see the class
        // remarks; this alone does not close the attribute-breakout gap
        // below, which needed a DOM-level sanitizer instead.
        Assert.Contains("TextRenderer", ViewerAssets.ViewerJs);
    }

    [Fact]
    public void ViewerJs_sanitizer_allowlists_tags_instead_of_blocking_them()
    {
        // marked.Renderer.image() interpolates the alt attribute with no
        // escaping call at all, so `![foo" onerror="alert(1)](x.png)` — a
        // plain markdown image, no raw HTML anywhere in the source — breaks
        // out of the alt attribute and adds a live onerror handler that
        // fires on page load. No renderer-hook override can close this in
        // general, because the defect is in how marked builds the attribute
        // string, not in a specific renderer call site. The only control
        // that holds is sanitizing the parsed DOM itself before it reaches
        // the live page. Smoke check only — see the class remarks for where
        // this was actually proven (Node/jsdom transcript in the Task 7
        // report; the browser check in Task 11).
        Assert.Contains("ALLOWED_TAGS", ViewerAssets.ViewerJs);
        Assert.Contains("ALLOWED_ATTRS", ViewerAssets.ViewerJs);
        Assert.Contains("DOMParser", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("SCRIPT:", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("IFRAME:", ViewerAssets.ViewerJs);
    }

    [Fact]
    public void ViewerJs_sanitizer_validates_url_schemes()
    {
        // `[click](javascript:alert(1))` and data: URIs survive marked's own
        // parsing verbatim into an href attribute; nothing in marked itself
        // rejects them. Smoke check only — see the class remarks.
        Assert.Contains("isSafeUrl", ViewerAssets.ViewerJs);
        Assert.Contains("\"http:\"", ViewerAssets.ViewerJs);
        Assert.Contains("\"https:\"", ViewerAssets.ViewerJs);
        Assert.Contains("\"mailto:\"", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("\"javascript:\": 1", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("\"data:\": 1", ViewerAssets.ViewerJs);
    }
}

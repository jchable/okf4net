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
/// image alt-text, GFM task-list checked/unchecked markers) confirming the
/// sanitizer isn't over-aggressive either.
/// Run it with <c>npm ci &amp;&amp; npm test</c> from that directory. CI runs
/// it too, as the <c>viewer sanitizer (JS)</c> job — that job, not the
/// smoke checks below, is what actually guards the sanitizer, and it is the
/// one to watch whenever <c>marked.min.js</c> is bumped.
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
    {
        // "marked" alone matches the minified body itself roughly a dozen
        // times, so it survives even if the actual licence banner comment is
        // stripped entirely. Assert on the copyright line the MIT licence
        // requires retention of.
        Assert.Contains("Copyright (c) 2011-2025, Christopher Jeffrey", ViewerAssets.MarkedJs, StringComparison.Ordinal);
    }

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
        //
        // Matched as the actual `marked.use({ renderer: { html: function` call
        // construct, not the bare words "html:" / "renderer" -- both of those
        // also appear in this very file's prose comments (see above), so a
        // plain Contains check would still pass after the code itself was
        // deleted.
        Assert.Matches(@"marked\.use\(\{\s*renderer:\s*\{\s*html:\s*function", ViewerAssets.ViewerJs);
    }

    [Fact]
    public void ViewerJs_also_patches_the_text_renderer()
    {
        // The vendored build (marked v15.0.12) routes image/title alt-text
        // through a *separate* TextRenderer instance that marked.use({
        // renderer: ... }) does not touch. Smoke check only — see the class
        // remarks; this alone does not close the attribute-breakout gap
        // below, which needed a DOM-level sanitizer instead.
        //
        // Matched as the actual assignment to `TextRenderer.prototype.html`,
        // not the bare word "TextRenderer" -- it also appears in this file's
        // prose comments, so a plain Contains check would still pass after
        // the code itself was deleted.
        Assert.Matches(@"marked\.TextRenderer\.prototype\.html\s*=\s*function", ViewerAssets.ViewerJs);
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
        // this was actually proven (tools/viewer-security-check/; the
        // browser check in Task 11).
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

    [Fact]
    public void ViewerJs_sanitizer_replaces_task_list_checkboxes_with_text_markers()
    {
        // GFM task lists (`- [ ] foo` / `- [x] foo`) render as
        // `<input type="checkbox" disabled>` (plus `checked` when ticked) in
        // marked's output. INPUT is rightly not allowlisted -- it is a live
        // form control -- but silently dropping it via the generic "keep
        // only the text" branch used for every other disallowed element
        // would erase real information: a reader could no longer tell a done
        // item from a pending one. The sanitizer special-cases it instead,
        // substituting a plain Unicode text marker (checked box / empty box)
        // that carries no attributes and cannot execute, rather than
        // allowlisting the element. Smoke check only — see the class
        // remarks; the real DOM assertions (no <input> survives, checked vs.
        // unchecked items render visually distinct text) live in
        // tools/viewer-security-check/run.js.
        Assert.Contains("\"checkbox\"", ViewerAssets.ViewerJs);
        Assert.Contains('☑', ViewerAssets.ViewerJs);
        Assert.Contains('☐', ViewerAssets.ViewerJs);
        Assert.DoesNotContain("INPUT: 1", ViewerAssets.ViewerJs);
    }
}

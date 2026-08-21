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
/// onload&gt;</c>/<c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>, a raw
/// <c>&lt;input&gt;</c> surviving only as an inert disabled checkbox when
/// (and only when) its type is checkbox — confirming no executable
/// output and no attribute breakout in any case, plus a battery of ordinary
/// markdown (headings, lists, bold, fenced code, relative links, plain
/// image alt-text, GFM task-list checked/unchecked state) confirming the
/// sanitizer isn't over-aggressive either.
/// Run it with <c>npm ci &amp;&amp; npm test</c> from that directory. CI runs
/// it too, as the <c>viewer sanitizer (JS)</c> job — that job, not the
/// smoke checks below, is what actually guards the sanitizer, and it is the
/// one to watch whenever <c>marked.min.js</c> is bumped or <c>viewer.js</c>
/// is edited.
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
    public void ViewerJs_does_not_patch_marked_renderer_hooks()
    {
        // An earlier version of viewer.js patched marked's `renderer.html`
        // hook (both the main Renderer and its separate TextRenderer) as a
        // "defence in depth" layer in front of the DOM sanitizer. It was
        // removed: measured against the vendored build plus the hostile
        // payload battery in tools/viewer-security-check/, that layer
        // stopped nothing the DOM sanitizer alone did not already stop, and
        // it silently deleted benign wrapped content the sanitizer
        // preserves (e.g. `<details><summary>...</summary>body</details>`
        // rendered as `""` instead of keeping "body"). Guard against it
        // being reintroduced.
        Assert.DoesNotContain("marked.use(", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("TextRenderer.prototype.html", ViewerAssets.ViewerJs);
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
        // the live page — the sanitizer below is the whole defense, not one
        // layer of it. Smoke check only — see the class remarks for where
        // this was actually proven (tools/viewer-security-check/).
        Assert.Contains("ALLOWED_TAGS", ViewerAssets.ViewerJs);
        Assert.Contains("ALLOWED_ATTRS", ViewerAssets.ViewerJs);
        Assert.Contains("DOMParser", ViewerAssets.ViewerJs);
        Assert.DoesNotContain("IFRAME:", ViewerAssets.ViewerJs);
        // SCRIPT and STYLE are deliberately named elsewhere (OPAQUE_TAGS,
        // see the next assertion) as elements to drop entirely rather than
        // fall into the generic "keep the text" branch -- that reference is
        // legitimate and is not the same thing as allowlisting them.
        Assert.Contains("OPAQUE_TAGS", ViewerAssets.ViewerJs);
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
    public void ViewerJs_sanitizer_lets_task_list_checkboxes_survive_as_real_inputs()
    {
        // GFM task lists (`- [ ] foo` / `- [x] foo`) render as
        // `<input type="checkbox" disabled>` (plus `checked` when ticked) in
        // marked's output. A screen reader announces a real, disabled
        // `<input type="checkbox">` as a checkbox with its state; a plain
        // Unicode glyph substituted for it is decorative text with no
        // announced state, so the sanitizer lets the checkbox survive as a
        // real element instead: INPUT is on ALLOWED_TAGS but gated by a
        // value constraint (only `type="checkbox"` qualifies -- any other
        // input type still falls through to the generic drop-and-keep-text
        // branch used for every other disallowed element), its attribute
        // allowlist is limited to `type`/`disabled`/`checked`, and
        // `disabled` is forced on every surviving instance so a bundle
        // cannot ship a live, focusable form control. Smoke check only —
        // see the class remarks; the real DOM assertions (no non-checkbox
        // <input> survives, a hostile `onfocus`/`autofocus` checkbox
        // survives only inert and disabled, checked vs. unchecked items
        // render with the correct `checked` state) live in
        // tools/viewer-security-check/run.js.
        Assert.Contains("\"checkbox\"", ViewerAssets.ViewerJs);
        Assert.Contains("TAG_VALUE_CONSTRAINTS", ViewerAssets.ViewerJs);
        Assert.Contains("INPUT: 1", ViewerAssets.ViewerJs);
        Assert.Contains("setAttribute(\"disabled\"", ViewerAssets.ViewerJs);
    }
}

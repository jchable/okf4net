// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The centre column's head and "Referenced by", as written (spec §11.3 C2–C6, §11.4 X10, §12.3).</summary>
public class PageHeadTests
{
    private const string Period =
        "---\ntype: Attested Computation\ntitle: Gross margin for a period\nstatus: stable\n"
        + "verified:\n  - { by: \"human:jsmith\", at: \"2026-07-01T09:00:00Z\" }\n"
        + "stale_after: \"2026-12-31\"\ntags: [finance, margin]\nruntime: bigquery\na: 1\nb: 2\nc: 3\n---\n"
        + "# Gross margin for a period\n\nText with [m](../metrics/margin.md).\n";

    private static ViewerSite Acme(TempDir src)
    {
        src.Write("acme/computations/gross-margin-period.md", Period);
        src.Write("acme/computations.md", "---\ntype: Note\ntitle: Computations\n---\nThe folder's own page.\n");
        src.Write("acme/metrics/margin.md", "---\ntype: Metric\ntitle: Margin\n---\nSee [p](../computations/gross-margin-period.md).\n");
        src.Write("acme/untyped.md", "---\ntitle: Untyped\n---\nNothing.\n");
        return SiteModel.Build(Bundle.Load(Path.Combine(src.Path, "acme")));
    }

    private static string Write(ViewerSite site, TempDir dest, string rel)
    {
        HtmlWriter.Write(site, dest.Path);
        return File.ReadAllText(Path.Combine(dest.Path, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int SlotOf(ViewerSite site, string id)
    {
        var concept = site.Index.Concepts.Single(c => c.Id.ToString() == id);
        return site.Index.Types[concept.TypeIndex].Slot;
    }

    [Fact]
    public void Breadcrumb_links_the_bundle_index_and_a_folder_that_is_also_a_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Contains(
            "<div class=\"okf-page-head\">\n<nav class=\"okf-crumbs\" aria-label=\"Breadcrumb\"><ol>\n"
            + "<li><a href=\"../index.html\">acme</a></li>\n"
            + "<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><a href=\"../computations.html\">computations</a></li>\n"
            + "<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><span aria-current=\"page\">gross-margin-period</span></li>\n"
            + "</ol></nav>\n",
            page);
    }

    [Fact]
    public void Breadcrumb_shows_a_folder_without_its_own_concept_as_text()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "metrics/margin.html");

        Assert.Contains("<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><span>metrics</span></li>\n", page);
    }

    [Fact]
    public void The_head_has_one_h1_with_the_display_title_and_replaces_the_meta_line()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<h1"));
        Assert.Contains("</nav>\n<h1>Gross margin for a period</h1>\n<div class=\"okf-chips\">", page);
        Assert.DoesNotContain("class=\"meta\"", page);
        Assert.Contains("<main id=\"okf-main\">\n<div class=\"okf-page-head\">", page);
    }

    [Fact]
    public void The_payload_carries_the_display_body_and_Body_stays_raw()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "computations/gross-margin-period.html");

        var start = page.IndexOf("id=\"okf-payload\">", StringComparison.Ordinal) + "id=\"okf-payload\">".Length;
        var json = page[start..page.IndexOf("</script>", start, StringComparison.Ordinal)];
        var body = JsonDocument.Parse(json).RootElement.GetProperty("body").GetString()!;

        Assert.StartsWith("\nText with", body, StringComparison.Ordinal);
        Assert.StartsWith("# Gross margin for a period", site.Pages.Single(p => p.Id.ToString() == "computations/gross-margin-period").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Chips_show_the_type_with_its_slot_the_status_the_trust_with_verifier_and_date_and_stale_after()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "computations/gross-margin-period.html");
        var slot = SlotOf(site, "computations/gross-margin-period");

        Assert.Contains(
            "<div class=\"okf-chips\">\n"
            + $"<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"{slot}\"></span>Attested Computation</span>\n"
            + "<span class=\"okf-chip okf-chip-status\">stable</span>\n"
            + "<span class=\"okf-chip okf-chip-trust\"><span class=\"okf-chip-glyph\" data-okf-trust=\"human\"></span>human-reviewed · jsmith · 2026-07-01</span>\n"
            + "<span class=\"okf-chip okf-chip-stale\"><span class=\"okf-chip-glyph\" data-okf-stale></span><span class=\"okf-chip-text\">stale after 2026-12-31</span></span>\n"
            + "</div>\n",
            page);
    }

    [Fact]
    public void An_untyped_unverified_page_shows_no_type_and_a_muted_trust_chip_without_glyph()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "untyped.html");

        Assert.Equal(5, SlotOf(site, "untyped"));
        Assert.Contains("<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"5\"></span>(no type)</span>\n", page);
        Assert.Contains("<span class=\"okf-chip okf-chip-trust okf-chip-unverified\">unverified</span>\n", page);
        Assert.DoesNotContain("okf-chip-status", page);
        Assert.DoesNotContain("okf-chip-stale", page);
    }

    [Fact]
    public void A_further_verification_is_counted_after_the_date()
    {
        var head = new ViewerPageHead("Note", null, "machine-confirmed", "tool:okf-ci", "2026-07-01", 2, null);

        Assert.Equal("machine-confirmed · tool:okf-ci · 2026-07-01 · +2", HtmlWriter.TrustText(head));
        Assert.Equal("machine-confirmed", HtmlWriter.TrustText(head with { Verifier = null, VerifiedDate = null, MoreVerifications = 0 }));
    }

    [Fact]
    public void Frontmatter_box_counts_every_field_and_marks_the_folded_ones()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        // Period declares ten entries: type, title, status, verified,
        // stale_after (shown above, folded), tags, runtime, a, b (the four
        // unfolded ones), c (folded).
        Assert.Contains("<section class=\"okf-fm\" id=\"okf-fm\" aria-labelledby=\"okf-fm-title\">\n<div class=\"okf-fm-head\"><h2 class=\"okf-section-title\" id=\"okf-fm-title\">Frontmatter · 10 fields</h2></div>\n<div class=\"okf-fm-grid\" id=\"okf-fm-grid\">\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">type</span><span class=\"okf-fm-value\">Attested Computation</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\"><span class=\"okf-fm-key\">tags</span><span class=\"okf-fm-value\">finance, margin</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\"><span class=\"okf-fm-key\">b</span><span class=\"okf-fm-value\">2</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">c</span><span class=\"okf-fm-value\">3</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">verified</span><span class=\"okf-fm-value okf-fm-struct\">", page);
    }

    [Fact]
    public void A_structured_value_is_written_with_its_line_breaks()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("n.md", "---\ntype: Note\ntitle: N\nexecutor:\n  resource: skills/run.md\n  runtime: python\n---\nBody.\n");
        var page = Write(SiteModel.Build(Bundle.Load(src.Path)), dest, "n.html");

        Assert.Contains("<span class=\"okf-fm-value okf-fm-struct\">resource: skills/run.md\nruntime: python</span>", page);
    }

    [Fact]
    public void A_single_field_is_singular_and_a_page_without_fields_has_no_box()
    {
        using var src = new TempDir();
        using var one = new TempDir();
        using var none = new TempDir();
        var single = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [new ViewerFrontmatterEntry("k", "v")], string.Empty, [], []);
        var empty = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        Assert.Contains("Frontmatter · 1 field</h2>", Write(new ViewerSite(src.Path, [single], string.Empty, []), one, "x.html"));
        Assert.DoesNotContain("okf-fm", Write(new ViewerSite(src.Path, [empty], string.Empty, []), none, "x.html"));
    }

    [Fact]
    public void Referenced_by_counts_its_rows_and_marks_each_with_its_target()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Contains("<h2 id=\"okf-backlinks-title\" class=\"okf-section-title\">Referenced by <span class=\"okf-count\">· 1</span></h2>\n<ul>\n", page);
        Assert.Contains("<li><a class=\"okf-row\" href=\"../metrics/margin.html\" data-okf-target=\"metrics/margin\">metrics/margin</a></li>\n", page);
    }

    [Fact]
    public void Hostile_status_verifier_date_keys_and_values_are_escaped()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("evil.md",
            "---\ntype: \"<b>T</b>\"\ntitle: Evil\nstatus: \"<img src=x onerror=alert(1)>\"\n"
            + "verified:\n  - { by: \"human:<script>x</script>\", at: \"<i>2026</i>\" }\n"
            + "bad<key>: \"<v>&\\\"\"\n---\nBody.\n");
        var page = Write(SiteModel.Build(Bundle.Load(src.Path)), dest, "evil.html");
        var head = page[page.IndexOf("<div class=\"okf-page-head\">", StringComparison.Ordinal)..page.IndexOf("<div id=\"okf-body\">", StringComparison.Ordinal)];

        foreach (var raw in new[] { "<img src=x", "<script>x", "<i>2026", "<b>T</b>", "<v>", "bad<key>" })
        {
            Assert.DoesNotContain(raw, head);
        }

        Assert.Contains("&lt;b&gt;T&lt;/b&gt;</span>", head);
        Assert.Contains("<span class=\"okf-chip okf-chip-status\">&lt;img src=x onerror=alert(1)&gt;</span>", head);
        Assert.Contains("human-reviewed · &lt;script&gt;x&lt;/script&gt; · &lt;i&gt;2026&lt;/i&gt;</span>", head);
        Assert.Contains("<span class=\"okf-fm-key\">bad&lt;key&gt;</span><span class=\"okf-fm-value\">&lt;v&gt;&amp;&quot;</span>", head);
    }
}

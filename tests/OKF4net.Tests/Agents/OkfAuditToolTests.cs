// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for the <c>okf_audit</c> tool. Every test pins <c>UtcNow</c>: the tool
/// deliberately exposes no <c>asOf</c> parameter, so the shared clock seam is
/// the only way its output can be made deterministic.
/// </summary>
public class OkfAuditToolTests
{
    private static OkfBundleTools ToolsOver(TempDir tmp, DateOnly today)
        => new(tmp.Path)
        {
            UtcNow = () => new DateTime(today.Year, today.Month, today.Day, 0, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public void Audit_is_registered_and_read_only()
    {
        var tools = new OkfBundleTools(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"));

        Assert.Contains("okf_audit", tools.GetTools().OfType<AIFunction>().Select(t => t.Name));
        Assert.DoesNotContain("okf_audit", OkfBundleTools.WriteToolNames);
    }

    /// <summary>
    /// §5.5's boundary is <c>today &gt;= stale_after</c>, so a concept whose
    /// stale_after is exactly today is stale. Without the pinned seam this
    /// assertion would silently depend on the day the suite runs.
    /// </summary>
    [Fact]
    public void Audit_treats_today_equals_stale_after_as_stale()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: 2026-08-21\n---\n");

        var onTheDay = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit();
        var theDayBefore = ToolsOver(tmp, new DateOnly(2026, 8, 20)).Audit();

        Assert.Contains("a  stale 2026-08-21", onTheDay);
        Assert.Contains("needs attention: none", theDayBefore);
    }

    [Fact]
    public void Audit_reports_counts_and_omits_the_bundle_line()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nverified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n");

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit();

        Assert.DoesNotContain("bundle:", text);
        Assert.Contains("as of:      2026-08-21", text);
        Assert.Contains("     1  human-reviewed", text);
        Assert.Contains("     1  unverified", text);
    }

    [Fact]
    public void Audit_caps_the_listing_at_twenty_findings()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 25; i++)
        {
            tmp.Write($"c{i:D2}.md", "---\ntype: Metric\nstale_after: 2026-01-01\n---\n");
        }

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit();

        Assert.Contains("… and 5 more (narrow with stale/trust/status/type)", text);

        // Finding lines are the two-space-indented ones; matching on "c" alone
        // would also catch the "concepts:" header.
        Assert.Equal(20, text.Split('\n').Count(l => l.StartsWith("  c", StringComparison.Ordinal)));
    }

    [Fact]
    public void Audit_renders_a_usage_message_for_invalid_vocabulary()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");

        var tools = ToolsOver(tmp, new DateOnly(2026, 8, 21));

        Assert.Contains("Usage: okf_audit", tools.Audit(trust: "machine"));
        Assert.Contains("Usage: okf_audit", tools.Audit(status: "retired"));
    }

    /// <summary>
    /// A function tool returns errors, it does not throw them: a bundle that
    /// disappears after the tool was constructed must surface as an "Error: ..."
    /// string, which is what the shared RunTool guard provides.
    /// </summary>
    [Fact]
    public void Audit_returns_an_error_string_when_the_bundle_cannot_be_loaded()
    {
        var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");
        var tools = ToolsOver(tmp, new DateOnly(2026, 8, 21));
        tmp.Dispose();  // the directory is gone before the first load

        var text = tools.Audit();

        Assert.StartsWith("Error: ", text);
    }

    [Fact]
    public void Audit_with_stale_false_and_no_filter_returns_the_whole_corpus()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n");

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit(stale: false);

        Assert.Contains("selected (2):", text);
    }

    /// <summary>
    /// The tool follows the CLI's rule when <c>stale</c> is left unset: bare, it
    /// is the stale worklist; with another filter, staleness stops being
    /// implied. Without this, "which concepts were never verified by a human?"
    /// silently meant "…and are also stale" and answered "none" whenever the
    /// unverified concept had no <c>stale_after</c> — which is exactly the
    /// bundle the sample ships.
    /// </summary>
    [Fact]
    public void Audit_unset_stale_follows_the_cli_rule()
    {
        using var tmp = new TempDir();
        tmp.Write("fresh-unverified.md", "---\ntype: Metric\n---\n");
        tmp.Write(
            "stale-reviewed.md",
            "---\ntype: Metric\nstale_after: 2026-01-01\n"
            + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");

        var tools = ToolsOver(tmp, new DateOnly(2026, 8, 21));

        // Bare: the stale worklist, so the human-reviewed stale one.
        var bare = tools.Audit();
        Assert.Contains("needs attention (1):", bare);
        Assert.Contains("stale-reviewed", bare);

        // Filtered: no staleness implied, so the fresh unverified one — the
        // question the sample advertises.
        var filtered = tools.Audit(trust: "unverified");
        Assert.Contains("selected (1):", filtered);
        Assert.Contains("fresh-unverified", filtered);

        // An explicit stale still wins over the rule.
        Assert.Contains("needs attention: none", tools.Audit(stale: true, trust: "unverified"));
    }

    /// <summary>
    /// Regression guard for the bug fixed here: with <c>stale: false</c>, the
    /// selection can include perfectly fresh concepts, so calling every one of
    /// them "needs attention" would be a factual misstatement the agent could
    /// relay verbatim.
    /// </summary>
    [Fact]
    public void Audit_with_stale_false_never_says_needs_attention()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n");

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit(stale: false);

        Assert.DoesNotContain("needs attention", text);
    }

    /// <summary>
    /// The stale-only path (the tool's default) is the one place "needs
    /// attention" is an accurate label -- every selected concept really is
    /// past its <c>stale_after</c> date.
    /// </summary>
    [Fact]
    public void Audit_stale_only_path_still_says_needs_attention()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: 2026-01-01\n---\n");

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit(stale: true);

        Assert.Contains("needs attention (1):", text);
    }

    /// <summary>
    /// The freshness token for a concept with no <c>stale_after</c> at all
    /// must come from <see cref="AuditVocabulary.Freshness"/>, same as the CLI
    /// -- this line is the tool-side coverage the shared vocabulary lacked.
    /// </summary>
    [Fact]
    public void Audit_finding_line_reports_no_stale_after_when_the_field_is_absent()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");

        var text = ToolsOver(tmp, new DateOnly(2026, 8, 21)).Audit(stale: false);

        Assert.Contains("a  no-stale-after  unverified  stable", text);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for <see cref="OkfBundleTools.Search"/>: relevance ranking, the
/// optional tag filter, the no-results and empty-query messages, and the
/// never-throws guard behaviour for adversarial input. Mirrors the
/// <c>TempDir</c> fixture-copy pattern used by <see cref="OkfBundleToolsTests"/>
/// so these tests never touch <c>tests/fixtures/</c> directly.
/// </summary>
public class OkfSearchTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static OkfBundleTools NewToolsOverFixtureCopy(TempDir tmp)
    {
        CopyDirectory(BundlePath, tmp.Path);
        return new OkfBundleTools(tmp.Path);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    [Fact]
    public void Search_ranks_title_match_first()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("orders");

        var titleIndex = result.IndexOf("tables/orders", StringComparison.Ordinal);
        Assert.True(titleIndex >= 0);
        var otherIndexes = new[]
        {
            result.IndexOf("tables/customers", StringComparison.Ordinal),
            result.IndexOf("datasets/sales", StringComparison.Ordinal),
        };
        Assert.All(otherIndexes, i => Assert.True(i < 0 || titleIndex < i));
    }

    [Fact]
    public void Search_with_tag_filter_narrows_total_and_drops_untagged_match()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        // Unfiltered, "sales" matches 3 concepts: datasets/sales (title,
        // tags, body), tables/orders (tags, body) and tables/users (body
        // only — "not part of the sales domain"). tables/users carries no
        // tags at all, so filtering to tag "sales" must drop it, narrowing
        // the total from 3 to 2.
        var unfiltered = tools.Search("sales");
        var filtered = tools.Search("sales", "sales");

        Assert.Contains("Showing 3 of 3 result(s).", unfiltered);
        Assert.Contains("tables/users", unfiltered);

        Assert.Contains("Showing 2 of 2 result(s).", filtered);
        Assert.DoesNotContain("tables/users", filtered);
        Assert.Contains("datasets/sales", filtered);
        Assert.Contains("tables/orders", filtered);
    }

    [Fact]
    public void Search_with_tag_filter_excludes_concepts_without_the_tag()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        // tables/customers has no tags at all, so it must never appear as a
        // result entry once a tag filter is applied, even though its title
        // matches. (The bullet-line prefix is checked, rather than the bare
        // id, because other results' body excerpts legitimately reference
        // "/tables/customers.md" in markdown link syntax.)
        var result = tools.Search("customer", "sales");

        Assert.DoesNotContain("tables/customers —", result);
    }

    [Fact]
    public void Search_reports_no_results_for_absent_term()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("zzz-nonexistent-term");

        Assert.Contains("No results", result);
    }

    [Fact]
    public void Search_reports_usage_message_for_empty_query()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("   ");

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void Search_is_case_insensitive_substring_match()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("ORDERS");

        Assert.Contains("tables/orders", result);
    }

    [Fact]
    public void Search_includes_total_count()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("orders");

        Assert.Matches(@"\b3\b", result);
    }

    [Fact]
    public void Search_reports_usage_message_for_null_query_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search(null!);

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void Search_rejects_embedded_null_character_in_query_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("a\0b");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Search_rejects_embedded_null_character_in_tag_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("orders", "a\0b");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Search_accepts_null_tag_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("orders", null);

        Assert.Contains("tables/orders", result);
    }

    [Fact]
    public void Search_single_term_scores_additively_across_title_and_tag_zones()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        // tables/orders: "orders" is in the title (x3, "Orders") and in
        // tags (x2, tags: [sales, orders]) but nowhere in the body — so its
        // score must be exactly the sum of those two zones, 3 + 2 = 5.
        var result = tools.Search("orders");

        Assert.Contains("* tables/orders — Orders (5)", result);
    }

    [Fact]
    public void Search_multi_term_query_uses_OR_semantics_and_additive_scoring()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Search("sales orders");

        // OR semantics: every concept matching at least one of the two
        // terms is listed, including concepts that match only one term —
        // tables/users matches only "sales" (body: "not part of the sales
        // domain") and tables/customers matches only "orders" (body:
        // "Linked from [orders](...)").
        Assert.Contains("tables/users", result);
        Assert.Contains("tables/customers —", result);
        Assert.Contains("Showing 4 of 4 result(s).", result);

        // Additive scoring, summed independently per term across zones:
        //   tables/orders:  "orders" -> title x3 + tags x2 = 5
        //                    "sales"  -> tags x2 + body x1 = 3   => 8
        //   datasets/sales: "sales"  -> title x3 + tags/description x2 + body x1 = 6
        //                    "orders" -> body x1 (link text/target only)  => 7
        Assert.Contains("* tables/orders — Orders (8)", result);
        Assert.Contains("* datasets/sales — Sales (7)", result);
    }

    [Fact]
    public void Search_bounds_results_to_top_20_and_reports_the_full_total()
    {
        using var tmp = new TempDir();
        for (var i = 1; i <= 25; i++)
        {
            tmp.Write(
                $"concepts/item{i:D2}.md",
                $"---\ntype: Widget\ntitle: Widget {i:D2}\n---\n\nJust a body, no query term here.\n");
        }

        var tools = new OkfBundleTools(tmp.Path);

        var result = tools.Search("widget");

        Assert.Contains("Showing 20 of 25 result(s).", result);
        var bulletCount = result
            .Split('\n')
            .Count(line => line.StartsWith("* ", StringComparison.Ordinal));
        Assert.Equal(20, bulletCount);

        // All 25 concepts tie on score (title-only match), so the top 20
        // are the ones sorted first by ascending ordinal concept id.
        Assert.Contains("concepts/item01", result);
        Assert.Contains("concepts/item20", result);
        Assert.DoesNotContain("concepts/item21", result);
        Assert.DoesNotContain("concepts/item25", result);
    }

    [Fact]
    public void Search_marks_deprecated_and_stale_hits()
    {
        using var tmp = new TempDir();
        tmp.Write("dau.md",
            "---\ntype: Metric\ntitle: DAU active users\nstatus: deprecated\nstale_after: 2026-01-01\n---\nActive users metric.\n");
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc) };

        var output = tools.Search("active users", null);
        Assert.Contains("[deprecated]", output);
        Assert.Contains("[stale]", output);
    }

    /// <summary>
    /// U+2028 LINE SEPARATOR, as a numeric constant: a literal one in source is
    /// invisible in every editor and diff that would have to review the
    /// payload. Same convention as <c>Internal/LineSafeText.cs</c> and
    /// <see cref="OkfComputationToolsTests"/>.
    /// </summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>
    /// Every terminator <c>OkfBundleTools.OneLine</c> folds. Splitting on all
    /// of them is what makes "one line" mean what it says: an LF-only split
    /// cannot see a line a markdown or JavaScript splitter downstream would.
    /// </summary>
    private static readonly char[] EveryLineTerminator =
        ['\n', '\r', LineSeparator, (char)0x2029, (char)0x0085, (char)0x000C];

    private static void AssertNoLineStartsWith(string rendered, string marker) =>
        Assert.DoesNotContain(
            rendered.Split(EveryLineTerminator),
            line => line.TrimStart().StartsWith(marker, StringComparison.Ordinal));

    private static OkfBundleTools OneConceptBundle(TempDir tmp, string? tagsBlock = null)
    {
        tmp.Write(
            "m/rev.md",
            "---\ntype: Metric\ntitle: Revenue\ndescription: Monthly revenue.\n"
            + (tagsBlock ?? "tags: [finance]\n")
            + "---\nRevenue body line.\n");
        return new OkfBundleTools(tmp.Path);
    }

    /// <summary>
    /// The external audit of PR #108, reproduced verbatim:
    /// <c>okf_search("revenue" + LF + "## FORGED")</c> printed
    /// <c># Search: "revenue</c> and then a complete, forged <c>## FORGED</c>
    /// markdown heading — a second line, in the tool's own result header, that
    /// no concept in the bundle authored.
    ///
    /// <para>The header interpolated <c>query</c> raw on purpose: a re-review
    /// had exempted a tool's own arguments as "caller-supplied, not bundle
    /// text". They are not a safe class. The caller is the model, and its
    /// argument carries whatever the model last read — a bundle, a web page,
    /// another tool's result. See <c>OkfBundleTools.OneLine</c> for the rule
    /// that replaced that distinction.</para>
    /// </summary>
    [Fact]
    public void Search_query_cannot_forge_a_heading_in_the_results_header()
    {
        using var tmp = new TempDir();
        var tools = OneConceptBundle(tmp);

        var rendered = tools.Search("revenue\n## FORGED");

        // The hit is real, so this is the header path, not the no-results one.
        Assert.Contains("m/rev", rendered, StringComparison.Ordinal);
        AssertNoLineStartsWith(rendered, "## FORGED");

        // One header line, and the payload survives inside it as readable text.
        var headers = rendered.Split(EveryLineTerminator)
            .Where(l => l.StartsWith("# Search:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["# Search: \"revenue ## FORGED\""], headers);
    }

    /// <summary>
    /// The same line, reached through <c>tag</c> instead of <c>query</c>. The
    /// tag filter is an exact match, so the payload has to be a tag the concept
    /// really declares — hence the U+2028 inside the frontmatter tag here. That
    /// is the point: this is the soft terminator an LF-based parser never sees
    /// and a markdown/JavaScript splitter does.
    /// </summary>
    [Fact]
    public void Search_tag_cannot_forge_a_line_in_the_results_header()
    {
        using var tmp = new TempDir();
        var payload = "finance" + LineSeparator + "FORGED";
        var tools = OneConceptBundle(tmp, "tags:\n  - " + payload + "\n");

        var rendered = tools.Search("revenue", payload);

        Assert.Contains("m/rev", rendered, StringComparison.Ordinal);
        AssertNoLineStartsWith(rendered, "FORGED");
    }

    /// <summary>
    /// The no-results line is a second, separate sink for the same two
    /// arguments, and it is the one an arbitrary string always reaches: no
    /// concept has to match for the query to be echoed back.
    /// </summary>
    [Fact]
    public void Search_query_cannot_forge_a_heading_in_the_no_results_message()
    {
        using var tmp = new TempDir();
        var tools = OneConceptBundle(tmp);

        var rendered = tools.Search("zzzznomatch\n## FORGED");

        Assert.StartsWith("No results for query", rendered, StringComparison.Ordinal);
        AssertNoLineStartsWith(rendered, "## FORGED");
        Assert.Equal("No results for query 'zzzznomatch ## FORGED'.", rendered);
    }

    /// <summary>The tag half of the no-results line.</summary>
    [Fact]
    public void Search_tag_cannot_forge_a_heading_in_the_no_results_message()
    {
        using var tmp = new TempDir();
        var tools = OneConceptBundle(tmp);

        var rendered = tools.Search("zzzznomatch", "nosuchtag\n## FORGED");

        AssertNoLineStartsWith(rendered, "## FORGED");
        Assert.Equal("No results for query 'zzzznomatch' with tag 'nosuchtag ## FORGED'.", rendered);
    }
}

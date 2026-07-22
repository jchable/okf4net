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
    private static readonly string BundlePath = Path.Combine(RepoRoot(), "tests", "fixtures", "appendix_a");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"could not locate OKF4net.sln above {AppContext.BaseDirectory}");
    }

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
    public void Search_with_tag_filter_narrows_results()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        // "sales" without a tag filter matches both the dataset and the
        // orders table (tags/description); filtering to a tag only the
        // dataset carries narrows the result set.
        var unfiltered = tools.Search("sales");
        var filtered = tools.Search("sales", "sales");

        Assert.Contains("datasets/sales", unfiltered);
        Assert.Contains("datasets/sales", filtered);
        Assert.Contains("tables/orders", unfiltered);
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
}

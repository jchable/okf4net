// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Skeleton tests for <see cref="OkfBundleTools"/>: constructor validation
/// and the lazy <c>Bundle</c> cache. Mirrors the <c>RepoRoot()</c> fixture
/// lookup pattern used by <see cref="CliTests"/> so the fixture path does
/// not depend on the process's current directory.
/// </summary>
public class OkfBundleToolsTests
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

    [Fact]
    public void Constructor_rejects_nonexistent_directory()
    {
        Assert.Throws<ArgumentException>(() => new OkfBundleTools("nonexistent-dir"));
    }

    [Fact]
    public void GetBundle_loads_appendix_a_fixture()
    {
        var tools = new OkfBundleTools(BundlePath);
        Assert.Equal(4, tools.GetBundle().Count);
    }

    /// <summary>
    /// Creates a tool set rooted at a fresh <see cref="TempDir"/> copy of the
    /// appendix_a fixture, so these tests never touch <c>tests/fixtures/</c>
    /// directly. Mirrors <c>GoldenParityTests.CopyDirectory</c>.
    /// </summary>
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
    public void ReadConcept_returns_title_and_backlinks_for_existing_concept()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ReadConcept("tables/orders");

        Assert.Contains("# Orders", result);
        Assert.Contains("## Backlinks", result);
        Assert.Contains("datasets/sales", result);
        Assert.Contains("tables/customers", result);
    }

    [Fact]
    public void ReadConcept_reports_unknown_concept_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ReadConcept("nope");

        Assert.Contains("not found", result);
    }

    [Fact]
    public void Browse_without_path_lists_bundle_root_entries()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Browse();

        Assert.Contains("datasets", result);
        Assert.Contains("tables", result);
    }

    [Fact]
    public void Browse_rejects_path_traversal()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Browse("../etc");

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Graph_without_argument_reports_bundle_wide_stats()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Graph();

        Assert.Contains("4 concepts", result);
    }

    [Fact]
    public void ReadConcept_marks_broken_outgoing_links()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        tmp.Write(
            "tables/dangling.md",
            "---\ntype: BigQuery Table\ntitle: Dangling\n---\n\nSee [ghost](/tables/ghost.md).\n");
        var tools = new OkfBundleTools(tmp.Path);

        var result = tools.ReadConcept("tables/dangling");

        Assert.Contains("## Outgoing links", result);
        Assert.Contains("tables/ghost (broken)", result);
    }

    [Fact]
    public void Browse_lists_concepts_when_directory_has_no_subdirectories()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Browse("tables");

        Assert.Contains("## Concepts", result);
        Assert.Contains("tables/orders", result);
        Assert.Contains("tables/customers", result);
        Assert.Contains("tables/users", result);
    }

    [Fact]
    public void Graph_with_concept_id_reports_its_link_detail()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Graph("tables/orders");

        Assert.Contains("# Graph: tables/orders", result);
        Assert.Contains("## Outgoing links", result);
        Assert.Contains("datasets/sales", result);
        Assert.Contains("## Backlinks", result);
        Assert.Contains("tables/customers", result);
    }

    [Fact]
    public void ReadConcept_reports_null_concept_id_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ReadConcept(null!);

        Assert.Contains("not found", result);
    }

    [Fact]
    public void ReadConcept_rejects_embedded_null_character_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ReadConcept("a\0b");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Browse_rejects_absolute_windows_path_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Browse("C:\\abs");

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Browse_rejects_embedded_null_character_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Browse("a\0b");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void Graph_reports_not_found_for_slash_only_id_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.Graph("///");

        Assert.Contains("not found", result);
    }
}

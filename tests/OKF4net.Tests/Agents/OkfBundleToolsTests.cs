// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Skeleton tests for <see cref="OkfBundleTools"/>: constructor validation
/// and the lazy <c>Bundle</c> cache. Uses <see cref="TestPaths.RepoRoot"/>
/// for fixture lookup so the fixture path does not depend on the process's
/// current directory.
/// </summary>
public class OkfBundleToolsTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

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
    /// F3: two spellings of the same bundle directory that differ only by a
    /// trailing directory separator (e.g. <c>/foo</c> vs. <c>/foo/</c>) must
    /// resolve to the SAME entry in the process-wide <c>BundleLocks</c>
    /// registry -- otherwise <see cref="Path.GetFullPath(string)"/> alone
    /// (without <see cref="Path.TrimEndingDirectorySeparator(string)"/>) would
    /// treat them as two different keys, silently defeating the per-path
    /// write lock the registry's own doc comment claims two such instances
    /// share. Reflection is used only to read the private <c>_bundleLock</c>
    /// instance field for the assertion -- the fix itself is a one-line
    /// normalization in the constructor, not a public API change.
    /// </summary>
    [Fact]
    public void Trailing_separator_spelling_of_the_same_bundle_root_shares_the_same_lock()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);

        var toolsA = new OkfBundleTools(tmp.Path);
        var trailingSpelling = tmp.Path.EndsWith(Path.DirectorySeparatorChar)
            ? tmp.Path
            : tmp.Path + Path.DirectorySeparatorChar;
        var toolsB = new OkfBundleTools(trailingSpelling);

        var lockField = typeof(OkfBundleTools).GetField("_bundleLock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var lockA = lockField.GetValue(toolsA);
        var lockB = lockField.GetValue(toolsB);

        Assert.NotNull(lockA);
        Assert.Same(lockA, lockB);
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
    public void ReadConcept_shows_meta_line_for_deprecated_stale_concept()
    {
        var dir = Directory.CreateTempSubdirectory("okfmeta").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "m"));
        File.WriteAllText(Path.Combine(dir, "m", "old.md"),
            "---\ntype: Metric\ntitle: Old\nstatus: deprecated\nstale_after: 2026-01-01\nverified: {by: human:ada}\n---\nBody.\n");
        var tools = new OkfBundleTools(dir) { UtcNow = () => new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc) };

        var output = tools.ReadConcept("m/old");
        Assert.Contains("status: deprecated", output);
        Assert.Contains("trust: human-reviewed", output);
        Assert.Contains("stale: yes", output);
    }

    [Fact]
    public void ReadConcept_omits_meta_line_for_plain_stable_concept()
    {
        var dir = Directory.CreateTempSubdirectory("okfmeta2").FullName;
        File.WriteAllText(Path.Combine(dir, "c.md"), "---\ntype: Metric\ntitle: Plain\n---\nBody.\n");
        var tools = new OkfBundleTools(dir);
        Assert.DoesNotContain("trust:", tools.ReadConcept("c"));
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

    // ----------------------------------------------------------------
    // Reparse-point ancestor guard (Browse side -- see OkfWriteToolsTests
    // for the WriteConcept counterpart). A junction/symlink placed INSIDE
    // the bundle (e.g. bundleRoot/linked) can point at an arbitrary
    // external directory. The lexical containment check (IsWithinBundleRoot)
    // alone would accept it -- Path.GetFullPath resolves "linked" to a path
    // string still under bundleRoot -- but the OS follows the reparse point
    // the moment Browse actually touches disk, escaping the bundle. This
    // test requires reparse-point-creation privilege (a Windows junction via
    // mklink /J needs none; the Directory.CreateSymbolicLink fallback does)
    // and skips itself via TryCreateJunctionToExternalDir's bool return when
    // neither mechanism is available, per xunit v2 having no Assert.Skip.
    // ----------------------------------------------------------------

    [Fact]
    public void Browse_rejects_a_junction_pointing_outside_the_bundle()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        using var external = new TempDir();
        external.Write("secret.md", "---\ntype: Note\ntitle: Secret\n---\nshould never be seen\n");

        if (!tmp.TryCreateJunctionToExternalDir("linked", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var result = tools.Browse("linked");

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using Microsoft.Extensions.AI;
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
    /// <see cref="OkfBundleTools.WriteToolNames"/> is the single source of
    /// truth two independent consumers filter on for a read-only tool set
    /// (<c>OkfMcpToolset.Build</c>'s <c>readOnly</c> flag in
    /// <c>OKF4net.Mcp</c>, and the <c>samples/acme-retail-agent</c> console
    /// sample) -- this test pins its exact contents and proves filtering
    /// <see cref="OkfBundleTools.GetTools"/> by it yields exactly the
    /// read-only subset, so a future write tool silently added to
    /// <see cref="OkfBundleTools.GetTools"/> without updating this set would
    /// fail this test rather than leaking into a "read-only" consumer.
    /// </summary>
    [Fact]
    public void WriteToolNames_matches_the_four_mutating_tools_and_filters_them_out()
    {
        Assert.Equal(
            new HashSet<string> { "okf_write_concept", "okf_append_log", "okf_regenerate_indexes", "okf_verify" },
            OkfBundleTools.WriteToolNames);

        var tools = new OkfBundleTools(BundlePath);
        var readOnlyNames = tools.GetTools()
            .OfType<AIFunction>()
            .Select(t => t.Name)
            .Where(name => !OkfBundleTools.WriteToolNames.Contains(name))
            .ToHashSet();

        Assert.Equal(8, readOnlyNames.Count);
        Assert.DoesNotContain("okf_write_concept", readOnlyNames);
        Assert.DoesNotContain("okf_append_log", readOnlyNames);
        Assert.DoesNotContain("okf_regenerate_indexes", readOnlyNames);
        Assert.DoesNotContain("okf_verify", readOnlyNames);
        Assert.Contains("okf_read_concept", readOnlyNames);
        Assert.Contains("okf_get_computation", readOnlyNames);
        Assert.Contains("okf_audit", readOnlyNames);
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
        using var tmp = new TempDir();
        tmp.Write("m/old.md",
            "---\ntype: Metric\ntitle: Old\nstatus: deprecated\nstale_after: 2026-01-01\nverified: {by: human:ada}\n---\nBody.\n");
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc) };

        var output = tools.ReadConcept("m/old");
        Assert.Contains("status: deprecated", output);
        Assert.Contains("trust: human-reviewed", output);
        Assert.Contains("stale: yes", output);
    }

    [Fact]
    public void ReadConcept_omits_meta_line_for_plain_stable_concept()
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", "---\ntype: Metric\ntitle: Plain\n---\nBody.\n");
        var tools = new OkfBundleTools(tmp.Path);
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
    // and, when neither mechanism is available, SKIPS via Xunit.SkippableFact
    // so the run summary shows it. It used to return early on
    // TryCreateJunctionToExternalDir's bool instead, which counted as PASSED
    // while verifying nothing.
    // ----------------------------------------------------------------

    [SkippableFact]
    public void Browse_rejects_a_junction_pointing_outside_the_bundle()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        using var external = new TempDir();
        external.Write("secret.md", "---\ntype: Note\ntitle: Secret\n---\nshould never be seen\n");

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("linked", external.Path), "no junction/symlink privilege on this machine");

        var result = tools.Browse("linked");

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Browse_rejects_a_path_crossing_a_junction_whose_link_status_cannot_be_inspected()
    {
        // Task H1: "x/y" is a junction to `external` whose attributes the
        // current user cannot read (deny ReadAttributes on it, deny listing on
        // "x"), while the OS still lets a read traverse it. The bundle is
        // loaded (and cached) before the junction exists, because a bundle
        // walk cannot list "x" once it denies listing. The lenient
        // IsReparsePoint answered "not a link" and Browse returned
        // external/z/index.md.
        using var tmp = new TempDir();
        tmp.Write("index.md", "# Root\n");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        var tools = new OkfBundleTools(tmp.Path);
        tools.Browse();
        using var external = new TempDir();
        external.Write(Path.Combine("z", "index.md"), "OUTSIDE-THE-BUNDLE\n");
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var result = tools.Browse("x/y/z");

        Assert.Equal("Error: path 'x/y/z' not found in the bundle. Use okf_browse to list available directories.", result);
    }

    [SkippableFact]
    public void GetComputation_refuses_a_computation_file_reached_through_a_junction_whose_link_status_cannot_be_inspected()
    {
        // H1 fix round, decision (a): the reviewer's P-scenario. The bundle is
        // loaded (and cached) BEFORE the junction exists -- a fresh load could
        // not list "x" once it denies listing -- exactly as a long-lived
        // okf-mcp instance holds it. "x/y" then becomes a junction to
        // `external` whose attributes cannot be read. TryResolveResource keeps
        // the lenient predicate (okf validate must not change) and still
        // answers Resolved; the read itself must re-check strictly and refuse
        // through okf_get_computation's existing "could not be read" path. On
        // 7ee7287 the tool returned the file's content from outside the bundle.
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: x/y/secret.sql\n---\n");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "x"));
        var tools = new OkfBundleTools(tmp.Path);
        Assert.Contains("could not be resolved (Missing)", tools.GetComputation("c/rev"));
        using var external = new TempDir();
        external.Write("secret.sql", "SELECT 'OUTSIDE-THE-BUNDLE'\n");
        using var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var result = tools.GetComputation("c/rev");

        Assert.DoesNotContain("OUTSIDE-THE-BUNDLE", result, StringComparison.Ordinal);
        Assert.Contains("Error: computation file 'x/y/secret.sql' could not be read: ", result, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void AppendLog_refuses_a_log_file_that_is_an_uninspectable_junction()
    {
        // H1 fix round (M1): "log.md" is a junction named like the log, whose
        // attributes cannot be read (the bundle root denies listing). Only the
        // early check on log.md itself refuses it with this message. (Not an
        // escape: without it the late strict check, or the OS refusing to write
        // a file over a directory, still refuses.)
        using var tmp = new TempDir();
        using var external = new TempDir();
        var tools = new OkfBundleTools(tmp.Path);
        using var junction = tmp.TryCreateUninspectableJunction("log.md", external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var result = tools.AppendLog("Update", "entry");

        Assert.Equal("Error: log.md is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to write through it.", result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [SkippableFact]
    public void AppendLog_late_guard_refuses_a_log_file_swapped_for_an_uninspectable_junction()
    {
        // H1 fix round (M1): nothing is at "log.md" when the early check runs;
        // the seam then plants an uninspectable junction named like it. Only
        // the late check on log.md itself refuses with this message. Skipped
        // before anything runs off Windows.
        Skip.IfNot(OperatingSystem.IsWindows(), "needs Windows (a junction plus deny ACEs)");
        using var tmp = new TempDir();
        using var external = new TempDir();
        var tools = new OkfBundleTools(tmp.Path);
        UninspectableJunction? junction = null;
        tools.BeforeLateReparseCheckForTest = () => junction = tmp.TryCreateUninspectableJunction("log.md", external.Path);

        try
        {
            var result = tools.AppendLog("Update", "entry");

            Skip.If(junction is null, "the junction's deny ACEs could not be set up on this machine");
            Assert.Equal("Error: log.md resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.", result);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
        }
        finally
        {
            junction?.Dispose();
        }
    }

    /// <summary>
    /// U+2028 LINE SEPARATOR, as a numeric constant: a literal one in source is
    /// invisible in every editor and diff that would have to review the payload.
    /// </summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>Every terminator <c>OkfBundleTools.OneLine</c> folds.</summary>
    private static readonly char[] EveryLineTerminator =
        ['\n', '\r', LineSeparator, (char)0x2029, (char)0x0085, (char)0x000C];

    private static void AssertNoLineStartsWith(string rendered, string marker) =>
        Assert.DoesNotContain(
            rendered.Split(EveryLineTerminator),
            line => line.TrimStart().StartsWith(marker, StringComparison.Ordinal));

    /// <summary>
    /// <c>okf_browse</c>'s "not found" line echoes its own <c>path</c>
    /// argument, and every path that does not resolve reaches it — so an
    /// arbitrary string is guaranteed to be rendered there. Same class as the
    /// <c>okf_search</c> header the PR #108 audit reproduced: an argument's
    /// provenance is not a boundary (see <c>OkfBundleTools.OneLine</c>).
    /// </summary>
    [Fact]
    public void Browse_path_cannot_forge_a_heading_in_the_not_found_message()
    {
        using var tmp = new TempDir();
        var rendered = new OkfBundleTools(tmp.Path).Browse("nowhere\n## FORGED");

        AssertNoLineStartsWith(rendered, "## FORGED");
        Assert.Equal(
            "Error: path 'nowhere ## FORGED' not found in the bundle. Use okf_browse to list available directories.",
            rendered);
    }

    /// <summary>The same argument, through <c>okf_browse</c>'s other refusal.</summary>
    [Fact]
    public void Browse_path_cannot_forge_a_heading_in_the_invalid_path_message()
    {
        using var tmp = new TempDir();
        var rendered = new OkfBundleTools(tmp.Path).Browse("../escape\n## FORGED");

        AssertNoLineStartsWith(rendered, "## FORGED");
        Assert.Equal(
            "Error: invalid path '../escape ## FORGED' — '..' segments and absolute paths are not allowed.",
            rendered);
    }

    /// <summary>
    /// The generated level listing prints the requested path as its H1. Getting
    /// a terminator in there needs a directory that really exists under that
    /// name, which is why the payload is U+2028: Windows rejects C0 controls in
    /// filenames but accepts U+2028, and U+2028 is exactly the soft terminator
    /// an LF-based reading of "this is one line" never sees.
    /// </summary>
    [Fact]
    public void Browse_path_cannot_forge_a_line_in_the_generated_listing()
    {
        using var tmp = new TempDir();
        var dir = "sub" + LineSeparator + "FORGED";
        Directory.CreateDirectory(Path.Combine(tmp.Path, dir));

        var rendered = new OkfBundleTools(tmp.Path).Browse(dir);

        AssertNoLineStartsWith(rendered, "FORGED");
        Assert.StartsWith("# sub FORGED", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ConceptNotFoundMessage</c> is shared by <c>okf_read_concept</c>,
    /// <c>okf_graph</c>, <c>okf_get_computation</c> and
    /// <c>okf_run_computation</c>, and it is the path an id that does not parse
    /// always takes — so it, not the tools' happy paths, is where an arbitrary
    /// <c>conceptId</c> gets rendered. <c>GuardConceptId</c> rejects only a NUL.
    /// </summary>
    [Fact]
    public void An_unknown_concept_id_cannot_forge_a_heading_in_the_not_found_message()
    {
        using var tmp = new TempDir();
        var tools = new OkfBundleTools(tmp.Path);
        const string Expected = "Concept 'ghost ## FORGED' not found. Use okf_browse to list available concepts.";

        foreach (var rendered in new[] { tools.ReadConcept("ghost\n## FORGED"), tools.Graph("ghost\n## FORGED") })
        {
            AssertNoLineStartsWith(rendered, "## FORGED");
            Assert.Equal(Expected, rendered);
        }
    }
}

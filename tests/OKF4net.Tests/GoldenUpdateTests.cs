// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// The update mode's pieces that can be exercised without setting the
/// variable: scope parsing (design §5.2 -- the whole list is validated before
/// any write), the atomic group commit with restore, the encoding contract of
/// what it writes, and the teardown check. The end-to-end behaviour under the
/// variable -- including the <c>--filter</c> interaction -- is verified by
/// hand once (README, "Regenerating") because it cannot run inside one test.
/// </summary>
public class GoldenUpdateTests
{
    [Fact]
    public void ParseScope_returns_an_empty_set_when_the_variable_is_unset_or_blank()
    {
        Assert.Empty(GoldenUpdate.ParseScope(null));
        Assert.Empty(GoldenUpdate.ParseScope(""));
        Assert.Empty(GoldenUpdate.ParseScope("   "));
    }

    [Fact]
    public void ParseScope_accepts_exact_test_names_and_trims_whitespace()
    {
        var scope = GoldenUpdate.ParseScope(" Info_output_matches_golden , Graph_dot_matches_golden ");
        Assert.Equal(["Graph_dot_matches_golden", "Info_output_matches_golden"], scope.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ParseScope_rejects_unknown_empty_and_duplicate_names()
    {
        var unknown = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,info_output_matches_golden"));
        Assert.Contains("unknown: info_output_matches_golden", unknown.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,,Graph_dot_matches_golden"));
        Assert.Contains("empty element", empty.Message, StringComparison.Ordinal);

        var duplicate = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,Info_output_matches_golden"));
        Assert.Contains("duplicate: Info_output_matches_golden", duplicate.Message, StringComparison.Ordinal);

        var wildcard = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Validate_*"));
        Assert.Contains("unknown: Validate_*", wildcard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseScope_reports_every_problem_at_once()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Nope,Info_output_matches_golden,,Info_output_matches_golden"));
        Assert.Contains("unknown: Nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("empty element", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate: Info_output_matches_golden", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_name_every_snapshot_file_exactly_once_and_every_golden_test()
    {
        var goldenRoot = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "golden");
        var onDisk = Directory.GetFiles(goldenRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(goldenRoot, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var declared = GoldenUpdate.Groups.Values.SelectMany(g => g).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(onDisk, declared);
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());

        var goldenTests = typeof(GoldenParityTests).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: false).Length > 0)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(goldenTests, GoldenUpdate.Groups.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Commit_writes_utf8_without_bom_and_lf_only()
    {
        using var root = new TempDir();
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Info_output_matches_golden"));

        GoldenUpdate.Commit(scope, "Info_output_matches_golden", [("info.out", "a\nb\n")], root.Path);

        var bytes = File.ReadAllBytes(Path.Combine(root.Path, "info.out"));
        Assert.Equal(new byte[] { 0x61, 0x0A, 0x62, 0x0A }, bytes);
        scope.Dispose(); // every requested test captured: no throw
    }

    [Fact]
    public void Commit_replaces_existing_files_of_the_group()
    {
        // The committed snapshots already exist; a Move that did not overwrite
        // would throw (or silently keep the old bytes). Both files must carry
        // the new content afterwards.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        root.Write("verify-dau.md", "OLD\n");
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")], root.Path);

        Assert.Equal("NEW\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        Assert.Equal("NEW2\n", File.ReadAllText(Path.Combine(root.Path, "verify-dau.md")));
    }

    [Fact]
    public void Commit_refuses_an_artefact_set_that_does_not_match_the_group()
    {
        using var root = new TempDir();
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "x\n")], root.Path));

        Assert.Contains("verify-dau.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing written", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root.Path, "verify.out")));
    }

    [Fact]
    public void Commit_reports_nothing_written_when_the_staging_directory_cannot_be_created()
    {
        // The staging directory is the first thing the commit creates (the
        // real case: TMP pointing at a file, a full or read-only temp volume).
        // The failure is injected rather than provoked through TMP/TEMP/TMPDIR:
        // those are process-wide, and every other test class -- xunit runs
        // them in parallel -- creates its TempDir from them.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        root.Write("verify-dau.md", "OLD2\n");
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(
                scope,
                "Verify_output_matches_golden",
                [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")],
                root.Path,
                () => throw new IOException("Cannot create the staging directory")));

        Assert.Contains("Cannot create the staging directory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing written", ex.Message, StringComparison.Ordinal);
        Assert.Equal("OLD\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        Assert.Equal("OLD2\n", File.ReadAllText(Path.Combine(root.Path, "verify-dau.md")));
    }

    [Fact]
    public void Commit_restores_the_group_when_a_later_move_fails()
    {
        // The first destination is a file, the second is a DIRECTORY, so the
        // second File.Move throws. The first file must be back to its
        // committed bytes, and the message must say the group was restored.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        Directory.CreateDirectory(Path.Combine(root.Path, "verify-dau.md"));
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")], root.Path));

        Assert.Contains("the group was restored to its committed state", ex.Message, StringComparison.Ordinal);
        Assert.Equal("OLD\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        var teardown = Assert.Throws<InvalidOperationException>(scope.Dispose); // not marked captured
        Assert.Contains("Verify_output_matches_golden", teardown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_removes_a_file_it_created_when_a_later_move_fails()
    {
        // No backup exists for verify.out (it did not exist), so restoring it
        // means deleting what the first move created.
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "verify-dau.md"));
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")], root.Path));

        Assert.Contains("the group was restored to its committed state", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root.Path, "verify.out")));
    }

    [Fact]
    public void Scope_teardown_fails_when_a_requested_test_never_captured()
    {
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Info_output_matches_golden,Graph_dot_matches_golden"));
        scope.MarkCaptured("Info_output_matches_golden");

        var ex = Assert.Throws<InvalidOperationException>(scope.Dispose);
        Assert.Contains("Graph_dot_matches_golden", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--filter", ex.Message, StringComparison.Ordinal);
    }
}

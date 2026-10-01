// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Snapshot tests: every output the CLI produces is compared against the
/// corresponding file under <c>tests/fixtures/golden/</c> -- read as text, and
/// for the <c>validate</c> outputs and <c>audit-v02.json</c> compared after the
/// OUTPUT's native path separators are normalised to '/'. The snapshots are
/// this project's own output, regenerable under the two regimes stated in
/// <c>tests/fixtures/README.md</c>: a presentation-only diff through the scoped
/// update mode (<see cref="GoldenUpdate"/>), a semantic diff or any edit to an
/// input bundle only after explicit arbitration and a dated README entry.
/// Conformance is not what these tests establish -- <c>docs/spec/SPEC.md</c>
/// is, through the tests that cite a section -- and the machine outputs are
/// pinned independently by <see cref="MachineOutputTests"/>. An unintended
/// diff here is a regression to investigate, never a snapshot to refresh.
/// </summary>
[Collection(GoldenUpdate.CollectionName)]
public class GoldenParityTests(GoldenUpdate.Scope scope)
{
    // `dotnet test` runs with the current directory set to the test
    // assembly's output folder (bin/Debug/net10.0), not the repo root, so
    // fixture paths are resolved relative to the repo root (located by
    // TestPaths.RepoRoot, walking up from the test assembly to the .sln).
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");
    private static readonly string GoldenRoot = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "golden");

    /// <summary>
    /// The date every <c>validate</c> snapshot is evaluated at. Pinned so the
    /// captured output cannot drift with the calendar: <c>okf_v02/metrics/dau.md</c>
    /// carries <c>stale_after: 2099-01-01T00:00:00Z</c>, and an unpinned run
    /// would gain a staleness warning on that day. Any date before the first
    /// <c>stale_after</c> in the fixtures gives the same output -- the validator
    /// has no "timestamp in the future" check -- but this is the date that was
    /// exercised when the pin was introduced, so keep it rather than a
    /// supposedly equivalent one.
    /// </summary>
    private const string PinnedAsOf = "2026-09-25";

    private static string Golden(string rel) => File.ReadAllText(Path.Combine(GoldenRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static (int Code, string Out, string Err) Run(params string[] args) => TestPaths.Run(args);

    /// <summary>
    /// Runs <paramref name="action"/> with the process's current directory
    /// temporarily set to the repo root, restoring it afterward. Needed only
    /// by <c>validate</c>/<c>info</c>: their output embeds the bundle path
    /// exactly as given on the command line (<c>Bundle.Root</c>,
    /// <c>Diagnostic.Path</c>), and the goldens were captured by invoking the
    /// CLI from the repo root with the relative argument
    /// <c>tests/fixtures/appendix_a</c> -- reproducing that exact embedded
    /// string requires doing the same here. No other
    /// test in this assembly consults <see cref="Environment.CurrentDirectory"/>
    /// (all others resolve fixtures to absolute paths), and xunit runs the
    /// methods of a single class sequentially by default, so this is safe.
    /// </summary>
    private static T WithRepoRootAsCwd<T>(Func<T> action)
    {
        var original = Environment.CurrentDirectory;
        Environment.CurrentDirectory = TestPaths.RepoRoot();
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }

    /// <summary>
    /// The one comparison path for every snapshot test, and where the capture
    /// path STARTS: the CLI (or the index generator) has already run by the
    /// time this is called, and an exception there is an ordinary failure in
    /// either mode, with nothing written. From here on, in update mode, every
    /// error surfaces as one "capture failure" whose message names its phase:
    /// the exit-code guard and the artefact reader (with its own cardinality
    /// guards) fail "before writing: nothing written"; the commit's own
    /// messages say whether staging failed (nothing written) or a move failed
    /// (the group restored, or what was not). All distinct from the deliberate
    /// <see cref="GoldenUpdate.RefusesToAssert"/> raised after a successful
    /// rewrite. Outside update mode every artefact is compared to its snapshot.
    /// </summary>
    /// <param name="test">The calling test's name (<c>nameof</c>).</param>
    /// <param name="code">The exit code observed.</param>
    /// <param name="expectedCode">The exit code the test expects.</param>
    /// <param name="artefacts">Reads each snapshot file of the test's group with the text to compare (or write), already normalised where the test normalises; may assert guards first.</param>
    private void AssertGolden(string test, int code, int expectedCode, Func<IReadOnlyList<(string Relative, string Actual)>> artefacts)
    {
        if (scope.Includes(test))
        {
            var phase = "before writing: nothing written";
            try
            {
                if (code != expectedCode)
                {
                    throw new InvalidOperationException($"exit code {code}, expected {expectedCode}");
                }

                var captured = artefacts().Select(a => (a.Relative, a.Actual)).ToList();
                phase = "in the commit, which reports its own state";
                GoldenUpdate.Commit(scope, test, captured);
            }
            catch (Exception e)
            {
                Assert.Fail($"capture failure for {test} ({phase}): {e.Message}");
            }

            Assert.Fail(GoldenUpdate.RefusesToAssert(test));
        }

        Assert.Equal(expectedCode, code);
        foreach (var (relative, actual) in artefacts())
        {
            Assert.Equal(Golden(relative), actual);
        }
    }

    [Fact]
    public void Validate_output_and_exitcode_match_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/appendix_a", "--as-of", PinnedAsOf));

        // The snapshot was captured on Linux, where paths display with '/'.
        // Per-file diagnostic paths combine the literal root with
        // Path.Combine, which emits the OS-native separator, so on Windows
        // the tail of each path comes out with '\'. A platform display
        // artifact, not a semantic difference: normalised in the OUTPUT before
        // comparing -- and, in update mode, written normalised, so the
        // snapshot stays the same on every platform.
        AssertGolden(nameof(Validate_output_and_exitcode_match_golden), r.Code, 0,
            () => [("validate.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_v02_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_v02_fixture_matches_golden), r.Code, 0,
            () => [("validate-v02.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_computation_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_computation", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_computation_fixture_matches_golden), r.Code, 0,
            () => [("validate-computation.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_reserved_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_reserved", "--as-of", PinnedAsOf));
        // §11 condition 3 fails: the bundle is non-conformant, and this CLI's
        // contract maps that verdict to exit code 1 -- the spec defines the
        // verdict, the integer is ours.
        AssertGolden(nameof(Validate_reserved_fixture_matches_golden), r.Code, 1,
            () => [("validate-reserved.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Info_output_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("info", "tests/fixtures/appendix_a"));
        AssertGolden(nameof(Info_output_matches_golden), r.Code, 0, () => [("info.out", r.Out)]);
    }

    /// <summary>
    /// The date is pinned with <c>--as-of</c> so the output cannot drift with
    /// the calendar: at 2099-06-01 the fixture's one <c>stale_after</c> has
    /// passed, so the report lists a stale finding.
    /// </summary>
    [Fact]
    public void Audit_report_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01"));
        // Every path in this output is a concept id, always '/'-normalised by
        // ConceptId.FromPath, so the comparison is as read.
        AssertGolden(nameof(Audit_report_matches_golden), r.Code, 0, () => [("audit-v02.out", r.Out)]);
    }

    [Fact]
    public void Audit_json_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01", "--json"));
        // Only the findings' `path` carries a native separator, and the
        // serializer escapes each backslash as the two-character sequence `\\`
        // in the JSON text; a single-char Replace would turn that pair into
        // "//", so the search pattern is the escaped sequence.
        AssertGolden(nameof(Audit_json_matches_golden), r.Code, 0, () => [("audit-v02.json", r.Out.Replace("\\\\", "/"))]);
    }

    [Fact]
    public void Graph_dot_matches_golden()
    {
        var r = Run("graph", BundlePath, "--dot");
        AssertGolden(nameof(Graph_dot_matches_golden), r.Code, 0, () => [("graph.dot", r.Out)]);
    }

    [Fact]
    public void Fmt_output_matches_golden()
    {
        var r = Run("fmt", Path.Combine(BundlePath, "tables", "users.md"));
        AssertGolden(nameof(Fmt_output_matches_golden), r.Code, 0, () => [("fmt/users.md", r.Out)]);
    }

    [Fact]
    public void Index_generation_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        AssertGolden(nameof(Index_generation_matches_golden), 0, 0, () =>
        {
            // Guards, inside the capture path so a failure is a capture
            // failure in update mode: three files written, three on disk, and
            // exactly the 3 generated index.md files plus the 5 source
            // documents copied in from appendix_a -- catches a file created in
            // excess or a net deletion; not a modified original, nor a
            // delete-and-create.
            Assert.Equal(3, written.Count);
            Assert.Equal(3, Directory.GetFiles(tmp.Path, "index.md", SearchOption.AllDirectories).Length);
            Assert.Equal(8, Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories).Length);

            return
            [
                ("index-input/index.md", File.ReadAllText(Path.Combine(tmp.Path, "index.md"))),
                ("index-input/datasets/index.md", File.ReadAllText(Path.Combine(tmp.Path, "datasets", "index.md"))),
                ("index-input/tables/index.md", File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"))),
            ];
        });
    }

    /// <summary>
    /// `verify` writes, so it runs against a throwaway copy of the v0.2 fixture
    /// rather than the fixture itself. The golden is hand-authored and verified
    /// against the design spec's output format -- there is no upstream `verify`
    /// to capture. The date is pinned with --at so it cannot drift.
    /// </summary>
    [Fact]
    public void Verify_output_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"), tmp.Path);

        var r = Run("verify", tmp.Path, "metrics/dau", "metrics/legacy", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        // stdout alone would stay green if the verb printed the right line and
        // wrote the wrong stamp, touched `generated`, or mangled the document.
        // The written file is the artefact that matters, so it is in the group.
        AssertGolden(nameof(Verify_output_matches_golden), r.Code, 0, () =>
        [
            ("verify.out", r.Out),
            ("verify-dau.md", File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md"))),
        ]);
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
}

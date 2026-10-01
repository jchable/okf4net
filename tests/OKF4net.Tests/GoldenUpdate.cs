// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Tests;

/// <summary>
/// The scoped update mode for the snapshots under <c>tests/fixtures/golden/</c>
/// (design §2, §5.2). Set <see cref="Variable"/> to a comma-separated list of
/// <see cref="GoldenParityTests"/> method names; each named test then REWRITES
/// its group of snapshot files from the value it would have compared, and
/// fails on purpose (<see cref="RefusesToAssert"/>): in update mode the
/// expected side was just produced by the harness that produced the actual
/// side, so a comparison would be a tautology, and a tautology reported green
/// is how a stale variable in a shell disarms a snapshot without anyone
/// noticing. Two runs, never one: rewrite, read the diff, re-run without the
/// variable.
///
/// <para>The scope is validated as a whole before the first test runs
/// (<see cref="Scope"/> is the collection fixture), so a typo rejects the list
/// instead of rewriting some groups and skipping one. At teardown the fixture
/// checks that every named test actually captured -- a test excluded by
/// <c>dotnet test --filter</c> would otherwise leave a red run that rewrote
/// less than it announced. Known limit: a filter that excludes the whole
/// collection never constructs this fixture, so the variable is then without
/// effect and the run green; nothing is written and no diff appears. The mode
/// captures; it never authorises: a semantic diff, or any edit to an input
/// bundle, still takes explicit arbitration and a dated entry in
/// <c>tests/fixtures/README.md</c>.</para>
/// </summary>
public static class GoldenUpdate
{
    /// <summary>The environment variable naming the tests to rewrite.</summary>
    public const string Variable = "OKF_UPDATE_GOLDEN";

    /// <summary>The xunit collection every snapshot test belongs to, so they share one <see cref="Scope"/>.</summary>
    public const string CollectionName = "GoldenParity";

    /// <summary>
    /// Test method name → the snapshot files (relative to <c>tests/fixtures/golden/</c>,
    /// '/' separators) that test captures as one group. A group is written
    /// whole or not at all. <c>GoldenUpdateTests</c> checks this table names
    /// every file on disk exactly once and every <c>[Fact]</c> in
    /// <see cref="GoldenParityTests"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Validate_output_and_exitcode_match_golden"] = ["validate.out"],
        ["Validate_v02_fixture_matches_golden"] = ["validate-v02.out"],
        ["Validate_computation_fixture_matches_golden"] = ["validate-computation.out"],
        ["Validate_reserved_fixture_matches_golden"] = ["validate-reserved.out"],
        ["Info_output_matches_golden"] = ["info.out"],
        ["Audit_report_matches_golden"] = ["audit-v02.out"],
        ["Audit_json_matches_golden"] = ["audit-v02.json"],
        ["Graph_dot_matches_golden"] = ["graph.dot"],
        ["Fmt_output_matches_golden"] = ["fmt/users.md"],
        ["Index_generation_matches_golden"] = ["index-input/index.md", "index-input/datasets/index.md", "index-input/tables/index.md"],
        ["Verify_output_matches_golden"] = ["verify.out", "verify-dau.md"],
    };

    private static string DefaultGoldenRoot => Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "golden");

    /// <summary>
    /// Parses the variable's value into the set of test names to rewrite.
    /// Unset or blank means "not in update mode". Exact names only, case
    /// sensitive, no wildcard; an unknown name, an empty element or a
    /// duplicate rejects the WHOLE list, with every problem listed, before
    /// anything is written.
    /// </summary>
    public static IReadOnlySet<string> ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in raw.Split(','))
        {
            var name = element.Trim();
            if (name.Length == 0)
            {
                problems.Add("empty element");
            }
            else if (!Groups.ContainsKey(name))
            {
                problems.Add("unknown: " + name);
            }
            else if (!names.Add(name))
            {
                problems.Add("duplicate: " + name);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Variable} rejected, nothing was rewritten. Exact GoldenParityTests method names, comma-separated, no wildcard. Problems: "
                + string.Join("; ", problems) + ". Known names: " + string.Join(", ", Groups.Keys.Order(StringComparer.Ordinal)));
        }

        return names;
    }

    /// <summary>
    /// Rewrites one test's snapshot group from the values that test would have
    /// compared. Guards first: the artefact set must equal the group exactly,
    /// or nothing is written. Then every new file is staged (UTF-8 without
    /// BOM, bytes as given -- LF stays LF), every existing destination is
    /// backed up, and the staged files are moved into place one by one. If a
    /// move fails, every destination already replaced is put back from its
    /// backup, so the committed group is whole again -- and if THAT fails, the
    /// message says which files are not restored rather than claiming they are.
    /// </summary>
    /// <param name="scope">The run's scope; the test is marked captured on success.</param>
    /// <param name="test">The <see cref="GoldenParityTests"/> method name.</param>
    /// <param name="artefacts">Each snapshot file of the group with the exact text to write.</param>
    /// <param name="goldenRoot">Where the snapshots live; tests pass a temporary directory.</param>
    public static void Commit(Scope scope, string test, IReadOnlyList<(string Relative, string Content)> artefacts, string? goldenRoot = null) =>
        Commit(scope, test, artefacts, goldenRoot, () => Directory.CreateTempSubdirectory("okf-golden-"));

    /// <summary>
    /// The public overload's implementation, with the staging directory's
    /// creation injected, so a test can make that first step fail without
    /// touching the process-wide temp variables -- which every other test
    /// class running in parallel reads.
    /// </summary>
    internal static void Commit(Scope scope, string test, IReadOnlyList<(string Relative, string Content)> artefacts, string? goldenRoot, Func<DirectoryInfo> createStaging)
    {
        var root = goldenRoot ?? DefaultGoldenRoot;
        var expected = Groups[test].Order(StringComparer.Ordinal).ToList();
        var given = artefacts.Select(a => a.Relative).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(given, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"nothing written: the group of {test} is [{string.Join(", ", expected)}] but the test offered [{string.Join(", ", given)}].");
        }

        DirectoryInfo? staging = null;
        try
        {
            // Phase 1, before writing: create the staging directory, stage
            // every new file and back up every existing destination. A failure
            // anywhere here -- the temp directory itself included -- has
            // replaced nothing, and says so.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var planned = new List<(string Staged, string Destination, string? Backup)>();
            try
            {
                staging = createStaging();
                foreach (var (relative, content) in artefacts)
                {
                    var native = relative.Replace('/', Path.DirectorySeparatorChar);
                    var staged = Path.Combine(staging.FullName, "new", native);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, utf8.GetBytes(content));

                    var destination = Path.Combine(root, native);
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        backup = Path.Combine(staging.FullName, "old", native);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(destination, backup);
                    }

                    planned.Add((staged, destination, backup));
                }
            }
            catch (Exception stagingFailure) when (stagingFailure is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{stagingFailure.Message}; nothing written (staging failed before any file was replaced).", stagingFailure);
            }

            // Phase 2, while replacing: move the staged files into place. A
            // failure here puts back what was already replaced.
            var replaced = new List<(string Destination, string? Backup)>();
            try
            {
                foreach (var (staged, destination, backup) in planned)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(staged, destination, overwrite: true);
                    replaced.Add((destination, backup));
                }
            }
            catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
            {
                var notRestored = new List<string>();
                foreach (var (destination, backup) in replaced)
                {
                    try
                    {
                        if (backup is null)
                        {
                            File.Delete(destination);
                        }
                        else
                        {
                            File.Copy(backup, destination, overwrite: true);
                        }
                    }
                    catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                    {
                        notRestored.Add(destination + " (" + restoreFailure.Message + ")");
                    }
                }

                var state = notRestored.Count == 0
                    ? "the group was restored to its committed state"
                    : "RESTORE FAILED, check `git status tests/fixtures/golden` -- not restored: " + string.Join("; ", notRestored);
                throw new InvalidOperationException($"{moveFailure.Message}; {state}.", moveFailure);
            }
        }
        finally
        {
            try
            {
                staging?.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the staging directory.
            }
        }

        scope.MarkCaptured(test);
    }

    /// <summary>The deliberate failure a test raises after rewriting its group.</summary>
    public static string RefusesToAssert(string test) =>
        $"{Variable}: {test} REWROTE its snapshot group [{string.Join(", ", Groups[test])}] and refuses to assert -- the expected side"
        + " was just produced by the same harness as the actual side, so this run proves nothing. Review"
        + " `git diff tests/fixtures/golden`, then re-run WITHOUT the variable to actually check it. If the"
        + " diff is semantic (a verdict, a count, an exit code, §8/§9 structure, a projected value, a written"
        + " document), it needs explicit arbitration and a dated entry in tests/fixtures/README.md.";

    /// <summary>
    /// The collection fixture shared by every snapshot test: parses the scope
    /// once before the first test, records which tests captured, and at
    /// teardown fails if a requested test never did. xunit constructs it
    /// through its ONE public constructor; the internal one is for tests of
    /// the mode itself.
    /// </summary>
    public sealed class Scope : IDisposable
    {
        private readonly HashSet<string> _captured = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>Reads the scope from <see cref="Variable"/>; xunit calls this before the first test of the collection.</summary>
        public Scope()
            : this(ParseScope(Environment.GetEnvironmentVariable(Variable)))
        {
        }

        internal Scope(IReadOnlySet<string> requested)
        {
            Requested = requested;
        }

        /// <summary>The tests to rewrite; empty when not in update mode.</summary>
        public IReadOnlySet<string> Requested { get; }

        /// <summary>Whether <paramref name="test"/> is in update mode this run.</summary>
        public bool Includes(string test) => Requested.Contains(test);

        /// <summary>Records that <paramref name="test"/> rewrote its group.</summary>
        public void MarkCaptured(string test)
        {
            lock (_gate)
            {
                _captured.Add(test);
            }
        }

        /// <summary>
        /// Fails the collection if a requested test never captured -- excluded
        /// by <c>--filter</c>, or failed on the capture path -- so a red run
        /// cannot have rewritten less than it announced.
        /// </summary>
        public void Dispose()
        {
            List<string> missing;
            lock (_gate)
            {
                missing = Requested.Except(_captured, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{Variable} named tests that did not capture: {string.Join(", ", missing)}. Either dotnet test --filter excluded"
                    + " them, or they failed on the capture path. Nothing was verified for them; the other groups may have been rewritten.");
            }
        }
    }
}

/// <summary>Binds the <see cref="GoldenUpdate.Scope"/> fixture to the snapshot tests' collection.</summary>
[CollectionDefinition(GoldenUpdate.CollectionName)]
public sealed class GoldenParityCollection : ICollectionFixture<GoldenUpdate.Scope>
{
}

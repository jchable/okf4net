using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Golden parity tests: every output the C# port produces is diffed against
/// the corresponding reference file under <c>tests/fixtures/golden/</c>,
/// captured (Task 12) by running the real Rust <c>okf</c> binary against
/// <c>tests/fixtures/appendix_a</c> on Linux. Any divergence beyond the one
/// documented platform artifact (see
/// <see cref="Validate_output_and_exitcode_match_rust"/>) is a port bug in
/// the C# side, never a reason to touch a golden fixture.
/// </summary>
public class GoldenParityTests
{
    // `dotnet test` runs with the current directory set to the test
    // assembly's output folder (bin/Debug/net10.0), not the repo root, so
    // fixture paths are resolved relative to the repo root (located by
    // walking up from the test assembly to the .sln), matching the pattern
    // used by CliTests.RepoRoot.
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

    private static readonly string BundlePath = Path.Combine(RepoRoot(), "tests", "fixtures", "appendix_a");
    private static readonly string GoldenRoot = Path.Combine(RepoRoot(), "tests", "fixtures", "golden");

    private static string Golden(string rel) => File.ReadAllText(Path.Combine(GoldenRoot, rel));

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (OkfCli.Run(args, o, e), o.ToString(), e.ToString());
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the process's current directory
    /// temporarily set to the repo root, restoring it afterward. Needed only
    /// by <c>validate</c>/<c>info</c>: their output embeds the bundle path
    /// exactly as given on the command line (<c>Bundle.Root</c>,
    /// <c>Diagnostic.Path</c>), and per the Task 12 report the goldens were
    /// captured by invoking the Rust binary from the repo root with the
    /// relative argument <c>tests/fixtures/appendix_a</c> -- reproducing
    /// that exact embedded string requires doing the same here. No other
    /// test in this assembly consults <see cref="Environment.CurrentDirectory"/>
    /// (all others resolve fixtures to absolute paths), and xunit runs the
    /// methods of a single class sequentially by default, so this is safe.
    /// </summary>
    private static T WithRepoRootAsCwd<T>(Func<T> action)
    {
        var original = Environment.CurrentDirectory;
        Environment.CurrentDirectory = RepoRoot();
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }

    [Fact]
    public void Validate_output_and_exitcode_match_rust()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/appendix_a"));
        Assert.Equal(int.Parse(Golden("validate.exitcode")), r.Code);

        // The golden was captured on Linux, where Rust's PathBuf::display
        // prints '/'. Our port's per-file diagnostic paths are built by
        // combining the literal root "tests/fixtures/appendix_a" with
        // Path.Combine for every subsequent path component, and
        // Path.Combine emits the OS-native separator -- so on Windows the
        // tail of each path (everything past the root) comes out with '\'.
        // This is a platform display artifact, not a semantic difference,
        // so it alone is normalized in the C# OUTPUT (never the golden)
        // before comparing. Every other golden in this file is compared
        // strictly byte-for-byte with no normalization.
        var normalized = r.Out.Replace('\\', '/');
        Assert.Equal(Golden("validate.out"), normalized);
    }

    [Fact]
    public void Info_output_matches_rust()
    {
        var r = WithRepoRootAsCwd(() => Run("info", "tests/fixtures/appendix_a"));
        Assert.Equal(0, r.Code);
        Assert.Equal(Golden("info.out"), r.Out);
    }

    [Fact]
    public void Graph_dot_matches_rust()
    {
        // Concept ids -- and therefore every string `graph --dot` prints --
        // are always normalized to '/' by ConceptId.FromPath regardless of
        // the bundle root's path style, so the absolute Windows BundlePath
        // is safe here: no separator artifact, no repo-root CWD dance
        // needed, and the comparison below is strict byte-for-byte.
        var r = Run("graph", BundlePath, "--dot");
        Assert.Equal(0, r.Code);
        Assert.Equal(Golden("graph.dot"), r.Out);
    }

    [Fact]
    public void Fmt_output_matches_rust()
    {
        var r = Run("fmt", Path.Combine(BundlePath, "tables", "users.md"));
        Assert.Equal(0, r.Code);
        Assert.Equal(Golden(Path.Combine("fmt", "users.md")), r.Out);
    }

    [Fact]
    public void Index_generation_matches_rust()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);
        Assert.Equal(3, written.Count);

        var generatedIndexFiles = Directory.GetFiles(tmp.Path, "index.md", SearchOption.AllDirectories);
        Assert.Equal(3, generatedIndexFiles.Length);

        string[] relIndexPaths =
        [
            "index.md",
            Path.Combine("datasets", "index.md"),
            Path.Combine("tables", "index.md"),
        ];
        foreach (var rel in relIndexPaths)
        {
            var actual = File.ReadAllText(Path.Combine(tmp.Path, rel));
            var expected = Golden(Path.Combine("index-input", rel));
            Assert.Equal(expected, actual);
        }

        // Exactly the 3 generated index.md files plus the 5 original
        // source documents copied in -- no extras.
        var allFiles = Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories);
        Assert.Equal(8, allFiles.Length);
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

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using OKF4net;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;
using TreeSitterExtractor = OkfProducer.CodeGraph.TreeSitter.TreeSitterExtractor;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// The end-to-end producer run, made permanent.
///
/// <para>Every earlier task in this plan that needed a whole run -- scan, extract with the real
/// tree-sitter grammar, resolve, generate, write -- rebuilt it as a throwaway script, ran it once by
/// hand, and threw it away. <c>producers/</c> is outside CI by decision, so nothing caught the next
/// drift either. This is that harness's permanent home: <see cref="Run"/> is the one composition both
/// <see cref="CheckTests"/> and <see cref="BlastRadiusTests"/> go through, and the golden bundle beside
/// it is what holds its output still.</para>
///
/// <para><b>It is not the shipped composition.</b> The CLI does not compose the code-graph stage yet
/// (Task 13 owns its flags), so this is the producer's pipeline assembled here, from the same
/// production types. What it verifies is those types; what it cannot verify is a CLI that wires them
/// differently -- which is why <see cref="ExistingBundleFrontmatter"/> lives in
/// <c>OkfProducer.Core</c> rather than in this file: it is the one piece a CLI could forget and
/// silently destroy hand-written descriptions with, so it is production code that both callers share
/// rather than a helper only the tests have.</para>
/// </summary>
internal static class ProducerFixture
{
    /// <summary>
    /// The concept-id prefix the generator owns, as the CLI will pass it -- the same value
    /// <see cref="PruningTests"/> uses, for the same reason: pruning is bounded by it.
    /// </summary>
    public const string OwnedPrefix = "code";

    /// <summary>
    /// The directory name the fixture repository must carry wherever it is copied.
    /// <c>RepositoryScanner</c> reads the repository's <i>directory name</i> as its name, and that
    /// name reaches <c>overview</c>'s title, description and body -- so a copy under a random
    /// temporary name would differ from the golden on three lines for a reason that has nothing to do
    /// with the code.
    /// </summary>
    public const string RepoDirectoryName = "fixture-repo";

    /// <summary>The permalink base the golden was captured with (§4.3: a code concept carries a <c>resource</c> only when a repo URL and a rev are both supplied).</summary>
    public const string RepoUrl = "https://example.com/acme/fixture";

    /// <summary>The ref permalinks are built against -- a branch name, never a sha (§4.3).</summary>
    public const string Rev = "main";

    /// <summary>The committed fixture repository: a tiny C# repo, one occurrence of each shape (see <c>fixtures/README.md</c>).</summary>
    public static string FixtureRepo { get; } = Path.Combine(FixturesRoot(), RepoDirectoryName);

    /// <summary>The committed golden bundle: what <see cref="Run"/> produces from <see cref="FixtureRepo"/>.</summary>
    public static string GoldenBundle { get; } = Path.Combine(FixturesRoot(), "golden");

    /// <summary>
    /// One complete generation of <paramref name="repoPath"/> into <paramref name="outPath"/>, under
    /// <see cref="WritePolicy.Update"/> -- the path a regeneration actually takes, pruning and field
    /// preservation included.
    /// </summary>
    public static RunOutcome Run(string repoPath, string outPath)
    {
        var snapshot = new RepositoryScanner().Scan(repoPath);

        using var extractor = new TreeSitterExtractor();
        var graph = new CodeGraphBuilder(extractor, [CSharpProfile.Instance], [new NameMatchResolver()])
            .Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        var options = new GenerateOptions
        {
            RepoUrl = RepoUrl,
            Rev = Rev,
            Profiles = [CSharpProfile.Instance],

            // The line that makes a manual description survive a regeneration -- and therefore the
            // line without which --check would report every hand-edited concept as drift for ever.
            ExistingFrontmatter = ExistingBundleFrontmatter.For(outPath),
        };

        var concepts = new ConceptGenerator().Generate(snapshot, graph, options);
        var manifest = GenerationManifest.ForRun(OwnedPrefix, concepts, graph.Status, ScopeOptions.Default);
        var write = new BundleWriter().Write(outPath, concepts, WritePolicy.Update, repoPath, manifest, graph.Status);

        return new RunOutcome(concepts.Count, graph.Status, write);
    }

    /// <summary>What one <see cref="Run"/> produced, for the assertions that care about counts rather than bytes.</summary>
    public sealed record RunOutcome(int Generated, RunStatus Status, WriteResult Write);

    /// <summary>Validates <paramref name="bundlePath"/> against a fixed clock, so no assertion here depends on today's date (§8.1).</summary>
    public static ValidationOutcome Validate(string bundlePath) =>
        new BundleValidationRunner().Validate(bundlePath, new FixedClock(new DateOnly(2026, 1, 1)));

    /// <summary>
    /// A copy of the fixture repository in a temporary directory that is <b>not</b> inside any git
    /// repository, under <see cref="RepoDirectoryName"/>.
    ///
    /// <para><b>Why a copy at all, when the fixture is right there on disk.</b> The committed fixture
    /// sits inside <i>this</i> repository's working tree, and `git rev-parse HEAD` run in a plain
    /// subdirectory of a git checkout answers for the enclosing repository -- so generating in place
    /// would stamp <c>overview</c> with OKF4net's own HEAD, and the golden would go stale on the very
    /// next commit to this repo, whatever the producer did. Outside git the stamp falls back to the
    /// wall clock and §6.2's two-field exclusion applies, which is the case the golden is captured
    /// under (ruling R5).</para>
    /// </summary>
    public static TempDir CopyRepoOutsideGit()
    {
        var temp = new TempDir();
        var repo = Path.Combine(temp.Path, RepoDirectoryName);
        CopyDirectory(FixtureRepo, repo);

        Assert.False(
            IsInsideGitRepository(repo),
            $"'{repo}' is inside a git repository, so `generated.at` and `revision` would be stamped from its HEAD "
                + "and this fixture would no longer exercise the outside-git path the golden was captured under. "
                + "Point TMPDIR/TEMP somewhere outside every git checkout.");

        return temp;
    }

    /// <summary>
    /// A copy of the fixture repository inside a freshly created git repository, under
    /// <see cref="RepoDirectoryName"/> and with everything committed -- the case where §6.2 excludes
    /// nothing, because <c>generated.at</c> and <c>revision</c> come from a HEAD that exists.
    /// </summary>
    public static TempDir CopyRepoIntoGit()
    {
        var temp = new TempDir();
        var repo = Path.Combine(temp.Path, RepoDirectoryName);
        CopyDirectory(FixtureRepo, repo);

        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "producer-fixture@example.invalid");
        Git(repo, "config", "user.name", "Producer Fixture");
        Git(repo, "config", "commit.gpgsign", "false");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "fixture");

        Assert.True(IsInsideGitRepository(repo), $"'{repo}' was initialised as a git repository but has no resolvable HEAD.");

        return temp;
    }

    /// <summary>
    /// A copy of the golden bundle in a temporary directory, free to be mutated by a test. The
    /// temporary directory <b>is</b> the bundle root: nothing reads a bundle's own directory name, and
    /// one less level keeps every call site's paths short.
    /// </summary>
    public static TempDir CopyGoldenBundle()
    {
        var temp = new TempDir();
        CopyDirectory(GoldenBundle, temp.Path);
        return temp;
    }

    /// <summary>
    /// Rewrites <see cref="GoldenBundle"/> from the repository copy in <paramref name="workspace"/>,
    /// from scratch -- the golden is machine output, so it is captured, never merged into.
    ///
    /// <para>Reached only from <c>CheckTests</c> under <c>OKFGEN_UPDATE_GOLDEN=1</c>. It generates into
    /// the workspace first and copies the finished result over, so an interrupted run cannot leave the
    /// committed golden half-written.</para>
    /// </summary>
    public static void RegenerateGolden(TempDir workspace)
    {
        var staging = Path.Combine(workspace.Path, "golden");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Run(Path.Combine(workspace.Path, RepoDirectoryName), staging);

        if (Directory.Exists(GoldenBundle))
        {
            Directory.Delete(GoldenBundle, recursive: true);
        }

        CopyDirectory(staging, GoldenBundle);
    }

    /// <summary>Rewrites one source file of a repository copy through <paramref name="edit"/>, always with <c>\n</c> line endings.</summary>
    public static void EditSource(string repoPath, string relativePath, Func<string, string> edit)
    {
        var path = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);
        var edited = edit(source);

        Assert.NotEqual(source, edited);
        File.WriteAllText(path, edited);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> immediately before the file's last <c>}</c> -- the closing brace
    /// of its last type, in a fixture where every file ends with one.
    ///
    /// <para><b>Why the end and not anywhere else.</b> A declaration's concept records its line span
    /// (<c>resource</c>, and the <c>## Signatures</c> label), so text inserted <i>above</i> a symbol
    /// moves that symbol's lines and rewrites its concept -- churn caused by the edit's position, not
    /// by what it declared. Appending inside the last type moves nothing but the type's own closing
    /// line, which keeps a blast-radius measurement about the mutation instead of about the diff's
    /// offset.</para>
    /// </summary>
    public static Func<string, string> InsertAtEndOfLastType(string text) => source =>
    {
        var brace = source.LastIndexOf('}');
        Assert.True(brace >= 0, "the fixture source has no closing brace to insert before.");

        return source[..brace] + text + source[brace..];
    };

    /// <summary>
    /// The exact text of <c>Scanner.Gone</c>, doc comment included -- the symbol both
    /// <see cref="BlastRadiusTests"/> and <see cref="CheckTests"/> delete, so it lives here rather
    /// than in one of them with a copy in the other.
    ///
    /// <para>It is the <b>last</b> declaration in its file on purpose: deleting text above another
    /// declaration would move that declaration's lines and rewrite its concept too, and a
    /// blast-radius measurement would then be about the edit's position rather than the deletion.
    /// Every escape here is <c>\n</c> rather than a verbatim literal, so the constant's runtime value
    /// is LF whatever line endings git checks this <c>.cs</c> file out with -- the fixture it is
    /// matched against is pinned to LF by <c>.gitattributes</c>.</para>
    /// </summary>
    public const string GoneMethod =
        "\n    /// <summary>Reads a legacy manifest. The symbol a mutation deletes; it is last in the file on purpose.</summary>\n"
        + "    public void Gone()\n    {\n    }\n";

    /// <summary>Removes <see cref="GoneMethod"/> from a repository copy's <c>src/Scanner.cs</c>, asserting it was there to remove.</summary>
    public static void DeleteGoneMethod(string repoPath) =>
        EditSource(repoPath, "src/Scanner.cs", source =>
        {
            Assert.Contains(GoneMethod, source, StringComparison.Ordinal);
            return source.Replace(GoneMethod, string.Empty, StringComparison.Ordinal);
        });

    /// <summary>
    /// Whether <paramref name="relativePath"/> is a concept file rather than one of the two things a
    /// bundle carries that no design decision controls: an <c>index.md</c>, rewritten mechanically by
    /// <c>IndexGenerator</c> whenever a directory's children change, and the generation manifest.
    ///
    /// <para>Compared as a whole file NAME, never as a suffix: a concept legitimately named
    /// <c>build-index</c> ends with "index.md" too, and dropping it would hide real churn. Shared by
    /// every caller precisely so the two spellings cannot drift apart.</para>
    /// </summary>
    public static bool IsConceptFile(string relativePath) =>
        relativePath.EndsWith(".md", StringComparison.Ordinal)
        && !string.Equals(Path.GetFileName(relativePath), "index.md", StringComparison.Ordinal);

    /// <summary>
    /// Fails loudly, with an explanation, instead of letting a test that needs a real git checkout
    /// degrade into something that passes for the wrong reason.
    /// </summary>
    public static void RequireGit()
    {
        using var temp = new TempDir();

        try
        {
            Git(temp.Path, "init", "-q");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Assert.Fail("This test needs a working `git` on PATH: it builds a real repository so the HEAD-commit stamp has a HEAD to read. " + ex.Message);
        }
    }

    /// <summary>Whether <c>git</c> can resolve a HEAD commit for <paramref name="path"/> -- the same question <c>BundleDrift.Check</c> asks to decide whether any field is excluded.</summary>
    public static bool IsInsideGitRepository(string path) => GitRevision.HeadSha(path) is not null;

    /// <summary>
    /// A directory reparse point at <paramref name="link"/> pointing at <paramref name="target"/>.
    ///
    /// <para>A symbolic link where the platform allows one; a junction on Windows, where creating a
    /// symbolic link needs SeCreateSymbolicLinkPrivilege (Developer Mode or an elevated shell) that an
    /// ordinary test run does not have. A junction is the same kind of object for every purpose these
    /// tests have: <c>Path.GetFullPath</c> does not resolve it, <c>File.Exists</c> and
    /// <c>File.Delete</c> follow it, and <c>FileSystemInfo.LinkTarget</c> reports it.</para>
    ///
    /// <para>If neither can be created this fails loudly rather than letting the test pass without a
    /// link -- which is exactly the shape of assertion this whole exercise exists to root out.</para>
    ///
    /// <para>Shared here rather than kept private to one test class: two files now need it, and a
    /// second copy is a second place for the fallback to rot.</para>
    /// </summary>
    public static void CreateDirectoryLink(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail($"could not create a symbolic link at '{link}': {ex.Message}");
            }

            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            process?.WaitForExit();
        }

        Assert.True(
            new DirectoryInfo(link).LinkTarget is not null,
            $"no symbolic link or junction could be created at '{link}', so this test would pass without exercising anything. "
                + "On Windows a junction needs no privilege; if even that failed, the temporary directory is on a filesystem "
                + "that has no reparse points and this test cannot run there.");
    }

    /// <summary>
    /// A <b>file</b> symbolic link at <paramref name="link"/> pointing at <paramref name="target"/>,
    /// or <see langword="false"/> when this platform will not create one.
    ///
    /// <para><b>The one link shape with no Windows fallback, which is why this reports failure instead
    /// of asserting.</b> A junction is a directory, so it cannot stand in for a link whose name has to
    /// be a file -- and a file symbolic link needs SeCreateSymbolicLinkPrivilege, which an ordinary
    /// Windows test run does not have. A caller that gets <see langword="false"/> must still exercise
    /// something (see <see cref="CreateDirectoryLink"/> for the substitute) and must say in its own
    /// assertions which half of the property it could and could not establish. Returning false and
    /// letting a test quietly assert nothing is the outcome this comment exists to prevent.</para>
    /// </summary>
    public static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return new FileInfo(link).LinkTarget is not null;
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    /// <summary>Every file under <paramref name="root"/>, keyed by its <c>/</c>-separated relative path.</summary>
    public static Dictionary<string, byte[]> SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    /// <summary>Runs <c>git</c> in <paramref name="cwd"/>, throwing with the tool's own stderr on a non-zero exit.</summary>
    public static void Git(string cwd, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start git for test setup.");
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");
        }
    }

    /// <summary>A temporary directory, deleted on <see cref="Dispose"/>.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FixturesRoot() => Path.Combine(RepositoryRoot(), "producers", "tests", "OkfProducer.Tests", "fixtures");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OKF4net.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find OKF4net.sln walking up from the test assembly.");
    }
}

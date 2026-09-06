// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using OKF4net;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

// `CodeGraph` alone would bind to the sibling namespace OkfProducer.Tests.CodeGraph, not to the type
// (CS0118) -- see the same alias, and the same reason, at the top of ConceptGenerator.cs.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §6: reproducibility, and the HEAD-commit stamp that makes it possible. A wall-clock <c>generated.at</c>
/// would make a later task's <c>--check</c> (a byte-for-byte regeneration diff) fail forever on that one
/// field -- exactly the guard that stops a stale bundle from shipping silently. Reading it off the HEAD
/// commit instead means a source-identical run is byte-identical too, and the stamp says which state of
/// the code the bundle reflects rather than when the command happened to run.
///
/// <para>Most of these tests run against THIS repository's own real git history (<see cref="RepoRoot"/>),
/// not a fixture path, because the property under test -- "the same commit yields the same stamp" -- can
/// only be pinned against a commit that actually exists. Those tests call <see cref="RequireGitCheckout"/>
/// first, so a run where <c>git</c> itself is unavailable fails with an explanation instead of comparing
/// two wall-clock reads that happen to land in the same second. One test builds its OWN throwaway git
/// repository instead, so it can pin the exact expected string against a commit whose author date,
/// committer date and offset it controls -- see
/// <see cref="HeadCommitInstant_uses_the_committer_date_not_the_author_date_and_normalises_to_UTC"/>.
/// A fixture directory outside git is covered separately, by
/// <see cref="Outside_a_git_repository_the_wall_clock_stands_in_and_no_revision_is_written"/>.</para>
/// </summary>
public class DeterminismTests
{
    [Fact]
    public void Two_runs_over_the_same_source_are_byte_identical()
    {
        RequireGitCheckout();

        using var a = new TempDir();
        using var b = new TempDir();
        var resultA = Write(Generate(), a.Path);
        var resultB = Write(Generate(), b.Path);

        // A floor: without it, a producer that silently wrote ZERO concepts (index.md aside --
        // IndexGenerator emits one regardless) would satisfy every assertion below vacuously.
        Assert.True(resultA.Written > 0, "the run wrote no concepts at all -- nothing here would discriminate a producer gone silent.");
        Assert.Equal(resultA.Written, resultB.Written);

        var filesA = RelativeMdFiles(a.Path);
        var filesB = RelativeMdFiles(b.Path);

        Assert.NotEmpty(filesA);

        // Both directions: the SAME set of relative paths, not "does everything in a also exist in
        // b" -- which would stay silent about a file b has and a does not.
        Assert.Equal(filesA, filesB);

        foreach (var rel in filesA)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(a.Path, rel)), File.ReadAllBytes(Path.Combine(b.Path, rel)));
        }
    }

    [Fact]
    public void Generated_at_is_the_HEAD_commit_instant_not_the_wall_clock()
    {
        RequireGitCheckout();

        // §6.1: a wall clock makes --check fail forever on that one field, and the stamp answers a
        // better question -- which state of the code this bundle reflects. Compared against an
        // INDEPENDENT call to GitRevision, not the value Generate() itself produced, so this actually
        // exercises the wiring rather than checking a value against itself. It is still the SAME
        // production code on both sides, though -- see the test below for a defect GitRevision itself
        // could carry that this comparison cannot catch.
        var at = Single(Generate(), "overview").Document.Frontmatter.GeneratedAt;

        Assert.Equal(GitRevision.HeadCommitInstant(RepoRoot), at);
        Assert.EndsWith("Z", at, StringComparison.Ordinal);   // §5: explicit UTC offset
    }

    [Fact]
    public void HeadCommitInstant_uses_the_committer_date_not_the_author_date_and_normalises_to_UTC()
    {
        // An INDEPENDENT oracle: this test drives `git` directly, through the RunGit helper below,
        // never through GitRevision -- and pins the EXACT expected string and sha. The test above
        // compares GitRevision's output to a second call of GitRevision, which cannot catch a defect
        // inside GitRevision itself: change `%cI` to `%aI`, or `HEAD` to `HEAD~1`, and that comparison
        // stays green because both sides move together. This one goes red for either, because the
        // author date, committer date and committer offset are all pinned independently here.
        using var repo = new TempDir();
        InitGitRepo(repo.Path);

        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "x");
        RunGit(repo.Path, ["add", "a.txt"]);

        // Author date and committer date deliberately disagree, and the committer date carries a
        // non-UTC offset -- exactly what distinguishes "reads %cI, converts to UTC" from "reads %aI"
        // or "forgets the offset conversion". 14:00+02:00 is 12:00Z.
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = "2020-01-01T00:00:00+00:00",
            ["GIT_COMMITTER_DATE"] = "2026-06-30T14:00:00+02:00",
        };
        RunGit(repo.Path, ["commit", "-q", "-m", "init"], env);

        var expectedSha = RunGit(repo.Path, ["rev-parse", "HEAD"]).Trim();

        Assert.Equal("2026-06-30T12:00:00Z", GitRevision.HeadCommitInstant(repo.Path));
        Assert.Equal(expectedSha, GitRevision.HeadSha(repo.Path));
    }

    [Fact]
    public void Only_overview_carries_at_and_revision()
    {
        RequireGitCheckout();

        // All ~480 code concepts are generated in one pass; storing `at` on each of them would repeat
        // the same fact hundreds of times and rewrite every file's timestamp on every regeneration,
        // regardless of what in the code actually changed. Looped over EVERY concept, not one sampled
        // member, so a regression in a DIFFERENT builder (BuildContainerConcept, BuildPackageConcept,
        // BuildDocConcept) that started emitting `at` cannot pass unnoticed.
        var concepts = Generate();
        Assert.NotEmpty(concepts);

        foreach (var concept in concepts)
        {
            var isOverview = concept.Id.ToString() == "overview";

            Assert.Equal(isOverview, concept.Document.Frontmatter.Get("revision") is not null);
            Assert.Equal(isOverview, concept.Document.Frontmatter.GeneratedAt is not null);
        }

        // `by` alone survives on a code concept -- but not on every family: packages/* and docs/*
        // carry no `generated` block at all, only code and container concepts do.
        Assert.NotNull(Single(concepts, "code/csharp/n/scanner/scan").Document.Frontmatter.Get("generated")?.AsMapping()?.Get("by"));
    }

    [Fact]
    public void Output_never_contains_an_absolute_path_or_a_backslash_separator()
    {
        var concepts = Generate();

        foreach (var concept in concepts)
        {
            Assert.DoesNotContain(RepoRoot, concept.Document.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", concept.Document.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", concept.Document.Frontmatter.Resource ?? "", StringComparison.Ordinal);
        }

        // Fixture sanity: the separator check above is a live guard only because Graph() below declares
        // a member at a BACKSLASH-separated path (`src\A\Scanner.cs`) -- every other fixture path in this
        // file is already `/`-separated, so without this NormalizeSeparators could be deleted and the
        // suite would stay green. SpanLabel renders RelativePath into the body, which is what this pins.
        Assert.Contains("Scanner.cs", Single(concepts, "code/csharp/n/scanner/scan").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_a_git_repository_the_wall_clock_stands_in_and_no_revision_is_written()
    {
        // §6.1's documented fallback. A real, existing directory that is simply not a git checkout --
        // not a bare nonexistent path like "/repo" (which every other test file in this project already
        // fixtures with, and which exercises GitRevision's EARLIER `Directory.Exists` guard instead):
        // this one reaches `git`, gets a non-zero exit, and takes the "git ran and failed" path rather
        // than the "no such directory" one. There is no sha to report, so `revision` is omitted rather
        // than fabricated.
        using var outsideGit = new TempDir();
        var snapshot = new RepositorySnapshot(outsideGit.Path, "my-repo", [], []);

        var overview = new ConceptGenerator().Generate(snapshot).Single(c => c.Id.ToString() == "overview");

        Assert.NotNull(overview.Document.Frontmatter.GeneratedAt);
        Assert.EndsWith("Z", overview.Document.Frontmatter.GeneratedAt, StringComparison.Ordinal);
        Assert.Null(overview.Document.Frontmatter.Get("revision"));
    }

    // -- fixture ----------------------------------------------------------------------------------

    /// <summary>This repository's own root -- the one directory guaranteed to be a real git checkout wherever these tests run.</summary>
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OKF4net.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find OKF4net.sln walking up from the test assembly.");
    }

    /// <summary>
    /// Fails loudly, with an explanation, instead of letting a test that needs a real git checkout
    /// degrade into comparing two wall-clock reads -- which is green by coincidence within a second and
    /// red across a second boundary with a message that never mentions git.
    /// </summary>
    private static void RequireGitCheckout() =>
        Assert.True(
            GitRevision.HeadSha(RepoRoot) is not null,
            $"This test requires '{RepoRoot}' to be a real git checkout with a resolvable HEAD -- it "
                + "compares against `git` directly and gives no useful signal if `git` itself is unavailable.");

    private static List<string> RelativeMdFiles(string root) =>
        [.. Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(p => p, StringComparer.Ordinal)];

    private static IReadOnlyList<GeneratedConcept> Generate() =>
        new ConceptGenerator().Generate(Snapshot(), Graph(), Options());

    private static RepositorySnapshot Snapshot() => new(
        RepoRoot,
        "my-repo",
        [new PackageManifest("nuget", "src/A/A.csproj", "lib", null)],
        [new DocFile("README.md", "Readme")]);

    private static GenerateOptions Options() => new()
    {
        RepoUrl = "https://github.com/o/r",
        Rev = "main",
        Profiles = [CSharp],
    };

    private static CodeGraphModel Graph() => new(
        [
            Type("N", "Scanner", "linked/Scanner.cs"),
            // Deliberately backslash-separated: every OTHER fixture path in this file is already
            // `/`-separated, so without a live symbol declared at a backslash path, the separator half
            // of Output_never_contains_an_absolute_path_or_a_backslash_separator could not go red.
            Member("N.Scanner", "Scan", "public void Scan()", "src\\A\\Scanner.cs"),
        ],
        [],
        RunStatus.Complete);

    private static GeneratedConcept Single(IReadOnlyList<GeneratedConcept> concepts, string id)
        => concepts.Single(c => c.Id.ToString() == id);

    private static WriteResult Write(IReadOnlyList<GeneratedConcept> concepts, string path)
    {
        var result = new BundleWriter().Write(path, concepts, WritePolicy.RequireEmpty, Path.GetTempPath());

        Assert.Empty(result.Failures);
        return result;
    }

    private static SymbolFact Type(string container, string name, string path) =>
        new(SymbolKind.Type, "csharp", container, name, $"public class {name}",
            SymbolVisibility.Public, path, 0, 1, 1, 2, null);

    private static SymbolFact Member(string container, string name, string signature, string path) =>
        new(SymbolKind.Member, "csharp", container, name, signature,
            SymbolVisibility.Public, path, 10, 11, 3, 4, null);

    private static readonly LanguageProfile CSharp = new(
        Language: "csharp",
        GrammarName: "c_sharp",
        DeclarationQuery: string.Empty,
        CallQuery: string.Empty,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);

    // -- an independent git oracle: drives `git` directly, never through GitRevision --------------

    private static void InitGitRepo(string path)
    {
        RunGit(path, ["init", "-q"]);
        RunGit(path, ["config", "user.email", "determinism-tests@example.invalid"]);
        RunGit(path, ["config", "user.name", "Determinism Tests"]);
        RunGit(path, ["config", "commit.gpgsign", "false"]);
    }

    private static string RunGit(string cwd, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env = null)
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

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start git for test setup.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-determinism-" + Guid.NewGuid());
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
}

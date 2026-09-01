// SPDX-License-Identifier: LGPL-3.0-or-later
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
/// <para>These tests run against THIS repository's own real git history (<see cref="RepoRoot"/>), not a
/// fixture path, because the property under test -- "the same commit yields the same stamp" -- can only
/// be pinned against a commit that actually exists. A fixture directory outside git is covered
/// separately, by <see cref="Outside_a_git_repository_the_wall_clock_stands_in_and_no_revision_is_written"/>.</para>
/// </summary>
public class DeterminismTests
{
    [Fact]
    public void Two_runs_over_the_same_source_are_byte_identical()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        Write(Generate(), a.Path);
        Write(Generate(), b.Path);

        foreach (var file in Directory.GetFiles(a.Path, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(a.Path, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(b.Path, rel)));
        }
    }

    [Fact]
    public void Generated_at_is_the_HEAD_commit_instant_not_the_wall_clock()
    {
        // §6.1: a wall clock makes --check fail forever on that one field, and the stamp answers a
        // better question -- which state of the code this bundle reflects. Compared against an
        // INDEPENDENT call to GitRevision, not the value Generate() itself produced, so this actually
        // exercises the wiring rather than checking a value against itself.
        var at = Single(Generate(), "overview").Document.Frontmatter.GeneratedAt;

        Assert.Equal(GitRevision.HeadCommitInstant(RepoRoot), at);
        Assert.EndsWith("Z", at, StringComparison.Ordinal);   // §5: explicit UTC offset
    }

    [Fact]
    public void Only_overview_carries_at_and_revision()
    {
        // All ~480 code concepts are generated in one pass; storing `at` on each of them would repeat
        // the same fact hundreds of times and rewrite every file's timestamp on every regeneration,
        // regardless of what in the code actually changed. `by` alone stays on every concept.
        var concepts = Generate();
        var overview = Single(concepts, "overview");
        var member = Single(concepts, "code/csharp/n/scanner/scan");

        Assert.NotNull(overview.Document.Frontmatter.Get("revision"));
        Assert.NotNull(overview.Document.Frontmatter.GeneratedAt);

        Assert.Null(member.Document.Frontmatter.Get("revision"));
        Assert.Null(member.Document.Frontmatter.GeneratedAt);
        Assert.NotNull(member.Document.Frontmatter.Get("generated")?.AsMapping()?.Get("by"));
    }

    [Fact]
    public void Output_never_contains_an_absolute_path_or_a_backslash_separator()
    {
        foreach (var concept in Generate())
        {
            Assert.DoesNotContain(RepoRoot, concept.Document.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", concept.Document.Frontmatter.Resource ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Outside_a_git_repository_the_wall_clock_stands_in_and_no_revision_is_written()
    {
        // §6.1's documented fallback: a repository root that names no git checkout at all -- the shape
        // every other test file in this project already fixtures with (a bare "/repo" that resolves to
        // nothing on disk). There is no sha to report, so `revision` is omitted rather than fabricated.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], []);

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
            Member("N.Scanner", "Scan", "public void Scan()", "linked/Scanner.cs"),
        ],
        [],
        RunStatus.Complete);

    private static GeneratedConcept Single(IReadOnlyList<GeneratedConcept> concepts, string id)
        => concepts.Single(c => c.Id.ToString() == id);

    private static void Write(IReadOnlyList<GeneratedConcept> concepts, string path)
    {
        var result = new BundleWriter().Write(path, concepts, WritePolicy.RequireEmpty, Path.GetTempPath());

        Assert.Empty(result.Failures);
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

// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.CodeGraph.TreeSitter;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.CodeGraph;

/// <summary>
/// §5.4: scope is decided by directory nature and by symbol visibility, never by hard-coding a
/// convention like <c>src/</c> that is only this repository's own.
/// </summary>
public class ScopeTests : IDisposable
{
    private static readonly RepositorySnapshot Snapshot = new("/repo", "test-repo", [], []);

    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file on the way out should not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static SymbolFact Member(string container, string name, SymbolVisibility visibility) =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            visibility, "A.cs", 0, 10, 1, 1, null);

    [Theory]
    [InlineData("bin/Debug/net10.0/Gen.cs")]
    [InlineData("obj/Debug/net10.0/Gen.cs")]
    [InlineData("node_modules/pkg/index.cs")]
    [InlineData(".git/hooks/x.cs")]
    public void Build_output_and_vendored_directories_are_never_scanned(string path)
        => Assert.False(FileEligibility.IsEligible(path, Snapshot, ScopeOptions.Default));

    [Theory]
    [InlineData("bin/Debug/net10.0/Gen.cs")]
    [InlineData("obj/Debug/net10.0/Gen.cs")]
    [InlineData("node_modules/pkg/index.cs")]
    [InlineData(".git/hooks/x.cs")]
    public void Build_output_and_vendored_directories_stay_excluded_even_with_include_tests(string path)
        => Assert.False(FileEligibility.IsEligible(path, Snapshot, ScopeOptions.Default with { IncludeTests = true }));

    [Fact]
    public void A_project_referencing_a_test_SDK_is_excluded_by_default()
    {
        // §5.4: on this repo that removes ~900 methods of OKF4net.Tests. Scoping
        // on `src/` instead would hard-code a convention that is only ours.
        var snapshot = SnapshotWithTestProject();

        Assert.False(FileEligibility.IsEligible("tests/OKF4net.Tests/AuditTests.cs", snapshot, ScopeOptions.Default));
        Assert.True(FileEligibility.IsEligible("tests/OKF4net.Tests/AuditTests.cs", snapshot, ScopeOptions.Default with { IncludeTests = true }));
    }

    [Fact]
    public void A_project_referencing_a_test_SDK_is_excluded_even_under_a_non_test_named_directory()
    {
        // Isolates the SDK-reference rule from the directory-naming convention below: this path
        // contains no "test"/"tests"/"spec" segment at all, so only the .csproj PackageReference
        // check can be responsible for the exclusion.
        var snapshot = SnapshotWithTestProject(projectDirectory: "integration/OKF4net.Verify");

        Assert.False(FileEligibility.IsEligible("integration/OKF4net.Verify/AuditTests.cs", snapshot, ScopeOptions.Default));
        Assert.True(FileEligibility.IsEligible("integration/OKF4net.Verify/AuditTests.cs", snapshot, ScopeOptions.Default with { IncludeTests = true }));
    }

    [Fact]
    public void A_project_not_referencing_a_test_SDK_is_included()
    {
        var snapshot = SnapshotWithProject(referencesTestSdk: false, projectDirectory: "lib/OkfProducer.Core");

        Assert.True(FileEligibility.IsEligible("lib/OkfProducer.Core/Foo.cs", snapshot, ScopeOptions.Default));
    }

    [Theory]
    // A public member of an internal type: capped at internal, so out of scope by default and in scope
    // with the flag. This is the case that shipped wrong -- filtering read the declared modifier alone,
    // so the member was emitted in a default-scope bundle AND tagged `public`, a visibility C# does not
    // give it. An operator who left --include-internal off precisely to keep internal API out of a
    // knowledge bundle got it published anyway. The producer's own fixture carries the shape, so the
    // golden blessed it: `code/csharp/n/shapes/hidden/never` and the container it forced into
    // existence are both gone now.
    [InlineData(SymbolVisibility.Public, SymbolVisibility.Internal, false, true)]
    // Both public: unaffected, and asserted so a fix that simply hid more is visible as one.
    [InlineData(SymbolVisibility.Public, SymbolVisibility.Public, true, true)]
    // The cap only ever REDUCES: a public container cannot lift an internal member into scope.
    [InlineData(SymbolVisibility.Internal, SymbolVisibility.Public, false, true)]
    // Private wins over everything, including the flag.
    [InlineData(SymbolVisibility.Public, SymbolVisibility.Private, false, false)]
    public void A_members_scope_is_capped_by_the_type_that_encloses_it(
        SymbolVisibility member, SymbolVisibility container, bool inDefaultScope, bool withInternal)
    {
        var enclosing = new SymbolFact(SymbolKind.Type, "csharp", "N", "Hidden", "class Hidden",
            container, "src/T.cs", 0, 1, 1, 2, null);
        var declared = new Dictionary<(string Container, string Name), SymbolFact>
        {
            [("N", "Hidden")] = enclosing,
        };

        var fact = new SymbolFact(SymbolKind.Member, "csharp", "N.Hidden", "Never", "void Never()",
            member, "src/T.cs", 2, 3, 3, 4, null);

        Assert.Equal(inDefaultScope, FileEligibility.IsInScope(fact, declared, ScopeOptions.Default));
        Assert.Equal(withInternal, FileEligibility.IsInScope(fact, declared, new ScopeOptions(IncludeTests: false, IncludeInternal: true)));
    }

    [Fact]
    public void An_unknown_container_caps_nothing_rather_than_hiding_what_it_encloses()
    {
        // The direction this must fail in. A container absent from the declared set is a namespace, or
        // a type in a file this run could not read -- treating it as private would delete concepts over
        // a file that merely failed to open, and §2.3 rates a wrongly-narrowed bundle below a wide one.
        // Reached on every ordinary member, whose container chain ends at a namespace.
        var fact = new SymbolFact(SymbolKind.Member, "csharp", "N.Nowhere", "Visible", "void Visible()",
            SymbolVisibility.Public, "src/T.cs", 0, 1, 1, 2, null);

        Assert.True(FileEligibility.IsInScope(fact, new Dictionary<(string, string), SymbolFact>(), ScopeOptions.Default));
    }

    [Fact]
    public void Project_ownership_matching_is_case_sensitive()
    {
        // M-1: a case-sensitive filesystem can hold both src/Foo and src/foo as genuinely distinct
        // directories, so a file in one is not owned by a project in the other.
        //
        // ON A PLATFORM WHERE THAT IS TRUE. This test used to assert `true` unconditionally, which is
        // a platform-independent claim about a platform-dependent fact: on Windows the two paths name
        // ONE directory, so the file really is owned by that project and excluding it is correct. It
        // passed there only because the production comparison was blind in the same way -- and that
        // blindness is B1-3, where a `.sln` entry cased differently from disk made a test project go
        // unrecognised and its files silently INCLUDED with `--include-tests` off.
        //
        // `BundlePaths.PathComparison` is the rule both findings actually want, and this assertion is
        // now the rule rather than one operating system's answer to it.
        var snapshot = SnapshotWithTestProject(projectDirectory: "src/Foo");

        Assert.Equal(
            !OperatingSystem.IsWindows(),
            FileEligibility.IsEligible("src/foo/Bar.cs", snapshot, ScopeOptions.Default));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("tests")]
    [InlineData("spec")]
    public void A_conventionally_named_directory_is_excluded_even_without_a_test_SDK(string dir)
        => Assert.False(FileEligibility.IsEligible($"{dir}/Thing.cs", Snapshot, ScopeOptions.Default));

    [Theory]
    [InlineData("test")]
    [InlineData("tests")]
    [InlineData("spec")]
    public void A_conventionally_named_directory_is_included_with_include_tests(string dir)
        => Assert.True(FileEligibility.IsEligible($"{dir}/Thing.cs", Snapshot, ScopeOptions.Default with { IncludeTests = true }));

    [Fact]
    public void Visibility_and_not_a_path_prefix_does_the_filtering()
    {
        Assert.False(FileEligibility.IsInScope(Member("T", "Hidden", SymbolVisibility.Private), ScopeOptions.Default));
        Assert.False(FileEligibility.IsInScope(Member("T", "Internal", SymbolVisibility.Internal), ScopeOptions.Default));
        Assert.True(FileEligibility.IsInScope(Member("T", "Internal", SymbolVisibility.Internal), ScopeOptions.Default with { IncludeInternal = true }));
        Assert.True(FileEligibility.IsInScope(Member("T", "Public", SymbolVisibility.Public), ScopeOptions.Default));
    }

    [Fact]
    public void The_visibility_filter_never_makes_an_ambiguous_call_resolvable_end_to_end()
    {
        // The same defect as CodeGraphBuilderTests' stubbed repro, driven through the real
        // tree-sitter extractor and the real NameMatchResolver on real C# source, because the stub
        // could in principle be modelling the wrong thing. "Helper" is declared twice in this
        // repository -- once public on A, once internal on B -- and C.Run calls the internal one.
        // Under the default scope the internal declaration is filtered out; if that filter runs
        // before the resolver, one declaration is left standing and the call links confidently to
        // A.Helper, which is not what the source says. Under --include-internal the very same
        // repository resolves to Unresolved, so the two runs would disagree about a fact of the
        // source, which is the tell that one of them is lying.
        var repoPath = CreateRepository(
            ("A.cs", """
                namespace N;

                public static class A
                {
                    public static void Helper() { }
                }
                """),
            ("B.cs", """
                namespace N;

                internal static class B
                {
                    internal static void Helper() { }
                }
                """),
            ("C.cs", """
                namespace N;

                public class C
                {
                    public void Run() => B.Helper();
                }
                """));
        var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);

        using var extractor = new TreeSitterExtractor();
        var builder = new CodeGraphBuilder(extractor, [CSharpProfile.Instance], [new NameMatchResolver()]);

        var narrow = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);
        var wide = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default with { IncludeInternal = true });

        var narrowEdge = Assert.Single(narrow.Edges, e => e.Site.CalledName == "Helper");
        var wideEdge = Assert.Single(wide.Edges, e => e.Site.CalledName == "Helper");
        Assert.Equal(EdgeConfidence.Unresolved, wideEdge.Confidence);
        Assert.Equal(EdgeConfidence.Unresolved, narrowEdge.Confidence);
        Assert.Null(narrowEdge.TargetContainer);
        Assert.Null(narrowEdge.TargetName);
    }

    private string CreateRepository(params (string RelativePath, string Source)[] files)
    {
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-scope-e2e-").FullName;
        _tempDirectories.Add(repoPath);
        foreach (var (relativePath, source) in files)
        {
            var fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);
        }

        return repoPath;
    }

    /// <summary>
    /// A real repository containing exactly one nuget project, at <paramref name="projectDirectory"/>,
    /// whose <c>.csproj</c> references <c>Microsoft.NET.Test.Sdk</c> -- matching what a real
    /// <see cref="RepositoryScanner"/> scan of a repo with a test project would produce, without
    /// widening <see cref="RepositorySnapshot"/>'s own shape.
    /// </summary>
    private RepositorySnapshot SnapshotWithTestProject(string projectDirectory = "tests/OKF4net.Tests") =>
        SnapshotWithProject(referencesTestSdk: true, projectDirectory);

    private RepositorySnapshot SnapshotWithProject(bool referencesTestSdk, string projectDirectory)
    {
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-scope-").FullName;
        _tempDirectories.Add(repoPath);
        var projectName = projectDirectory.Split('/')[^1];
        var csprojRelativePath = $"{projectDirectory}/{projectName}.csproj";
        var csprojFullPath = Path.Combine(repoPath, csprojRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(csprojFullPath)!);

        var packageReference = referencesTestSdk
            ? """<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />"""
            : string.Empty;
        File.WriteAllText(csprojFullPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                {packageReference}
              </ItemGroup>
            </Project>
            """);

        var package = new PackageManifest("nuget", csprojRelativePath, projectName, null);
        return new RepositorySnapshot(repoPath, "test-repo", [package], []);
    }


    [Fact]
    public void A_test_project_named_with_a_different_case_in_the_solution_is_still_excluded()
    {
        // The two sides of this comparison come from different places, which is what the old flat
        // Ordinal missed. A file's path is walked off disk; the project's comes from
        // `PackageManifest.RelativePath`, which `RepositoryScanner` may have read out of a `.sln`'s
        // TEXT and never normalised against disk. On Windows a differently-cased entry there still
        // passes `File.Exists`, so the project was found and its `.csproj` read -- and then the path
        // comparison rejected it, silently INCLUDING a test project with `--include-tests` off.
        //
        // On a case-sensitive filesystem the two really are different directories, so the file is
        // genuinely not owned by that project and staying included is correct. The assertion is
        // therefore conditioned on the platform, which is what `BundlePaths.PathComparison` encodes:
        // this test pins the rule, not one operating system's answer to it.
        var snapshot = SnapshotWithTestProject(projectDirectory: "integration/OKF4net.Verify");
        var differentlyCased = "Integration/OKF4net.Verify/AuditTests.cs";

        var eligible = FileEligibility.IsEligible(differentlyCased, snapshot, ScopeOptions.Default);

        Assert.Equal(!OperatingSystem.IsWindows(), eligible);
    }
    [Fact]
    public void An_unreadable_project_file_leaves_the_run_going_instead_of_ending_it()
    {
        // ReferencesTestSdk is reached from CodeGraphBuilder.Build's per-file loop, whose body is
        // deliberately NOT wrapped -- so an exception escaping this call does not skip one file, it
        // ends the whole repository's run. UnauthorizedAccessException is the reachable case (an ACL
        // that denies read on a .csproj), and it cannot be provoked on a real file without an
        // elevation this suite does not have: that is why the read is a seam and this reader is a
        // double rather than a chmod.
        var snapshot = SnapshotWithTestProject(projectDirectory: "integration/OKF4net.Verify");

        var eligible = FileEligibility.IsEligible(
            "integration/OKF4net.Verify/AuditTests.cs", snapshot, ScopeOptions.Default, new DenyingReader());

        // Unreadable means "not demonstrably owned by a test project", so the file stays in scope.
        // That direction is deliberate: the alternative -- treating an unreadable .csproj as a test
        // project -- would silently drop real source on a permissions accident.
        Assert.True(eligible);
    }

    /// <summary>
    /// Reports every path as present and then refuses to open it: the shape of an ACL that grants
    /// metadata and denies read. Remove the matching <c>catch</c> from
    /// <c>FileEligibility.ReferencesTestSdk</c> and the test above throws instead of failing, which is
    /// exactly the production behaviour it exists to forbid.
    /// </summary>
    private sealed class DenyingReader : IFileSystemReader
    {
        public long? TryGetLength(string absolutePath) => 1;

        public Stream OpenRead(string absolutePath) => throw new UnauthorizedAccessException(absolutePath);
    }
}

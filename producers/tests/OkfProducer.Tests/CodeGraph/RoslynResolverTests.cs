// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using OkfProducer.CodeGraph.Roslyn;
using OkfProducer.CodeGraph.TreeSitter;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;
using Xunit.Abstractions;

namespace OkfProducer.Tests.CodeGraph;

/// <summary>
/// These tests shell out to <c>dotnet msbuild</c> against real projects, which is deliberate: the
/// whole claim under test is that MSBuild's own item and property query is enough to build a correct
/// compilation without <c>MSBuildWorkspace</c>, and that claim cannot be checked against a mock of
/// MSBuild. They therefore need a restored repository (the producer solution's own build restores
/// <c>src/OKF4net</c>, since <c>OkfProducer.Core</c> references it); every assertion that depends on
/// that says so in its failure message, so a clean-clone failure explains itself.
/// </summary>
public sealed class RoslynResolverTests : IClassFixture<RoslynResolverTests.ScratchProject>
{
    private readonly ScratchProject _scratch;
    private readonly ITestOutputHelper _output;

    public RoslynResolverTests(ScratchProject scratch, ITestOutputHelper output)
    {
        _scratch = scratch;
        _output = output;
    }

    [Fact]
    public void The_msbuild_query_returns_generated_sources_too()
    {
        // Correction 1 from the spike: -t:ResolveReferences alone omits GlobalUsings.g.cs and
        // AssemblyInfo.cs, and ImplicitUsings is on by default, so every file relying on an implicit
        // using then fails -- and a compilation with errors mis-attributes calls rather than merely
        // missing them.
        var inputs = MsBuildProjectQuery.Query(RepoProject("src/OKF4net/OKF4net.csproj"));

        Assert.Contains(inputs.CompileFiles, f => f.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
        Assert.Contains(inputs.CompileFiles, f => f.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal));
        Assert.True(inputs.References.Count > 100, $"only {inputs.References.Count} references resolved; is the repository restored?");
    }

    [Fact]
    public void An_unknown_language_version_fails_loudly_instead_of_degrading()
    {
        // Correction 3: Microsoft.CodeAnalysis.CSharp 4.14.0 could not parse LangVersion 14 and the
        // spike fell back to Preview, which silently changes parse semantics. The producer must not.
        var inputs = FakeInputs with { LangVersion = "99" };

        var ex = Assert.Throws<InvalidOperationException>(() => CompilationFactory.Create(inputs));

        Assert.Contains("LangVersion", ex.Message, StringComparison.Ordinal);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pinned_roslyn_knows_the_language_version_this_repository_sets()
    {
        // The other half of correction 3, and the one that actually catches a bad pin: the throw above
        // proves the producer refuses to degrade, this proves it does not have to. LangVersion 14 is
        // what Directory.Build.props sets, and Microsoft.CodeAnalysis.CSharp 4.14.0 rejects it.
        var inputs = FakeInputs with { LangVersion = "14" };

        Assert.NotNull(CompilationFactory.Create(inputs));
    }

    // Source for the test below. The ambiguity is inter-type: two same-named methods on unrelated
    // types is the shape the spike measured at 38-39% of internal call edges, so it is the common
    // case, not a corner. Only the container distinguishes them -- and the container is exactly what
    // a semantic model knows and a name match cannot.
    public const string AmbiguitySource = """
        namespace Ambiguity;
        public class Alpha { public bool Overlapping(object o) => true; }
        public class Beta { public bool Overlapping(object o) => false; }
        public class Caller { public void Go(Alpha a) { a.Overlapping(null); } }
        """;

    [Fact]
    public void An_inter_type_ambiguity_that_NameMatch_cannot_settle_resolves_Exact()
    {
        var site = Assert.Single(_scratch.SitesIn("Ambiguity.cs"), s => s.CalledName == "Overlapping");

        // Precondition: the baseline genuinely cannot settle it, so the assertion below is measuring
        // the Roslyn resolver rather than restating what NameMatchResolver already knew.
        var baseline = Assert.Single(new NameMatchResolver().Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.Unresolved, baseline.Confidence);

        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Ambiguity.Alpha", edge.TargetContainer);
        Assert.Equal("Overlapping", edge.TargetName);
    }

    [Fact]
    public void An_exact_target_joins_the_extracted_symbol_it_names()
    {
        // Task 3's bug class: a target whose (Container, Name) does not have the same shape as the
        // extracted SymbolFact's is silently degraded to Unresolved by CodeGraphBuilder, so an edge
        // can be "Exact" and still render as plain text. Assert the join, not just the confidence.
        var site = Assert.Single(_scratch.SitesIn("Ambiguity.cs"), s => s.CalledName == "Overlapping");
        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Contains(_scratch.Symbols, s => s.Container == edge.TargetContainer && s.Name == edge.TargetName);
    }

    // Source for the two tests below. "café" is 5 UTF-8 bytes and 4 UTF-16 code units, so every offset
    // after it differs between the two unit systems -- which is the entire hazard: a mis-converted
    // offset does not fail to match, it matches a different call.
    public const string NonAsciiSource = """
        namespace NonAscii;
        public class Widget
        {
            public void Accented() { }
            public void Run() { var café = "🎯"; Accented(); }
        }
        """;

    [Fact]
    public void The_scenario_really_does_exercise_the_unit_mismatch()
    {
        // Guards the test below from silently becoming a no-op: if the source ever loses its
        // non-ASCII text, the UTF-8 and UTF-16 offsets coincide and "attachment survives" proves
        // nothing at all.
        var site = Assert.Single(_scratch.SitesIn("NonAscii.cs"), s => s.CalledName == "Accented" && s.CallerName == "Run");
        var utf16 = NonAsciiSource.LastIndexOf("Accented", StringComparison.Ordinal);

        Assert.NotEqual(utf16, site.Offset);
        Assert.Equal(Utf8Offsets.ToUtf8(NonAsciiSource, utf16), site.Offset);
    }

    [Fact]
    public void Attachment_survives_a_non_ascii_line_before_the_call()
    {
        var site = Assert.Single(_scratch.SitesIn("NonAscii.cs"), s => s.CalledName == "Accented" && s.CallerName == "Run");

        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("NonAscii.Widget", edge.TargetContainer);
        Assert.Equal("Accented", edge.TargetName);
    }

    // Source for the test below. `Concat` is declared once in this repository-of-one-file, so
    // NameMatchResolver links `string.Concat` to it -- confidently, and wrongly.
    public const string ExternalSource = """
        namespace External;
        public class Joiner
        {
            public string Concat(string a) => a;
            public string Run() => string.Concat("x", "y");
        }
        """;

    [Fact]
    public void A_call_into_the_BCL_retracts_the_baselines_name_only_guess()
    {
        var site = Assert.Single(_scratch.SitesIn("External.cs"), s => s.CalledName == "Concat" && s.CallerName == "Run");

        var baseline = Assert.Single(new NameMatchResolver().Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.ByName, baseline.Confidence);
        Assert.Equal("Concat", baseline.TargetName);

        // Roslyn knows the target is System.String.Concat, which this repository does not declare, so
        // it withdraws the link rather than leaving a confident edge to an unrelated method.
        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.Unresolved, edge.Confidence);
        Assert.Null(edge.TargetContainer);
    }

    [Fact]
    public void Attachment_to_tree_sitter_sites_holds_above_the_measured_floor()
    {
        // 8.4: assert the RATE against a floor, not one lucky call. A grammar or Roslyn upgrade that
        // degrades this must fail loudly rather than silently move calls into `## Calls (unresolved)`.
        // The oracle is the normalised offset -- the same identity the resolver matches on -- not a
        // name compared within a few lines, which would accept an attachment that had shifted.
        var measured = MeasureAttachment(RepoProject("src/OKF4net/OKF4net.csproj"));
        _output.WriteLine($"src/OKF4net: attached {measured.Attached}/{measured.Total}, exact {measured.Exact}/{measured.Total}, joined {measured.Joined}/{measured.Exact}");

        // Measured 2026-08-31 (SDK 10.0.204, Microsoft.CodeAnalysis.CSharp 5.3.0,
        // tree-sitter-c-sharp via TreeSitter.DotNet 1.3.0): 1121/1121, 100.0%. The floor sits at 0.98
        // rather than 1.00 so an ordinary grammar or language change costing a handful of exotic call
        // shapes does not fail the build, while a systematic offset drift -- which collapses the rate,
        // not nudges it -- still does.
        Assert.True(measured.Total > 500, $"only {measured.Total} call sites extracted; expected src/OKF4net to yield far more. Is the repository restored?");
        Assert.True(
            measured.Attached / (double)measured.Total >= 0.98,
            $"attachment fell to {measured.Attached}/{measured.Total} ({measured.Attached / (double)measured.Total:P1}).");
    }

    [Fact]
    public void Resolution_of_repository_internal_calls_holds_above_the_measured_floor()
    {
        // Attachment says the two engines still agree on where the calls are; this says the
        // compilation is still good enough to say what they call. They fail for different reasons --
        // a grammar change breaks the first, a broken reference graph breaks the second -- so a floor
        // on attachment alone would let a silently degraded symbol table through.
        var measured = MeasureAttachment(RepoProject("src/OKF4net/OKF4net.csproj"));
        _output.WriteLine($"src/OKF4net: attached {measured.Attached}/{measured.Total}, exact {measured.Exact}/{measured.Total}, joined {measured.Joined}/{measured.Exact}");

        // Measured 2026-08-31: 443/1121, 39.5% -- the other ~60% are calls into the BCL, which have no
        // concept to point at and are correctly Unresolved. That figure lines up with the spike's
        // 38-39% inter-type-ambiguous share, i.e. with the calls NameMatchResolver refuses to guess at.
        Assert.True(
            measured.Exact / (double)measured.Total >= 0.35,
            $"only {measured.Exact}/{measured.Total} sites ({measured.Exact / (double)measured.Total:P1}) resolved to a declaration in source.");

        // The verdict has to survive CodeGraphBuilder, which degrades any target whose
        // (Container, Name) matches no extracted SymbolFact -- an Exact edge that does not join
        // renders as plain text, so confidence alone would be a rate that looks good and ships nothing.
        // This is also the guard on container SHAPE: Roslyn's own display strings would render generics
        // with their type arguments and join nothing at all, which is the bug Task 3 shipped once.
        // Measured 2026-08-31: 443/443, 100%.
        Assert.True(
            measured.Joined / (double)measured.Exact >= 0.95,
            $"only {measured.Joined}/{measured.Exact} exact targets ({measured.Joined / (double)measured.Exact:P1}) join an extracted symbol. Unjoined: {measured.Unjoined}");
    }

    [Fact]
    public void A_project_that_cannot_be_queried_is_reported_unavailable_rather_than_resolved_from()
    {
        // Degradation is a first-class path: this is the "not restored / MSBuild cannot answer" case.
        // The resolver must own nothing (so NameMatchResolver's baseline stands untouched) AND say why
        // -- "could not run" has to be distinguishable from "ran and resolved nothing".
        var missing = Path.Combine(Path.GetTempPath(), "okf-producer-does-not-exist", "Ghost.csproj");

        var resolver = RoslynResolver.Create(RepoRoot(), [missing]);

        Assert.False(resolver.IsAvailable);
        Assert.False(resolver.IsComplete);
        Assert.False(resolver.Owns("src/OKF4net/ConceptId.cs"));
        var report = Assert.Single(resolver.Projects);
        Assert.Equal(RoslynProjectAvailability.MsBuildQueryFailed, report.Availability);
        Assert.NotEqual(string.Empty, report.Detail);
    }

    [Fact]
    public void A_resolver_that_ran_cleanly_says_so_even_when_it_resolves_nothing()
    {
        // The other side of the same coin. Handed no sites, the resolver returns no edges -- exactly
        // what an unavailable one returns -- so the only thing separating the two is this status.
        Assert.True(_scratch.Resolver.IsAvailable);
        Assert.True(_scratch.Resolver.IsComplete);
        Assert.Empty(_scratch.Resolver.Resolve([], _scratch.Symbols));
        Assert.All(_scratch.Resolver.Projects, p => Assert.Equal(RoslynProjectAvailability.Compiled, p.Availability));
    }

    [Fact]
    public void Chained_behind_NameMatch_in_CodeGraphBuilder_the_exact_verdict_wins()
    {
        // Every other test in this file calls Resolve directly. This one runs the real wiring, because
        // the override is not something this resolver does on its own: CodeGraphBuilder keys verdicts
        // by (RelativePath, Offset) and lets the later resolver overwrite, and the graph it returns is
        // the only place where "Exact" has actually survived the target-must-join invariant. A shape
        // mismatch between what the builder hands over and what this resolver returns would leave every
        // assertion above green and the produced graph unchanged.
        var snapshot = new RepositorySnapshot(_scratch.Root, "scratch", [], []);
        using var extractor = new TreeSitterExtractor();
        var builder = new CodeGraphBuilder(
            extractor,
            [CSharpProfile.Instance],
            [new NameMatchResolver(), _scratch.Resolver]);

        var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        var edge = Assert.Single(graph.Edges, e => e.Site.CalledName == "Overlapping");
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Ambiguity.Alpha", edge.TargetContainer);

        // And the baseline still owns what this resolver declined to settle: the BCL call is back to
        // Unresolved rather than linked to the same-named local method (see the retraction test above).
        var external = Assert.Single(graph.Edges, e => e.Site.CalledName == "Concat" && e.Site.CallerName == "Run");
        Assert.Equal(EdgeConfidence.Unresolved, external.Confidence);
    }

    [Fact]
    public void A_project_completed_by_a_source_generator_degrades_rather_than_resolving_from_a_hole()
    {
        // A known, deliberate limit, pinned so it cannot change silently in either direction. The
        // MSBuild query returns the files the SDK generates, but a ROSLYN source generator runs inside
        // the compiler and contributes no Compile item, so members it generates are simply absent here.
        // Running generators would mean executing analyzer assemblies the scanned repository chooses,
        // which is a separate decision with a security dimension -- so what is asserted is that the
        // resolver notices and steps back, never that it resolves calls against a partial symbol table.
        // System.Text.Json's generator ships with the SDK, so this needs no PackageReference.
        using var repository = new GeneratedMemberRepository();

        var resolver = RoslynResolver.Create(repository.Root, [repository.Project]);

        Assert.False(resolver.IsAvailable);
        var report = Assert.Single(resolver.Projects);
        Assert.Equal(RoslynProjectAvailability.CompilationHadErrors, report.Availability);
        Assert.False(resolver.Owns("Serialization.cs"));
    }

    [Fact]
    public void A_multi_targeting_project_is_queried_for_one_of_its_frameworks()
    {
        // A multi-targeting project's OUTER build has no ResolveReferences target at all -- MSBuild
        // answers MSB4057 -- so without the retry the whole project is lost to a degradation that
        // looks like an unrestored repository. The first framework listed is selected, deliberately:
        // a rule readable from the project file beats "newest", which would silently change which
        // symbols exist the day someone adds a TFM.
        using var repository = new MultiTargetRepository();

        var inputs = MsBuildProjectQuery.Query(repository.Project);

        Assert.Equal("net10.0", inputs.TargetFramework);
        Assert.NotEmpty(inputs.CompileFiles);
        Assert.NotEmpty(inputs.References);
    }

    [Fact]
    public void A_project_that_pins_no_language_version_is_not_treated_as_an_unknown_one()
    {
        // The failure this guards is a crash, not a wrong answer: LanguageVersionFacts.TryParse("")
        // is false, so folding "absent" into "unrecognised" would make the loud-failure path fire on
        // any project that simply does not pin a version. An absent property is what makes the SDK
        // pass csc no /langversion, and LanguageVersion.Default is by definition what csc does then.
        var inputs = FakeInputs with { LangVersion = string.Empty };

        Assert.NotNull(CompilationFactory.Create(inputs));
    }

    [Fact]
    public void An_unowned_file_is_left_to_the_baseline()
    {
        Assert.False(_scratch.Resolver.Owns("some/other/repository/File.cs"));

        var foreignSite = new CallSite("N.T", "M", "Overlapping", "some/other/repository/File.cs", 0);
        Assert.Empty(_scratch.Resolver.Resolve([foreignSite], _scratch.Symbols));
    }

    [Fact]
    public void A_project_reference_resolves_from_source_when_its_bin_output_is_absent()
    {
        // Correction 2, measured rather than asserted. MSBuild resolves a ProjectReference to
        // bin/<config>/<tfm>/*.dll, which exists only after a BUILD; the spike measured OKF4net.Mcp
        // going from 0 errors to 4 (CS0234 on a namespace, CS0246/CS0103 on a type) when those
        // references were dropped. This builds the same situation deliberately -- a restored, never
        // built pair of projects -- and shows the CompilationReference route closing it.
        using var repository = new TwoProjectRepository();

        var libraryInputs = MsBuildProjectQuery.Query(repository.LibraryProject);
        var applicationInputs = MsBuildProjectQuery.Query(repository.ApplicationProject);

        var libraryReference = Assert.Single(applicationInputs.References, r => r.ProjectPath is not null);
        Assert.Equal(repository.LibraryProject, libraryReference.ProjectPath, ignoreCase: true);

        repository.DeleteLibraryOutput();

        // Without the substitution: exactly the failure the spike measured.
        var unsatisfied = CompilationFactory.Create(applicationInputs, projectCompilations: null, out var missing);
        Assert.NotEmpty(missing);
        Assert.NotEmpty(unsatisfied.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        // With it: clean, from a repository that was only ever restored.
        var library = CompilationFactory.Create(libraryInputs);
        var application = CompilationFactory.Create(
            applicationInputs,
            new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(StringComparer.OrdinalIgnoreCase)
            {
                [repository.LibraryProject] = library,
            },
            out var stillMissing);

        Assert.Empty(stillMissing);
        Assert.Empty(application.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_call_across_an_unbuilt_project_reference_still_resolves_Exact()
    {
        // The same situation, end to end through the resolver rather than through the factory: the
        // resolver pulls the referenced project into its own closure and compiles it from source.
        using var repository = new TwoProjectRepository();
        repository.DeleteLibraryOutput();

        var resolver = RoslynResolver.Create(repository.Root, [repository.ApplicationProject]);

        Assert.True(resolver.IsComplete, Describe(resolver));
        Assert.Equal(2, resolver.Projects.Count);

        using var extractor = new TreeSitterExtractor();
        var extracted = extractor.Extract("app/Program.cs", repository.ApplicationSourceFile, CSharpProfile.Instance, ExtractionLimits.Default);
        var site = Assert.Single(extracted.Sites, s => s.CalledName == "Greet");

        var edge = Assert.Single(resolver.Resolve([site], extracted.Symbols));
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Library.Greeter", edge.TargetContainer);
        Assert.Equal("Greet", edge.TargetName);
    }

    private static string Describe(RoslynResolver resolver) =>
        string.Join("; ", resolver.Projects.Select(p => $"{Path.GetFileName(p.ProjectPath)}: {p.Availability} {p.Detail}"));

    /// <summary>
    /// A hand-built <see cref="ProjectInputs"/> for the tests that only exercise
    /// <see cref="CompilationFactory"/>'s parse options: no MSBuild round trip, no files, nothing that
    /// could fail for an unrelated reason.
    /// </summary>
    private static ProjectInputs FakeInputs { get; } = new(
        ProjectPath: Path.Combine(Path.GetTempPath(), "Fake", "Fake.csproj"),
        AssemblyName: "Fake",
        CompileFiles: [],
        References: [],
        DefineConstants: "TRACE;DEBUG",
        LangVersion: "14",
        Nullable: true,
        AllowUnsafe: false,
        OutputType: "Library",
        TargetFramework: "net10.0");

    private static (int Attached, int Total, int Exact, int Joined, string Unjoined) MeasureAttachment(string projectPath)
    {
        var repositoryRoot = RepoRoot();
        var resolver = RoslynResolver.Create(repositoryRoot, [projectPath]);

        Assert.True(
            resolver.IsAvailable,
            $"no project compiled, so attachment cannot be measured. This test needs a restored repository. {Describe(resolver)}");

        using var extractor = new TreeSitterExtractor();
        var sites = new List<CallSite>();
        var symbols = new List<SymbolFact>();

        foreach (var file in EnumerateSourceFiles(Path.GetDirectoryName(projectPath)!))
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var extracted = extractor.Extract(relativePath, file, CSharpProfile.Instance, ExtractionLimits.Default);
            sites.AddRange(extracted.Sites);
            symbols.AddRange(extracted.Symbols);
        }

        var owned = sites.Where(s => resolver.Owns(s.RelativePath)).ToList();
        var edges = resolver.Resolve(owned, symbols);

        // The same key CodeGraphBuilder joins on. A lookup only; nothing iterates it into a result.
        var declared = new HashSet<(string Container, string Name)>(symbols.Select(s => (s.Container, s.Name)));
        var exact = edges.Where(e => e.Confidence == EdgeConfidence.Exact).ToList();
        var unjoined = exact.Where(e => !declared.Contains((e.TargetContainer!, e.TargetName!))).ToList();

        return (
            edges.Count,
            owned.Count,
            exact.Count,
            exact.Count - unjoined.Count,
            // Named, not just counted: when this floor is one day breached, the difference between a
            // reader knowing which targets stopped joining and knowing only that fewer did is the
            // difference between a five-minute diagnosis and a bisect.
            string.Join(" | ", unjoined
                .Select(e => $"{e.TargetContainer}.{e.TargetName}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(8)));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string RepoProject(string relativePath) =>
        Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Walks up from the test assembly to the directory holding <c>OKF4net.sln</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OKF4net.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>
    /// One restored, single-project scratch repository holding every small scenario in this file, so
    /// the whole class pays for one <c>dotnet restore</c> and one MSBuild query rather than one per
    /// test. Each scenario lives in its own namespace with its own member names, so no scenario can
    /// change another's name-match ambiguity.
    /// </summary>
    public sealed class ScratchProject : IDisposable
    {
        private static readonly (string FileName, string Source)[] Sources =
        [
            ("Ambiguity.cs", AmbiguitySource),
            ("NonAscii.cs", NonAsciiSource),
            ("External.cs", ExternalSource),
        ];

        private readonly Dictionary<string, ExtractionResult> _extracted = new(StringComparer.Ordinal);

        public ScratchProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "okf-producer-roslyn-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Root);

            var projectPath = Path.Combine(Root, "Scratch.csproj");
            File.WriteAllText(projectPath, ProjectFile, new UTF8Encoding(false));

            foreach (var (fileName, source) in Sources)
            {
                // UTF-8 with no BOM, LF-normalised, so the bytes on disk are exactly the bytes the
                // const string in this file describes -- the offsets asserted above are computed
                // against that string.
                File.WriteAllText(Path.Combine(Root, fileName), source.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            }

            Restore(projectPath);

            Resolver = RoslynResolver.Create(Root, [projectPath]);
            Assert.True(Resolver.IsComplete, Describe(Resolver));

            using var extractor = new TreeSitterExtractor();
            var symbols = new List<SymbolFact>();
            foreach (var (fileName, _) in Sources)
            {
                var result = extractor.Extract(fileName, Path.Combine(Root, fileName), CSharpProfile.Instance, ExtractionLimits.Default);
                _extracted[fileName] = result;
                symbols.AddRange(result.Symbols);
            }

            Symbols = symbols;
        }

        public string Root { get; }

        public RoslynResolver Resolver { get; }

        /// <summary>Every scenario's symbols, so a name-match baseline sees the whole scratch repository.</summary>
        public IReadOnlyList<SymbolFact> Symbols { get; }

        public IReadOnlyList<CallSite> SitesIn(string fileName) => _extracted[fileName].Sites;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file on the way out should not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // No LangVersion, deliberately: this project exercises whatever the SDK reports for a plain
        // net10.0 library, which is the case a real repository without an explicit LangVersion hits.
        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
    }

    /// <summary>
    /// A restored-but-never-built two-project repository: an application with a
    /// <c>ProjectReference</c> to a library it calls into. <see cref="DeleteLibraryOutput"/> removes
    /// whatever the restore or the MSBuild query happened to produce, so the application's resolved
    /// reference points at a <c>bin/</c> assembly that is not there -- the exact condition correction
    /// 2 exists for.
    /// </summary>
    private sealed class TwoProjectRepository : ScratchRepository
    {
        public TwoProjectRepository()
            : base("tworef")
        {
            LibraryProject = Write("lib/Library.csproj", LibraryProjectFile);
            ApplicationProject = Write("app/Application.csproj", ApplicationProjectFile);
            Write("lib/Greeter.cs", LibrarySource);
            ApplicationSourceFile = Write("app/Program.cs", ApplicationSource);

            Restore(ApplicationProject);
        }

        public string LibraryProject { get; }

        public string ApplicationProject { get; }

        public string ApplicationSourceFile { get; }

        public void DeleteLibraryOutput()
        {
            var bin = Path.Combine(Root, "lib", "bin");
            if (Directory.Exists(bin))
            {
                Directory.Delete(bin, recursive: true);
            }
        }

        private const string LibraryProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;

        private const string ApplicationProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\lib\Library.csproj" />
              </ItemGroup>
            </Project>
            """;

        private const string LibrarySource = """
            namespace Library;
            public class Greeter { public string Greet(string who) => who; }
            """;

        private const string ApplicationSource = """
            namespace App;
            public class Program
            {
                public static string Run() => new Library.Greeter().Greet("world");
            }
            """;
    }

    /// <summary>A restored repository whose single project targets two frameworks.</summary>
    private sealed class MultiTargetRepository : ScratchRepository
    {
        public MultiTargetRepository()
            : base("multitfm")
        {
            Project = Write("Multi.csproj", ProjectFile);
            Write("Calculator.cs", Source);

            Restore(Project);
        }

        public string Project { get; }

        // netstandard2.0 second, and second on purpose: it is the framework the "first listed" rule
        // must NOT pick, and it reports LangVersion 7.3 rather than 14, so picking the wrong one
        // changes the answer visibly instead of coincidentally agreeing.
        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """;

        private const string Source = """
            namespace Multi;
            public class Calculator { public int One() => 1; public int Two() => One() + One(); }
            """;
    }

    /// <summary>
    /// A restored repository whose source calls members that only a Roslyn source generator produces.
    /// <c>System.Text.Json</c>'s generator ships with the SDK and is added as an <c>Analyzer</c> item
    /// automatically, so this needs no <c>PackageReference</c> and restores offline.
    /// </summary>
    private sealed class GeneratedMemberRepository : ScratchRepository
    {
        public GeneratedMemberRepository()
            : base("generated")
        {
            Project = Write("Generated.csproj", ProjectFile);
            Write("Serialization.cs", Source);

            Restore(Project);
        }

        public string Project { get; }

        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;

        // `Default` and the base class's abstract members are emitted by the generator, not written
        // here -- so a compilation that does not run generators reports errors on exactly them.
        private const string Source = """
            using System.Text.Json.Serialization;

            namespace Generated;

            public record Payload(string Name);

            [JsonSerializable(typeof(Payload))]
            public partial class PayloadContext : JsonSerializerContext { }

            public static class Writer
            {
                public static string Write(Payload payload) =>
                    System.Text.Json.JsonSerializer.Serialize(payload, PayloadContext.Default.Payload);
            }
            """;
    }

    /// <summary>
    /// A throwaway directory under the system temp holding one scratch repository. Outside the
    /// repository tree on purpose, so it inherits none of this repo's <c>Directory.Build.props</c> --
    /// these fixtures are meant to exercise what MSBuild reports for a plain project, not for one
    /// carrying this repository's settings.
    /// </summary>
    private abstract class ScratchRepository : IDisposable
    {
        protected ScratchRepository(string prefix) =>
            Root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"okf-producer-{prefix}-" + Guid.NewGuid().ToString("N")[..12])).FullName;

        public string Root { get; }

        /// <summary>
        /// Writes one file, creating its directory, and returns its full path. UTF-8 with no BOM and
        /// LF endings, so the bytes on disk are exactly the bytes the <c>const string</c> describes --
        /// which is what makes an offset asserted against that string meaningful.
        /// </summary>
        protected string Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
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

    private static void Restore(string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"`dotnet restore {projectPath}` exited {process.ExitCode}: {stdout.GetAwaiter().GetResult()} {stderr.GetAwaiter().GetResult()}");
    }
}

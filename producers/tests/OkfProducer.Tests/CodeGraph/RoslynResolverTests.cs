// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using OkfProducer.Cli;
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

    [Theory]
    // Well-formed JSON that is not the object -getItem/-getProperty promise. TryGetProperty throws
    // InvalidOperationException on every one of these, not "returns false".
    [InlineData("[]", "is not an object")]
    [InlineData("7", "is not an object")]
    [InlineData("null", "is not an object")]
    [InlineData("\"Items\"", "is not an object")]
    // The object is right; a group inside it is not. EnumerateObject/EnumerateArray throw here.
    [InlineData("""{ "Properties": [] }""", "printed `Properties` as Array rather than Object")]
    [InlineData("""{ "Items": 7 }""", "printed `Items` as Number rather than Object")]
    [InlineData("""{ "Items": { "Compile": "one-file.cs" } }""", "printed item group `Compile` as String rather than an array")]
    [InlineData("""{ "Items": { "Compile": [ "one-file.cs" ] } }""", "printed an entry of item group `Compile` as String rather than an object")]
    // Item METADATA is not a path anything validated, and a repository-authored target can set
    // MSBuildSourceProjectFile to whatever it likes. Path.GetFullPath then throws ArgumentException on
    // a NUL (and PathTooLongException on a 40 KB value), neither of them an MsBuildQueryException, in
    // the very method the guards above were added to.
    [InlineData(
        """{"Items":{"ReferencePath":[{"FullPath":"a.dll","ReferenceSourceTarget":"ProjectReference","MSBuildSourceProjectFile":"x\u0000y"}]}}""",
        "printed `MSBuildSourceProjectFile` as a value that is not a path")]
    public void A_malformed_msbuild_answer_degrades_one_project_rather_than_aborting_the_run(string json, string because)
    {
        // The whole point is the exception TYPE, not that it throws. RoslynResolver.QueryProjectClosure
        // catches exactly MsBuildQueryException, deliberately and by its own doc, so anything else
        // escaping this reader skips the per-project degradation and takes generation down for the
        // ENTIRE repository -- landing on OkfgenCli.Generate's coarse top-level filter by coincidence.
        // Syntactically invalid JSON was already wrapped; syntactically VALID JSON of the wrong shape
        // was not, and that is the gap these cases hold.
        //
        // `because` exists because a refusal that fires for the WRONG reason passes the type assertion
        // just as happily: five distinct guards stand between this call and a ProjectInputs, and
        // asserting only the type let any of them answer for any case. The fragments are the guards'
        // own words, so a case that starts being refused a guard earlier now fails here.
        var ex = Record.Exception(() => MsBuildProjectQuery.ReadInputs("C:/repo/Some.csproj", json));

        Assert.IsType<MsBuildQueryException>(ex);
        Assert.Contains("Some.csproj", ex.Message, StringComparison.Ordinal);
        Assert.Contains(because, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_msbuild_prints_as_a_non_string_reads_as_unset_rather_than_throwing()
    {
        // The other direction, and deliberately NOT a refusal: Property() already treats an empty
        // value as "not set", so a non-string value has a correct answer that costs nothing. Only a
        // shape that would be read downstream as a fact about the project -- an item group that is
        // not a list -- is refused, because there an empty result reads as "no Compile items".
        var inputs = MsBuildProjectQuery.ReadInputs(
            "C:/repo/Some.csproj",
            """{ "Properties": { "LangVersion": 14, "AssemblyName": "Chosen" }, "Items": { "Compile": [ { "FullPath": "a.cs" } ] } }""");

        Assert.Equal(string.Empty, inputs.LangVersion);
        Assert.Equal("Chosen", inputs.AssemblyName);
        Assert.Equal(["a.cs"], inputs.CompileFiles);
    }

    [Fact]
    public void An_unknown_language_version_fails_loudly_instead_of_degrading()
    {
        // Correction 3: Microsoft.CodeAnalysis.CSharp 4.14.0 could not parse LangVersion 14 and the
        // spike fell back to Preview, which silently changes parse semantics. The producer must not.
        var inputs = FakeInputs with { LangVersion = "99" };

        var ex = Assert.Throws<UnknownLanguageVersionException>(() => CompilationFactory.Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _));

        // Its own type so RoslynResolver can catch THIS rather than every InvalidOperationException a
        // compilation might raise -- but still an InvalidOperationException, which is the contract
        // callers were given.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("99", ex.LangVersion);
        Assert.Contains("LangVersion", ex.Message, StringComparison.Ordinal);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_language_version_costs_its_own_project_and_no_other()
    {
        // The loudness of the refusal must not become a loss of the whole run. Correction 3's hazard
        // is a SILENT fallback to a preview language version; declining to compile the one project
        // that pinned an unknown version answers that completely, and every other project in the
        // repository can still be resolved exactly. So: unavailable, named, run continues.
        using var repository = new UnknownLanguageVersionRepository();

        var resolver = RoslynResolver.Create(repository.Root, [repository.StrandedProject, repository.SoundProject]);

        var stranded = Assert.Single(resolver.Projects, p => p.ProjectPath == repository.StrandedProject);
        Assert.Equal(RoslynProjectAvailability.UnknownLanguageVersion, stranded.Availability);
        Assert.Contains("99", stranded.Detail, StringComparison.Ordinal);

        // The run continued, and said honestly that it is partial.
        Assert.True(resolver.IsAvailable, Describe(resolver));
        Assert.False(resolver.IsComplete);

        // The stranded project is left to the baseline; the sound one still resolves exactly.
        Assert.False(resolver.Owns("stranded/Stranded.cs"));
        Assert.True(resolver.Owns("sound/Sound.cs"), Describe(resolver));

        using var extractor = new TreeSitterExtractor();
        var extracted = extractor.Extract("sound/Sound.cs", repository.SoundSourceFile, CSharpProfile.Instance, ExtractionLimits.Default);
        var site = Assert.Single(extracted.Sites, s => s.CalledName == "Inner");

        var edge = Assert.Single(resolver.Resolve([site], extracted.Symbols));
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Sound.Caller", edge.TargetContainer);
    }

    [Fact]
    public void The_pinned_roslyn_knows_the_language_version_this_repository_sets()
    {
        // The other half of correction 3, and the one that actually catches a bad pin: the throw above
        // proves the producer refuses to degrade, this proves it does not have to. LangVersion 14 is
        // what Directory.Build.props sets, and Microsoft.CodeAnalysis.CSharp 4.14.0 rejects it.
        var inputs = FakeInputs with { LangVersion = "14" };

        Assert.NotNull(CompilationFactory.Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _));
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
        // Against the text the fixture actually WROTE, not against the literal. ScratchRepo normalizes
        // line endings to \n on write (see Sources below) while this constant carries whatever the .cs
        // file on disk carries -- and with core.autocrlf=true that is \r\n on a fresh checkout, four
        // extra bytes ahead of the call, which shifted every offset here and failed this test on any
        // clone that had not been hand-edited to LF. Found while implementing Task 11; the normalize
        // now happens on both sides of the comparison, so the test no longer depends on how git
        // materialised its own source file.
        var written = NonAsciiSource.ReplaceLineEndings("\n");
        var site = Assert.Single(_scratch.SitesIn("NonAscii.cs"), s => s.CalledName == "Accented" && s.CallerName == "Run");
        var utf16 = written.LastIndexOf("Accented", StringComparison.Ordinal);

        Assert.NotEqual(utf16, site.Offset);
        Assert.Equal(Utf8Offsets.ToUtf8(written, utf16), site.Offset);
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

        // Named, not merely non-empty. `Detail != ""` passed while this case reported "the dotnet CLI
        // was not found" -- Process.Start throws Win32Exception for a WorkingDirectory that is not
        // there (measured on this host), and that catch's message blames a missing SDK, sending an
        // operator hunting for something that is installed. The refusal was right and its reason was
        // wrong, which is exactly the failure a `NotEqual(string.Empty, ...)` cannot see.
        Assert.Contains("its directory does not exist", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet CLI was not found", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolver_that_ran_cleanly_says_so_even_when_it_resolves_nothing()
    {
        // The other side of the same coin. Handed no sites, the resolver returns no edges -- exactly
        // what an unavailable one returns -- so the only thing separating the two is this status.
        //
        // `Assert.Empty(Resolve([], symbols))` used to stand here and has been removed rather than
        // moved: Resolve builds its result by iterating `sites`, so it returns empty for ANY
        // implementation of it, including a broken one. That assertion could not fail. The three
        // below can: each names a value this fixture's own restore-and-compile has to have produced.
        Assert.True(_scratch.Resolver.IsAvailable);
        Assert.True(_scratch.Resolver.IsComplete);
        Assert.All(_scratch.Resolver.Projects, p => Assert.Equal(RoslynProjectAvailability.Compiled, p.Availability));
    }

    [Fact]
    public void A_resolver_with_no_projects_at_all_is_not_complete()
    {
        // The one input where a vacuous answer is the WRONG answer: Projects.All(...) over an empty
        // list is true, so without the Count > 0 clause a resolver that resolved absolutely nothing --
        // every call in the repository fell back to name matching -- would report itself complete.
        // Reachable in practice: finding no .csproj in a C# repository yields an empty list, not an
        // error.
        //
        // What that wrong answer costs is a SILENT report, not a deletion. Task 11 settled that this
        // property is not the pruning gate and cannot be one: no resolver contributes a symbol to
        // CodeGraph.Symbols, so a degraded resolver can turn a call link into a code span but never
        // make a concept absent, and pruning acts on absence. See RoslynResolver.IsComplete's own doc
        // comment; pruning gates on RunStatus.TraversalComplete plus the per-file FileStatus.
        var resolver = RoslynResolver.Create(RepoRoot(), []);

        Assert.False(resolver.IsComplete);
        Assert.False(resolver.IsAvailable);
        Assert.Empty(resolver.Projects);
        Assert.False(resolver.Owns("src/OKF4net/ConceptId.cs"));
    }

    // Source for the test below. Roslyn strips the @ from a verbatim identifier and the grammar keeps
    // it, so the two disagree about this method's name on both sides of the join at once.
    public const string VerbatimSource = """
        namespace Verbatim;
        public class Holder
        {
            public int @class() => 1;
            public int Caller() => @class();
        }
        """;

    [Fact]
    public void A_verbatim_identifier_keeps_its_at_sign_on_both_sides_of_the_identity()
    {
        // Identity mismatch on the exact axis this task is about, in both halves: the name guard
        // compares the call site's spelling (CalledName, the grammar's raw token) against Roslyn's,
        // and the target's name has to equal SymbolFact.Name for CodeGraphBuilder to join it. Roslyn's
        // ValueText/ISymbol.Name give "class" for both; the extractor gives "@class" for both.
        var site = Assert.Single(_scratch.SitesIn("Verbatim.cs"), s => s.CallerName == "Caller");
        Assert.Equal("@class", site.CalledName);

        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Verbatim.Holder", edge.TargetContainer);
        Assert.Equal("@class", edge.TargetName);

        // And it actually joins, which is the half CodeGraphBuilder would otherwise degrade.
        Assert.Contains(_scratch.Symbols, s => s.Container == edge.TargetContainer && s.Name == edge.TargetName);
    }

    // Source for the test below. Written to disk as UTF-8 WITH a BOM and with CRLF endings -- the one
    // fixture in this file that is not UTF-8-without-BOM and LF.
    public const string BomCrlfSource = """
        namespace BomCrlf;
        public class Meter
        {
            public int Depth() => 3;
            public int Gauge() => Depth();
        }
        """;

    [Fact]
    public void Offset_identity_holds_for_a_file_with_a_BOM_and_CRLF_endings()
    {
        // The property this whole class is built around is about encoding: a BOM stripped by one
        // engine and kept by the other shifts every offset in the file by three bytes and credits
        // calls to whatever sits three bytes away. It was asserted for exactly one encoding shape.
        var path = Path.Combine(_scratch.Root, "BomCrlf.cs");
        var bytes = File.ReadAllBytes(path);

        // The fixture really is what this test claims, read back off disk rather than trusted from
        // the writer: without these two, every assertion below would pass just as happily on a plain
        // LF, no-BOM file, and the test would measure nothing it is named for.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), StringComparison.Ordinal);

        var site = Assert.Single(_scratch.SitesIn("BomCrlf.cs"), s => s.CallerName == "Gauge");

        // Offsets are measured against the DECODED text, and SourceDecoder.DecodeStrict strips the
        // BOM -- so the expected value carries no +3. It does carry one extra byte per preceding
        // CRLF, which is what the second assertion pins: the same source spelled with LF would put
        // this call somewhere else, so an offset that matched both would prove nothing.
        Assert.Equal(BomCrlfSource.ReplaceLineEndings("\r\n").IndexOf("Depth();", StringComparison.Ordinal), site.Offset);
        Assert.NotEqual(BomCrlfSource.ReplaceLineEndings("\n").IndexOf("Depth();", StringComparison.Ordinal), site.Offset);

        // And Roslyn, reading the same bytes through the same decoder, lands on the same offset.
        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("BomCrlf.Meter", edge.TargetContainer);
        Assert.Equal("Depth", edge.TargetName);
    }

    // Source for the two tests below. Both hold a CONTAINER segment whose source spelling differs
    // from ISymbol.Name, which is the axis SimpleNameOf already guards for the target's own name and
    // ContainerPathOf did not guard at all: Roslyn strips the @ from a verbatim identifier, so the
    // namespace and the type here come back as `event.class` where the grammar hands the extractor
    // `@event.@class`.
    public const string ContainerSpellingSource = """
        namespace @event;
        public class @class
        {
            public int Reserved() => 1;
            public int ReservedCaller() => Reserved();
        }
        """;

    // The other half of the same defect, and it needs no verbatim identifier at all: Roslyn mangles
    // an explicit interface implementation's name to its qualified form (Explicitly.IShape.Draw), so
    // a local function declared inside one reports a container carrying two extra dots -- and
    // therefore two extra SEGMENTS -- that the extractor's `Explicitly.Square.Draw` does not have.
    public const string ExplicitImplementationSource = """
        namespace Explicitly;
        public interface IShape { void Draw(); }
        public class Square : IShape
        {
            public int Sides;
            void IShape.Draw()
            {
                int Nested() => 4;
                Sides = Nested();
            }
        }
        """;

    [Fact]
    public void A_verbatim_identifier_keeps_its_at_sign_in_the_container_as_well_as_the_name()
    {
        var site = Assert.Single(_scratch.SitesIn("ContainerSpelling.cs"), s => s.CallerName == "ReservedCaller");

        // Precondition, and the whole point of the test: the baseline settles this one CORRECTLY,
        // because the name is unique in the scratch repository. So a different container from this
        // resolver is not a missed opportunity, it is a regression against not running it at all --
        // the one outcome 2.1's chaining is supposed to make impossible, since CodeGraphBuilder
        // overwrites the baseline's verdict first and only then degrades a non-joining Exact.
        var baseline = Assert.Single(new NameMatchResolver().Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.ByName, baseline.Confidence);
        Assert.Equal("@event.@class", baseline.TargetContainer);

        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("@event.@class", edge.TargetContainer);
        Assert.Equal("Reserved", edge.TargetName);

        // And it joins, which is the half CodeGraphBuilder would otherwise degrade.
        Assert.Contains(_scratch.Symbols, s => s.Container == edge.TargetContainer && s.Name == edge.TargetName);
    }

    [Fact]
    public void An_explicit_interface_implementation_contributes_the_segment_source_spells()
    {
        var site = Assert.Single(_scratch.SitesIn("ExplicitImplementation.cs"), s => s.CalledName == "Nested");

        var baseline = Assert.Single(new NameMatchResolver().Resolve([site], _scratch.Symbols));
        Assert.Equal(EdgeConfidence.ByName, baseline.Confidence);
        Assert.Equal("Explicitly.Square.Draw", baseline.TargetContainer);

        var edge = Assert.Single(_scratch.Resolver.Resolve([site], _scratch.Symbols));

        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("Explicitly.Square.Draw", edge.TargetContainer);
        Assert.Contains(_scratch.Symbols, s => s.Container == edge.TargetContainer && s.Name == edge.TargetName);
    }

    [Fact]
    public void An_exact_verdict_on_an_oddly_spelled_container_survives_the_builders_join()
    {
        // The two assertions above measure this resolver; this one measures what the produced graph
        // actually holds, which is the only place the "strictly worse than the baseline" failure is
        // visible: CodeGraphBuilder overwrites the baseline edge with the Exact one and only then
        // degrades it to Unresolved when its (Container, Name) joins no SymbolFact.
        var snapshot = new RepositorySnapshot(_scratch.Root, "scratch", [], []);
        using var extractor = new TreeSitterExtractor();
        var builder = new CodeGraphBuilder(
            extractor,
            [CSharpProfile.Instance],
            [new NameMatchResolver(), _scratch.Resolver]);

        var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        var edge = Assert.Single(graph.Edges, e => e.Site.CalledName == "Reserved");
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("@event.@class", edge.TargetContainer);
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
    public void A_failed_project_does_not_retract_correct_edges_in_the_clean_projects_that_call_it()
    {
        // The blast radius of the source-generator limitation, and the reason it needed fixing rather
        // than documenting: project A fails (its generator's members are missing), but A's bin/ dll
        // exists, so clean project B compiles against A's METADATA and a B->A call binds to a
        // non-source symbol. Retracting there would delete a NameMatchResolver edge that was correct
        // and whose target concept exists -- the extractor read A's source regardless of whether
        // Roslyn could compile A. So one project's failure would silently cost edges in every project
        // that calls into it.
        using var repository = new GeneratorAcrossProjectsRepository();

        var resolver = RoslynResolver.Create(repository.Root, [repository.ApplicationProject]);

        // The situation is really set up: A failed, B compiled.
        var library = Assert.Single(resolver.Projects, p => p.ProjectPath == repository.LibraryProject);
        Assert.Equal(RoslynProjectAvailability.CompilationHadErrors, library.Availability);
        var application = Assert.Single(resolver.Projects, p => p.ProjectPath == repository.ApplicationProject);
        Assert.Equal(RoslynProjectAvailability.Compiled, application.Availability);
        Assert.False(resolver.IsComplete);

        using var extractor = new TreeSitterExtractor();
        var extracted = extractor.Extract("app/Program.cs", repository.ApplicationSourceFile, CSharpProfile.Instance, ExtractionLimits.Default);

        // The cross-project call: no verdict at all, so the baseline's ByName edge survives.
        var intoLibrary = Assert.Single(extracted.Sites, s => s.CalledName == "Greet");
        Assert.Empty(resolver.Resolve([intoLibrary], extracted.Symbols));

        // The contrast, and the half that must NOT change: a genuinely external target is still
        // retracted, because there the baseline's name-only guess is wrong.
        var intoBcl = Assert.Single(extracted.Sites, s => s.CalledName == "Concat");
        var external = Assert.Single(resolver.Resolve([intoBcl], extracted.Symbols));
        Assert.Equal(EdgeConfidence.Unresolved, external.Confidence);
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

        Assert.NotNull(CompilationFactory.Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _));
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
        // Ordinal, not ignoreCase: the substitution below keys on this path, and the whole producer
        // compares paths ordinally. If MSBuild's spelling of a project path ever stopped matching the
        // one it was given, that is a finding about normalisation and this assertion should surface it.
        Assert.Equal(repository.LibraryProject, libraryReference.ProjectPath);

        repository.DeleteLibraryOutput();

        // Without the substitution: exactly the failure the spike measured.
        var unsatisfied = CompilationFactory.Create(applicationInputs, projectCompilations: null, SourceFileGate.Unbounded, out var missing);
        Assert.NotEmpty(missing);
        Assert.NotEmpty(unsatisfied.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        // With it: clean, from a repository that was only ever restored.
        var library = CompilationFactory.Create(libraryInputs, projectCompilations: null, SourceFileGate.Unbounded, out _);
        var application = CompilationFactory.Create(
            applicationInputs,
            new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(StringComparer.Ordinal)
            {
                [repository.LibraryProject] = library,
            },
            SourceFileGate.Unbounded,
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

    [Fact]
    public void A_compile_item_over_the_size_cap_is_refused_by_the_roslyn_engine_too()
    {
        // --max-file-size's help says "Largest source file, in bytes, the code stage will read", and
        // the Roslyn half of the code stage did a bare File.ReadAllBytes on every Compile item MSBuild
        // listed. Nothing of that content reaches the bundle -- symbols come only from the extractor --
        // so this is a documented bound one of the two engines did not honour, not a disclosure hole.
        using var repository = new OversizedSourceRepository();
        var limits = ExtractionLimits.Default with { MaxFileBytes = 4096 };

        // Preconditions, measured off disk rather than assumed from how the fixture was written.
        Assert.True(new FileInfo(repository.BigFile).Length > limits.MaxFileBytes);
        Assert.True(new FileInfo(repository.SmallFile).Length < limits.MaxFileBytes);

        // The tree-sitter half already refuses it, which is the disagreement this closes.
        using var extractor = new TreeSitterExtractor();
        var extracted = extractor.Extract("Big.cs", repository.BigFile, CSharpProfile.Instance, limits);
        Assert.Equal(FileStatus.SkippedTooLarge, extracted.Status);

        // Under the default 2 MiB cap the Roslyn half does read it, so the assertion below is about
        // the cap and not about the file being unreadable for some unrelated reason.
        var uncapped = RoslynResolver.Create(repository.Root, [repository.Project]);
        Assert.True(uncapped.Owns("Big.cs"), Describe(uncapped));

        var capped = RoslynResolver.Create(repository.Root, [repository.Project], limits);

        Assert.False(capped.Owns("Big.cs"));
        // Still a cap and not a collapse: the project compiled and its in-bounds file is owned.
        Assert.True(capped.IsComplete, Describe(capped));
        Assert.True(capped.Owns("Small.cs"), Describe(capped));
    }

    [Fact]
    public void A_compile_item_that_is_not_a_path_is_dropped_rather_than_thrown()
    {
        // A Compile item's FullPath is a string MSBuild PRINTED, not a path anything validated, and a
        // repository-authored target can set it to whatever it likes. Measured on this host:
        // `new FileInfo("x\0y")` throws ArgumentException and a 40 KB path throws PathTooLongException.
        // Only the second derives from IOException, so only the second was caught -- the first escaped
        // CompilationFactory, escaped RoslynResolver.Compile, and aborted the run.
        //
        // Dropped rather than refused, unlike the reference metadata in ReadReferences: an unreadable
        // Compile item is the same gap as a missing one, which TryParse already reports by omission and
        // the caller's zero-errors gate then catches.
        var inputs = FakeInputs with { CompileFiles = ["x\u0000y.cs", new string('a', 40000) + ".cs"] };

        var compilation = CompilationFactory.Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _);

        Assert.Empty(compilation.SyntaxTrees);
    }

    [Fact]
    public void An_msbuild_answer_too_large_to_hold_is_refused_rather_than_read()
    {
        // The reader used to be a bare ReadToEndAsync on a stream whose SIZE the scanned repository
        // controls. What was offered as the way to drive that does NOT work: with -getItem/-getProperty
        // the console log is suppressed, so an injected `-v:diag`, and a target emitting three 100 KB
        // <Message> lines, each printed 344,326 bytes here -- byte-identical to the clean run. What
        // does work is the JSON itself. This fixture's Directory.Build.targets declares 10,000 Compile
        // items with 3,000-character paths and takes the same query to ~100 MB in about three seconds,
        // from fifteen lines of build logic. It doubles as a live demonstration of the other half of
        // the threat model: a Directory.Build.targets rewriting the answer the producer asked for.
        using var repository = new FloodingAnswerRepository();

        var ex = Assert.Throws<MsBuildQueryException>(() => MsBuildProjectQuery.Query(repository.Project));

        // Named, so a refusal that fired for some other reason -- an unrestored project, a missing
        // dotnet -- cannot pass this. And refused rather than truncated: a truncated answer parses to
        // nothing useful anyway, and half an item list is the half-answer Query's contract forbids.
        Assert.Contains("printed more than 32 MiB", ex.Message, StringComparison.Ordinal);
    }

    [DirectoryLinkFact]
    public void A_compile_item_from_outside_the_repository_is_not_walked_up_to_the_filesystem_root()
    {
        // The bound, and the reason it is a counted depth rather than a string match. The walk used to
        // stop only on reaching the repository root or running out of parents, so for a Compile item
        // from OUTSIDE the repository -- `<Compile Include="..\..\Shared\X.cs"/>`, ordinary in real
        // solutions -- the root was never met and every ancestor up to the filesystem root was probed.
        // Here the shared tree is reached through a link, which is exactly the shape that made this
        // matter: on macOS or Linux a shared tree commonly sits under a symlinked ancestor
        // (/tmp -> /private/tmp), so EVERY such item was dropped.
        using var repository = new LinkedSourceRepository();

        // Precondition, measured off disk: the item really is reached through a link, and the file
        // itself is not one -- so anything that refuses it refused it by walking.
        Assert.NotNull(new DirectoryInfo(repository.OutsideLinkDirectory).LinkTarget);
        Assert.Null(new FileInfo(repository.OutsideCompileItem).LinkTarget);

        var resolver = RoslynResolver.Create(repository.OutsideRepositoryRoot, [repository.OutsideProject]);

        var report = Assert.Single(resolver.Projects);
        Assert.Equal(RoslynProjectAvailability.Compiled, report.Availability);
        Assert.True(resolver.Owns("Caller.cs"), Describe(resolver));
    }

    [DirectoryLinkFact]
    public void A_compile_item_behind_a_link_inside_the_repository_is_still_refused()
    {
        // The other side of the same bound: within the repository the walk still runs, and still
        // refuses. Round 1 recorded this branch as read-verified because creating a link "needs
        // privileges this test run does not have"; that is wrong on this host -- a directory JUNCTION
        // needs no elevation on Windows, and DirectoryLinkFact falls back to one when
        // Directory.CreateSymbolicLink is refused. So the branch is executed here, not reasoned about.
        using var repository = new LinkedSourceRepository();

        Assert.NotNull(new DirectoryInfo(repository.InsideLinkDirectory).LinkTarget);

        var resolver = RoslynResolver.Create(repository.InsideRepositoryRoot, [repository.InsideProject]);

        // Refused by omission, exactly as a missing file is: the type it declares is then undefined at
        // its use site, the zero-errors gate reports the project unavailable, and the name-matching
        // baseline carries it.
        var report = Assert.Single(resolver.Projects);
        Assert.Equal(RoslynProjectAvailability.CompilationHadErrors, report.Availability);
        Assert.Contains("CS0246", report.Detail, StringComparison.Ordinal);
        Assert.False(resolver.Owns("Main.cs"));
    }

    [Fact]
    public void No_msbuild_skips_the_stage_that_executes_the_scanned_repositorys_build_logic()
    {
        // Lives with the resolver rather than in CliTests because the property under test is a fact
        // about THIS stage: whether it ran at all. Driven through the shipped composition, because
        // "no dotnet msbuild was spawned" is not something the resolver can be asked -- it is the
        // absence of the call that creates it.
        //
        // The repository is deliberately NOT restored, so the query fails and the run says so by
        // name. That note is the observable difference: it can only exist if msbuild ran.
        using var repository = new UnrestoredProjectRepository();
        using var workspace = new Workspace();

        var ran = Generate(repository.Root, Path.Combine(workspace.Root, "with"));
        Assert.Equal(0, ran.ExitCode);
        Assert.Contains("not compiled", ran.Error, StringComparison.Ordinal);

        var skipped = Generate(repository.Root, Path.Combine(workspace.Root, "without"), "--no-msbuild");
        Assert.Equal(0, skipped.ExitCode);
        Assert.DoesNotContain("not compiled", skipped.Error, StringComparison.Ordinal);

        // And it is disclosed rather than silent: what an operator loses here is edges, and a run
        // that quietly resolved fewer calls would look exactly like a run that had fewer to resolve.
        Assert.Contains("--no-msbuild", skipped.Error, StringComparison.Ordinal);
        Assert.Contains("name-matching baseline", skipped.Error, StringComparison.Ordinal);

        // Both costs, not just the one the flag is usually described by. §5.1's source-ownership map
        // comes out of the SAME MSBuild query, so skipping the stage also drops the package ->
        // namespace level of the containment spine -- and under --update that overwrites the links a
        // previous run had. Three places used to enumerate this flag's cost as "edges" alone.
        Assert.Contains("containment link", skipped.Error, StringComparison.Ordinal);
        Assert.Contains("--update", skipped.Error, StringComparison.Ordinal);

        // The rest of the stage still ran: the bundle has code concepts either way.
        Assert.True(Directory.Exists(Path.Combine(workspace.Root, "without", "code")));
    }

    private static (int ExitCode, string Output, string Error) Generate(string repoPath, string outPath, params string[] extra)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["generate", "--repo", repoPath, "--out", outPath, .. extra];
        var exitCode = OkfgenCli.Run(args, output, error);

        return (exitCode, output.ToString(), error.ToString());
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
        private static readonly (string FileName, string Source, bool BomAndCrlf)[] Sources =
        [
            ("Ambiguity.cs", AmbiguitySource, false),
            ("NonAscii.cs", NonAsciiSource, false),
            ("External.cs", ExternalSource, false),
            ("Verbatim.cs", VerbatimSource, false),
            ("ContainerSpelling.cs", ContainerSpellingSource, false),
            ("ExplicitImplementation.cs", ExplicitImplementationSource, false),
            ("BomCrlf.cs", BomCrlfSource, true),
        ];

        private readonly Dictionary<string, ExtractionResult> _extracted = new(StringComparer.Ordinal);

        public ScratchProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "okf-producer-roslyn-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Root);

            var projectPath = Path.Combine(Root, "Scratch.csproj");
            File.WriteAllText(projectPath, ProjectFile, new UTF8Encoding(false));

            foreach (var (fileName, source, bomAndCrlf) in Sources)
            {
                // UTF-8, LF-normalised and no BOM by default, so the bytes on disk are exactly the
                // bytes the const string in this file describes -- the offsets asserted above are
                // computed against that string.
                //
                // Exactly one file is written the other way, BOM + CRLF. This class is built around a
                // property about ENCODING -- "a BOM stripped by one engine and kept by the other
                // credits calls to whatever sits three bytes away" -- and one encoding shape cannot
                // hold a claim about two.
                File.WriteAllText(
                    Path.Combine(Root, fileName),
                    source.ReplaceLineEndings(bomAndCrlf ? "\r\n" : "\n"),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: bomAndCrlf));
            }

            Restore(projectPath);

            Resolver = RoslynResolver.Create(Root, [projectPath]);
            Assert.True(Resolver.IsComplete, Describe(Resolver));

            using var extractor = new TreeSitterExtractor();
            var symbols = new List<SymbolFact>();
            foreach (var (fileName, _, _) in Sources)
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

    /// <summary>
    /// A restored repository of two independent projects, one of which pins a <c>LangVersion</c> no
    /// Roslyn build knows. They do not reference each other, so nothing but the resolver's own
    /// per-project scoping keeps the sound one working.
    /// </summary>
    private sealed class UnknownLanguageVersionRepository : ScratchRepository
    {
        public UnknownLanguageVersionRepository()
            : base("langversion")
        {
            StrandedProject = Write("stranded/Stranded.csproj", StrandedProjectFile);
            Write("stranded/Stranded.cs", StrandedSource);
            SoundProject = Write("sound/Sound.csproj", SoundProjectFile);
            SoundSourceFile = Write("sound/Sound.cs", SoundSource);

            Restore(StrandedProject);
            Restore(SoundProject);
        }

        public string StrandedProject { get; }

        public string SoundProject { get; }

        public string SoundSourceFile { get; }

        // MSBuild reports LangVersion as a plain property and never validates it here: the query runs
        // ResolveReferences/GenerateGlobalUsings/GenerateAssemblyInfo, none of which invoke csc. So
        // "99" survives all the way to LanguageVersionFacts.TryParse, which is the point.
        private const string StrandedProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <LangVersion>99</LangVersion>
              </PropertyGroup>
            </Project>
            """;

        private const string SoundProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        private const string StrandedSource = """
            namespace Stranded;
            public class Orphan { public int Leaf() => 1; public int Branch() => Leaf(); }
            """;

        private const string SoundSource = """
            namespace Sound;
            public class Caller { public int Inner() => 1; public int Outer() => Inner(); }
            """;
    }

    /// <summary>
    /// A <b>built</b> two-project repository where the library uses a Roslyn source generator and the
    /// application calls a plain method on it. Built rather than merely restored, deliberately: the
    /// library's <c>bin/</c> assembly has to exist for the application to compile against its metadata,
    /// which is the whole situation under test. Real <c>csc</c> runs the generator, so the build
    /// succeeds even though this producer's own compilation of the library cannot.
    /// </summary>
    private sealed class GeneratorAcrossProjectsRepository : ScratchRepository
    {
        public GeneratorAcrossProjectsRepository()
            : base("generator-xproj")
        {
            LibraryProject = Write("lib/Library.csproj", LibraryProjectFile);
            Write("lib/Greeter.cs", LibrarySource);
            ApplicationProject = Write("app/Application.csproj", ApplicationProjectFile);
            ApplicationSourceFile = Write("app/Program.cs", ApplicationSource);

            Build(ApplicationProject);
        }

        public string LibraryProject { get; }

        public string ApplicationProject { get; }

        public string ApplicationSourceFile { get; }

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

        // Greet is ordinary and is what the application calls; PayloadContext.Default is what only the
        // System.Text.Json generator supplies, and so what makes this project fail to compile here.
        private const string LibrarySource = """
            using System.Text.Json.Serialization;

            namespace Library;

            public record Payload(string Name);

            [JsonSerializable(typeof(Payload))]
            public partial class PayloadContext : JsonSerializerContext { }

            public class Greeter
            {
                public string Greet(string who) => who;
                public string Encode(Payload p) => System.Text.Json.JsonSerializer.Serialize(p, PayloadContext.Default.Payload);
            }
            """;

        private const string ApplicationSource = """
            namespace App;
            public class Program
            {
                public static string Run() => new Library.Greeter().Greet("world");
                public static string Join() => string.Concat("x", "y");
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
    /// A restored single-project repository holding one ordinary source file and one deliberately
    /// oversized one that <b>nothing references</b>, so dropping the big one still leaves a clean
    /// compilation -- which is what lets the assertion be about the cap rather than about a project
    /// that stopped compiling.
    /// </summary>
    private sealed class OversizedSourceRepository : ScratchRepository
    {
        public OversizedSourceRepository()
            : base("oversize")
        {
            Project = Write("Oversize.csproj", ProjectFile);
            SmallFile = Write("Small.cs", SmallSource);
            BigFile = Write("Big.cs", BigSource());

            Restore(Project);
        }

        public string Project { get; }

        public string SmallFile { get; }

        public string BigFile { get; }

        private static string BigSource()
        {
            var padding = string.Join("\n", Enumerable.Repeat("// padding, so this file is over the cap the test sets", 400));
            return $"namespace Oversize;\n{padding}\npublic class Bulky {{ public int Weigh() => 1; }}\n";
        }

        private const string SmallSource = """
            namespace Oversize;
            public class Slight { public int Weigh() => 2; }
            """;

        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
    }

    /// <summary>
    /// A one-project repository whose <c>Directory.Build.targets</c> inflates the answer MSBuild
    /// prints past the reader's cap -- 10,000 <c>Compile</c> items with 3,000-character paths, which
    /// measured ~100 MB of stdout in ~3 s on this host against ~344 KB for the same query without it.
    ///
    /// <para>
    /// The declaration is deliberately of files that do not exist: nothing here ever compiles the
    /// project, only queries it, and materialising 10,000 real files would cost far more than the
    /// property under test is worth.
    /// </para>
    /// </summary>
    private sealed class FloodingAnswerRepository : ScratchRepository
    {
        public FloodingAnswerRepository()
            : base("flood")
        {
            Project = Write("Flood.csproj", ProjectFile);
            Write("Directory.Build.targets", FloodTargets);
            Restore(Project);
        }

        public string Project { get; }

        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        private const string FloodTargets = """
            <Project>
              <Target Name="Flood" BeforeTargets="ResolveReferences">
                <PropertyGroup>
                  <P>0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789</P>
                  <P>$(P)$(P)$(P)$(P)$(P)$(P)$(P)$(P)$(P)$(P)</P>
                  <P>$(P)$(P)$(P)</P>
                  <L>a0.cs;a1.cs;a2.cs;a3.cs;a4.cs;a5.cs;a6.cs;a7.cs;a8.cs;a9.cs</L>
                  <L>$(L);$(L)b;$(L)c;$(L)d;$(L)e;$(L)f;$(L)g;$(L)h;$(L)i;$(L)j</L>
                  <L>$(L);$(L)B;$(L)C;$(L)D;$(L)E;$(L)F;$(L)G;$(L)H;$(L)I;$(L)J</L>
                  <L>$(L);$(L)K;$(L)L;$(L)M;$(L)N;$(L)O;$(L)P;$(L)Q;$(L)R;$(L)S</L>
                </PropertyGroup>
                <ItemGroup>
                  <Seed Include="$(L)" />
                  <Compile Include="@(Seed->'$(P)%(Identity)')" />
                </ItemGroup>
              </Target>
            </Project>
            """;
    }

    /// <summary>
    /// A <see cref="FactAttribute"/> that skips itself when this host cannot create a directory link
    /// at all, rather than passing vacuously.
    ///
    /// <para>
    /// It skips on very few hosts. <see cref="Directory.CreateSymbolicLink(string, string)"/> works
    /// unprivileged on Linux and macOS, and on Windows a directory JUNCTION needs no elevation either
    /// -- measured on this host, where <c>mklink /J</c> succeeds as an ordinary user and
    /// <see cref="FileSystemInfo.LinkTarget"/> reports its target, which is the only thing
    /// <c>CompilationFactory</c> reads. Wave 2b round 1 recorded the reparse-point branch as
    /// unreachable from a test for want of privileges; that was not true, and these two tests are what
    /// it cost to find out.
    /// </para>
    /// </summary>
    private sealed class DirectoryLinkFactAttribute : FactAttribute
    {
        public DirectoryLinkFactAttribute()
        {
            if (!DirectoryLinks.Supported)
            {
                Skip = "this host can create neither a directory symbolic link nor a junction";
            }
        }
    }

    /// <summary>Creates directory links for the fixtures that need one, by whichever mechanism this host allows.</summary>
    private static class DirectoryLinks
    {
        private static readonly Lazy<bool> Probe = new(ProbeOnce);

        public static bool Supported => Probe.Value;

        /// <summary>Creates <paramref name="target"/>, links <paramref name="link"/> to it, and returns the link's path.</summary>
        public static string Create(string link, string target)
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);

            if (!TryLink(link, target))
            {
                throw new InvalidOperationException($"could not create a directory link at {link} -> {target}.");
            }

            return link;
        }

        private static bool TryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return new DirectoryInfo(link).LinkTarget is not null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows without Developer Mode refuses a symbolic link; a junction is still allowed.
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var startInfo = new ProcessStartInfo("cmd")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);

            try
            {
                using var process = Process.Start(startInfo)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }

            return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
        }

        private static bool ProbeOnce()
        {
            var scratch = Path.Combine(Path.GetTempPath(), "okf-producer-linkprobe-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                // The target has to exist before the probe: `mklink /J` refuses a missing one, so a
                // probe without this step would report "unsupported" on a host that supports it fine.
                Directory.CreateDirectory(Path.Combine(scratch, "target"));
                return TryLink(Path.Combine(scratch, "link"), Path.Combine(scratch, "target"));
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
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

    /// <summary>
    /// Two one-project repositories, each reaching a source file through a directory link, laid out to
    /// separate the two halves of <c>CompilationFactory</c>'s reparse-point check.
    ///
    /// <para>
    /// <c>outside/</c> holds the repository at <c>outside/repo</c> and its shared source at
    /// <c>outside/shared</c>, a link one level ABOVE that root -- the linked out-of-repository
    /// <c>Compile</c> item. <c>inside/</c> holds both inside the root, so the walk is bounded and still
    /// finds the link.
    /// </para>
    /// </summary>
    private sealed class LinkedSourceRepository : ScratchRepository
    {
        public LinkedSourceRepository()
            : base("linked")
        {
            // --- The out-of-repository half. Root/outside/repo is the repository; the source lives at
            // Root/outside/shared/Shared.cs, one level above it and reached through a link.
            Write("outside/shared-real/Shared.cs", SharedSource);
            OutsideLinkDirectory = DirectoryLinks.Create(
                Path.Combine(Root, "outside", "shared"), Path.Combine(Root, "outside", "shared-real"));
            OutsideCompileItem = Path.Combine(OutsideLinkDirectory, "Shared.cs");
            OutsideProject = Write("outside/repo/Linked.csproj", OutsideProjectFile);
            Write("outside/repo/Caller.cs", CallerSource);
            OutsideRepositoryRoot = Path.Combine(Root, "outside", "repo");
            Restore(OutsideProject);

            // --- The in-repository half: the same shape, one level DOWN from the root instead of up.
            Write("inside/real/Hidden.cs", HiddenSource);
            InsideLinkDirectory = DirectoryLinks.Create(
                Path.Combine(Root, "inside", "linked"), Path.Combine(Root, "inside", "real"));
            InsideProject = Write("inside/Linked.csproj", InsideProjectFile);
            Write("inside/Main.cs", MainSource);
            InsideRepositoryRoot = Path.Combine(Root, "inside");
            Restore(InsideProject);
        }

        public string OutsideRepositoryRoot { get; }

        public string OutsideProject { get; }

        public string OutsideLinkDirectory { get; }

        public string OutsideCompileItem { get; }

        public string InsideRepositoryRoot { get; }

        public string InsideProject { get; }

        public string InsideLinkDirectory { get; }

        private const string SharedSource = """
            namespace Shared;
            public class Helper { public int Value() => 7; }
            """;

        private const string CallerSource = """
            namespace Linked;
            public class Caller { public int Go() => new Shared.Helper().Value(); }
            """;

        private const string HiddenSource = """
            namespace Linked;
            public class Hidden { public int Value() => 7; }
            """;

        private const string MainSource = """
            namespace Linked;
            public class Main { public int Go() => new Hidden().Value(); }
            """;

        private const string OutsideProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\shared\Shared.cs" />
              </ItemGroup>
            </Project>
            """;

        private const string InsideProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Main.cs" />
                <Compile Include="linked\Hidden.cs" />
              </ItemGroup>
            </Project>
            """;
    }

    /// <summary>
    /// A single-project repository that is deliberately never restored, so <c>dotnet msbuild</c>'s
    /// <c>ResolveReferences</c> fails on it -- the documented common cause of a project the exact
    /// resolver cannot query, and the one that makes a run print a note naming that project.
    /// </summary>
    private sealed class UnrestoredProjectRepository : ScratchRepository
    {
        public UnrestoredProjectRepository()
            : base("unrestored")
        {
            Write("Unrestored.csproj", ProjectFile);
            Write("Widget.cs", Source);
        }

        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        private const string Source = """
            namespace Unrestored;
            public class Widget
            {
                public int Weight() => 1;
                public int Total() => Weight();
            }
            """;
    }

    /// <summary>An empty throwaway directory to write bundles into, so no <c>--out</c> lands inside a scanned repository.</summary>
    private sealed class Workspace : ScratchRepository
    {
        public Workspace()
            : base("workspace")
        {
        }
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

    private static void Restore(string projectPath) => Dotnet("restore", projectPath);

    /// <summary>
    /// A full build, for the one fixture that needs a project's <c>bin/</c> assembly to actually exist.
    /// Real <c>csc</c> runs source generators, so a project this producer cannot compile still builds
    /// here -- which is precisely the asymmetry that fixture exercises.
    /// </summary>
    private static void Build(string projectPath) => Dotnet("build", projectPath);

    private static void Dotnet(string verb, string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add(verb);
        startInfo.ArgumentList.Add(projectPath);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"`dotnet {verb} {projectPath}` exited {process.ExitCode}: {stdout.GetAwaiter().GetResult()} {stderr.GetAwaiter().GetResult()}");
    }
}

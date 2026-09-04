// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.CodeGraph;

public class CodeGraphBuilderTests
{
    private static readonly LanguageProfile CSharpProfile =
        new("csharp", "tree-sitter-c-sharp", "", "", "///", [".cs"]);

    private static readonly IReadOnlyList<LanguageProfile> CSharpProfiles = [CSharpProfile];

    private static SymbolFact Member(string container, string name, string path = "A.cs", SymbolVisibility visibility = SymbolVisibility.Public) =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            visibility, path, 0, 10, 1, 1, null);

    private sealed class StubExtractor(params SymbolFact[] symbols) : ILanguageExtractor
    {
        public IReadOnlyList<CallSite> Sites { get; init; } = [];

        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits) =>
            new([.. symbols.Where(s => s.RelativePath == relativePath)],
                [.. Sites.Where(s => s.RelativePath == relativePath)],
                FileStatus.Extracted);
    }

    private sealed class CapturingExtractor : ILanguageExtractor
    {
        public LanguageProfile? ReceivedProfile { get; private set; }

        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits)
        {
            ReceivedProfile = profile;
            return new ExtractionResult([], [], FileStatus.Extracted);
        }
    }

    private sealed class StubResolver(string owned, EdgeConfidence confidence, string? targetContainer = null, string? targetName = null) : ISymbolResolver
    {
        public bool Owns(string relativePath) => relativePath == owned;

        public IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols) =>
            [.. sites.Select(s => new ResolvedEdge(s, targetContainer ?? "T", targetName ?? s.CalledName, confidence))];
    }

    [Fact]
    public void A_later_resolver_overrides_an_earlier_verdict_for_files_it_owns()
    {
        // §2.1: resolvers are chained, not exclusive. NameMatch gives a baseline
        // for every language; Roslyn overrides it for the files it owns, at
        // identity of call site. "Callee" is a real symbol here (not just a name the resolver made
        // up) so CodeGraph's own consistency invariant -- a resolved target absent from Symbols
        // degrades to Unresolved -- does not itself interfere with what this test is pinning.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller"), Member("T", "Callee")) { Sites = [site] },
            CSharpProfiles,
            [new StubResolver("A.cs", EdgeConfidence.ByName), new StubResolver("A.cs", EdgeConfidence.Exact)]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
    }

    [Fact]
    public void A_resolver_that_does_not_own_a_file_leaves_the_earlier_verdict_alone()
    {
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller"), Member("T", "Callee")) { Sites = [site] },
            CSharpProfiles,
            [new StubResolver("A.cs", EdgeConfidence.ByName), new StubResolver("Other.cs", EdgeConfidence.Exact)]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.ByName, Assert.Single(graph.Edges).Confidence);
    }

    [Fact]
    public void An_edge_whose_caller_is_filtered_out_of_scope_is_dropped_entirely()
    {
        // §5.4 + CodeGraph's own consistency invariant: "Hidden" is Private, so it never reaches
        // Symbols under the default scope -- there is no concept left for this edge to hang off, so
        // it must not survive into Edges either, resolved or not.
        var site = new CallSite("T", "Hidden", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Hidden", visibility: SymbolVisibility.Private)) { Sites = [site] },
            CSharpProfiles, []);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.DoesNotContain(graph.Symbols, s => s.Name == "Hidden");
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void An_edge_whose_resolved_target_is_filtered_out_of_scope_degrades_to_unresolved()
    {
        // The caller ("Caller") is public and survives scope filtering; the resolver still resolves
        // the call to a real symbol ("PrivateTarget") that is itself Private and gets filtered out.
        // Pointing the edge at a concept that will never exist is worse than not resolving it at all
        // -- §4.5 already renders an unresolved call as plain text, which is the correct fallback.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller"), Member("T", "PrivateTarget", visibility: SymbolVisibility.Private)) { Sites = [site] },
            CSharpProfiles,
            [new StubResolver("A.cs", EdgeConfidence.Exact, targetContainer: "T", targetName: "PrivateTarget")]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.DoesNotContain(graph.Symbols, s => s.Name == "PrivateTarget");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EdgeConfidence.Unresolved, edge.Confidence);
        Assert.Null(edge.TargetContainer);
        Assert.Null(edge.TargetName);
    }

    [Fact]
    public void With_no_resolver_at_all_the_shape_of_the_output_is_unchanged()
    {
        // The property the two-seam design exists to guarantee: a missing
        // resolver degrades precision, never the shape.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] }, CSharpProfiles, []);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(graph.Edges).Confidence);
        Assert.Single(graph.Symbols);
    }

    [Fact]
    public void Symbols_and_edges_come_out_in_a_deterministic_order()
    {
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "b", "B.cs"), Member("T", "a", "A.cs")), CSharpProfiles, []);

        var graph = builder.Build(SnapshotWith("B.cs", "A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(["a", "b"], graph.Symbols.Select(s => s.Name));
    }

    [Fact]
    public void A_file_matching_no_profile_is_skipped_without_affecting_completeness()
    {
        // The extractor is configured to return a symbol for "A.txt" if it's ever asked to extract
        // that file, but no registered profile claims the ".txt" extension -- proving the file
        // never reaches the extractor at all, and that skipping it this way is not the same as a
        // FileStatus skip reason (it must not flip RunStatus.IsComplete to false).
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller", "A.txt")), CSharpProfiles, []);

        var graph = builder.Build(SnapshotWith("A.txt"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Empty(graph.Symbols);
        Assert.True(graph.Status.IsComplete);
    }

    [Fact]
    public void A_file_matching_a_profile_reaches_the_extractor_with_that_profile()
    {
        var profile = new LanguageProfile("csharp", "tree-sitter-c-sharp", "decl-query", "call-query", "///", [".cs"]);
        var extractor = new CapturingExtractor();
        var builder = new CodeGraphBuilder(extractor, [profile], []);

        builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Same(profile, extractor.ReceivedProfile);
    }

    [Fact]
    public void Edges_tying_on_caller_and_callee_break_the_tie_by_offset_and_stay_stable_across_builds()
    {
        // Two call sites to the same method from the same caller tie on
        // (CallerContainer, CallerName, CalledName) -- constructed out of ascending order (the later
        // offset first) so a correct implementation must actively sort by offset rather than happen
        // to preserve dictionary/insertion order.
        var lateSite = new CallSite("T", "Caller", "Callee", "A.cs", 50);
        var earlySite = new CallSite("T", "Caller", "Callee", "A.cs", 10);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [lateSite, earlySite] }, CSharpProfiles, []);
        var snapshot = SnapshotWith("A.cs");

        var firstBuild = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);
        var secondBuild = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal([10, 50], firstBuild.Edges.Select(e => e.Site.Offset));
        Assert.Equal(
            firstBuild.Edges.Select(e => e.Site.Offset),
            secondBuild.Edges.Select(e => e.Site.Offset));
    }

    [Fact]
    public void A_name_declared_once_publicly_and_once_internally_stays_unresolved_under_the_default_scope()
    {
        // The resolver must see BOTH declarations of "Helper", not just the public one the default
        // scope keeps. Filtering first leaves exactly one declaration standing, which
        // NameMatchResolver reads as unambiguous and links -- confidently, and to the wrong concept,
        // since the call is to the internal one. "Missing is acceptable, wrong is not" (§2.1), so
        // the correct verdict is Unresolved.
        var site = new CallSite("N.C", "Run", "Helper", "C.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(
                Member("N.A", "Helper"),
                Member("N.B", "Helper", "B.cs", SymbolVisibility.Internal),
                Member("N.C", "Run", "C.cs"))
            { Sites = [site] },
            CSharpProfiles,
            [new NameMatchResolver()]);

        var graph = builder.Build(SnapshotWith("A.cs", "B.cs", "C.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EdgeConfidence.Unresolved, edge.Confidence);
        Assert.Null(edge.TargetContainer);
        Assert.Null(edge.TargetName);
    }

    [Fact]
    public void Narrowing_visibility_scope_never_turns_an_unresolved_call_into_a_resolved_one()
    {
        // The general property behind the case above, stated over the one axis this builder actually
        // controls: SymbolFact.Visibility. IncludeInternal=true is the wider scope, ScopeOptions.Default
        // the narrower one. Whatever the wide run leaves Unresolved, the narrow run must leave
        // Unresolved too -- narrowing scope may only ever LOSE edges, never invent one.
        //
        // The property is stated over the VISIBILITY filter only, and that is not a hedge: the
        // IncludeTests filter excludes whole files before they are ever opened
        // (FileEligibility.IsEligible, applied in the walk above), so declarations in an excluded
        // file are invisible to the resolver by construction and no in-process check can restore
        // them. Only the visibility filter runs on symbols the run already extracted, which is
        // exactly why it is the one that can be made not to lie.
        var site = new CallSite("N.C", "Run", "Helper", "C.cs", 42);
        CodeGraphBuilder Builder() => new(
            new StubExtractor(
                Member("N.A", "Helper"),
                Member("N.B", "Helper", "B.cs", SymbolVisibility.Internal),
                Member("N.C", "Run", "C.cs"))
            { Sites = [site] },
            CSharpProfiles,
            [new NameMatchResolver()]);
        var snapshot = SnapshotWith("A.cs", "B.cs", "C.cs");

        var wide = Builder().Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default with { IncludeInternal = true });
        var narrow = Builder().Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(wide.Edges).Confidence);
        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(narrow.Edges).Confidence);
    }

    [Fact]
    public void A_private_declaration_of_the_same_name_makes_the_call_ambiguous_even_though_it_is_never_in_scope()
    {
        // Private is out of scope under every flag, so there is no wider run to compare against --
        // but the reason the ambiguity matters does not come from the flags. Two declarations of
        // "Helper" exist in this repository and a name-only resolver cannot tell which one the call
        // meant; linking it to the public one because the private one was filtered away is the same
        // confident wrong edge, arrived at through a filter no flag can lift. Ambiguity is decided
        // over what the source DECLARES, not over what this run chose to publish.
        var site = new CallSite("N.C", "Run", "Helper", "C.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(
                Member("N.A", "Helper"),
                Member("N.B", "Helper", "B.cs", SymbolVisibility.Private),
                Member("N.C", "Run", "C.cs"))
            { Sites = [site] },
            CSharpProfiles,
            [new NameMatchResolver()]);

        var graph = builder.Build(SnapshotWith("A.cs", "B.cs", "C.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(graph.Edges).Confidence);
    }

    /// <summary>
    /// Builds the <see cref="RepositorySnapshot"/> <see cref="RepositoryScanner"/> would produce for
    /// a repo containing an empty file at each of <paramref name="relativePaths"/>. Real files on
    /// disk, not a fabricated snapshot field, because <see cref="RepositorySnapshot"/> itself carries
    /// no file listing -- <see cref="CodeGraphBuilder"/> discovers eligible files by walking
    /// <see cref="RepositorySnapshot.RepoPath"/>, the same way <see cref="RepositoryScanner"/> does
    /// for manifests.
    /// </summary>
    private static RepositorySnapshot SnapshotWith(params string[] relativePaths)
    {
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-codegraph-").FullName;
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
        }

        return new RepositorySnapshot(repoPath, "test-repo", [], []);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.CodeGraph;

public class CodeGraphBuilderTests
{
    private static SymbolFact Member(string container, string name, string path = "A.cs") =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            SymbolVisibility.Public, path, 0, 10, 1, 1, null);

    private sealed class StubExtractor(params SymbolFact[] symbols) : ILanguageExtractor
    {
        public IReadOnlyList<CallSite> Sites { get; init; } = [];

        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile) =>
            new([.. symbols.Where(s => s.RelativePath == relativePath)],
                [.. Sites.Where(s => s.RelativePath == relativePath)],
                FileStatus.Extracted);
    }

    private sealed class StubResolver(string owned, EdgeConfidence confidence) : ISymbolResolver
    {
        public bool Owns(string relativePath) => relativePath == owned;

        public IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols) =>
            [.. sites.Select(s => new ResolvedEdge(s, "T", s.CalledName, confidence))];
    }

    [Fact]
    public void A_later_resolver_overrides_an_earlier_verdict_for_files_it_owns()
    {
        // §2.1: resolvers are chained, not exclusive. NameMatch gives a baseline
        // for every language; Roslyn overrides it for the files it owns, at
        // identity of call site.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] },
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
            new StubExtractor(Member("T", "Caller")) { Sites = [site] },
            [new StubResolver("A.cs", EdgeConfidence.ByName), new StubResolver("Other.cs", EdgeConfidence.Exact)]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.ByName, Assert.Single(graph.Edges).Confidence);
    }

    [Fact]
    public void With_no_resolver_at_all_the_shape_of_the_output_is_unchanged()
    {
        // The property the two-seam design exists to guarantee: a missing
        // resolver degrades precision, never the shape.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] }, []);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(graph.Edges).Confidence);
        Assert.Single(graph.Symbols);
    }

    [Fact]
    public void Symbols_and_edges_come_out_in_a_deterministic_order()
    {
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "b", "B.cs"), Member("T", "a", "A.cs")), []);

        var graph = builder.Build(SnapshotWith("B.cs", "A.cs"), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.Equal(["a", "b"], graph.Symbols.Select(s => s.Name));
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

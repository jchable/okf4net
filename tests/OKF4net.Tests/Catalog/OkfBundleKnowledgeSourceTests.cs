// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="OkfBundleKnowledgeSource"/>: stateless per-call loading,
/// mapping of <see cref="ScoredConcept"/> to <see cref="KnowledgePassage"/>,
/// scoring/ordering parity with the core <see cref="ConceptSearch"/>, and
/// never-throws behaviour on a bad bundle directory.
/// </summary>
public class OkfBundleKnowledgeSourceTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

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

    [Fact]
    public async Task SearchAsync_maps_every_scored_concept_to_a_passage_carrying_the_source_id()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Null(result.Diagnostic);
        Assert.NotEmpty(result.Passages);
        Assert.All(result.Passages, p => Assert.Equal("mine", p.SourceId));
    }

    [Fact]
    public async Task SearchAsync_passage_fields_match_the_core_scorer_exactly()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);
        var query = new KnowledgeQuery("orders sales");

        var result = await source.SearchAsync(query);

        var bundle = Bundle.Load(tmp.Path);
        var expected = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);

        Assert.Equal(expected.Count, result.Passages.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var p = result.Passages[i];
            Assert.Equal(e.Concept.Id.ToString(), p.ConceptId);
            Assert.Equal(e.Concept.Document.Frontmatter.Title, p.Title);
            Assert.Equal(e.Score, p.Score);
            Assert.Equal(ConceptSearch.Excerpt(e.Concept.Document.Body, query.Text) ?? string.Empty, p.Excerpt);
            Assert.Equal(Path.GetRelativePath(bundle.Root, e.Concept.Path).Replace(Path.DirectorySeparatorChar, '/'), p.BundleRelativePath);
        }

        // The scorer's own contract: descending score, ties broken by ascending concept id --
        // assert the passage order itself is non-increasing in score as an extra sanity check.
        for (var i = 1; i < result.Passages.Count; i++)
        {
            Assert.True(result.Passages[i - 1].Score >= result.Passages[i].Score);
        }
    }

    [Fact]
    public async Task SearchAsync_relativizes_the_absolute_concept_path_against_the_bundle_root()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.All(result.Passages, p => Assert.False(Path.IsPathRooted(p.BundleRelativePath)));
    }

    [Fact]
    public async Task SearchAsync_over_missing_directory_returns_SourceUnavailable_without_throwing()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "does-not-exist");
        var source = new OkfBundleKnowledgeSource("gone", missing);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Empty(result.Passages);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, result.Diagnostic!.Code);
        Assert.Equal("gone", result.Diagnostic.SourceId);
    }

    [Fact]
    public async Task SearchAsync_with_blank_query_returns_no_passages_without_throwing()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("   "));

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Passages);
    }

    [Fact]
    public async Task SearchAsync_honors_the_tag_filter()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var unfiltered = await source.SearchAsync(new KnowledgeQuery("sales"));
        var filtered = await source.SearchAsync(new KnowledgeQuery("sales", "sales"));

        Assert.Equal(3, unfiltered.Passages.Count);
        Assert.Equal(2, filtered.Passages.Count);
        Assert.DoesNotContain(filtered.Passages, p => p.ConceptId == "tables/users");
    }

    // ---- Defensive null-Text guard (NRT is a compile-time hint only) -------

    [Fact]
    public async Task SearchAsync_with_null_query_Text_returns_empty_without_throwing()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        // KnowledgeQuery.Text is annotated non-null, but nothing at runtime
        // stops a null from arriving here (deserialization, reflection, an
        // explicit `!`) -- this must never throw a NullReferenceException,
        // per this type's documented never-throw contract.
        var result = await source.SearchAsync(new KnowledgeQuery(null!));

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Passages);
    }

    // ---- BundleRelativePath is normalized to '/' regardless of OS ---------

    [Fact]
    public async Task SearchAsync_normalizes_nested_BundleRelativePath_to_forward_slashes()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        // appendix_a's concepts live under nested directories (e.g. tables/orders.md),
        // so this exercises an actual multi-segment relative path, not just a bare filename.
        Assert.Contains(result.Passages, p => p.BundleRelativePath.Contains('/'));
        Assert.All(result.Passages, p => Assert.DoesNotContain('\\', p.BundleRelativePath));
    }

    // ---- Trust/status/stale_after are carried through from frontmatter -----

    [Fact]
    public async Task Passage_carries_trust_status_and_stale_after()
    {
        using var tmp = new TempDir();
        tmp.Write("dau.md",
            "---\ntype: Metric\ntitle: DAU active\ndescription: d\nstatus: deprecated\nverified: {by: human:ada}\nstale_after: 2026-01-01\n---\nActive users.\n");
        var source = new OkfBundleKnowledgeSource("s1", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("active"));
        var p = Assert.Single(result.Passages);
        Assert.Equal(TrustTier.HumanReviewed, p.TrustTier);
        Assert.Equal(ConceptStatus.Deprecated, p.Status);
        Assert.Equal("2026-01-01", p.StaleAfter);
    }

    // ---- Passages is a genuine read-only view (not just a List<T> hidden behind an interface) --

    [Fact]
    public async Task SearchAsync_Passages_cannot_be_downcast_to_a_mutable_list_and_mutated()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));
        Assert.NotEmpty(result.Passages);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<KnowledgePassage>)result.Passages;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }
}

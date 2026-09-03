// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

public class BundleWriterTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "okfproducer-write-" + Guid.NewGuid());
        return path;
    }

    /// <summary>
    /// A repo path unrelated to any <c>outPath</c> under test -- used for calls that don't care about
    /// the Finding 3 self/ancestor guard, so it never accidentally trips it.
    /// </summary>
    private static string UnrelatedRepoPath() =>
        Path.Combine(Path.GetTempPath(), "okfproducer-write-repo-" + Guid.NewGuid());

    private static GeneratedConcept SampleConcept(string id = "overview") =>
        new(ConceptId.Parse(id),
            OkfDocumentBuilder.ForType("Repository").Title("t").Description("d").Body("# t\n").Build());

    [Fact]
    public void Write_to_a_missing_directory_creates_it_and_writes_all_concepts()
    {
        var outPath = CreateTempDir();
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty, UnrelatedRepoPath());

            Assert.Equal(1, result.Written);
            Assert.Empty(result.Failures);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "index.md")));
        }
        finally
        {
            if (Directory.Exists(outPath)) Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_RequireEmpty_into_a_non_empty_directory_throws_and_writes_nothing()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "existing.txt"), "pre-existing");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty, UnrelatedRepoPath()));

            Assert.False(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "existing.txt")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Update_into_a_non_empty_directory_preserves_untouched_files()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "hand-written.md"), "---\ntype: Note\n---\n\nkept\n");
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Update, UnrelatedRepoPath());

            Assert.Equal(1, result.Written);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "hand-written.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Reset_deletes_and_recreates_the_directory()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "stale.md"), "---\ntype: Note\n---\n\nstale\n");
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Reset, UnrelatedRepoPath());

            Assert.Equal(1, result.Written);
            Assert.False(File.Exists(Path.Combine(outPath, "stale.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_regenerates_the_index_after_writing_concepts()
    {
        var outPath = CreateTempDir();
        try
        {
            new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty, UnrelatedRepoPath());

            // "t" alone would match almost any generated text (e.g. inside "Contents"); assert on the
            // actual link target IndexGenerator emits for the one concept we wrote, so this only
            // passes if the index genuinely reflects that concept.
            var indexText = File.ReadAllText(Path.Combine(outPath, "index.md"));
            Assert.Contains("overview.md", indexText);
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_reports_a_reserved_concept_id_as_a_failure_without_stopping_the_rest()
    {
        var outPath = CreateTempDir();
        var concepts = new List<GeneratedConcept>
        {
            SampleConcept("overview"),
            new(ConceptId.Parse("index"),
                OkfDocumentBuilder.ForType("Documentation").Title("t").Description("d").Body("# t\n").Build()),
        };
        try
        {
            var result = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty, UnrelatedRepoPath());

            Assert.Equal(1, result.Written);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("index", failure.Id.ToString());
            Assert.Contains("reserved concept id", failure.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Reset_refuses_to_delete_the_repository_it_scanned_when_out_equals_repo()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "stale.md"), "---\ntype: Note\n---\n\nstale\n");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Reset, outPath));

            Assert.Contains("Refusing to reset", ex.Message);
            Assert.True(File.Exists(Path.Combine(outPath, "stale.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Reset_refuses_to_delete_an_ancestor_of_the_repository_it_scanned()
    {
        var outPath = CreateTempDir();
        var repoPath = Path.Combine(outPath, "nested", "repo");
        Directory.CreateDirectory(repoPath);
        File.WriteAllText(Path.Combine(outPath, "stale.md"), "---\ntype: Note\n---\n\nstale\n");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Reset, repoPath));

            Assert.Contains("Refusing to reset", ex.Message);
            Assert.True(File.Exists(Path.Combine(outPath, "stale.md")));
            Assert.True(Directory.Exists(repoPath));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }
}

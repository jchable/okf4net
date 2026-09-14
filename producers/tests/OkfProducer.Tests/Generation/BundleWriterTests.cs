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

    private static GeneratedConcept NoteConcept(string id) =>
        new(ConceptId.Parse(id),
            OkfDocumentBuilder.ForType("Note").Title(id).Description("d").Body($"# {id}\n").Build());

    /// <summary>
    /// The finding this pins: <c>Path.GetFullPath</c> preserves a trailing separator on <c>--out</c>
    /// (shell completion writes one routinely), and <c>BundlePaths.IsInside</c> used to compare against
    /// a doubled separator no real path could start with -- so every staged concept was refused as
    /// "leaves the bundle root through a symbolic link or junction" and the run wrote nothing at all,
    /// even though nothing was linked. A trailing slash must write exactly what its absence writes.
    /// </summary>
    [Fact]
    public void Write_into_an_out_path_with_a_trailing_separator_writes_the_same_concepts_as_without_it()
    {
        var concepts = new[] { SampleConcept(), NoteConcept("reports/one"), NoteConcept("reports/two") };

        var withoutSlash = CreateTempDir();
        var withSlash = CreateTempDir();
        try
        {
            var plain = new BundleWriter().Write(withoutSlash, concepts, WritePolicy.RequireEmpty, UnrelatedRepoPath());
            var slashed = new BundleWriter().Write(withSlash + Path.DirectorySeparatorChar, concepts, WritePolicy.RequireEmpty, UnrelatedRepoPath());

            Assert.Empty(plain.Failures);
            Assert.Empty(slashed.Failures);
            Assert.Equal(plain.Written, slashed.Written);
            Assert.True(File.Exists(Path.Combine(withSlash, "overview.md")));
            Assert.True(File.Exists(Path.Combine(withSlash, "reports", "one.md")));
            Assert.True(File.Exists(Path.Combine(withSlash, "reports", "two.md")));
        }
        finally
        {
            if (Directory.Exists(withoutSlash)) Directory.Delete(withoutSlash, recursive: true);
            if (Directory.Exists(withSlash)) Directory.Delete(withSlash, recursive: true);
        }
    }

    /// <summary>
    /// The second, quieter half of the same finding: <c>CreateStagingDirectory</c> used
    /// <c>Path.GetDirectoryName(Path.GetFullPath(outPath))</c>, and for a trailing-slash <c>outPath</c>
    /// that returns the bundle directory itself (the trailing separator reads as an empty final path
    /// component), not its parent -- so the staging directory landed INSIDE the bundle it is documented
    /// to sit beside. Exercised through the <c>internal</c> seam directly: <c>Write</c>'s own
    /// <c>finally</c> always deletes the staging directory before returning, so nothing that only calls
    /// <c>Write</c> can observe where it was created.
    /// </summary>
    [Fact]
    public void CreateStagingDirectory_with_a_trailing_separator_on_out_lands_beside_the_bundle_not_inside_it()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        string? staging = null;
        try
        {
            staging = BundleWriter.CreateStagingDirectory(outPath + Path.DirectorySeparatorChar);

            var expectedParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outPath)));
            Assert.Equal(expectedParent, Path.GetDirectoryName(staging));
            Assert.False(
                BundlePaths.IsInside(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outPath)), staging),
                $"staging directory '{staging}' was created inside the bundle at '{outPath}'.");
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.Delete(outPath, recursive: true);
        }
    }

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

    [Fact]
    public void Write_Reset_refuses_a_bundle_holding_a_link_rather_than_emptying_it_and_then_throwing()
    {
        // MEASURED on Windows 11 build 26200 / .NET 10.0.8, six runs out of six:
        // Directory.Delete(outPath, recursive: true) over a tree containing a junction deletes the
        // real files, unlinks the junction, leaves the far end alone -- and THEN throws
        // UnauthorizedAccessException("Access to the path 'sub' is denied"). So the unguarded reset
        // emptied the bundle and threw out of Write before CommitStaging could put anything back, and
        // the operator was left with neither the old bundle nor the new one. That is the exact outcome
        // IBundleWriter's transactional guarantee says cannot happen.
        //
        // Both halves are asserted, because the guard is only worth having for the second: the
        // exception type says it was refused deliberately, and stale.md says the bundle survived the
        // refusal. Delete the FirstLinkUnder block in ResetBundle and both go red -- the throw becomes
        // an UnauthorizedAccessException and stale.md is gone by the time it arrives.
        var outPath = CreateTempDir();
        var outside = Path.Combine(Path.GetTempPath(), "okfproducer-write-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(outPath, "code"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim.md"), "---\ntype: Note\n---\n\nnot the bundle's\n");
        File.WriteAllText(Path.Combine(outPath, "stale.md"), "---\ntype: Note\n---\n\nstale\n");

        var link = Path.Combine(outPath, "code", "sub");
        ProducerFixture.CreateDirectoryLink(link, outside);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Reset, UnrelatedRepoPath()));

            Assert.Contains("Refusing to reset", ex.Message);
            Assert.Contains("code/sub", ex.Message);

            Assert.True(File.Exists(Path.Combine(outPath, "stale.md")), "the reset emptied the bundle before it failed.");
            Assert.False(File.Exists(Path.Combine(outPath, "overview.md")), "nothing was committed, so no concept should have landed either.");
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);
            Assert.True(File.Exists(Path.Combine(outside, "victim.md")), "the far side of the link is not this producer's to touch.");
        }
        finally
        {
            // Unlinked before the recursive delete, for the reason under test.
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(outPath))
            {
                Directory.Delete(outPath, recursive: true);
            }

            Directory.Delete(outside, recursive: true);
        }
    }
}

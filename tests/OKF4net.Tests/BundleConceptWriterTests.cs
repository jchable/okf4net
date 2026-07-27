// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Direct tests for the promoted core write primitive
/// <see cref="BundleConceptWriter"/>. The exhaustive tool-surface parity is
/// still carried by the existing OkfWriteToolsTests / OkfBundleToolsTests /
/// OkfContextProviderMemoryTests suites (which now run over the same primitive
/// via OkfBundleTools); these assert the primitive directly.
/// </summary>
public class BundleConceptWriterTests
{
    private const string ValidFrontmatter =
        "type: BigQuery Table\n"
        + "title: Refunds\n"
        + "description: One row per refund.\n"
        + "timestamp: 2026-07-22T00:00:00Z\n";

    [Fact]
    public void WriteConcept_creates_a_validated_file()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("tables/refunds", ValidFrontmatter, "# Refunds\n\nBody.\n");

        Assert.Contains("Written", result);
        var path = Path.Combine(tmp.Path, "tables", "refunds.md");
        Assert.True(File.Exists(path));
        OkfDocument.Parse(File.ReadAllText(path)).Validate();
    }

    [Fact]
    public void WriteConcept_missing_required_frontmatter_writes_nothing()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("tables/refunds", "type: X\n", "# body\n");

        Assert.StartsWith("Error:", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "refunds.md")));
    }

    [Fact]
    public void AppendToConceptAtomic_creates_then_appends()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var r1 = writer.AppendToConceptAtomic("memory/2026-07-24", ValidFrontmatter, cur => cur is null ? "first\n" : cur + "second\n");
        var r2 = writer.AppendToConceptAtomic("memory/2026-07-24", ValidFrontmatter, cur => cur is null ? "first\n" : cur.TrimEnd('\n') + "\n\nsecond\n");

        Assert.StartsWith("Written", r1);
        Assert.StartsWith("Written", r2);
        var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "memory", "2026-07-24.md"))).Body;
        Assert.Contains("first", body, StringComparison.Ordinal);
        Assert.Contains("second", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWriteCommitted_fires_after_a_successful_write()
    {
        using var tmp = new TempDir();
        var fired = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () => fired++);

        writer.WriteConcept("a/b", ValidFrontmatter, "# body\n");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Two_writers_over_the_same_root_share_one_lock_and_never_lose_an_append()
    {
        using var tmp = new TempDir();
        var writerA = new BundleConceptWriter(tmp.Path);
        var writerB = new BundleConceptWriter(tmp.Path + Path.DirectorySeparatorChar); // different spelling, same canonical root
        const int iterations = 16;

        Parallel.For(0, iterations, i =>
        {
            var w = i % 2 == 0 ? writerA : writerB;
            w.AppendToConceptAtomic(
                "memory/day",
                ValidFrontmatter,
                cur => (cur is null ? string.Empty : cur.TrimEnd('\n') + "\n") + $"line {i}\n");
        });

        var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "memory", "day.md"))).Body;
        for (var i = 0; i < iterations; i++)
        {
            Assert.Contains($"line {i}", body, StringComparison.Ordinal);
        }
    }
}

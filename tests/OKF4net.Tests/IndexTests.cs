namespace OKF4net.Tests;

/// <summary>
/// Index generation tests, mirroring the reference <c>tests/test_index.py</c>
/// via the Rust port (tests/index.rs). Literals and assertions copied
/// verbatim.
/// </summary>
public class IndexTests
{
    /// <summary>Port of <c>write_doc</c> (tests/index.rs:8-13).</summary>
    private static void WriteDoc(TempDir tmp, string rel, string type_, string title, string description)
    {
        var contents =
            "---\n" +
            $"type: {type_}\n" +
            $"title: {title}\n" +
            $"description: {description}\n" +
            "timestamp: 2026-05-27T00:00:00+00:00\n" +
            "---\n\n" +
            $"# {title}\n\n" +
            $"{description}\n";
        tmp.Write(rel, contents);
    }

    [Fact]
    public void Regenerate_groups_by_type_and_links_relative()
    {
        // tests/index.rs:15-37
        using var tmp = new TempDir();
        WriteDoc(tmp, "datasets/ga4.md", "BigQuery Dataset", "GA4 Dataset", "GA4 obfuscated ecommerce sample.");
        WriteDoc(tmp, "tables/events_.md", "BigQuery Table", "events_*", "Daily-sharded GA4 event tables.");
        WriteDoc(tmp, "tables/users.md", "BigQuery Table", "users", "Per-user dimension.");

        // Deterministic synthesizer so we can assert on the root index text.
        IndexGenerator.Synthesize synth = (_, children) => $"stub: {children.Count} items";
        var written = IndexGenerator.RegenerateIndexesWith(tmp.Path, synth);
        Assert.NotEmpty(written);

        var tablesIndex = File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"));
        Assert.StartsWith("# BigQuery Table", tablesIndex);
        Assert.Contains("[events_*](events_.md)", tablesIndex);
        Assert.Contains("[users](users.md)", tablesIndex);
        Assert.Contains("Daily-sharded GA4 event tables.", tablesIndex);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("# Subdirectories", rootIndex);
        Assert.Contains("(datasets/index.md) - GA4 obfuscated ecommerce sample.", rootIndex);
        Assert.Contains("(tables/index.md) - stub: 2 items", rootIndex);
    }

    [Fact]
    public void Regenerate_skips_empty_directories()
    {
        // tests/index.rs:39-46
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty_dir"));
        var written = IndexGenerator.RegenerateIndexes(tmp.Path);
        Assert.Empty(written);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "empty_dir", "index.md")));
    }

    [Fact]
    public void Regenerate_single_child_reuses_description()
    {
        // tests/index.rs:48-66
        using var tmp = new TempDir();
        WriteDoc(tmp, "datasets/only.md", "BigQuery Dataset", "Only Dataset", "The only dataset in this bundle.");

        var calls = 0;
        IndexGenerator.Synthesize counting = (_, children) =>
        {
            calls++;
            return $"stub: {children.Count} items";
        };
        IndexGenerator.RegenerateIndexesWith(tmp.Path, counting);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("(datasets/index.md) - The only dataset in this bundle.", rootIndex);
        Assert.Equal(0, calls);
    }
}

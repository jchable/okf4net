// SPDX-License-Identifier: LGPL-3.0-or-later
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

    [Fact]
    public void BuildIndexText_sorts_unicode_titles_like_rust_to_lowercase()
    {
        // Reviewer's scenario: .NET's ToLowerInvariant leaves U+0130 (İ)
        // unchanged, so under the old StringComparer.Ordinal sort "İtem"
        // (starting with U+0130) sorted AFTER "j-item" (U+0130 > 'j' in
        // UTF-16 ordinal order). Rust's `to_lowercase` maps U+0130 to "i̇"
        // (lowercase i + combining dot above), which sorts BEFORE "j-item".
        var entries = new List<IndexEntry>
        {
            new("Thing", "j-item", "j-item.md", string.Empty),
            new("Thing", "İtem", "item.md", string.Empty),
        };

        var text = IndexGenerator.BuildIndexText(entries);

        var iTemIndex = text.IndexOf("[İtem]", StringComparison.Ordinal);
        var jItemIndex = text.IndexOf("[j-item]", StringComparison.Ordinal);
        Assert.True(iTemIndex >= 0 && jItemIndex >= 0);
        Assert.True(iTemIndex < jItemIndex, "İtem must sort before j-item, matching Rust's to_lowercase.");
    }

    [Fact]
    public void BuildIndexText_preserves_ascii_sort_order()
    {
        // Golden safety net: for pure-ASCII titles, the Unicode-faithful
        // sort must agree with the old plain-ASCII-lowercase sort, so the
        // ASCII golden fixtures are unaffected by this change.
        var entries = new List<IndexEntry>
        {
            new("Thing", "Zebra", "zebra.md", string.Empty),
            new("Thing", "apple", "apple.md", string.Empty),
            new("Thing", "Banana", "banana.md", string.Empty),
        };

        var text = IndexGenerator.BuildIndexText(entries);

        var appleIndex = text.IndexOf("[apple]", StringComparison.Ordinal);
        var bananaIndex = text.IndexOf("[Banana]", StringComparison.Ordinal);
        var zebraIndex = text.IndexOf("[Zebra]", StringComparison.Ordinal);
        Assert.True(appleIndex < bananaIndex && bananaIndex < zebraIndex);
    }

    [Fact]
    public void Regenerate_does_not_list_a_dotfile_named_dot_md()
    {
        // Regression: same underlying bug as Bundle.CollectMarkdown -- a
        // file named EXACTLY ".md" has no extension under Rust's
        // path.extension() (index.rs:130, 229), so it must not be treated
        // as a markdown entry to list or recurse into as a concept.
        using var tmp = new TempDir();
        WriteDoc(tmp, "datasets/only.md", "BigQuery Dataset", "Only Dataset", "The only dataset in this bundle.");
        File.WriteAllText(Path.Combine(tmp.Path, "datasets", ".md"), "not a real concept file");

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);
        Assert.NotEmpty(written);

        var datasetsIndex = File.ReadAllText(Path.Combine(tmp.Path, "datasets", "index.md"));
        Assert.Contains("[Only Dataset](only.md)", datasetsIndex);
        Assert.DoesNotContain("(.md)", datasetsIndex);
    }

    // ----------------------------------------------------------------
    // A2: symlink walk fidelity, mirroring BundleTests' equivalent pair.
    // index.rs's own collect_markdown (index.rs:223-234, used only to
    // compute which directories need an index.md at all) recurses via
    // `entry.file_type()?.is_dir()` -- lstat-based, so a symlinked directory
    // is never descended into and never contributes to
    // directories_to_index. This test requires symlink-creation privilege
    // and skips itself (via TempDir.TryCreateDirectorySymlink's bool return)
    // when unavailable.
    // ----------------------------------------------------------------

    [Fact]
    public void Symlinked_subdirectory_does_not_get_its_own_generated_index()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "real/a.md", "BigQuery Dataset", "A", "desc");
        if (!tmp.TryCreateDirectorySymlink("linked", "real"))
        {
            return; // no symlink privilege on this machine -- skip.
        }

        IndexGenerator.RegenerateIndexes(tmp.Path);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "real", "index.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "linked", "index.md")));
    }
}

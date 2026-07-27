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

        // "real" (which contains a markdown file) gets its own index.
        Assert.True(File.Exists(Path.Combine(tmp.Path, "real", "index.md")));

        // The symlinked "linked" is neither descended into nor listed as a
        // subdirectory in the parent index -- matching Rust's lstat-based
        // collect_markdown, which skips it entirely. We assert on the parent
        // index's CONTENT rather than File.Exists("linked/index.md"), which
        // would be meaningless: "linked" resolves to "real", so that path IS
        // "real/index.md" and always exists on any symlink-resolving filesystem.
        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("real/index.md", rootIndex);
        Assert.DoesNotContain("linked", rootIndex);
    }

    // ----------------------------------------------------------------
    // A3: late reparse re-check (TOCTOU consistency fix). DirectoriesToIndex's
    // early skip (in CollectMarkdown) only proves a directory was real AT
    // COLLECTION TIME -- it does not protect against the directory being
    // replaced by a symlink/junction any time between collection and the
    // moment RegenerateIndexesWith actually writes that directory's
    // index.md. This test uses the internal BeforeLateReparseCheckForTest
    // hook to deterministically substitute "real" (which legitimately
    // passed the early skip, since it was a genuine directory containing
    // a.md when DirectoriesToIndex ran) with a junction to an external
    // directory, landing exactly in the narrow window between collection
    // and write -- a substitution the early skip could never have caught.
    // It then asserts the write did NOT follow the junction out of the
    // bundle: no index.md appears in the external target directory.
    // Requires junction/symlink-creation privilege; probes for it up front
    // (without mutating anything the real assertion depends on) and skips
    // cleanly if unavailable, per the other reparse-point tests' pattern.
    // ----------------------------------------------------------------

    [Fact]
    public void Index_write_is_not_written_through_a_directory_substituted_after_collection()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "real/a.md", "BigQuery Dataset", "A", "desc");
        using var external = new TempDir();

        if (!tmp.TryCreateJunctionToExternalDir("privilege-probe", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        Directory.Delete(Path.Combine(tmp.Path, "privilege-probe"));

        var realDir = Path.Combine(tmp.Path, "real");
        IndexGenerator.BeforeLateReparseCheckForTest = directory =>
        {
            if (!string.Equals(directory, realDir, StringComparison.Ordinal))
            {
                return; // not the directory this test cares about -- leave it alone.
            }

            // "real" legitimately passed DirectoriesToIndex's early skip: it
            // was a genuine directory containing a.md when collection ran.
            // Swap it out for a junction to an external directory right
            // before the late re-check runs, simulating a concurrent local
            // substitution landing in that narrow window.
            Directory.Delete(realDir, recursive: true);
            Assert.True(tmp.TryCreateJunctionToExternalDir("real", external.Path));
        };

        try
        {
            var written = IndexGenerator.RegenerateIndexes(tmp.Path);

            Assert.DoesNotContain(written, p => string.Equals(p, Path.Combine(realDir, "index.md"), StringComparison.Ordinal));
        }
        finally
        {
            IndexGenerator.BeforeLateReparseCheckForTest = null;
        }

        // The write must not have followed the junction: no index.md should
        // have landed in the external directory the junction points at.
        Assert.False(File.Exists(Path.Combine(external.Path, "index.md")));
    }

    // ----------------------------------------------------------------
    // F2 [Security]: the late guard re-checked HasReparsePointAncestor
    // (ancestors of the directory about to be indexed) but never the
    // index.md file node itself -- asymmetric with WriteConcept/AppendLog,
    // which both check ReparsePoints.IsReparsePoint on the target FILE node
    // in addition to its ancestor chain. A pre-planted "bundle/tables/index.md"
    // symlink pointing at an external file would otherwise sail through the
    // ancestor check (index.md's only ancestor, "tables", is a genuine
    // directory) and get silently overwritten via File.WriteAllText
    // following the link. This test pre-plants such a symlink BEFORE calling
    // RegenerateIndexes (not via the BeforeLateReparseCheckForTest race hook
    // used by A3 -- this is a plain pre-existing-file scenario, not a race)
    // and asserts the write through it is skipped (consistent with the
    // existing skip-not-abort behavior) while the bundle root's own
    // unrelated index.md is still written normally. Requires symlink-
    // creation privilege; skips cleanly if unavailable.
    // ----------------------------------------------------------------

    [Fact]
    public void Index_write_is_skipped_when_the_target_index_md_is_itself_a_planted_symlink()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "tables/a.md", "BigQuery Table", "A", "desc");
        using var external = new TempDir();
        var externalFile = external.Write("external.md", "external content, must not be overwritten\n");

        if (!tmp.TryCreateFileSymlinkToExternalFile(Path.Combine("tables", "index.md"), externalFile))
        {
            return; // no symlink-creation privilege on this machine -- skip.
        }

        var before = File.ReadAllText(externalFile);

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        var tablesIndexPath = Path.Combine(tmp.Path, "tables", "index.md");
        Assert.DoesNotContain(written, p => string.Equals(p, tablesIndexPath, StringComparison.Ordinal));

        // The external file must be untouched -- proof the write was
        // skipped, not merely that "tables/index.md" is absent from the
        // returned list while the write still silently landed elsewhere.
        Assert.Equal(before, File.ReadAllText(externalFile));

        // The bundle root's own, unrelated index.md must still be written
        // normally -- the symlinked child must not suppress regeneration for
        // the rest of the bundle.
        Assert.True(File.Exists(Path.Combine(tmp.Path, "index.md")));
    }

    // ----------------------------------------------------------------
    // A4: symlinked-root regression guard. HasReparsePointAncestor's late
    // re-check must NEVER inspect bundleRoot itself -- only directories
    // strictly between the write target and bundleRoot. A bundle root that
    // is itself a symlink/junction/mount is a legitimate, common setup
    // (symlinked project directories, container/WSL bind mounts, macOS's
    // /var), and DirectoriesToIndex's early traversal already indexes such
    // a root unconditionally -- it never checks its own starting root for
    // being a reparse point. An earlier revision of the late check
    // inspected bundleRoot too, which meant EVERY directory in such a
    // bundle has bundleRoot on its ancestor chain, so EVERY index write was
    // silently skipped -- `okf index <symlinked-bundle>` wrote nothing at
    // all. This test points RegenerateIndexes directly AT a junction (the
    // bundle root itself is the reparse point, not a subdirectory of it)
    // and asserts the index is still written normally.
    // Requires junction/symlink-creation privilege; probes for it up front
    // and skips cleanly if unavailable, per the other reparse-point tests'
    // pattern.
    // ----------------------------------------------------------------

    [Fact]
    public void Symlinked_bundle_root_still_gets_its_index_written()
    {
        using var content = new TempDir();
        WriteDoc(content, "a.md", "BigQuery Dataset", "A", "desc");

        using var parent = new TempDir();
        if (!parent.TryCreateJunctionToExternalDir("bundle-root-link", content.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var bundleRoot = Path.Combine(parent.Path, "bundle-root-link");

        var written = IndexGenerator.RegenerateIndexes(bundleRoot);

        Assert.NotEmpty(written);
        Assert.True(File.Exists(Path.Combine(bundleRoot, "index.md")));
        // Same physical file, reached through the real (non-linked) path --
        // proof the write actually landed, not just that the junction makes
        // File.Exists resolve optimistically.
        Assert.True(File.Exists(Path.Combine(content.Path, "index.md")));
    }
}

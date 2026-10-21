// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="IndexGenerator"/> on a provider (spec §8, §9, §11; decisions B5, B13, B14): the
/// provider overloads return a <see cref="BundleWriteReport"/>, every <c>index.md</c> is written
/// conditioned on what was read and retried on a conflict, a directory with no file is no longer
/// referenced (D2), and the <c>string</c> overloads keep their return values, their
/// <c>relativeDir</c> and their exception behaviour.
/// </summary>
public class IndexGeneratorProviderTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Doc(string type, string title, string description) =>
        $"---\ntype: {type}\ntitle: {title}\ndescription: {description}\n---\n\n# {title}\n";

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static InMemoryBundleProvider Storage(params (string Key, string Text)[] objects)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        foreach (var (key, text) in objects)
        {
            storage.Add(key, text);
        }

        return storage;
    }

    private static readonly (string Key, string Text)[] Sample =
    [
        ("datasets/ga4.md", Doc("BigQuery Dataset", "GA4 Dataset", "GA4 sample.")),
        ("tables/events.md", Doc("BigQuery Table", "events", "Daily event tables.")),
        ("tables/users.md", Doc("BigQuery Table", "users", "Per-user dimension.")),
        ("log.md", "# Log\n\n## 2026-10-19\n* **Update**: x.\n"),
    ];

    [Fact]
    public void The_provider_overload_writes_what_the_string_overload_writes_and_reports_the_keys_in_order()
    {
        using var tmp = new TempDir();
        foreach (var (key, text) in Sample)
        {
            tmp.Write(key, text);
        }

        var paths = IndexGenerator.RegenerateIndexes(tmp.Path);
        var storage = Storage(Sample);

        var report = IndexGenerator.RegenerateIndexes(storage);

        Assert.True(report.Succeeded);
        Assert.Empty(report.Deleted);
        Assert.Equal([K("datasets/index.md"), K("tables/index.md"), K("index.md")], report.Written);
        Assert.Equal([.. report.Written.Select(k => BundleDisplayPath.Compose(tmp.Path, k))], paths);
        foreach (var key in report.Written)
        {
            Assert.Equal(File.ReadAllText(BundleDisplayPath.Compose(tmp.Path, key)), Text(storage, key.ToString()));
        }
    }

    [Fact]
    public void Each_index_is_written_conditioned_on_the_version_read()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")), ("a/index.md", "# stale\n"));

        IndexGenerator.RegenerateIndexes(storage);

        var writes = storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["write a/index.md IfMatch", "write index.md MustNotExist"], writes);
    }

    [Fact]
    public void A_root_index_written_meanwhile_is_read_again_and_its_marker_carried_forward()
    {
        var storage = Storage(("a.md", Doc("Note", "A", "a.")));
        storage.BeforeNextWrite(p => p.Add("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Knowledge\n"));

        var report = IndexGenerator.RegenerateIndexes(storage);

        Assert.Equal([K("index.md")], report.Written);
        Assert.Equal(
            ["read index.md", "write index.md MustNotExist", "read index.md", "write index.md IfMatch"],
            storage.Calls.Where(c => c.EndsWith("index.md", StringComparison.Ordinal) || c.Contains("index.md ", StringComparison.Ordinal)));
        var doc = OkfDocument.Parse(Text(storage, "index.md"));
        Assert.Equal("0.2", doc.Frontmatter.Get("okf_version")?.AsDisplayString());
        Assert.Contains("* [A](a.md) - a.\n", Text(storage, "index.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_that_keeps_conflicting_is_a_failure_and_the_other_directories_are_written()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        for (var n = 1; n <= 3; n++)
        {
            storage.FailWrite(n, new BundleWriteConflictException(K("a/index.md")));
        }

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal("index.md kept changing while it was being written; the write was refused after 3 attempts", failure.Reason);
        Assert.Equal([K("index.md")], report.Written);
        Assert.False(storage.Exists(K("a/index.md")));
        // The parent lists the directory, without the description of an index that was not written.
        Assert.Equal("# Subdirectories\n\n* [a](a/index.md)\n", Text(storage, "index.md"));
    }

    [Fact]
    public void A_provider_refusal_is_a_failure_in_the_report_and_a_silent_skip_for_the_string_overloads()
    {
        var refused = new BundleAccessRefusedException(K("a/index.md"), "secret-token=123 refused by the storage");
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        storage.FailWrite(1, refused);

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal("access to 'a/index.md' was refused by the provider", failure.Reason);
        Assert.Equal([K("index.md")], report.Written);

        var legacy = Storage(("a/x.md", Doc("Note", "X", "x.")));
        legacy.FailWrite(1, refused);
        var skipped = IndexGenerator.Regenerate(legacy, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true);
        Assert.Empty(skipped.Failed);
        Assert.Equal([K("index.md")], skipped.Written);
    }

    [Fact]
    public void Any_other_failure_is_a_failure_in_the_report_and_ends_a_string_overload_run_as_before()
    {
        var storage = Storage(("a/x.md", Doc("Note", "X", "x.")));
        storage.FailWrite(1, new IOException("disk full at C:/secret/path"));
        var report = IndexGenerator.RegenerateIndexes(storage);
        Assert.Equal("I/O error while writing the index (IOException)", Assert.Single(report.Failed).Reason);
        Assert.Equal([K("index.md")], report.Written);

        var io = Storage(("a/x.md", Doc("Note", "X", "x.")));
        io.FailWrite(1, new IOException("disk full"));
        Assert.Equal("disk full", Assert.Throws<IOException>(() => IndexGenerator.Regenerate(io, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true)).Message);
        Assert.False(io.Exists(K("index.md")));

        var conflicting = Storage(("a/x.md", Doc("Note", "X", "x.")));
        for (var n = 1; n <= 3; n++)
        {
            conflicting.FailWrite(n, new BundleWriteConflictException(K("a/index.md")));
        }

        var conflict = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.Regenerate(conflicting, IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, conflict.Reason);
        Assert.False(conflicting.Exists(K("index.md")));
    }

    [Fact]
    public void An_unreadable_root_index_is_left_as_it_is_and_reported()
    {
        const string Malformed = "---\nokf_version: \"0.2\"\n# unterminated frontmatter block, no closing ---\n";
        var storage = Storage(("index.md", Malformed), ("a/x.md", Doc("Note", "X", "x.")));

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("index.md"), failure.Key);
        Assert.Equal("the existing index.md could not be read or parsed, so it was left as it is", failure.Reason);
        Assert.Equal([K("a/index.md")], report.Written);
        Assert.Equal(Malformed, Text(storage, "index.md"));

        var locked = Storage(("a.md", Doc("Note", "A", "a.")), ("index.md", "# Knowledge\n")).FailReadOf("index.md", new IOException("locked"));
        Assert.Equal("the existing index.md could not be read or parsed, so it was left as it is", Assert.Single(IndexGenerator.RegenerateIndexes(locked).Failed).Reason);
        var legacy = IndexGenerator.Regenerate(Storage(("index.md", Malformed), ("a.md", Doc("Note", "A", "a."))), IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true);
        Assert.Empty(legacy.Written);
        Assert.Empty(legacy.Failed);
    }

    /// <summary>
    /// A new <c>index.md</c> is a creation like any other: the tree check of spec §2 runs before
    /// it, so an <c>index.md</c> that would also be the prefix of other keys is not written. The
    /// string overloads throw what the filesystem provider throws for a directory there.
    /// </summary>
    [Fact]
    public void A_new_index_that_would_make_the_tree_invalid_is_refused_before_it_is_written()
    {
        static InMemoryBundleProvider Conflicting() =>
            new InMemoryBundleProvider(BundleKeyConstraints.FileDirectoryConflict, canWrite: true)
                .Add("a/y.md", "---\ntype: Note\ntitle: Y\ndescription: y.\n---\n")
                .Add("a/index.md/x.md", "---\ntype: Note\ntitle: X\ndescription: x.\n---\n");
        var storage = Conflicting();

        var report = IndexGenerator.RegenerateIndexes(storage);

        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.StartsWith("\"a/index.md\" cannot be created: objects exist under it", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("write a/index.md MustNotExist", storage.Calls);
        Assert.Equal([K("a/index.md/index.md"), K("index.md")], report.Written);

        var thrown = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.Regenerate(Conflicting(), IndexGenerator.DefaultSynthesize, BundleLoadOptions.Default, legacy: true));
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, thrown.Reason);
    }

    /// <summary>
    /// The one exception the string overloads throw for a cause they met before: a directory
    /// named <c>index.md</c> with files under it, where the index of its parent must go. The write
    /// over a directory threw <c>UnauthorizedAccessException</c>; the creation is now refused before
    /// any write, as the name cannot hold an object. The indexes written before it stay written.
    /// </summary>
    [Fact]
    public void On_the_filesystem_a_directory_named_index_md_ends_the_run_with_a_conflict()
    {
        using var tmp = new TempDir();
        tmp.Write("x/a.md", Doc("Note", "A", "a."));
        tmp.Write("x/index.md/b.md", Doc("Note", "B", "b."));

        var thrown = Assert.Throws<BundleWriteConflictException>(() => IndexGenerator.RegenerateIndexes(tmp.Path));

        Assert.Equal(BundleWriteConflictReason.NameUnavailable, thrown.Reason);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "x", "index.md", "index.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// Spec §11, "Dossiers vides" (D2): a directory with no file under it, at any depth, no longer
    /// appears in its parent's index; one that holds only non-<c>.md</c> files still does.
    /// </summary>
    [Fact]
    public void An_empty_directory_is_no_longer_referenced_but_one_holding_only_non_markdown_files_still_is()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", Doc("Note", "A", "a."));
        tmp.Write("assets/logo.png", "png");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "nested", "deeper"));

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        Assert.Equal([Path.Combine(tmp.Path, "notes", "index.md"), Path.Combine(tmp.Path, "index.md")], written);
        Assert.Equal(
            "# Subdirectories\n\n* [assets](assets/index.md)\n* [notes](notes/index.md) - a.\n",
            File.ReadAllText(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// No listing follows a link (the provider's walk skips it), so a directory whose only entries
    /// are symlinks or junctions holds no object and is not referenced either. Before the provider
    /// it was, being a real directory.
    /// </summary>
    [SkippableFact]
    public void A_directory_holding_only_links_is_not_referenced()
    {
        using var external = new TempDir();
        external.Write("a.md", Doc("Note", "A", "a."));
        using var tmp = new TempDir();
        tmp.Write("notes/n.md", Doc("Note", "N", "n."));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("onlylinks", "j"), external.Path), "neither a junction nor a directory symlink can be created here");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        Assert.Equal("# Subdirectories\n\n* [notes](notes/index.md) - n.\n", File.ReadAllText(Path.Combine(tmp.Path, "index.md")));
    }

    /// <summary>
    /// Spec §11, "<c>Synthesize</c>": the string overload passes the directory relative to the
    /// root with the OS separator, as <c>Path.GetRelativePath</c> did (<c>a\b</c> on Windows); the
    /// provider overload passes the key prefix, <c>a/b</c>, on every OS.
    /// </summary>
    [Fact]
    public void Synthesize_gets_the_os_separator_from_the_string_overload_and_a_slash_from_the_provider_overload()
    {
        var files = new[] { ("a/b/x.md", Doc("Note", "X", "x.")), ("a/b/y.md", Doc("Note", "Y", "y.")) };
        using var tmp = new TempDir();
        foreach (var (key, text) in files)
        {
            tmp.Write(key, text);
        }

        var fromString = new List<string>();
        IndexGenerator.RegenerateIndexesWith(tmp.Path, (dir, children) =>
        {
            fromString.Add(dir);
            return "s";
        });
        var fromProvider = new List<string>();
        IndexGenerator.RegenerateIndexesWith(Storage(files), (dir, children) =>
        {
            fromProvider.Add(dir);
            return "s";
        });

        Assert.Equal([Path.Combine("a", "b")], fromString);
        Assert.Equal(OperatingSystem.IsWindows() ? "a" + (char)92 + "b" : "a/b", fromString[0]); // 92: a backslash
        Assert.Equal(["a/b"], fromProvider);
    }

    /// <summary>
    /// The run's key policy covers the index keys it produces (external review, F1): a host
    /// validator refusing <c>a/index.md</c> makes that index a failure in the report of both
    /// provider overloads, and a silent skip under the <c>string</c> overloads' contract. It is
    /// never read nor written, whether it would be created or replaced; the others are written.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_index_the_key_policy_refuses_is_neither_read_nor_written(bool exists)
    {
        const string Old = "# Note\n\n* [old](x.md)\n";
        const string Reason = "index.md cannot be written under the bundle's key policy: index excluded by host";
        var options = new BundleLoadOptions { ExtraKeyValidators = [k => k.ToString() == "a/index.md" ? "index excluded by host" : null] };
        InMemoryBundleProvider Make() => exists
            ? Storage(("a/x.md", Doc("Note", "X", "x.")), ("a/index.md", Old))
            : Storage(("a/x.md", Doc("Note", "X", "x.")));
        static bool Touches(string call) => call.Contains("a/index.md", StringComparison.Ordinal);

        var storage = Make();
        var report = IndexGenerator.RegenerateIndexes(storage, options);
        var failure = Assert.Single(report.Failed);
        Assert.Equal(K("a/index.md"), failure.Key);
        Assert.Equal(Reason, failure.Reason);
        Assert.Equal([K("index.md")], report.Written);
        Assert.DoesNotContain(storage.Calls, Touches);

        var synthesized = Make();
        var withSynthesize = IndexGenerator.RegenerateIndexesWith(synthesized, IndexGenerator.DefaultSynthesize, options);
        Assert.Equal(Reason, Assert.Single(withSynthesize.Failed).Reason);
        Assert.DoesNotContain(synthesized.Calls, Touches);

        var legacy = Make();
        var skipped = IndexGenerator.Regenerate(legacy, IndexGenerator.DefaultSynthesize, options, legacy: true);
        Assert.Empty(skipped.Failed);
        Assert.Equal([K("index.md")], skipped.Written);
        Assert.DoesNotContain(legacy.Calls, Touches);
        if (exists)
        {
            Assert.Equal(Old, Text(legacy, "a/index.md"));
        }
        else
        {
            Assert.False(legacy.Exists(K("a/index.md")));
        }
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // pins the obsolete path-based projections (lot D): the strings they always were, until their removal
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <see cref="OkfBundleTools"/> over a <see cref="BundleHandle"/> (spec §5.2, §9): the image
/// is the handle's, shared with whoever else holds the handle; the write tools are offered
/// only when the provider can write; and no tool reads outside the provider — browsing,
/// the change log and a computation's file all go through it, so they work over a provider
/// that is not a directory at all.
/// </summary>
public class OkfBundleToolsHandleTests
{
    private const string Note = "---\ntype: Note\ntitle: {0}\ndescription: {1}\n---\n\nBody of {0}.\n";

    private static InMemoryBundleProvider Storage(bool canWrite = true) => new InMemoryBundleProvider(canWrite: canWrite)
        .Add("tables/orders.md", string.Format(null, Note, "Orders", "One row per order."))
        .Add("tables/index.md", "# tables\n\n* [Orders](orders.md) - One row per order.\n")
        .Add("notes/a.md", string.Format(null, Note, "A", "a."))
        .Add("log.md", "# Log\n\n## 2026-07-22\n\n* **Creation**: Bootstrapped.\n")
        .Add("assets/logo.png", "png");

    [Fact]
    public void Two_tool_sets_over_one_handle_share_one_image_and_a_write_refreshes_it_for_both()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var a = new OkfBundleTools(handle);
        var b = new OkfBundleTools(handle);

        Assert.Same(a.GetBundle(), b.GetBundle());
        Assert.Same(handle, a.Handle);
        Assert.Equal("memory://memory", a.Location);
        Assert.Throws<NotSupportedException>(() => a.BundleRoot);

        var written = a.WriteConcept("notes/b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n");

        Assert.StartsWith("Written", written, StringComparison.Ordinal);
        Assert.True(handle.IsStale);
        Assert.Equal(3, b.GetBundle().Count);
        Assert.Same(a.GetBundle(), b.GetBundle());
    }

    [Fact]
    public void A_root_string_builds_a_handle_of_its_own_and_keeps_the_root()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", string.Format(null, Note, "A", "a."));
        var first = new OkfBundleTools(tmp.Path);
        var second = new OkfBundleTools(tmp.Path);

        Assert.NotSame(first.Handle, second.Handle);
        Assert.Equal(tmp.Path, first.BundleRoot);
        Assert.Equal(tmp.Path, first.Location);
        Assert.Equal(BundleRefreshMode.Manual, first.Handle.RefreshPolicy.Mode);
        Assert.IsType<FileSystemBundleProvider>(first.Handle.Provider);
    }

    [Theory]
    [InlineData(OkfToolMode.ReadWrite)]
    [InlineData(OkfToolMode.RequireApprovalForWrites)]
    [InlineData(OkfToolMode.ReadOnly)]
    public void Over_a_provider_that_cannot_write_no_write_tool_is_offered_whatever_the_mode(OkfToolMode mode)
    {
        using var handle = new BundleHandle(Storage(canWrite: false));
        var names = new OkfBundleTools(handle).GetTools(mode).Cast<AIFunction>().Select(f => f.Name).ToList();

        Assert.Equal(8, names.Count);
        Assert.DoesNotContain(names, OkfBundleTools.WriteToolNames.Contains);
        Assert.Contains("okf_read_concept", names);

        using var writable = new BundleHandle(Storage(canWrite: true));
        Assert.Equal(mode == OkfToolMode.ReadOnly ? 8 : 12, new OkfBundleTools(writable).GetTools(mode).Count);
    }

    [Fact]
    public void Writes_over_a_read_only_provider_throw_before_touching_anything()
    {
        var storage = Storage(canWrite: false);
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        storage.ClearCalls();

        Assert.Throws<BundleReadOnlyException>(() => tools.WriteConcept("notes/b", "type: Note\ntitle: B\ndescription: b.\n", "B.\n"));
        Assert.Throws<BundleReadOnlyException>(() => tools.AppendLog("Update", "x"));
        Assert.Throws<BundleReadOnlyException>(() => tools.RegenerateIndexes());
        // Verify runs inside RunTool, which renders the library's refusal as its Error: text.
        Assert.Equal("Error: the provider is read-only; the write was refused before being attempted", tools.Verify("tables/orders", "human:ada", "2026-07-22T00:00:00Z"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal) || c.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public void Browse_reads_the_index_and_lists_prefixes_through_the_provider()
    {
        using var handle = new BundleHandle(Storage());
        var tools = new OkfBundleTools(handle);

        Assert.Equal("# tables\n\n* [Orders](orders.md) - One row per order.\n", tools.Browse("tables"));
        Assert.StartsWith("# notes/\n", tools.Browse("notes/"), StringComparison.Ordinal); // the heading echoes the path as given, as before
        Assert.Contains("notes/a", tools.Browse("notes"), StringComparison.Ordinal);
        Assert.StartsWith("# (bundle root)", tools.Browse(), StringComparison.Ordinal);
        Assert.StartsWith("# assets", tools.Browse("assets"), StringComparison.Ordinal); // a prefix with a non-.md object exists
        Assert.Equal("Error: path 'nope' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("nope"));
        Assert.Equal("Error: path 'tables/orders.md' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("tables/orders.md"));
    }

    [Fact]
    public void Browse_reads_an_index_as_strict_UTF8_and_the_live_index_not_the_cached_one()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        tools.GetBundle();

        storage.Add("tables/index.md", "# tables (rewritten behind the image)\n");
        Assert.Equal("# tables (rewritten behind the image)\n", tools.Browse("tables"));

        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        File.WriteAllBytes(Path.Combine(tmp.Path, "tables", "index.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        Assert.StartsWith("Error: ", new OkfBundleTools(tmp.Path).Browse("tables"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_directory_is_not_a_prefix()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var tools = new OkfBundleTools(tmp.Path);

        Assert.Equal("Error: path 'empty' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("empty"));
    }

    [Fact]
    public void A_prefix_the_image_knows_costs_no_listing_and_one_it_does_not_costs_one()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        tools.GetBundle();
        storage.ClearCalls();

        // notes/ holds a concept of the image: proven without touching the storage.
        Assert.StartsWith("# notes", tools.Browse("notes"), StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("enumerate", StringComparison.Ordinal));

        // assets/ holds only a non-.md object, which the image does not list: one listing.
        Assert.StartsWith("# assets", tools.Browse("assets"), StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c.StartsWith("enumerate assets", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_the_provider_refuses_is_an_error_naming_the_key_and_not_the_generated_listing()
    {
        // The refusal is not "no such file": were it read as one (or swallowed), Browse
        // would answer the generated listing and the guard's verdict would be lost.
        var storage = Storage().FailReadOf("tables/index.md", new BundleAccessRefusedException(BundleKey.Parse("tables/index.md"), "/host/secret/storage/detail"));
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        var rendered = tools.Browse("tables");

        Assert.Equal("Error: access to 'tables/index.md' was refused by the provider", rendered);
        Assert.DoesNotContain("secret", rendered, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void An_index_that_is_a_symlink_to_a_file_outside_the_bundle_is_refused_not_read()
    {
        using var tmp = new TempDir();
        using var outside = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        var secret = outside.Write("secret.txt", "# OUTSIDE-THE-BUNDLE\n");
        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile(Path.Combine("tables", "index.md"), secret), "no symlink privilege on this machine");

        var rendered = new OkfBundleTools(tmp.Path).Browse("tables");

        Assert.DoesNotContain("OUTSIDE-THE-BUNDLE", rendered, StringComparison.Ordinal);
        Assert.StartsWith("Error: ", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_with_a_byte_order_mark_is_browsed_without_it_as_before()
    {
        // The direct File.ReadAllText dropped one leading BOM; the strict reader keeps it as
        // U+FEFF, so Browse (the one producer of an index text, for the tool and for the
        // context provider's root block) drops exactly one.
        var bom = ((char)0xFEFF).ToString();
        var storage = Storage().Add("tables/index.md", bom + "# tables\n").Add("index.md", bom + "# root\n");
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        Assert.Equal("# tables\n", tools.Browse("tables"));
        Assert.Equal("# root\n", tools.Browse());

        storage.Add("tables/index.md", bom + bom + "# twice\n");
        Assert.Equal(bom + "# twice\n", tools.Browse("tables"));
    }

    [Fact]
    public void A_path_is_case_exact_and_a_dot_segment_names_no_directory()
    {
        // Browse("Tables") used to resolve to tables/ on a case-folding volume (NTFS, default
        // macOS), and an Index.md was taken for the directory's index; a prefix is now exactly
        // the key's spelling, and "." is a segment like any other, naming nothing.
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", string.Format(null, Note, "Orders", "o."));
        tmp.Write("tables/index.md", "# tables\n");
        tmp.Write("cased/a.md", string.Format(null, Note, "A", "a."));
        tmp.Write("cased/Index.md", "# CASED\n");
        var tools = new OkfBundleTools(tmp.Path);

        Assert.Equal("# tables\n", tools.Browse("tables"));
        Assert.Equal("Error: path 'Tables' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("Tables"));
        Assert.Equal("Error: path './tables' not found in the bundle. Use okf_browse to list available directories.", tools.Browse("./tables"));
        Assert.DoesNotContain("CASED", tools.Browse("cased"), StringComparison.Ordinal);
    }

    [Fact]
    public void BundleRoot_keeps_the_callers_spelling_of_the_root()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", string.Format(null, Note, "A", "a."));
        var trailing = tmp.Path + Path.DirectorySeparatorChar;
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, tmp.Path);

        Assert.Equal(trailing, new OkfBundleTools(trailing).BundleRoot);
        Assert.Equal(relative, new OkfBundleTools(relative).BundleRoot);
        Assert.Equal(1, new OkfBundleTools(relative).GetBundle().Count);
    }

    [Fact]
    public void ChangesSince_reads_every_log_through_the_provider()
    {
        var storage = Storage().Add("notes/log.md", "# Log\n\n## 2026-07-23\n\n* **Update**: Nested.\n");
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        var report = tools.ChangesSince("2026-07-01");

        Assert.Contains("## log.md\n", report, StringComparison.Ordinal);
        Assert.Contains("## notes/log.md\n", report, StringComparison.Ordinal);
        Assert.Contains("Bootstrapped.", report, StringComparison.Ordinal);
        Assert.Contains("Nested.", report, StringComparison.Ordinal);

        storage.FailReadOf("notes/log.md", new IOException("disk"));
        var skipped = tools.ChangesSince("2026-07-01");
        Assert.Contains("> Skipped notes/log.md (could not be read: I/O error).", skipped, StringComparison.Ordinal);
        Assert.Contains("Bootstrapped.", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void RegenerateIndexes_writes_through_the_handles_provider_and_refreshes_the_image()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        tools.GetBundle();

        var result = tools.RegenerateIndexes();

        // `assets` holds no .md, so it is referenced by the root index but gets none of its own.
        Assert.Equal("Regenerated 3 index file(s):\n- notes/index.md\n- tables/index.md\n- index.md\n", result);
        Assert.Contains(storage.Calls, c => c == "write index.md MustNotExist");
        Assert.Contains(storage.Calls, c => c == "write tables/index.md IfMatch");
        Assert.Equal(3, tools.GetBundle().IndexKeys.Count);
        Assert.Contains("* [assets](assets/index.md)", tools.Browse(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetComputation_reads_the_file_through_the_provider()
    {
        var storage = Storage()
            .Add("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/q.sql\nattester: { resource: att.py }\n---\n")
            .Add("references/q.sql", "SELECT 1\n")
            .Add("att.py", "def attest(**_):\n    return {}\n");
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        var rendered = tools.GetComputation("c/rev");

        Assert.Contains("File: references/q.sql\n\n```\nSELECT 1\n```\n", rendered, StringComparison.Ordinal);

        storage.FailReadOf("references/q.sql", new IOException("disk"));
        Assert.Contains("Error: computation file 'references/q.sql' could not be read: I/O error reading 'references/q.sql' (IOException)", tools.GetComputation("c/rev"), StringComparison.Ordinal);
    }
}

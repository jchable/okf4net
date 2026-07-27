// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for bundle loading and the cross-link graph, exercised against the
/// spec's Appendix A minimal example bundle.
/// </summary>
public class BundleTests
{
    /// <summary>
    /// Builds the Appendix A example bundle and returns its temp dir.
    /// </summary>
    private static TempDir AppendixA()
    {
        var tmp = new TempDir();
        tmp.Write(
            "datasets/sales.md",
            "---\n" +
            "type: BigQuery Dataset\n" +
            "title: Sales\n" +
            "description: All sales-related tables for the retail business.\n" +
            "resource: https://console.cloud.google.com/bigquery?p=acme&d=sales\n" +
            "tags: [sales]\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "The sales dataset contains transactional tables, including\n" +
            "[orders](/tables/orders.md) and [customers](/tables/customers.md).\n");
        tmp.Write(
            "tables/orders.md",
            "---\n" +
            "type: BigQuery Table\n" +
            "title: Orders\n" +
            "description: One row per completed customer order.\n" +
            "resource: https://console.cloud.google.com/bigquery?p=acme&d=sales&t=orders\n" +
            "tags: [sales, orders]\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "# Schema\n\n" +
            "Part of the [sales dataset](/datasets/sales.md). FK to [customers](/tables/customers.md).\n");
        tmp.Write(
            "tables/customers.md",
            "---\n" +
            "type: BigQuery Table\n" +
            "title: Customers\n" +
            "description: One row per customer.\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "Linked from [orders](/tables/orders.md).\n");
        return tmp;
    }

    [Fact]
    public void Loads_all_concepts()
    {
        using var tmp = AppendixA();
        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(3, bundle.Count);
        Assert.True(bundle.Contains(ConceptId.Parse("tables/orders")));
        Assert.True(bundle.Contains(ConceptId.Parse("datasets/sales")));
        Assert.Empty(bundle.ParseErrors);
    }

    [Fact]
    public void Resolves_cross_links_and_backlinks()
    {
        using var tmp = AppendixA();
        var bundle = Bundle.Load(tmp.Path);

        var sales = ConceptId.Parse("datasets/sales");
        var orders = ConceptId.Parse("tables/orders");
        var customers = ConceptId.Parse("tables/customers");

        var salesLinks = bundle.LinksFrom(sales).Select(l => l.Target).ToList();
        Assert.Contains(orders, salesLinks);
        Assert.Contains(customers, salesLinks);
        Assert.All(bundle.LinksFrom(sales), l => Assert.True(l.Exists));

        // orders is linked from sales and customers.
        var backlinks = bundle.Backlinks(orders);
        Assert.Contains(sales, backlinks);
        Assert.Contains(customers, backlinks);

        Assert.Empty(bundle.BrokenLinks());
    }

    [Fact]
    public void Broken_links_are_detected_but_not_fatal()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "a.md",
            "---\ntype: Note\n---\nSee [missing](/does/not/exist.md).\n");
        var bundle = Bundle.Load(tmp.Path);
        var broken = bundle.BrokenLinks();
        Assert.Single(broken);
        Assert.Equal("/does/not/exist.md", broken[0].RawTarget);

        // Broken links are informational, not conformance errors.
        var report = BundleValidator.Validate(bundle);
        Assert.True(report.IsConformant);
        Assert.Contains(report.Of(Severity.Info), d => d.Message.Contains("does/not/exist"));
    }

    [Fact]
    public void Appendix_a_is_conformant()
    {
        using var tmp = AppendixA();
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);
        Assert.True(report.IsConformant);
        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public void Missing_type_is_a_conformance_error()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: No Type\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);
        Assert.False(report.IsConformant);
        Assert.Contains(report.Of(Severity.Error), d => d.Message.Contains("type"));
    }

    [Fact]
    public void Reserved_files_are_recognized_not_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "# Listing\n\n* [a](a.md)\n");
        tmp.Write("log.md", "# Log\n\n## 2026-05-22\n* **Update**: did a thing.\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(1, bundle.Count); // only a.md is a concept
        Assert.Single(bundle.IndexFiles);
        Assert.Single(bundle.LogFiles);
    }

    [Fact]
    public void Okf_version_read_from_root_index()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.1\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal("0.1", bundle.OkfVersion);
    }

    [Fact]
    public void Okf_version_reflects_the_load_time_snapshot_not_the_current_file()
    {
        // OkfVersion must be captured while Load builds the bundle, so it
        // stays consistent with the rest of the (immutable) snapshot. If it
        // were read lazily on first access, overwriting the root index.md
        // between Load and the first access would leak the newer on-disk
        // value ("0.2") into an instance that otherwise represents the "0.1"
        // load. Deleting it entirely likewise must not turn the value null.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        var indexPath = tmp.Write("index.md", "---\nokf_version: \"0.1\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);

        // Mutate the file *before* the first OkfVersion read.
        File.WriteAllText(indexPath, "---\nokf_version: \"0.2\"\n---\n\n# Listing\n");
        Assert.Equal("0.1", bundle.OkfVersion);

        // And a later disappearance of the file does not change the answer.
        File.Delete(indexPath);
        Assert.Equal("0.1", bundle.OkfVersion);
    }

    [Fact]
    public void Walk_order_is_component_wise_not_a_flat_string_sort()
    {
        // Regression: a directory `orders/` containing `extra.md`, plus a
        // sibling file `orders.md`. The per-directory walk visits the
        // `orders` directory before the `orders.md` file (the directory
        // name "orders" sorts before "orders.md" since it's a string
        // prefix), so the correct concept order is `orders/extra` before
        // `orders` -- component-wise path ordering (directories sort before
        // sibling files). A flat ordinal sort of full path strings would
        // invert this, since '.' (0x2E) sorts before '\' (0x5C).
        using var tmp = new TempDir();
        tmp.Write("orders/extra.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("orders.md", "---\ntype: Note\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(2, bundle.Count);
        Assert.Equal("orders/extra", bundle.Concepts[0].Id.ToString());
        Assert.Equal("orders", bundle.Concepts[1].Id.ToString());
    }

    [Fact]
    public void Invalid_utf8_aborts_the_whole_load()
    {
        // Regression: File.ReadAllText silently substitutes U+FFFD for
        // invalid UTF-8 byte sequences instead of failing. The loader must
        // instead surface an I/O error and abort the whole load.
        using var tmp = new TempDir();
        File.WriteAllBytes(System.IO.Path.Combine(tmp.Path, "bad.md"), [0xC3, 0x28]);
        Assert.Throws<BundleLoadException>(() => Bundle.Load(tmp.Path));
    }

    [Fact]
    public void Dotfile_named_dot_md_is_not_treated_as_a_markdown_file()
    {
        // Regression: a file named EXACTLY ".md" has no extension -- a
        // leading-dot-only filename is a dotfile, not a "stem.ext" split --
        // so it must not be treated as a markdown file. Both .NET's
        // Path.GetExtension(".md") and a naive EndsWith(".md") check
        // return/match ".md", wrongly treating it as a markdown file. If
        // collected, it would even fail to parse -- FromPath strips ".md",
        // leaving an empty segment, which segment validation rejects --
        // surfacing as a spurious ParseErrors entry instead of being
        // silently skipped like any other non-.md file.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, ".md"), "not a real concept file");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Empty(bundle.ParseErrors);
        Assert.Equal(1, bundle.Count);
        Assert.Equal("a", bundle.Concepts[0].Id.ToString());
    }

    // ----------------------------------------------------------------
    // A2: symlink walk fidelity. The bundle walk classifies each directory
    // entry by its own type via lstat-based detection, reporting the type of
    // the entry ITSELF rather than its target. A symlink is neither a
    // directory nor a regular file, so it matches neither arm and the entry
    // is skipped outright -- different from Directory.Exists/File.Exists,
    // which resolve through the link.
    //
    // Both tests require symlink-creation privilege
    // (SeCreateSymbolicLinkPrivilege on Windows, absent without Developer
    // Mode or an elevated process); they skip themselves via
    // TempDir.TryCreate*Symlink's bool return when unavailable, per xunit v2
    // having no Assert.Skip.
    // ----------------------------------------------------------------

    [Fact]
    public void Symlinked_markdown_file_is_skipped_not_loaded_as_a_concept()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "---\ntype: Note\ntitle: Real\n---\nbody\n");
        if (!tmp.TryCreateFileSymlink("link.md", "real.md"))
        {
            return; // no symlink privilege on this machine -- skip.
        }

        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(1, bundle.Count);
        Assert.Equal("real", bundle.Concepts[0].Id.ToString());
        Assert.False(bundle.Contains(ConceptId.Parse("link")));
        Assert.Empty(bundle.ParseErrors);
    }

    [Fact]
    public void Symlinked_directory_is_not_descended_into()
    {
        using var tmp = new TempDir();
        tmp.Write("real/a.md", "---\ntype: Note\ntitle: A\n---\nbody\n");
        if (!tmp.TryCreateDirectorySymlink("linked", "real"))
        {
            return; // no symlink privilege on this machine -- skip.
        }

        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(1, bundle.Count);
        Assert.Equal("real/a", bundle.Concepts[0].Id.ToString());
        Assert.DoesNotContain(bundle.Concepts, c => c.Id.ToString().StartsWith("linked", StringComparison.Ordinal));
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust bundle-loading and cross-link-graph tests
/// (tests/bundle.rs), exercised against the spec's Appendix A minimal
/// example bundle.
/// </summary>
public class BundleTests
{
    /// <summary>
    /// Builds the Appendix A example bundle and returns its temp dir. Port
    /// of <c>appendix_a()</c> (tests/bundle.rs:10-49); literals copied
    /// verbatim.
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
        // tests/bundle.rs:51-59
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
        // tests/bundle.rs:61-81
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
        // tests/bundle.rs:83-99.
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
        // tests/bundle.rs:101-108
        using var tmp = AppendixA();
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);
        Assert.True(report.IsConformant);
        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public void Missing_type_is_a_conformance_error()
    {
        // tests/bundle.rs:110-118
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
        // tests/bundle.rs:120-130
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
        // tests/bundle.rs:132-139
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.1\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal("0.1", bundle.OkfVersion);
    }

    [Fact]
    public void Okf_version_is_memoized_after_first_read()
    {
        // Bundle.OkfVersion re-read the root index.md and re-parsed it on
        // every access. Prove memoization: after the first (successful)
        // read, delete the root index.md entirely -- if OkfVersion were
        // still doing disk I/O per access, the second read would fail to
        // find the file and return null instead of the cached value.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        var indexPath = tmp.Write("index.md", "---\nokf_version: \"0.1\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);

        var first = bundle.OkfVersion;
        Assert.Equal("0.1", first);

        File.Delete(indexPath);

        var second = bundle.OkfVersion;
        Assert.Equal(first, second);
        Assert.Equal("0.1", second);
    }

    [Fact]
    public void Walk_order_is_component_wise_not_a_flat_string_sort()
    {
        // Regression: a directory `orders/` containing `extra.md`, plus a
        // sibling file `orders.md`. The per-directory walk visits the
        // `orders` directory before the `orders.md` file (the directory
        // name "orders" sorts before "orders.md" since it's a string
        // prefix), so the correct concept order is `orders/extra` before
        // `orders` -- matching Rust's PathBuf (component-wise) Ord. A flat
        // ordinal sort of full path strings would invert this, since '.'
        // (0x2E) sorts before '\' (0x5C).
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
        // invalid UTF-8 byte sequences instead of failing, unlike Rust's
        // fs::read_to_string (which yields an io::Error, propagated by `?`
        // and turned into BundleError::Io -- aborting the whole load).
        using var tmp = new TempDir();
        File.WriteAllBytes(System.IO.Path.Combine(tmp.Path, "bad.md"), [0xC3, 0x28]);
        Assert.Throws<BundleLoadException>(() => Bundle.Load(tmp.Path));
    }

    [Fact]
    public void Dotfile_named_dot_md_is_not_treated_as_a_markdown_file()
    {
        // Regression: Rust's path.extension() == Some("md") (bundle.rs:216)
        // is false for a file named EXACTLY ".md" -- a leading-dot-only
        // filename has no extension in Rust's model (it's a dotfile, not a
        // "stem.ext" split). Both .NET's Path.GetExtension(".md") and a
        // naive EndsWith(".md") check return/match ".md", wrongly treating
        // it as a markdown file. If collected, it would even fail to parse
        // -- ConceptId::from_path strips ".md", leaving an empty segment,
        // which ValidateSegment rejects -- surfacing as a spurious
        // ParseErrors entry instead of being silently skipped like any
        // other non-.md file.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, ".md"), "not a real concept file");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Empty(bundle.ParseErrors);
        Assert.Equal(1, bundle.Count);
        Assert.Equal("a", bundle.Concepts[0].Id.ToString());
    }

    // ----------------------------------------------------------------
    // A2: symlink walk fidelity. Rust's collect_markdown (bundle.rs:207-222)
    // recurses via `entry.file_type()`, an lstat-based query reporting the
    // type of the directory entry ITSELF rather than its target. A symlink's
    // file_type() has is_dir() == false AND is_file() == false, so it
    // matches neither match arm and the entry is skipped outright --
    // different from Directory.Exists/File.Exists, which resolve through
    // the link like Rust's (following) Path::is_dir()/Path::is_file().
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

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Writing every embedded asset generically (spec §12.0, §12.6).</summary>
public class HtmlWriterAssetsTests
{
    private static ViewerSite Site(TempDir src)
    {
        src.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\nBody.\n");
        return SiteModel.Build(Bundle.Load(src.Path));
    }

    [Fact]
    public void Every_embedded_asset_is_written_byte_for_byte_under_assets()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(Site(src), dest.Path);

        Assert.NotEmpty(ViewerAssets.Paths);
        foreach (var path in ViewerAssets.Paths)
        {
            var file = Path.Combine(dest.Path, "assets", path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), path);
            Assert.Equal(ViewerAssets.Bytes(path), File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void Assets_come_first_in_ordinal_order_then_the_index_script_then_index_html_then_the_pages()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        var written = HtmlWriter.Write(Site(src), dest.Path);

        var assets = ViewerAssets.Paths.Select(p => "assets/" + p).ToList();
        Assert.Equal(assets, written.Take(assets.Count));
        Assert.Equal("assets/okf-index.js", written[assets.Count]);
        Assert.Equal("index.html", written[assets.Count + 1]);
        // The graph page is written between index.html and the concept pages;
        // the concept pages come last.
        Assert.Equal("a.html", written[^1]);
    }

    [Fact]
    public void Asset_paths_are_relative_slash_separated_ordinal_and_never_a_page_or_the_generated_index()
    {
        foreach (var path in ViewerAssets.Paths)
        {
            Assert.DoesNotContain('\\', path);
            Assert.False(path.StartsWith('/'), path);
            // GuardNoCaseCollisions leaves assets/ out of its set because no
            // asset path ends in .html: this pins that half of its argument.
            Assert.False(path.EndsWith(".html", StringComparison.OrdinalIgnoreCase), path);
            Assert.NotEqual("okf-index.js", path);
        }

        Assert.Equal(ViewerAssets.Paths.OrderBy(p => p, StringComparer.Ordinal), ViewerAssets.Paths);
    }

    [Fact]
    public void The_P1_assets_stay_embedded_and_their_properties_read_them()
    {
        foreach (var name in new[] { "viewer.css", "viewer.js", "marked.min.js", "okf-theme.js", "okf-site.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js", "okf-resize.js" })
        {
            Assert.Contains(name, ViewerAssets.Paths);
        }

        Assert.Equal(ViewerAssets.Text("viewer.css"), ViewerAssets.Css);
        Assert.Equal(ViewerAssets.Text("viewer.js"), ViewerAssets.ViewerJs);
        Assert.Equal(ViewerAssets.Text("okf-toc.js"), ViewerAssets.TocJs);
    }

    [Fact]
    public void The_font_provenance_readme_is_never_embedded()
        => Assert.DoesNotContain("fonts/README.md", ViewerAssets.Paths);

    [Fact]
    public void Editor_backups_and_os_litter_are_never_embedded()
    {
        // The project's wildcard embeds whatever sits under Assets/, tracked or
        // not: its Exclude list is what keeps a stray backup out of every site.
        foreach (var path in ViewerAssets.Paths)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            Assert.False(
                name.EndsWith(".orig", StringComparison.Ordinal)
                    || name.EndsWith(".bak", StringComparison.Ordinal)
                    || name.EndsWith('~')
                    || name.EndsWith(".swp", StringComparison.Ordinal)
                    || name is ".DS_Store" or "Thumbs.db",
                path);
        }
    }

    [Fact]
    public void An_asset_that_is_not_embedded_is_a_clear_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ViewerAssets.Bytes("no-such-asset.js"));
        Assert.Contains("no-such-asset.js", ex.Message, StringComparison.Ordinal);
    }
}

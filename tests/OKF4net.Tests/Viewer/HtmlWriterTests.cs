// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for writing a <see cref="ViewerSite"/> out as a static site.</summary>
public class HtmlWriterTests
{
    private static Bundle SampleBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: The users table\n---\nSome **body**.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Write_creates_the_index_and_one_page_per_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var written = HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "index.html")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "tables", "users.html")));
        Assert.Contains("index.html", written);
    }

    [Fact]
    public void Write_emits_the_shared_assets_once()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.css")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.js")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "marked.min.js")));
    }

    [Fact]
    public void Write_links_assets_with_a_path_relative_to_the_page_depth()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var nested = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("../assets/viewer.css", nested);

        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("assets/viewer.css", root);
        Assert.DoesNotContain("../assets/viewer.css", root);
    }

    [Fact]
    public void Write_embeds_the_body_as_json_rather_than_pre_rendered_html()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("id=\"okf-payload\"", page);
        Assert.Contains("Some **body**.", page);
    }

    [Fact]
    public void Write_renders_the_frontmatter_table()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("The users table", page);
    }

    [Fact]
    public void Write_creates_the_output_directory_when_missing()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));
        var target = Path.Combine(dest.Path, "does", "not", "exist");

        HtmlWriter.Write(site, target);

        Assert.True(File.Exists(Path.Combine(target, "index.html")));
    }

    [Fact]
    public void Write_leaves_unrelated_files_in_the_output_directory_alone()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var keep = Path.Combine(dest.Path, "keep-me.txt");
        File.WriteAllText(keep, "untouched");
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.Equal("untouched", File.ReadAllText(keep));
    }

    [Fact]
    public void Write_refuses_to_write_inside_the_bundle_it_renders()
    {
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.Throws<ArgumentException>(
            () => HtmlWriter.Write(site, Path.Combine(src.Path, "site")));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_surfaces_parse_errors_on_the_index_page()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        // A well-formed, terminated frontmatter block that merely omits
        // `type` would NOT reach Bundle.ParseErrors -- neither
        // OkfDocument.Parse nor ConceptId.FromPath require it (§11 `type`
        // conformance is a *validation* concern, not a *parse* one), so it
        // loads as an ordinary concept instead. An unterminated frontmatter
        // block is a genuine parse failure that Bundle.Load does collect
        // into ParseErrors (see SiteModelTests.Build_surfaces_parse_errors_rather_than_dropping_them).
        src.Write("broken.md", "---\ntitle: No closing delimiter\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var index = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("broken.md", index);
    }

    [Fact]
    public void Write_escapes_html_metacharacters_in_a_concept_title()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: \"<img src=x onerror=alert(1)>\"\ndescription: d\n---\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;img", page);
    }

    [Fact]
    public void Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: Evil\ndescription: d\n---\n</script><img src=x onerror=alert(1)>\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        // RenderShell always emits exactly three <script> elements (the JSON
        // payload, marked, viewer.js). A fourth </script> would mean the
        // body's own literal "</script>" text broke out of the payload
        // container instead of staying HTML-safe-JSON-escaped inside it.
        Assert.Equal(3, CountOccurrences(page, "</script>"));
        Assert.DoesNotContain("<img src=x", page);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

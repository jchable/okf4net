// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// The licence text of the vendored marked (MIT) travels with every copy of
/// the code it covers: embedded in the assembly, written into every generated
/// site, shipped in the <c>okf-render</c> release archives, and named by
/// NOTICE. The MIT licence requires "this permission notice" in all copies.
/// </summary>
/// <remarks>
/// The packaging assertions below read <c>packaging/New-ReleaseArchive.ps1</c>
/// and <c>.github/workflows/ci.yml</c> as TEXT: they are smoke checks that the
/// manifest names the file, not proof an archive holds it. The proof is CI's
/// "Archives exist, hold exactly their declared files" step.
/// </remarks>
public class ViewerLicencesTests
{
    private const string Path = "licenses/marked-LICENSE.md";

    private static string Repo(params string[] parts) => System.IO.Path.Combine([TestPaths.RepoRoot(), .. parts]);

    [Fact]
    public void The_marked_licence_is_embedded_with_its_permission_notice_and_copyright()
    {
        Assert.Contains(Path, ViewerAssets.Paths);
        var text = ViewerAssets.Text(Path);
        Assert.Contains("Permission is hereby granted, free of charge", text, StringComparison.Ordinal);
        Assert.Contains("The above copyright notice and this permission notice shall be included in", text, StringComparison.Ordinal);
        Assert.Contains("Christopher Jeffrey", text, StringComparison.Ordinal);
        // marked's own LICENSE.md also carries the Markdown (Gruber) BSD notice
        // its code derives from; the file is kept verbatim, so it is here too.
        Assert.Contains("John Gruber", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_written_site_holds_the_marked_licence_byte_for_byte()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        var file = System.IO.Path.Combine(dest.Path, "assets", "licenses", "marked-LICENSE.md");
        Assert.True(File.Exists(file));
        Assert.Equal(ViewerAssets.Bytes(Path), File.ReadAllBytes(file));
    }

    [Fact]
    public void The_okf_render_archive_manifest_and_the_ci_check_both_name_it_and_its_source_exists()
    {
        // Smoke check (see the class remarks): the manifest text names the file.
        var archive = File.ReadAllText(Repo("packaging", "New-ReleaseArchive.ps1"));
        Assert.Contains("'licenses/marked-LICENSE.md'", archive, StringComparison.Ordinal);
        Assert.Contains("'src/OKF4net.Viewer/Assets/licenses'", archive, StringComparison.Ordinal);
        Assert.Contains("$licensesDir/marked-LICENSE.md", archive, StringComparison.Ordinal);
        Assert.True(File.Exists(Repo("src", "OKF4net.Viewer", "Assets", "licenses", "marked-LICENSE.md")));

        var ci = File.ReadAllText(Repo(".github", "workflows", "ci.yml"));
        Assert.Contains("'licenses/marked-LICENSE.md'", ci, StringComparison.Ordinal);
        // ...and the AOT smoke step lists it among the assets the native binary must write.
        Assert.Contains("\"licenses/marked-LICENSE.md\",", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void NOTICE_and_the_viewer_README_name_the_shipped_licence_file()
    {
        Assert.Contains("marked-LICENSE.md", File.ReadAllText(Repo("NOTICE")), StringComparison.Ordinal);
        Assert.Contains("marked-LICENSE.md", File.ReadAllText(Repo("src", "OKF4net.Viewer", "README.md")), StringComparison.Ordinal);
    }
}

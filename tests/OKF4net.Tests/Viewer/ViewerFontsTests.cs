// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The embedded fonts and their <c>@font-face</c> rules (spec §11.0, A16).</summary>
public class ViewerFontsTests
{
    private static readonly (string Family, int Weight)[] Faces =
        [("Inter", 400), ("Inter", 500), ("Inter", 600), ("Inter Tight", 600), ("Inter Tight", 900), ("Space Mono", 400), ("Space Mono", 700)];

    private sealed record FontFace(string Family, int Weight, string Url, string Rule);

    private static List<FontFace> DeclaredFaces(string css)
    {
        var faces = new List<FontFace>();
        foreach (Match m in Regex.Matches(css, @"@font-face\s*\{([^}]*)\}"))
        {
            var rule = m.Groups[1].Value;
            faces.Add(new FontFace(
                Regex.Match(rule, "font-family:\\s*\"([^\"]+)\"").Groups[1].Value,
                int.Parse(Regex.Match(rule, @"font-weight:\s*(\d+)").Groups[1].Value, CultureInfo.InvariantCulture),
                Regex.Match(rule, "url\\(\"([^\"]+)\"\\)").Groups[1].Value,
                rule));
        }

        return faces;
    }

    [Fact]
    public void The_stylesheet_declares_exactly_the_seven_faces_in_order_before_anything_else()
    {
        var css = ViewerAssets.Css;

        Assert.Equal(Faces, DeclaredFaces(css).Select(f => (f.Family, f.Weight)));
        // "@font-face {" (with its brace), not "@font-face": the marker comment
        // above the block also holds "@font-face" and would make this vacuous.
        // The LAST rule must come before the :root tokens, so every face does.
        var lastFace = css.LastIndexOf("@font-face {", StringComparison.Ordinal);
        Assert.True(
            lastFace >= 0 && lastFace < css.IndexOf(":root {", StringComparison.Ordinal),
            "the @font-face rules must open the stylesheet, every one before the :root tokens (spec §12.6)");
    }

    [Fact]
    public void Every_face_loads_an_embedded_woff2_relative_to_the_stylesheet()
    {
        foreach (var face in DeclaredFaces(ViewerAssets.Css))
        {
            // Relative to assets/viewer.css, so valid at every page depth.
            Assert.Matches(@"^fonts/[a-z0-9-]+\.woff2$", face.Url);
            Assert.Contains(face.Url, ViewerAssets.Paths);
            Assert.Contains("format(\"woff2\")", face.Rule, StringComparison.Ordinal);
            Assert.Contains("font-display: swap", face.Rule, StringComparison.Ordinal);
            Assert.Matches(@"unicode-range:\s*U\+", face.Rule);
        }
    }

    [Fact]
    public void Every_woff2_is_one_and_together_they_stay_within_the_budget()
    {
        var fonts = ViewerAssets.Paths
            .Where(p => p.StartsWith("fonts/", StringComparison.Ordinal) && p.EndsWith(".woff2", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(fonts);

        long total = 0;
        foreach (var path in fonts)
        {
            var bytes = ViewerAssets.Bytes(path);
            Assert.Equal("wOF2", Encoding.ASCII.GetString(bytes, 0, 4));
            total += bytes.Length;
        }

        Assert.True(total <= 300_000, $"the woff2 files weigh {total} bytes, over the 300 000 of spec §11.0: back to the owner");
    }

    [Theory]
    [InlineData("fonts/OFL-Inter.txt")]
    [InlineData("fonts/OFL-InterTight.txt")]
    [InlineData("fonts/OFL-SpaceMono.txt")]
    public void Each_family_ships_its_SIL_OFL_text(string path)
        => Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", ViewerAssets.Text(path), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Every_embedded_font_file_matches_the_size_and_sha256_its_provenance_readme_records()
    {
        // The README is not embedded, so it is read from the repository. Its
        // two tables (fonts, then licences) end each row with the byte count
        // and the sha256; ReadAllLines splits on \n or \r\n alike, so the
        // README's own line endings cannot matter.
        var readme = Path.Combine(TestPaths.RepoRoot(), "src", "OKF4net.Viewer", "Assets", "fonts", "README.md");
        var recorded = File.ReadAllLines(readme)
            .Select(line => Regex.Match(line, @"^\| `([^`]+)` \|.*\| (\d+) \| `([0-9a-f]{64})` \|$"))
            .Where(m => m.Success)
            .ToDictionary(
                m => "fonts/" + m.Groups[1].Value,
                m => (Bytes: long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), Sha256: m.Groups[3].Value));

        // Every embedded file under fonts/ is recorded, and nothing else is.
        Assert.Equal(
            ViewerAssets.Paths.Where(p => p.StartsWith("fonts/", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            recorded.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(7, recorded.Count);

        // The EMBEDDED bytes are hashed raw, never line-ending-normalised: the
        // OFL texts are stored LF and .gitattributes marks fonts/** -text so no
        // checkout rewrites them. If that rule were lost, an autocrlf checkout
        // would embed CRLF texts -- exactly the drift this must catch, which
        // normalising before hashing would hide.
        foreach (var (path, expected) in recorded)
        {
            var bytes = ViewerAssets.Bytes(path);
            Assert.True(expected.Bytes == bytes.Length, $"{path}: {bytes.Length} bytes embedded, {expected.Bytes} recorded");
            Assert.True(
                expected.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                $"{path}: the embedded bytes do not hash to the recorded sha256");
        }
    }

    [Fact]
    public void The_written_site_holds_the_fonts_and_the_licences_but_not_the_provenance_readme()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        foreach (var face in DeclaredFaces(ViewerAssets.Css))
        {
            Assert.True(File.Exists(Path.Combine(dest.Path, "assets", face.Url.Replace('/', Path.DirectorySeparatorChar))), face.Url);
        }

        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "OFL-Inter.txt")));
        Assert.False(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "README.md")));
    }
}

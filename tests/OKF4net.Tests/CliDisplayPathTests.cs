// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Spec §8's display rule, pinned without the path-separator normalisation the
/// golden tests apply: on Windows a concept path must still be printed with
/// backslashes, byte for byte what <c>Path.Combine(root, …)</c> produced before
/// the provider seam.
/// </summary>
public class CliDisplayPathTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = OkfCli.Run(args, TextReader.Null, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Validate_and_info_print_concept_paths_with_the_os_separator_unnormalised()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: missing.sql\n---\n");
        tmp.Write("tables/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n"); // a YAML alias: the subset rejects it, so it lands in ParseErrors
        var expectedOrders = Path.Combine(tmp.Path, "tables", "orders.md");
        var expectedBroken = Path.Combine(tmp.Path, "tables", "broken.md");

        var validate = Run("validate", tmp.Path);
        Assert.Contains(expectedOrders + ": frontmatter path 'resource'", validate.Out);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains('\\', expectedOrders);
            Assert.DoesNotContain(tmp.Path.Replace('\\', '/') + "/tables/orders.md", validate.Out);
        }

        var info = Run("info", tmp.Path);
        Assert.Contains("unparseable files:\n  " + expectedBroken + ": ", info.Out);

        // `okf audit` prints ids, not paths (the .out golden has no path); `audit --json`
        // carries `"path"`, which the golden compares after `\\\\` → `/` normalisation.
        // Without a filter, audit lists only stale concepts and this fixture has none
        // (`"findings":[]`), so filter by type to get a finding with a path. The
        // JSON-escaped display path must appear verbatim: on Windows that is the
        // backslash doubled.
        var audit = Run("audit", tmp.Path, "--json", "--type", "Table");
        Assert.Equal(0, audit.Code);
        var jsonEscaped = expectedOrders.Replace("\\", "\\\\");
        Assert.Contains("\"path\":\"" + jsonEscaped + "\"", audit.Out);
    }

    /// <summary>
    /// The display path of a name the loader rejects is the one <c>dev</c> printed before the
    /// provider seam: <c>Path.Combine(root, …)</c> over the name as found on disk, unquoted —
    /// in <see cref="Bundle.ParseErrors"/>, in <see cref="Diagnostic.Path"/>, in
    /// <c>validate</c>'s text and in <c>validate --json</c>'s <c>path</c> (JSON-escaped by the
    /// writer, never pre-quoted). A decomposed <c>é</c> is rejected on every OS.
    /// </summary>
    [Fact]
    public void A_rejected_name_keeps_its_unquoted_display_path_on_every_os()
    {
        using var tmp = new TempDir();
        var nfd = "cafe\u0301.md";
        tmp.Write(nfd, "---\ntype: Note\n---\n");
        tmp.Write("sub/cafe\u0301.md", "---\ntype: Note\n---\n");
        AssertRejectedDisplayPaths(tmp.Path, [Path.Combine(tmp.Path, nfd), Path.Combine(tmp.Path, "sub", nfd)]);
    }

    /// <summary>
    /// The same for a Windows reserved name (<c>con.md</c>), which <c>dev</c> loaded and
    /// printed as <c>root\con.md</c>: now a parse error (announced), with the same path string.
    /// On Windows the file can only be created and removed through the <c>\\?\</c> namespace.
    /// </summary>
    [Fact]
    public void A_reserved_name_keeps_its_unquoted_display_path()
    {
        using var tmp = new TempDir();
        string[] names = ["con.md", Path.Combine("sub", "nul.txt.md")];
        Directory.CreateDirectory(Path.Combine(tmp.Path, "sub"));
        var prefix = OperatingSystem.IsWindows() ? @"\\?\" : string.Empty;
        try
        {
            foreach (var name in names)
            {
                File.WriteAllText(prefix + Path.Combine(tmp.Path, name), "---\ntype: Note\n---\n");
            }

            AssertRejectedDisplayPaths(tmp.Path, [.. names.Select(n => Path.Combine(tmp.Path, n))]);
        }
        finally
        {
            foreach (var name in names)
            {
                File.Delete(prefix + Path.Combine(tmp.Path, name));
            }
        }
    }

    /// <summary>
    /// A name that is not even a key (a control character, possible only off Windows):
    /// <c>dev</c> sent it through <c>ConceptId.FromPath</c>'s failure into
    /// <see cref="Bundle.ParseErrors"/> with its raw path; the CLI prints that path as is.
    /// </summary>
    [SkippableFact]
    public void A_name_that_is_not_a_key_keeps_its_raw_display_path()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows file names cannot hold a control character");
        using var tmp = new TempDir();
        var raw = "a\u0001b.md";
        tmp.Write(raw, "---\ntype: Note\n---\n");
        AssertRejectedDisplayPaths(tmp.Path, [Path.Combine(tmp.Path, raw)]);
    }

    private static void AssertRejectedDisplayPaths(string root, string[] expected)
    {
        var bundle = Bundle.Load(root);
        Assert.Equal(expected.Order(StringComparer.Ordinal), bundle.ParseErrors.Select(e => e.Path).Order(StringComparer.Ordinal));
        var diagnostics = BundleValidator.Validate(bundle).Diagnostics.Where(d => d.Code == DiagnosticCode.UnparseableDocument);
        Assert.Equal(expected.Order(StringComparer.Ordinal), diagnostics.Select(d => d.Path!).Order(StringComparer.Ordinal));

        var text = Run("validate", root).Out;
        foreach (var path in expected)
        {
            Assert.Contains("[error] " + path + ": unparseable concept document: ", text, StringComparison.Ordinal);
        }

        using var json = System.Text.Json.JsonDocument.Parse(Run("validate", root, "--json").Out);
        var jsonPaths = json.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("code").GetString() == "UnparseableDocument")
            .Select(d => d.GetProperty("path").GetString()!);
        Assert.Equal(expected.Order(StringComparer.Ordinal), jsonPaths.Order(StringComparer.Ordinal));
    }
}

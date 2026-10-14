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
}

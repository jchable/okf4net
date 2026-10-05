// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Hostile YAML, parsed in a CHILD process with a deadline. A stack overflow
/// cannot be caught: it kills the process it happens in, so a parser that
/// overflowed on one of these inputs inside the xunit host would take the
/// whole test run down rather than fail one test. Each case runs
/// <c>tests/YamlHostileProbe</c> (<c>dotnet YamlHostileProbe.dll &lt;case&gt;</c>),
/// which parses the input through the library's public entry points
/// (<c>YamlValue.Parse</c>, then <c>OkfDocument.Parse</c> with the input as
/// frontmatter) and prints what each one did. The assertion is a clean exit
/// reporting the nesting refusal — never a crash, never a timeout.
/// The inputs themselves are described in the probe's <c>Program.cs</c>.
/// </summary>
public class YamlHostileInputTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("flow-sequences", 1)]
    [InlineData("flow-mappings", 1)]
    [InlineData("dash-lines", 1001)]
    [InlineData("block-then-flow", 999)]
    public void A_hostile_input_is_refused_in_a_clean_exit(string name, int line)
    {
        var (code, stdout, stderr) = RunProbe(name);

        Assert.True(code == 0, $"probe exited with {code} (a crash, e.g. a stack overflow?)\nstdout:\n{stdout}\nstderr:\n{stderr}");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [
                $"YamlParseException line={line}: YAML error at line {line}: nesting depth limit exceeded",
                $"DocumentParseException: Invalid YAML in frontmatter: YAML error at line {line}: nesting depth limit exceeded",
            ],
            lines);
    }

    /// <summary>
    /// The limit itself fits on a fresh process's main thread: 1000 block
    /// mappings, the block shape with the most stack frames per level, parse
    /// rather than overflow. A guard that refused at 1001 but crashed at 1000
    /// would be no guard.
    /// </summary>
    [Fact]
    public void The_deepest_accepted_block_document_fits_on_the_stack()
    {
        var (code, stdout, stderr) = RunProbe("limit-block-mappings");

        Assert.True(code == 0, $"probe exited with {code}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["accepted", "accepted"], lines);
    }

    /// <summary>
    /// Runs the probe on <paramref name="name"/>, killing it at the deadline.
    /// The probe is built next to this test project in the same configuration
    /// and target framework (the test project's <c>ProjectReference</c> to it,
    /// with <c>ReferenceOutputAssembly="false"</c>, guarantees the build): this
    /// assembly runs from <c>tests/OKF4net.Tests/bin/&lt;config&gt;/&lt;tfm&gt;/</c>,
    /// so the probe is at <c>tests/YamlHostileProbe/bin/&lt;config&gt;/&lt;tfm&gt;/</c>.
    /// </summary>
    private static (int Code, string Stdout, string Stderr) RunProbe(string name)
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tfm = testBin.Name;
        var config = testBin.Parent!.Name;
        var probe = Path.Combine(TestPaths.RepoRoot(), "tests", "YamlHostileProbe", "bin", config, tfm, "YamlHostileProbe.dll");
        Assert.True(File.Exists(probe), $"probe not built at {probe}");

        // The SDK sets DOTNET_HOST_PATH for the processes it starts, the test
        // host included; outside it, the dotnet on PATH.
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(name);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Deadline))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"probe '{name}' did not finish within {Deadline.TotalSeconds} s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

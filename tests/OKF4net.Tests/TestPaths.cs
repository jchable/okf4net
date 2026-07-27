// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Shared test-fixture path helpers: locating the repo root from the test
/// assembly's output directory, and running the <c>okf</c> CLI in-process.
/// Consolidates the byte-identical <c>RepoRoot()</c>/<c>Run()</c> pairs
/// previously duplicated across <see cref="CliTests"/>,
/// <see cref="GoldenParityTests"/>, and every <c>OKF4net.Tests.Agents</c>
/// test class that needs the <c>appendix_a</c> fixture path.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Locates the repo root by walking up from the test assembly's output
    /// folder (<c>bin/Debug/net10.0</c>) to the directory containing
    /// <c>OKF4net.sln</c>. <c>dotnet test</c> runs with that output folder as
    /// the current directory, not the repo root, so fixture paths must be
    /// resolved this way rather than assumed relative to the process's
    /// current directory.
    /// </summary>
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"could not locate OKF4net.sln above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Runs the <c>okf</c> CLI in-process via <see cref="OkfCli.Run"/>,
    /// capturing stdout/stderr, and returns the exit code plus both streams.
    /// </summary>
    internal static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (OkfCli.Run(args, o, e), o.ToString(), e.ToString());
    }
}

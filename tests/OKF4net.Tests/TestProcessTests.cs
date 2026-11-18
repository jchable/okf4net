// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;

namespace OKF4net.Tests;

/// <summary>The bounded child-process runner keeps its deadline: a tree that never ends is killed, grandchild included.</summary>
public class TestProcessTests
{
    [Fact]
    public async Task A_process_tree_that_outlives_the_deadline_is_killed_whole_and_reported_as_a_timeout()
    {
        using var dir = new TempDir();
        var pidFile = Path.Combine(dir.Path, "pids");
        var clock = Stopwatch.StartNew();

        // The probe's "sleep" mode starts a grandchild, writes both PIDs, then sleeps two minutes (as does the grandchild).
        // 20 s leaves a loaded machine time to start two dotnet processes; the whole test is bounded well under a minute.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            TestProcess.RunAsync(TestProcess.DotnetHost(), [S3HostileEnvProbePath(), "sleep", pidFile], null, TimeSpan.FromSeconds(20)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the deadline was not kept: {clock.Elapsed}");

        Assert.True(File.Exists(pidFile), "the child did not write its PIDs within the deadline: the tree cannot be checked");
        var pids = File.ReadAllLines(pidFile).Where(l => l.Length > 0).Select(int.Parse).ToArray();
        Assert.Equal(2, pids.Length);

        // Bounded wait for both to be gone (the kill is asynchronous on some platforms).
        var gone = Stopwatch.StartNew();
        while (pids.Any(IsAlive) && gone.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100);
        }

        Assert.DoesNotContain(pids, IsAlive);
    }

    [Fact]
    public async Task A_process_that_ends_in_time_returns_its_code_and_both_streams()
    {
        var (code, output, error) = await TestProcess.RunAsync(TestProcess.DotnetHost(), [S3HostileEnvProbePath()], null, TimeSpan.FromSeconds(60));
        Assert.Equal(3, code); // the probe's usage error
        Assert.Equal("", output);
        Assert.StartsWith("usage: S3HostileEnvProbe", error, StringComparison.Ordinal);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited between the lookup and the question
        }
    }

    /// <summary>
    /// The probe is built next to this test project in the same configuration and target framework (the test project's
    /// <c>ProjectReference</c> to it, with <c>ReferenceOutputAssembly="false"</c>, guarantees the build).
    /// </summary>
    internal static string S3HostileEnvProbePath()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var probe = Path.Combine(TestPaths.RepoRoot(), "tests", "S3HostileEnvProbe", "bin", testBin.Parent!.Name, testBin.Name, "S3HostileEnvProbe.dll");
        Assert.True(File.Exists(probe), $"probe not built at {probe}");
        return probe;
    }
}

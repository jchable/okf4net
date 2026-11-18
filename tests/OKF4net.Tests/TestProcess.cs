// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;

namespace OKF4net.Tests;

/// <summary>A child process with a real deadline: both streams drained concurrently, the whole tree killed on expiry.</summary>
internal static class TestProcess
{
    /// <summary>
    /// The dotnet host for a child <c>dotnet &lt;dll&gt;</c>: the SDK sets <c>DOTNET_HOST_PATH</c> for the processes it
    /// starts, the test host included; outside it, the <c>dotnet</c> on <c>PATH</c>.
    /// </summary>
    public static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";

    /// <summary>
    /// Runs <paramref name="file"/> with <paramref name="args"/>; <paramref name="env"/> sets (a value) or removes
    /// (<see langword="null"/>) variables of the inherited environment. The deadline covers the exit AND the end of
    /// both streams (a grandchild holding the pipes open cannot keep the caller waiting): on expiry the whole process
    /// tree is killed and <see cref="TimeoutException"/> thrown.
    /// </summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(string file, IEnumerable<string> args, IDictionary<string, string?>? env, TimeSpan deadline)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env ?? new Dictionary<string, string?>())
        {
            if (v is null)
            {
                start.Environment.Remove(k);
            }
            else
            {
                start.Environment[k] = v;
            }
        }

        using var p = Process.Start(start) ?? throw new InvalidOperationException("process did not start: " + file);
        using var cts = new CancellationTokenSource(deadline);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The root already exited (only the streams were late): there is no tree left to walk from it.
            }

            p.WaitForExit(5000); // bounded: reaps the killed process
            throw new TimeoutException($"{file} did not finish within {deadline}");
        }

        return (p.ExitCode, await stdout, await stderr);
    }
}

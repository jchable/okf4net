// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The only <see cref="IContainerEngine"/> implementation shipped here:
/// shells to a Docker-CLI-compatible binary (<c>docker</c>, <c>podman</c>,
/// or <c>nerdctl</c> — their <c>run</c> surface is compatible, so one
/// parameterized class covers all three). This task adds only
/// <see cref="BuildRunArguments"/>, the pure part: it never spawns a
/// process, so it is unit-tested directly without Docker. It deliberately
/// does NOT declare <c>: IContainerEngine</c> yet — that interface requires
/// a <c>RunAsync</c> method, added in Task 9 by editing this same file
/// further (not a `partial` split, there is only ever one file); claiming
/// the interface here without it would fail to compile (CS0535, a missing
/// interface member), not just warn.
/// </summary>
public sealed class CliContainerEngine(string binaryName = "docker") : IContainerEngine
{
    /// <summary>
    /// Builds the <c>run</c> argument list for <paramref name="spec"/>. Every
    /// value (image, env vars, resource limits, the container name) is its
    /// own array element — never concatenated into one string — because this
    /// list is fed straight into <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// (Task 9), which passes each element to the child process verbatim,
    /// with no shell involved anywhere in this project's own process.
    /// </summary>
    internal static IReadOnlyList<string> BuildRunArguments(ContainerRunSpec spec, string containerName)
    {
        var args = new List<string> { "run", "-i", "--rm", "--name", containerName };

        if (spec.NetworkMode is { } network)
        {
            args.Add("--network");
            args.Add(network);
        }

        if (spec.MemoryBytes is { } memory)
        {
            args.Add("--memory");
            args.Add(memory.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.Cpus is { } cpus)
        {
            args.Add("--cpus");
            args.Add(cpus.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.PidsLimit is { } pids)
        {
            args.Add("--pids-limit");
            args.Add(pids.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in spec.Environment)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }

        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }

    /// <summary>Stdout/stderr are each capped at 8 MiB; a container that floods either past this is a stage failure, not an OOM.</summary>
    private const int MaxOutputBytes = 8 * 1024 * 1024;

    /// <inheritdoc />
    public async ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default)
    {
        var containerName = $"okf-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo
        {
            FileName = binaryName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in BuildRunArguments(spec, containerName))
        {
            psi.ArgumentList.Add(arg);
        }

        using var timeoutCts = spec.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdinTask = WriteStdinAsync(process.StandardInput, spec.Stdin);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputBytes);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillContainerAsync(containerName).ConfigureAwait(false);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // The CALLER asked to stop -- propagate a real
                // OperationCanceledException tied to their own token, so
                // AttestationOrchestrator's IsCallerCancellation recognises
                // it and rethrows rather than converting it into an
                // ordinary Fail(...) outcome.
                throw new OperationCanceledException("container run was cancelled by the caller", cancellationToken);
            }

            // Otherwise this project's OWN timeout fired -- a genuine stage
            // failure, reported the same way a non-zero exit code is.
            throw new ContainerExecutionException(
                "container run exceeded its timeout",
                await SafeAwaitAsync(stdoutTask).ConfigureAwait(false),
                await SafeAwaitAsync(stderrTask).ConfigureAwait(false));
        }

        await stdinTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ContainerRunResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Retries <c>binaryName kill</c> once after a short
    /// delay, best-effort: a container whose creation was still in flight
    /// when the first attempt ran reports "no such container" and is caught
    /// by the retry once it actually starts. Never throws -- a failure here
    /// only means <see cref="RunAsync"/> also calls <see cref="Process.Kill(bool)"/>
    /// on its own local process, which is the other half of teardown.
    /// </summary>
    private async Task KillContainerAsync(string containerName)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var kill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            kill.StartInfo.ArgumentList.Add("kill");
            kill.StartInfo.ArgumentList.Add(containerName);

            try
            {
                kill.Start();
                await kill.WaitForExitAsync().ConfigureAwait(false);
                if (kill.ExitCode == 0)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Best-effort teardown; Process.Kill on the local process
                // handles the case where the engine binary itself is gone.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <paramref name="input"/> then closes the stream. A container
    /// that exits before consuming all of stdin closes its end of the pipe
    /// first, which surfaces here as an <see cref="IOException"/> ("broken
    /// pipe") -- an expected occurrence (a script that errors out early),
    /// not a reason to let a raw exception replace the container's actual
    /// exit code and output in <see cref="RunAsync"/>. Swallowed here;
    /// <see cref="RunAsync"/>'s own exit-code check reports the real
    /// failure.
    /// </summary>
    private static async Task WriteStdinAsync(StreamWriter writer, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                await writer.WriteAsync(input).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Drains <paramref name="reader"/> to its end regardless of
    /// <paramref name="maxBytes"/>, so the child's pipe never backs up and
    /// blocks it — but only the first <paramref name="maxBytes"/> characters
    /// are kept. Runs concurrently with the other stream and with the stdin
    /// write in <see cref="RunAsync"/>, which is what actually avoids the
    /// classic redirected-pipe deadlock.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            var toKeep = Math.Max(0, Math.Min(read, maxBytes - total));
            if (toKeep > 0)
            {
                sb.Append(buffer, 0, toKeep);
                total += toKeep;
            }
        }

        return sb.ToString();
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }
}

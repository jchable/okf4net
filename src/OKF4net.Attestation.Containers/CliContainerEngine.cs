// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The only <see cref="IContainerEngine"/> implementation shipped here:
/// shells to a Docker-CLI-compatible binary (<c>docker</c>, <c>podman</c>,
/// or <c>nerdctl</c> — their <c>run</c> surface is compatible, so one
/// parameterized class covers all three). <see cref="BuildRunArguments"/> is
/// the pure argument-construction half: it never spawns a process, so it is
/// unit-tested directly without Docker. <see cref="RunAsync"/> is the real
/// execution half — it actually spawns the child process, so it is
/// exercised only by manual review and, later, against real Docker; there is
/// no automated test for that half in this repository.
/// </summary>
public sealed class CliContainerEngine(string binaryName = "docker") : IContainerEngine
{
    /// <summary>
    /// Builds the <c>run</c> argument list for <paramref name="spec"/>. Every
    /// value (image, env vars, resource limits, the container name) is its
    /// own array element — never concatenated into one string — because this
    /// list is fed straight into <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// in <see cref="RunAsync"/>, which passes each element to the child
    /// process verbatim, with no shell involved anywhere in this project's
    /// own process.
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
            // Explicit, no-BOM UTF-8: without this, .NET derives the pipe
            // encoding from the console codepage, which on a headless host
            // is not guaranteed to be UTF-8. Since ScriptComputationExecutor
            // puts the sanctioned script's raw text on stdin with no
            // JSON-escaping, a non-ASCII character silently mistranscoded on
            // the way in would mean the container runs a DIFFERENT program
            // than the one actually sanctioned. A BOM would corrupt it too.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ContainerExecutionException($"container engine '{binaryName}' could not be started", "", e.Message);
        }

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
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Best-effort: the TOCTOU window between the HasExited
                    // check and this call means the process may have exited
                    // right here (the very thing KillContainerAsync
                    // succeeding is expected to cause), which Process.Kill
                    // can surface as AggregateException/InvalidOperationException.
                    // Letting that escape would replace the
                    // OperationCanceledException/ContainerExecutionException
                    // this block exists to shape.
                }
            }

            // stdinTask is otherwise abandoned on these throwing paths;
            // observe it so a fault there never surfaces as an unobserved
            // task exception instead of the exception this block throws.
            try
            {
                await stdinTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
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
                    StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
    /// exit code and output in <see cref="RunAsync"/>. Any other exception
    /// here is swallowed for the same reason: this method is best-effort by
    /// construction, and <see cref="RunAsync"/>'s own exit-code check
    /// reports the real failure.
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
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (Exception)
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

// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The only <see cref="IContainerEngine"/> implementation shipped here:
/// shells to a Docker-CLI-compatible binary (<c>docker</c>, <c>podman</c>,
/// or <c>nerdctl</c> — their <c>run</c> surface is compatible, so one
/// parameterized class covers all three). <see cref="BuildRunArguments"/> is
/// the pure argument-construction half: it never spawns a process, so it is
/// unit-tested directly without Docker. <see cref="RunAsync"/> is the real
/// execution half — it actually spawns the child process, so no test in the
/// CI-filtered run covers it. It <i>is</i> covered by
/// <c>tests/OKF4net.Tests/Attestation.Containers/ContainerIntegrationTests.cs</c>,
/// which drives it against a real engine; those tests carry
/// <c>[Trait("Category", "ContainerIntegration")]</c> and are excluded from CI by
/// decision, so they run only when someone runs them.
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

        if (spec.ReadOnlyRootFilesystem)
        {
            args.Add("--read-only");
        }

        foreach (var tmpfs in spec.TmpfsMounts)
        {
            args.Add("--tmpfs");
            args.Add(tmpfs);
        }

        // A ceiling is either absent (null -- the flag is omitted and the engine's own
        // default applies) or a real ceiling. It is never zero or negative, because
        // docker and podman read those as UNLIMITED: emitting `--memory 0` would
        // remove the ceiling while looking like it set one.
        //
        // ContainerRuntimeProfile already rejects such a value in its init accessors,
        // but ContainerRunSpec is a public record a host can build by hand and pass
        // straight to this engine, so the guarantee cannot live only up there.
        if (spec.MemoryBytes is { } memory)
        {
            args.Add("--memory");
            args.Add(ResourceCeiling.Positive(memory, nameof(spec.MemoryBytes)).ToString(CultureInfo.InvariantCulture));
        }

        if (spec.Cpus is { } cpus)
        {
            args.Add("--cpus");
            args.Add(ResourceCeiling.Positive(cpus, nameof(spec.Cpus)).ToString(CultureInfo.InvariantCulture));
        }

        if (spec.PidsLimit is { } pids)
        {
            args.Add("--pids-limit");
            args.Add(ResourceCeiling.Positive(pids, nameof(spec.PidsLimit)).ToString(CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Stdout/stderr are each capped at 8 Mi <b>characters</b>; a container that
    /// floods either past this is a stage failure, not an OOM. Characters, not
    /// bytes, and deliberately so — the point is to bound host memory with an order
    /// of magnitude to spare, not to enforce an exact byte budget. Worst case
    /// (4-byte UTF-8 throughout) the real ceiling is 32 MiB of source bytes held as
    /// 16 MiB of UTF-16, still far below anything that threatens the host.
    /// </summary>
    private const int MaxOutputChars = 8 * 1024 * 1024;

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

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ContainerExecutionException($"container engine '{binaryName}' could not be started", "", e.Message);
        }

        // The wall clock starts once the child is actually running, not while this
        // process is still assembling arguments.
        //
        // Note what it still covers, because it surprises people: `run` pulls the
        // image when it is absent, and that pull happens INSIDE this child, so a
        // cold pull is charged against Timeout like any other work. On a slow link a
        // first run can spend the whole default budget fetching an image and time
        // out before the computation starts. Pre-pull the image, or raise the
        // profile's Timeout for the first run.
        using var timeoutCts = spec.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdinTask = WriteStdinAsync(process.StandardInput, spec.Stdin);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputChars);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputChars);

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

                // Drain both redirected streams before waiting. A child whose pipe
                // buffer fills blocks on the write and never exits, so a `kill` that
                // printed enough (an engine that is verbose about an unknown
                // container, say) would deadlock the teardown it is part of. Reading
                // to end also means the `catch` below sees a real failure rather than
                // a hang.
                var drainOut = kill.StandardOutput.ReadToEndAsync();
                var drainErr = kill.StandardError.ReadToEndAsync();
                await kill.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);

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

            // Back off only when another attempt follows. Sleeping after the last one
            // delayed the caller's OperationCanceledException by 250 ms for nothing.
            if (attempt < 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
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
    /// <paramref name="maxChars"/>, so the child's pipe never backs up and
    /// blocks it — but only the first <paramref name="maxChars"/> characters
    /// are kept. Runs concurrently with the other stream and with the stdin
    /// write in <see cref="RunAsync"/>, which is what actually avoids the
    /// classic redirected-pipe deadlock.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            var toKeep = Math.Max(0, Math.Min(read, maxChars - total));
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

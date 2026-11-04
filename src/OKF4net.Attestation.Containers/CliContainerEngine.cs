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
/// or <c>nerdctl</c> — their <c>create</c>/<c>start</c> surface is compatible, so one
/// parameterized class covers all three). A run is two commands, <c>create</c> then
/// <c>start -a -i</c>, never <c>run</c>: see <see cref="RunAsync"/> for why.
/// <see cref="BuildRunArguments"/> is
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
    /// Builds the <c>create</c> argument list for <paramref name="spec"/> (the container,
    /// with all its isolation flags; <see cref="BuildStartArguments"/> is the command that
    /// then runs it). Every
    /// value (image, env vars, resource limits, the container name) is its
    /// own array element — never concatenated into one string — because this
    /// list is fed straight into <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// in <see cref="RunAsync"/>, which passes each element to the child
    /// process verbatim, with no shell involved anywhere in this project's
    /// own process.
    /// </summary>
    internal static IReadOnlyList<string> BuildRunArguments(ContainerRunSpec spec, string containerName)
    {
        var args = new List<string> { "create", "-i", "--rm", "--name", containerName };

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

        if (spec.User is { } user)
        {
            args.Add("--user");
            args.Add(user);
        }

        if (spec.DropAllCapabilities)
        {
            args.Add("--cap-drop");
            args.Add("ALL");
        }

        if (spec.NoNewPrivileges)
        {
            args.Add("--security-opt");
            args.Add("no-new-privileges");
        }

        // A ceiling is either absent (null -- the flag is omitted and the engine's own
        // default applies) or a real ceiling. It is never zero or negative, because
        // docker and podman read those as UNLIMITED: emitting `--memory 0` would
        // remove the ceiling while looking like it set one.
        //
        // ContainerIsolation already rejects such a value in its init accessors,
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
    /// The command that runs the container <see cref="BuildRunArguments"/> created, with
    /// this process's stdin, stdout and stderr attached to it. <c>-i</c> is needed here as
    /// well as at <c>create</c>; the client exits with the container's exit code.
    /// </summary>
    internal static IReadOnlyList<string> BuildStartArguments(string containerName) =>
        ["start", "-a", "-i", containerName];

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
        // Timeout is validated here, before anything is started -- not left to the
        // CancellationTokenSource that will eventually enforce it. Two reasons. That
        // constructor ACCEPTS Timeout.InfiniteTimeSpan, as "never fire", so it would
        // remove the wall-clock ceiling while looking like one; and the values it does
        // reject would throw AFTER process.Start(), leaving the engine process and
        // its container running with nothing to tear them down. ContainerRunSpec is
        // a public record a host can build by hand, so the profile's own check is not
        // enough -- the same reason BuildRunArguments re-checks the other ceilings.
        if (spec.Timeout is { } requestedTimeout)
        {
            ResourceCeiling.Timeout(requestedTimeout, nameof(spec.Timeout));
        }

        var containerName = $"okf-{Guid.NewGuid():N}";

        // A run is `create` then `start -a -i`, not `run` (#110). `run` is one client
        // doing both, so a client killed while it is mid-`create` leaves the daemon free
        // to commit that create afterwards, and nothing on our side can tell whether it
        // did: no count of absent rounds proves it will not. Here `create` is its own
        // command, awaited to its end even when the run is cancelled (see
        // TearDownContainerAsync): once it has exited, the daemon's answer is known and
        // the container either exists or never will, so the removal that follows is
        // checked against a settled state, not guessed at.
        //
        // The wall clock starts once the first child is actually running, not while this
        // process is still assembling arguments.
        //
        // Note what it still covers, because it surprises people: `create` pulls the
        // image when it is absent, and that pull happens INSIDE this child, so a
        // cold pull is charged against Timeout like any other work. On a slow link a
        // first run can spend the whole default budget fetching an image and time
        // out before the computation starts. Pre-pull the image, or raise the
        // profile's Timeout for the first run.
        using var create = NewClient(BuildRunArguments(spec, containerName));
        StartClient(create);
        using var timeoutCts = spec.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // `create` reads nothing: close its stdin at once. Its stdout is the container id
        // and its stderr the pull progress or the reason it failed; both are kept (bounded)
        // only so a failure can be reported.
        var createStdin = WriteStdinAsync(create.StandardInput, null);
        var createStdout = ReadBoundedAsync(create.StandardOutput.BaseStream, LenientUtf8, MaxOutputChars);
        var createStderr = ReadBoundedAsync(create.StandardError.BaseStream, LenientUtf8, MaxOutputChars);
        try
        {
            await create.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TearDownContainerAsync(containerName, create, clientIsCreate: true).ConfigureAwait(false);
            throw await CancellationOutcomeAsync(cancellationToken, createStdout, createStderr).ConfigureAwait(false);
        }

        await createStdin.ConfigureAwait(false);
        if (create.ExitCode != 0)
        {
            // The engine refused to create the container (image not found, bad flag,
            // daemon down): there is none to start or to remove, and the exit code and
            // message are what `run` would have reported for the same failure.
            return new ContainerRunResult(
                create.ExitCode,
                await SafeAwaitAsync(createStdout).ConfigureAwait(false),
                await SafeAwaitAsync(createStderr).ConfigureAwait(false));
        }

        using var process = NewClient(BuildStartArguments(containerName));
        try
        {
            StartClient(process);
        }
        catch (ContainerExecutionException)
        {
            // The container exists and nothing will ever run it: remove it.
            await TearDownContainerAsync(containerName, create, clientIsCreate: false).ConfigureAwait(false);
            throw;
        }

        var stdinTask = WriteStdinAsync(process.StandardInput, spec.Stdin);
        // stdout is the receipt an attester authenticates (§10.5), so it is decoded
        // strictly: a byte that is not valid UTF-8 fails the stage below rather than
        // being replaced with U+FFFD, which would hand the attester text no container
        // wrote. stderr keeps the lenient decoder on purpose: it is host-side
        // diagnostics, never authenticated, and a strict decoder there would hide the
        // very traceback a host needs to see why a run failed.
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, StrictUtf8, MaxOutputChars);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, LenientUtf8, MaxOutputChars);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TearDownContainerAsync(containerName, process, clientIsCreate: false).ConfigureAwait(false);

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

            throw await CancellationOutcomeAsync(cancellationToken, stdoutTask, stderrTask).ConfigureAwait(false);
        }

        await stdinTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (stdout.Truncated || stderr.Truncated)
        {
            // The cap is a stage failure, not a quiet truncation. A cut-off stdout
            // prefix handed to receipt parsing as if it were complete, or a flooded
            // stderr waved through with a zero exit code, would both hide the flood --
            // and the second is not hypothetical: nothing downstream ever reads
            // stderr's length.
            var flooded = stdout.Truncated ? "stdout" : "stderr";
            throw new ContainerExecutionException(
                $"container run exceeded the output ceiling of {MaxOutputChars} characters on {flooded} (exit code {process.ExitCode})",
                stdout.Text,
                stderr.Text);
        }

        if (stdout.InvalidBytes)
        {
            // Reported after exit, like the ceiling: the reader kept draining past the
            // bad bytes so the child could finish. stdout.Text holds only what was
            // decoded from reads completed before the one that contained them --
            // possibly nothing, since how the pipe chunks its reads is not ours to
            // choose -- and goes to the host-side Stdout property, never into the
            // message. The message carries only the exit code, an int, like the
            // ceiling's.
            throw new ContainerExecutionException(
                $"container stdout was not valid UTF-8 (exit code {process.ExitCode})",
                stdout.Text,
                stderr.Text);
        }

        return new ContainerRunResult(process.ExitCode, stdout.Text, stderr.Text);
    }

    /// <summary>
    /// One engine client process for <paramref name="arguments"/>, not started, with all
    /// three standard streams redirected.
    /// </summary>
    private Process NewClient(IReadOnlyList<string> arguments)
    {
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
            //
            // The output encodings below only configure the StreamReaders Process
            // builds, and those readers are never read from: RunAsync reads raw bytes
            // off their BaseStream and ReadBoundedAsync decodes them itself (strictly
            // for stdout). They stay explicit so Process never consults the console
            // codepage to build readers nothing uses.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = psi };
    }

    private void StartClient(Process client)
    {
        try
        {
            client.Start();
        }
        catch (Win32Exception e)
        {
            throw new ContainerExecutionException($"container engine '{binaryName}' could not be started", "", e.Message);
        }
    }

    /// <summary>
    /// The exception <see cref="RunAsync"/> throws once teardown has run for a cancelled
    /// wait: the caller's own cancellation, or this project's timeout.
    /// </summary>
    private static async Task<Exception> CancellationOutcomeAsync(CancellationToken callerToken, Task<BoundedRead> stdout, Task<BoundedRead> stderr)
    {
        if (callerToken.IsCancellationRequested)
        {
            // The CALLER asked to stop -- propagate a real
            // OperationCanceledException tied to their own token, so
            // AttestationOrchestrator's IsCallerCancellation recognises
            // it and rethrows rather than converting it into an
            // ordinary Fail(...) outcome.
            return new OperationCanceledException("container run was cancelled by the caller", callerToken);
        }

        // Otherwise this project's OWN timeout fired -- a genuine stage
        // failure, reported the same way a non-zero exit code is.
        return new ContainerExecutionException(
            "container run exceeded its timeout",
            await SafeAwaitAsync(stdout).ConfigureAwait(false),
            await SafeAwaitAsync(stderr).ConfigureAwait(false));
    }

    /// <summary>
    /// The whole teardown budget for <see cref="TearDownContainerAsync"/>: the stop
    /// command, the wait for the local client to exit, every removal round AND the delays
    /// between them together, not a per-command bound. A slow but
    /// responsive engine (a few hundred ms per <c>kill</c>, <c>rm</c> or <c>inspect</c>) finishes well inside it;
    /// an engine whose daemon has gone away and never answers is cut off here rather
    /// than being allowed to turn the timeout <see cref="RunAsync"/> promised into a
    /// multi-attempt hang (a Medium external-audit finding: a 750 ms-per-call `kill`
    /// previously stretched a 30 ms <see cref="ContainerRunSpec.Timeout"/> to ~1.74 s,
    /// and the old worst case -- two 5 s per-attempt bounds plus the delay -- was
    /// closer to 10 s). This is this host's own contract, not something the OKF spec
    /// requires: <see cref="RunAsync"/> promises <see cref="ContainerRunSpec.Timeout"/>
    /// bounds the whole run, and teardown after that timeout fires is part of what the
    /// caller is still waiting on.
    /// </summary>
    private const int TeardownBudgetSeconds = 3;

    /// <summary>Default value of <see cref="TeardownBudget"/>; see that property.</summary>
    internal static readonly TimeSpan DefaultTeardownBudget = TimeSpan.FromSeconds(TeardownBudgetSeconds);

    /// <summary>
    /// The whole teardown budget for <see cref="TearDownContainerAsync"/> -- see
    /// <see cref="DefaultTeardownBudget"/> for what it covers and why 3 s. Internal and
    /// settable only so tests can shrink it: a real caller always gets the 3 s default,
    /// but a test that wants to discriminate this budget's behaviour from the old
    /// unbounded-per-attempt one on a loaded CI runner needs a much smaller number to
    /// get a comfortable timing margin either side of the assertion.
    /// </summary>
    internal TimeSpan TeardownBudget { get; init; } = DefaultTeardownBudget;

    /// <summary>
    /// How much of <see cref="TeardownBudget"/> must remain before another removal round
    /// in <see cref="TearDownContainerAsync"/> is started at all -- including the delay
    /// that precedes it. Below this, the caller is already close enough to its
    /// deadline that starting another round (which cannot itself be bounded by
    /// less time than it would need to even report failure cleanly) is not worth it.
    /// </summary>
    private static readonly TimeSpan RetryThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay between two removal rounds; counts against <see cref="TeardownBudget"/>.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How many consecutive rounds must find the container absent before teardown believes it.</summary>
    private const int AbsentRoundsRequired = 2;

    /// <summary>
    /// How long, from the first round that found the container absent, absence must hold
    /// before teardown believes it -- but only while the create is unsettled (its client had
    /// to be killed before it exited) and the container has not been seen to exist at all
    /// (see <see cref="TearDownContainerAsync"/>). A <c>create</c> still in
    /// flight when the client died is committed by the daemon on its own schedule. The
    /// figures below were measured when every run was a single <c>run</c> client, so every
    /// early cancellation took this path; they do not describe the two-command run. Measured
    /// on Docker 29.4.1 for Windows, early-cancelled runs: no leak in ~2900 runs on a
    /// quiet daemon; during one period of heavy load from other sessions on the shared
    /// daemon, 18 leaks in 400 runs with two rounds alone and 12 in 700 with this window
    /// -- fewer, not none. Where teardown durations were recorded the leaking runs had
    /// used up the whole <see cref="TeardownBudget"/> (2.8-3.4 s against ~2 s when quiet).
    /// Counts against <see cref="TeardownBudget"/>.
    /// </summary>
    private static readonly TimeSpan AbsentWindow = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Tears down the container <see cref="RunAsync"/> asked the engine to create, and
    /// the local client driving it, best-effort, and VERIFIES that the container is
    /// gone rather than trusting any exit code to say so (#110). Never throws.
    /// <list type="number">
    /// <item>Settling the client. When the run was cancelled during <c>start</c> (the
    /// container exists, its <c>create</c> long done): <c>kill &lt;name&gt;</c>, once, which
    /// stops a container that is running right now at once (the same SIGKILL on all three
    /// engines this class drives; its result only says the container existed), then the
    /// local client process tree is killed and waited for. When it was cancelled during
    /// <c>create</c>: that client is NOT killed but waited for, within two thirds of the budget, because a
    /// <c>create</c> client killed mid-flight leaves the daemon free to commit it later,
    /// unseen. Once it has exited, the daemon's answer is known and the container either
    /// exists or never will -- the create is "settled", and absence needs no window of
    /// waiting for a late commit (#110). Only a <c>create</c> that outlasts the budget
    /// (a cold image pull, say) is killed, and it is then unsettled. This all comes BEFORE
    /// the removal, so that nothing on our side is still driving what the removal is
    /// about to race.</item>
    /// <item>Rounds of <c>rm -f &lt;name&gt;</c> then <c>inspect &lt;name&gt;</c>.
    /// <c>rm -f</c> removes a container in any state -- created, running, exited --
    /// but its exit code proves nothing: Docker exits 0 for a name that does not exist
    /// (observed on Docker 29.4.1), so "succeeded" cannot be told from "never existed
    /// yet". <c>inspect</c> exits non-zero for a name the engine does not know (Docker
    /// 29.4.1: exit 1, "No such object" on stderr); that, seen after the client has exited,
    /// is the only signal taken as absence. A non-zero exit without that text (daemon down,
    /// permission error) is unknown and restarts the count. Absence has to be seen in <see cref="AbsentRoundsRequired"/>
    /// consecutive rounds, a <see cref="RetryDelay"/> apart -- and, only while the create
    /// is unsettled and the engine has not once confirmed the container exists
    /// (<c>kill</c> exiting 0, or <c>inspect</c> finding it), to hold for
    /// <see cref="AbsentWindow"/> as well, because only then can a <c>create</c> still be
    /// in flight. A round that finds the container present, or cannot get an answer,
    /// starts the count over. Podman's and nerdctl's exit codes for these
    /// commands are NOT verified by this repo (it has never run either) -- the design
    /// assumes only that <c>inspect</c> of a missing name exits non-zero and says
    /// nothing about what their <c>rm -f</c> of a missing name does.</item>
    /// </list>
    /// Everything shares one <see cref="TeardownBudget"/> deadline, computed once here,
    /// so an unresponsive engine cannot make teardown outlive the timeout
    /// <see cref="RunAsync"/> promised. A command that hits the remaining budget is not
    /// followed by another, and a further round only starts when at least
    /// <see cref="RetryThreshold"/> of the budget is left (delay included). Residual
    /// risk: a <c>create</c> that does not exit within two thirds of the budget is killed
    /// unsettled, and one the daemon then commits after the last check is not seen. That
    /// needs a create that slow (an image pull, a daemon that has stopped answering), no
    /// longer a create racing a kill. A failure of any command here only means the container
    /// may be left behind, never an exception: <see cref="RunAsync"/> then reports the
    /// same <see cref="OperationCanceledException"/> or timeout failure it would have.
    /// </summary>
    private async Task TearDownContainerAsync(string containerName, Process client, bool clientIsCreate)
    {
        var elapsed = Stopwatch.StartNew();
        TimeSpan Remaining()
        {
            var left = TeardownBudget - elapsed.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        // Whether the daemon has finished with `create`. Once it has, nothing can appear
        // later under this name, so absence needs only AbsentRoundsRequired rounds.
        // The start phase starts from there: its `create` exited 0 before `start` was run.
        // In the create phase it is the point of waiting for that client below.
        bool settled;
        int? killed = null;
        if (clientIsCreate)
        {
            // Do NOT kill a `create` that is still running: a client killed mid-create
            // leaves the daemon free to commit it afterwards, which nothing here could
            // see. Let it finish -- it is a fraction of a second once the image is local --
            // within two thirds of the budget, which leaves the removal rounds their time;
            // only a client that outlasts that is killed, and then the create is unsettled
            // and the absence window below applies again. There is no container to `kill`
            // yet, and the removal covers any that appears.
            var createWait = TimeSpan.FromTicks(TeardownBudget.Ticks * 2 / 3);
            settled = await WaitForClientAsync(client, createWait < Remaining() ? createWait : Remaining()).ConfigureAwait(false);
            if (!settled)
            {
                await StopClientAsync(client, Remaining()).ConfigureAwait(false);
            }
        }
        else
        {
            if (Remaining() > TimeSpan.Zero)
            {
                killed = (await TryEngineCommandOnceAsync(Remaining(), "kill", containerName).ConfigureAwait(false))?.ExitCode;
            }

            await StopClientAsync(client, Remaining()).ConfigureAwait(false);
            settled = true;
        }

        // A kill that exited 0 confirms the container exists (Docker 29.4.1: a running
        // container -> 0, a name it does not know or a container that is not running ->
        // 1); so does an inspect that finds it. Either means its create is done too.
        var existed = killed == 0;
        var absentRounds = 0;
        TimeSpan? firstAbsent = null;
        while (Remaining() > TimeSpan.Zero)
        {
            // The exit code is deliberately ignored; inspect below is the check.
            await TryEngineCommandOnceAsync(Remaining(), "rm", "-f", containerName).ConfigureAwait(false);
            if (Remaining() == TimeSpan.Zero)
            {
                return;
            }

            var inspected = await TryEngineCommandOnceAsync(Remaining(), "inspect", containerName).ConfigureAwait(false);
            if (inspected is { ExitCode: 0 })
            {
                existed = true;
                absentRounds = 0;
                firstAbsent = null;
            }
            else if (inspected is { } reply && IsNotFound(reply.Error))
            {
                absentRounds++;
                firstAbsent ??= elapsed.Elapsed;
                if (absentRounds >= AbsentRoundsRequired && (settled || existed || elapsed.Elapsed - firstAbsent.Value >= AbsentWindow))
                {
                    return;
                }
            }
            else
            {
                // Unknown: no answer, or a non-zero exit that does not say "no such"
                // (daemon down, transport or permission error). Not evidence of absence.
                absentRounds = 0;
                firstAbsent = null;
            }

            var remaining = Remaining();
            if (remaining < RetryThreshold)
            {
                return;
            }

            await Task.Delay(RetryDelay < remaining ? RetryDelay : remaining).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits, within <paramref name="bound"/>, for the engine client to exit on its own,
    /// without killing it. True if it has exited; false if it had not when the bound ran out
    /// (or the wait failed). Never throws.
    /// </summary>
    private static async Task<bool> WaitForClientAsync(Process client, TimeSpan bound)
    {
        try
        {
            if (client.HasExited)
            {
                return true;
            }

            if (bound <= TimeSpan.Zero)
            {
                return false;
            }

            using var cts = new CancellationTokenSource(bound);
            await client.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills the local engine client and its children and waits, within
    /// <paramref name="bound"/>, for it to be gone. Best-effort: the process may already
    /// have exited between a check and the kill (which <see cref="Process.Kill(bool)"/>
    /// can surface as an AggregateException or InvalidOperationException), and letting
    /// that escape would replace the OperationCanceledException/ContainerExecutionException
    /// <see cref="RunAsync"/> is about to shape.
    /// </summary>
    private static async Task StopClientAsync(Process client, TimeSpan bound)
    {
        try
        {
            if (!client.HasExited)
            {
                client.Kill(entireProcessTree: true);
            }

            if (bound > TimeSpan.Zero)
            {
                using var cts = new CancellationTokenSource(bound);
                await client.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // See above: best-effort.
        }
    }

    /// <summary>
    /// The exit code and standard error of one engine command the teardown issued.
    /// </summary>
    private readonly record struct EngineReply(int ExitCode, string Error);

    /// <summary>
    /// True when an engine's error text says the name is unknown to it. Docker's
    /// <c>inspect</c> of a missing name says "No such object" (29.4.1) or "No such
    /// container"; a daemon that is down, or a permission error, says neither, so it is
    /// never taken as absence. Podman's and nerdctl's wording is NOT verified by this repo.
    /// </summary>
    private static bool IsNotFound(string error) =>
        error.Contains("no such", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one <c>binaryName &lt;arguments&gt;</c> (<c>kill</c>, <c>rm -f</c> or
    /// <c>inspect</c> of the run's container -- the only three the teardown issues),
    /// bounded by <paramref name="bound"/> (a slice of <see cref="TearDownContainerAsync"/>'s
    /// overall <see cref="TeardownBudget"/>, never negative -- the caller clamps).
    /// Returns the exit code and standard error, or <see langword="null"/> when the command gave no
    /// answer: it could not be started, threw, or hit <paramref name="bound"/>. The
    /// output drains share that bound, so a child whose descendant keeps a pipe open
    /// cannot hold the teardown past its budget.
    /// </summary>
    private async Task<EngineReply?> TryEngineCommandOnceAsync(TimeSpan bound, params string[] arguments)
    {
        using var command = new Process
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
        foreach (var argument in arguments)
        {
            command.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            command.Start();

            // Drain both redirected streams before waiting. A child whose pipe
            // buffer fills blocks on the write and never exits, so a command that
            // printed enough (an engine that is verbose about an unknown
            // container, say) would deadlock the teardown it is part of. Reading
            // to end also means the `catch` below sees a real failure rather than
            // a hang.
            var drainOut = command.StandardOutput.ReadToEndAsync();
            var drainErr = command.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(bound);
            try
            {
                await command.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await Task.WhenAll(drainOut, drainErr).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The engine itself is not answering within its slice of the
                // budget. Kill this child (and whatever it spawned) so it cannot
                // outlive the run.
                try
                {
                    command.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                return null;
            }

            return new EngineReply(command.ExitCode, drainErr.Result);
        }
        catch (Exception)
        {
            // Best-effort teardown; the local client is killed separately, which
            // handles the case where the engine binary itself is gone.
            return null;
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

    /// <summary>UTF-8 that throws on an invalid sequence; stdout's decoder.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 that replaces an invalid sequence with U+FFFD; stderr's decoder.</summary>
    internal static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>How many bytes <see cref="ReadBoundedAsync"/> reads from its stream at a time.</summary>
    internal const int ReadBufferBytes = 8192;

    /// <summary>
    /// Drains <paramref name="stream"/> to its end regardless of
    /// <paramref name="maxChars"/> or of what it contains, so the child's pipe never
    /// backs up and blocks it — but only the first <paramref name="maxChars"/>
    /// characters are kept, and <see cref="BoundedRead.Truncated"/> says whether
    /// anything was dropped, so <see cref="RunAsync"/> can fail the stage instead of
    /// passing a prefix off as the whole. Runs concurrently with the other stream and
    /// with the stdin write in <see cref="RunAsync"/>, which is what actually avoids
    /// the classic redirected-pipe deadlock.
    /// <para>
    /// Bytes are decoded with one <see cref="Decoder"/> for the whole stream, which
    /// carries a multi-byte sequence split across two reads over to the next call.
    /// When <paramref name="encoding"/> throws on invalid bytes, the first
    /// <see cref="DecoderFallbackException"/> sets <see cref="BoundedRead.InvalidBytes"/>
    /// and stops decoding — a decoder is not reused after it throws — but not reading:
    /// the rest of the stream is read and discarded, exactly as after truncation.
    /// The decoder is flushed at end of stream, so a sequence cut off by the end is
    /// invalid too. <see cref="BoundedRead.Text"/> then holds only what was decoded
    /// from reads completed <i>before</i> the read that contained the invalid bytes: a
    /// throwing <c>GetChars</c> yields nothing for its buffer, so valid bytes ahead of
    /// the bad ones in that same read are lost with them. How much survives — and so
    /// whether <see cref="BoundedRead.Truncated"/> was reached first — depends on how
    /// the pipe chunked its reads, and may be nothing: diagnostics only, never a
    /// receipt.
    /// </para>
    /// <para>
    /// One leading UTF-8 byte-order mark is skipped, as the <see cref="StreamReader"/>
    /// <see cref="Process"/> hands out did before this method read raw bytes (a
    /// receipt starting with one parsed then and must still parse). Only UTF-8's:
    /// that reader also switched to UTF-16 or UTF-32 on their marks, which here are
    /// simply bytes — invalid UTF-8 for the strict decoder.
    /// </para>
    /// </summary>
    internal static async Task<BoundedRead> ReadBoundedAsync(Stream stream, UTF8Encoding encoding, int maxChars)
    {
        var decoder = encoding.GetDecoder();
        var bytes = new byte[ReadBufferBytes];
        var chars = new char[encoding.GetMaxCharCount(ReadBufferBytes)];
        var sb = new StringBuilder();
        var total = 0;
        var truncated = false;
        var invalid = false;
        var atStart = true;

        void Keep(int decoded)
        {
            // The encoding is UTF-8, so a U+FEFF as the very first character can
            // only have come from the bytes EF BB BF at the start of the stream.
            var start = 0;
            if (atStart && decoded > 0)
            {
                atStart = false;
                start = chars[0] == '\uFEFF' ? 1 : 0;
            }

            var available = decoded - start;
            var toKeep = Math.Max(0, Math.Min(available, maxChars - total));
            if (toKeep > 0)
            {
                sb.Append(chars, start, toKeep);
                total += toKeep;
            }

            if (toKeep < available)
            {
                truncated = true;
            }
        }

        int read;
        while ((read = await stream.ReadAsync(bytes.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (invalid)
            {
                continue;
            }

            try
            {
                Keep(decoder.GetChars(bytes, 0, read, chars, 0, flush: false));
            }
            catch (DecoderFallbackException)
            {
                invalid = true;
            }
        }

        if (!invalid)
        {
            try
            {
                Keep(decoder.GetChars(bytes, 0, 0, chars, 0, flush: true));
            }
            catch (DecoderFallbackException)
            {
                invalid = true;
            }
        }

        return new BoundedRead(sb.ToString(), truncated, invalid);
    }

    private static async Task<string> SafeAwaitAsync(Task<BoundedRead> task)
    {
        try
        {
            return (await task.ConfigureAwait(false)).Text;
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// What <see cref="CliContainerEngine"/>'s bounded stream reader kept, whether it had to
/// drop anything to stay within its cap, and whether its (strict) decoder met bytes that
/// were not valid in its encoding.
/// </summary>
internal readonly record struct BoundedRead(string Text, bool Truncated, bool InvalidBytes);

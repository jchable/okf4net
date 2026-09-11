// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// Runs one container to completion. The single implementation shipped here
/// is CliContainerEngine; this abstraction exists
/// so a fundamentally different engine (e.g. a future Kubernetes Jobs
/// backend) can be added without touching any binder/executor/attester.
/// </summary>
public interface IContainerEngine
{
    /// <summary>Runs <paramref name="spec"/> in a fresh container and returns its result.</summary>
    ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything one container run needs. <see cref="Stdin"/> is the only
/// channel that ever carries bundle-derived content (script/SQL/attester
/// text) — no volume is ever mounted (see the design doc's "pas de montage
/// de volume" principle).
/// </summary>
/// <param name="Image">The container image to run.</param>
/// <param name="Command">The command run inside the image — always a short, fixed sequence (an interpreter, or a project-authored wrapper), never bundle-derived.</param>
/// <param name="Stdin">Piped to the container's stdin, then the stream is closed.</param>
/// <param name="Environment">Environment variables set on the container.</param>
/// <param name="NetworkMode">Passed as <c>--network</c> when non-null (e.g. <c>"none"</c>); omitted (engine default) when null.</param>
/// <param name="MemoryBytes">Passed as <c>--memory</c> when non-null.</param>
/// <param name="Cpus">Passed as <c>--cpus</c> when non-null.</param>
/// <param name="PidsLimit">Passed as <c>--pids-limit</c> when non-null.</param>
/// <param name="Timeout">A wall-clock ceiling enforced by the engine itself, independent of the caller's <see cref="CancellationToken"/>.</param>
public sealed record ContainerRunSpec(
    string Image,
    IReadOnlyList<string> Command,
    string? Stdin,
    IReadOnlyDictionary<string, string> Environment,
    string? NetworkMode,
    long? MemoryBytes,
    double? Cpus,
    int? PidsLimit,
    TimeSpan? Timeout);

/// <summary>One container run's outcome: exit code plus captured (size-bounded) stdout/stderr.</summary>
public sealed record ContainerRunResult(int ExitCode, string Stdout, string Stderr);

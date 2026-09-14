// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IComputationExecutor"/> for <see cref="ContainerRuntimeKind.Script"/>
/// profiles. The bound computation text IS the program: it is piped on
/// stdin to <see cref="ContainerRuntimeProfile.Interpreter"/> unchanged, and
/// is expected to print a JSON object on stdout matching
/// <c>executor.receipt</c>'s declared fields. Parameter values reach the
/// script separately, as JSON in the <c>OKF_PARAMS_JSON</c> environment
/// variable — never spliced into the script's own source.
/// </summary>
public sealed class ScriptComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    /// <inheritdoc />
    public async ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default)
    {
        // TMPDIR -> the first tmpfs mount, so a script's own temp files land in the
        // scratch the host named rather than in a /tmp that may be read-only. Applied
        // before OKF_PARAMS_JSON, which this executor owns and the host cannot override.
        var env = new Dictionary<string, string>(ScratchDirectory.Apply(profile.Environment, profile.TmpfsMounts))
        {
            ["OKF_PARAMS_JSON"] = JsonSerializer.Serialize(bound.Values),
        };

        var spec = new ContainerRunSpec(
            Image: profile.Image,
            Command: [profile.Interpreter, "-"],
            Stdin: bound.BoundText ?? "",
            Environment: env,
            NetworkMode: profile.NetworkMode,
            MemoryBytes: profile.MemoryBytes,
            Cpus: profile.Cpus,
            PidsLimit: profile.PidsLimit,
            Timeout: profile.Timeout)
        {
            ReadOnlyRootFilesystem = profile.ReadOnlyRootFilesystem,
            TmpfsMounts = profile.TmpfsMounts,
        };

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "script");
    }
}

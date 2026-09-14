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
        var env = new Dictionary<string, string>(profile.Environment)
        {
            ["OKF_PARAMS_JSON"] = JsonSerializer.Serialize(bound.Values),
        };

        // ToRunSpec points TMPDIR at the first tmpfs mount (unless the host set one), so
        // a script's own temp files land in the scratch the host named rather than in a
        // /tmp that may be read-only. OKF_PARAMS_JSON is this executor's own and wins
        // over any host value either way.
        var spec = profile.Isolation.ToRunSpec(profile.Image, [profile.Interpreter, "-"], bound.BoundText ?? "", env, profile.NetworkMode);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "script");
    }
}

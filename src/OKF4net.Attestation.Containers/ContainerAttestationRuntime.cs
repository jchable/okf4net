// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// The library's entry point: an <see cref="IAttestationRuntime"/> for one
/// bundle <c>runtime</c> name, backed entirely by containers. A host
/// constructs one per profile and registers it in
/// <c>AttestationRuntimeRegistry</c> — see the design doc's "Bundle de
/// validation" section for a worked example.
/// </summary>
public sealed class ContainerAttestationRuntime : IAttestationRuntime
{
    /// <summary>
    /// Wires <paramref name="profile"/>'s executor (chosen by
    /// <see cref="ContainerRuntimeProfile.Kind"/>) and the shared
    /// <see cref="AllowlistParameterBinder"/>, plus a <see cref="ContainerAttester"/>
    /// configured by <paramref name="attesterOptions"/> (defaulted when
    /// omitted — the attester's image is never <paramref name="profile"/>'s).
    /// </summary>
    public ContainerAttestationRuntime(IContainerEngine engine, ContainerRuntimeProfile profile, ContainerAttesterOptions? attesterOptions = null)
    {
        Binder = new AllowlistParameterBinder();
        Executor = profile.Kind switch
        {
            ContainerRuntimeKind.Script => new ScriptComputationExecutor(engine, profile),
            ContainerRuntimeKind.SqlClient => new SqlClientComputationExecutor(engine, profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "unknown ContainerRuntimeKind"),
        };
        Attester = new ContainerAttester(engine, attesterOptions ?? new ContainerAttesterOptions());
    }

    /// <inheritdoc />
    public IParameterBinder Binder { get; }

    /// <inheritdoc />
    public IComputationExecutor Executor { get; }

    /// <inheritdoc />
    public IAttester Attester { get; }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation;

/// <summary>
/// A fixed, host-supplied dictionary of runtimes, keyed by exact runtime
/// name (§10.2 <c>runtime</c> field).
/// </summary>
/// <param name="runtimes">The runtimes to register, keyed by name.</param>
public sealed class AttestationRuntimeRegistry(IReadOnlyDictionary<string, IAttestationRuntime> runtimes)
    : IAttestationRuntimeRegistry
{
    /// <inheritdoc />
    public bool TryGet(string runtime, out IAttestationRuntime? found)
    {
        if (runtime is not null && runtimes.TryGetValue(runtime, out var r))
        {
            found = r;
            return true;
        }

        found = null;
        return false;
    }
}

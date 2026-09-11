// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The single <see cref="IParameterBinder"/> shared by every
/// <see cref="ContainerRuntimeKind"/>. Never edits
/// <see cref="BoundComputation.BoundText"/> — the sanctioned computation
/// text (SQL placeholders included) is carried through byte-for-byte. Its
/// only job is filtering and type-checking the supplied values against the
/// concept's declared <c>parameters</c> (see
/// <see cref="Internal.DeclaredParameterFilter"/>); dialect-specific
/// transport (JSON for a script, native driver parameters for SQL) is the
/// executor's job (the executors), not the binder's.
/// </summary>
public sealed class AllowlistParameterBinder : IParameterBinder
{
    /// <inheritdoc />
    public ValueTask<BoundComputation> BindAsync(
        AttestedComputationContract contract,
        SanctionedComputation computation,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        var filtered = DeclaredParameterFilter.FilterAndTypeCheck(values, contract.Parameters);
        var bound = new BoundComputation(contract.Runtime ?? "", computation.InlineCode, null, filtered);
        return ValueTask.FromResult(bound);
    }
}

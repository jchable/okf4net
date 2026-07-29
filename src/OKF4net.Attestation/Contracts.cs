// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation;

/// <summary>
/// Binds parameter values into a sanctioned computation for a specific
/// runtime (§10.2/§10.3), producing an opaque executable artifact.
/// </summary>
public interface IParameterBinder
{
    /// <summary>
    /// Binds <paramref name="values"/> into <paramref name="computation"/>
    /// for the runtime named by <paramref name="contract"/>.
    /// </summary>
    /// <param name="contract">The §10.2 contract projected from the concept's frontmatter.</param>
    /// <param name="computation">The sanctioned computation: inline code, or (for a file source) its already-read text.</param>
    /// <param name="values">The parameter values supplied for this run.</param>
    /// <param name="cancellationToken">A token to cancel the binding.</param>
    ValueTask<BoundComputation> BindAsync(
        AttestedComputationContract contract,
        SanctionedComputation computation,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes a bound computation artifact and produces a receipt.</summary>
public interface IComputationExecutor
{
    /// <summary>
    /// Executes <paramref name="bound"/>, producing a <see cref="Receipt"/>
    /// shaped by the contract's <c>executor.receipt</c> field list.
    /// </summary>
    /// <param name="bound">The artifact produced by <see cref="IParameterBinder.BindAsync"/>.</param>
    /// <param name="contract">The §10.2 contract projected from the concept's frontmatter.</param>
    /// <param name="cancellationToken">A token to cancel the execution.</param>
    ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs deterministic (non-LLM) verification of an execution's receipt,
/// given the full attestation context.
/// </summary>
public interface IAttester
{
    /// <summary>
    /// Verifies <paramref name="context"/>, returning a pass/fail verdict.
    /// </summary>
    /// <param name="context">The full context of the run being attested.</param>
    /// <param name="cancellationToken">A token to cancel the attestation.</param>
    ValueTask<AttestationVerdict> AttestAsync(
        AttestationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The binder/executor/attester triplet a host registers for one runtime
/// name (e.g. <c>bigquery</c>, <c>python</c>, <c>Looker</c>).
/// </summary>
public interface IAttestationRuntime
{
    /// <summary>The parameter binder for this runtime.</summary>
    IParameterBinder Binder { get; }

    /// <summary>The computation executor for this runtime.</summary>
    IComputationExecutor Executor { get; }

    /// <summary>The attester for this runtime.</summary>
    IAttester Attester { get; }
}

/// <summary>Resolves an <see cref="IAttestationRuntime"/> by runtime name (exact match).</summary>
public interface IAttestationRuntimeRegistry
{
    /// <summary>
    /// Looks up the runtime registered under <paramref name="runtime"/>.
    /// </summary>
    /// <param name="runtime">The runtime name from the concept's <c>runtime</c> field.</param>
    /// <param name="found">The registered runtime, if any; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a runtime is registered under that exact name.</returns>
    bool TryGet(string runtime, out IAttestationRuntime? found);
}

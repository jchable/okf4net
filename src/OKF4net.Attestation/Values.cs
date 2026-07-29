// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation;

/// <summary>
/// An artifact bound by an <see cref="IParameterBinder"/>: opaque to
/// OKF4net (the binder produces it, the executor of the same runtime host
/// consumes it).
/// </summary>
/// <param name="Runtime">The runtime name this artifact was bound for.</param>
/// <param name="BoundText">The computation with values bound, if textual (e.g. SQL) — supports §10.5(a).</param>
/// <param name="Payload">An optional runtime-specific carrier.</param>
/// <param name="Values">The parameter values that were bound.</param>
public sealed record BoundComputation(
    string Runtime,
    string? BoundText,
    object? Payload,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>Proof of one run, shaped by the contract's <c>executor.receipt</c> field list.</summary>
/// <param name="Fields">The receipt's named fields.</param>
public sealed record Receipt(IReadOnlyDictionary<string, object?> Fields);

/// <summary>The verdict an <see cref="IAttester"/> renders for one run.</summary>
/// <param name="Passed"><see langword="true"/> if the receipt was verified.</param>
/// <param name="Detail">An optional human-readable explanation.</param>
public readonly record struct AttestationVerdict(bool Passed, string? Detail);

/// <summary>
/// The full context handed to an <see cref="IAttester"/> (decision 8,
/// §10.5(a)(b)): everything it might need to verify a receipt.
/// </summary>
/// <param name="Contract">The §10.2 contract projected from the concept's frontmatter.</param>
/// <param name="Computation">The sanctioned computation that was run.</param>
/// <param name="Bound">The bound artifact that was executed.</param>
/// <param name="Values">The parameter values supplied for this run.</param>
/// <param name="Receipt">The receipt produced by the executor.</param>
public sealed record AttestationContext(
    AttestedComputationContract Contract,
    SanctionedComputation Computation,
    BoundComputation Bound,
    IReadOnlyDictionary<string, object?> Values,
    Receipt Receipt);

/// <summary>Whether a concept's lifecycle admits it as fresh, stale, or undetermined, under the gating policy (§10.6).</summary>
public enum StaleState
{
    /// <summary>The concept's lifecycle is fresh under the applicable policy.</summary>
    Fresh,

    /// <summary>The concept's lifecycle is stale under the applicable policy.</summary>
    Stale,

    /// <summary>Staleness could not be determined (e.g. no lifecycle information).</summary>
    Unknown,
}

/// <summary>
/// The gated result of one attested computation run. Never thrown for an
/// expected failure — errors-as-data.
/// </summary>
/// <param name="Displayable"><see langword="true"/> only when <see cref="ReceiptShapeOk"/> and the verdict passed and staleness is admitted.</param>
/// <param name="Verdict">The attester's verdict, if attestation was reached.</param>
/// <param name="Receipt">The receipt produced by the executor, if execution was reached.</param>
/// <param name="ReceiptShapeOk"><see langword="true"/> if every field named in <c>executor.receipt</c> is present in <see cref="Receipt"/>.</param>
/// <param name="Stale">The concept's staleness under the applicable gating policy.</param>
/// <param name="Reasons">Explains everything that kept the result from being <see cref="Displayable"/>.</param>
/// <param name="Error">A binder/executor/attester exception that was captured, if any.</param>
public sealed record AttestationOutcome(
    bool Displayable,
    AttestationVerdict? Verdict,
    Receipt? Receipt,
    bool ReceiptShapeOk,
    StaleState Stale,
    IReadOnlyList<string> Reasons,
    Exception? Error);

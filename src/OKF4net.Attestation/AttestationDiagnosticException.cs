// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation;

/// <summary>
/// A stage failure whose message was written by an OKF4net component — a
/// binder rejecting a value's type, an executor that could not parse a
/// receipt, a container that exited non-zero — and therefore carries no host
/// secret. <see cref="AttestationOrchestrator"/> renders the
/// <see cref="Exception.Message"/> of this type into
/// <see cref="AttestationOutcome.Reasons"/>; every other exception is reported
/// by type name only, because a host-plugged runtime's message can name a
/// connection string, a query, or the row it choked on. A host runtime that
/// wants its own diagnosis to reach the model derives from this type and
/// takes responsibility for what the message contains.
/// </summary>
public class AttestationDiagnosticException : Exception
{
    /// <summary>Creates the exception with a message safe to render to a model.</summary>
    public AttestationDiagnosticException(string message) : base(message) { }

    /// <summary>Creates the exception with a message safe to render to a model and the exception that caused it.</summary>
    public AttestationDiagnosticException(string message, Exception? innerException) : base(message, innerException) { }
}

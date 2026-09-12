// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// A container run failed to produce a usable result (non-zero exit,
/// malformed JSON, missing prerequisite). The <see cref="Exception.Message"/>
/// itself — "SQL wrapper exited with code 1", "script stdout was not a JSON
/// object" — is authored by this library and safe for the orchestrator to
/// render into <c>AttestationOutcome.Reasons</c> (see
/// <see cref="AttestationDiagnosticException"/>); <see cref="Stdout"/> and
/// <see cref="Stderr"/> are not, and stay host-side-only via
/// <c>AttestationOutcome.Error</c>.
/// </summary>
public sealed class ContainerExecutionException : AttestationDiagnosticException
{
    /// <summary>Creates the exception with the captured stdout/stderr.</summary>
    public ContainerExecutionException(string message, string stdout, string stderr)
        : base(message)
    {
        Stdout = stdout;
        Stderr = stderr;
    }

    /// <summary>The container's captured (size-bounded) standard output.</summary>
    public string Stdout { get; }

    /// <summary>The container's captured (size-bounded) standard error.</summary>
    public string Stderr { get; }
}

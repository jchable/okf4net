// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// A container run failed to produce a usable result (non-zero exit,
/// malformed JSON, missing prerequisite). The exception message and
/// <see cref="Stdout"/>/<see cref="Stderr"/> may carry bundle-derived detail
/// (a query, a stack trace) — safe only for host-side inspection via
/// <c>AttestationOutcome.Error</c>, never for the model-facing <c>Reasons</c>
/// list, which the orchestrator populates from this exception's TYPE alone.
/// </summary>
public sealed class ContainerExecutionException : Exception
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

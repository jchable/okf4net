// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

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
    /// <summary>
    /// How much of each captured stream <see cref="ToString"/> keeps. The end is kept, not
    /// the start: a traceback puts the actual error on its last lines.
    /// </summary>
    private const int ToStringTailChars = 4096;

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

    /// <summary>
    /// The usual exception text followed by the tail of each non-empty captured stream, so
    /// a host that logs <c>AttestationOutcome.Error</c> sees why the container failed, not
    /// only that it did. <see cref="Exception.Message"/> is left as the throw site wrote it.
    /// Same host-side-only caveat as <see cref="Stdout"/>/<see cref="Stderr"/>.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder(base.ToString());
        AppendTail(sb, "stderr", Stderr);
        AppendTail(sb, "stdout", Stdout);
        return sb.ToString();
    }

    private static void AppendTail(StringBuilder sb, string name, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        sb.Append('\n').Append("--- container ").Append(name);
        if (text.Length > ToStringTailChars)
        {
            sb.Append(" (last ").Append(ToStringTailChars).Append(" of ").Append(text.Length).Append(" characters)");
            text = text[^ToStringTailChars..];
        }

        sb.Append(" ---\n").Append(text);
    }
}

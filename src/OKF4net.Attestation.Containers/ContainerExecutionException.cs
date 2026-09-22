// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Attestation;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// A container run failed to produce a usable result (non-zero exit,
/// malformed JSON, missing prerequisite). The <see cref="Exception.Message"/>
/// itself — "SQL wrapper exited with code 1", "script stdout was not a JSON
/// object" — is authored by this library: it never interpolates a captured
/// stream (the one parser-authored text it may carry is a
/// <c>JsonException</c>'s message), and may be rendered by the orchestrator into
/// <c>AttestationOutcome.Reasons</c> (see
/// <see cref="AttestationDiagnosticException"/>). <see cref="Stdout"/> and
/// <see cref="Stderr"/> — and therefore <see cref="ToString"/>, which appends
/// their tails — are host-side only: they reach a host through
/// <c>AttestationOutcome.Error</c> and never reach the model.
/// </summary>
public sealed class ContainerExecutionException : AttestationDiagnosticException
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

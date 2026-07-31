// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Validation;

/// <summary>The result of validating a bundle: rendered diagnostic lines plus error/warning counts.</summary>
public sealed record ValidationOutcome(int ErrorCount, int WarningCount, IReadOnlyList<string> DiagnosticLines)
{
    /// <summary>True if there are no errors (warnings do not affect conformance).</summary>
    public bool IsConformant => ErrorCount == 0;
}

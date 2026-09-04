// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Which parts of a repository's code an extraction run should cover. Declared in Task 1 so that
/// <see cref="CodeGraphBuilder.Build"/> can take it from the start; Task 4 gives it real behaviour
/// (via <c>FileEligibility</c>) -- Task 1 threads it through without acting on it.
/// </summary>
public sealed record ScopeOptions(bool IncludeTests, bool IncludeInternal)
{
    /// <summary>Tests and internal-only symbols excluded (both <see langword="false"/>).</summary>
    public static ScopeOptions Default { get; } = new(false, false);
}

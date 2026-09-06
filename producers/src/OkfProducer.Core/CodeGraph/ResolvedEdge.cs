// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>How confidently a <see cref="CallSite"/> was resolved to a target symbol.</summary>
public enum EdgeConfidence
{
    /// <summary>No resolver owned this call site's file, or none could match it.</summary>
    Unresolved,

    /// <summary>Matched a target by name alone, with no type/overload information.</summary>
    ByName,

    /// <summary>
    /// Matched a target with full symbol/type information (e.g. a Roslyn binding).
    ///
    /// <para><b>What the BINDING settled and what this edge can carry are not the same thing.</b>
    /// <see cref="ResolvedEdge"/> names its target by <c>(TargetContainer, TargetName)</c>, and §3.2
    /// deliberately merges a method's overloads into ONE concept -- so an exact binding to
    /// <c>Register(Scanner)</c> and one to <c>Register(Scanner, string)</c> arrive at the same key and
    /// point at the same concept. That is correct given the merge, and it is also the honest limit of
    /// this value: <c>Exact</c> says the resolver knew which declaration it meant, not that the bundle
    /// can express which one.</para>
    ///
    /// <para>An explicitly implemented interface member no longer collapses this way -- the extractor
    /// names it apart from a public member of the same name -- so overload sets are the residual case,
    /// and the only one.</para>
    /// </summary>
    Exact,
}

/// <summary>A <see cref="CallSite"/> paired with its resolution verdict.</summary>
public sealed record ResolvedEdge(CallSite Site, string? TargetContainer, string? TargetName, EdgeConfidence Confidence);

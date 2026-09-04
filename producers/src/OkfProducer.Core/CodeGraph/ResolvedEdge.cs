// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>How confidently a <see cref="CallSite"/> was resolved to a target symbol.</summary>
public enum EdgeConfidence
{
    /// <summary>No resolver owned this call site's file, or none could match it.</summary>
    Unresolved,

    /// <summary>Matched a target by name alone, with no type/overload information.</summary>
    ByName,

    /// <summary>Matched a target with full symbol/type information (e.g. a Roslyn binding).</summary>
    Exact,
}

/// <summary>A <see cref="CallSite"/> paired with its resolution verdict.</summary>
public sealed record ResolvedEdge(CallSite Site, string? TargetContainer, string? TargetName, EdgeConfidence Confidence);

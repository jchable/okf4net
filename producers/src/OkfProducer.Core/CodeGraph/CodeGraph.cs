// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// The full code graph for a repository: every declared symbol and every resolved call edge.
/// Internally consistent by construction: no <see cref="ResolvedEdge"/> in <see cref="Edges"/> ever
/// references a symbol -- as caller or as resolved target -- absent from <see cref="Symbols"/>. An
/// edge whose caller was filtered out of scope (§5.4) is dropped entirely, since there is no concept
/// left to hang it on; an edge whose resolved target was filtered out degrades to
/// <see cref="EdgeConfidence.Unresolved"/> rather than pointing at a concept that will not exist,
/// which then renders as plain text per §4.5's fallback for an unresolved call.
/// </summary>
public sealed record CodeGraph(IReadOnlyList<SymbolFact> Symbols, IReadOnlyList<ResolvedEdge> Edges, RunStatus Status)
{
    /// <summary>
    /// How many declarations this run dropped because an enclosing TYPE capped them, though their own
    /// modifier was in scope -- a <c>public</c> member of an <c>internal</c> class, and everything
    /// nested below one.
    ///
    /// <para><b>It exists so the drop is not silent.</b> Scope filtering on effective visibility is
    /// correct -- C# caps a member at its container, so such a member is not reachable outside the
    /// assembly whatever its keyword says -- but it removes concepts an earlier version of this
    /// producer emitted, and a regeneration over an existing bundle PRUNES them. The writer's
    /// scope-narrowing guard cannot catch that: it compares the recorded scope FLAGS, and here the
    /// flags are identical on both sides. It is the rule that narrowed, not the run. Measured on a
    /// two-level fixture: five concepts deleted, nothing printed.</para>
    ///
    /// <para>Not on <see cref="RunStatus"/>, which is about FILES -- was each one visited, did it parse
    /// -- while this is about declarations inside files that were read perfectly well. An <c>init</c>
    /// property defaulting to zero for the same reason <see cref="SymbolFact.HeaderEndLine"/> is one:
    /// every fixture in this solution constructs a graph positionally, and zero means "nothing was
    /// capped", which is what those fixtures mean.</para>
    /// </summary>
    public int CappedByContainer { get; init; }
}

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
public sealed record CodeGraph(IReadOnlyList<SymbolFact> Symbols, IReadOnlyList<ResolvedEdge> Edges, RunStatus Status);

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>The full code graph for a repository: every declared symbol and every resolved call edge.</summary>
public sealed record CodeGraph(IReadOnlyList<SymbolFact> Symbols, IReadOnlyList<ResolvedEdge> Edges, RunStatus Status);

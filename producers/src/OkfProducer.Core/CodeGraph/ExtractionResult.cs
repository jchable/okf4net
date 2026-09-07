// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>What one file yielded: its declarations, its call sites, and how the extraction went.</summary>
public sealed record ExtractionResult(
    IReadOnlyList<SymbolFact> Symbols,
    IReadOnlyList<CallSite> Sites,
    FileStatus Status);

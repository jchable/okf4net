// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Resolves call sites to their target symbols for the files it owns. Resolvers are chained, not
/// exclusive (§2.1): a name-matching resolver can give every language a baseline verdict, while a
/// language-specific resolver (e.g. Roslyn) overrides it, at call-site identity, for the files it owns.
/// </summary>
public interface ISymbolResolver
{
    /// <summary>Whether this resolver can produce verdicts for call sites found in <paramref name="relativePath"/>.</summary>
    bool Owns(string relativePath);

    /// <summary>Resolves <paramref name="sites"/> against the known <paramref name="symbols"/>.</summary>
    IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols);
}

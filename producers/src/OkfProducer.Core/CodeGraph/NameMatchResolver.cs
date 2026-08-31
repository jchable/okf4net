// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// The language-agnostic baseline resolver (§2.1): matches a call site to a declared symbol by name
/// alone, with no type or overload information. <see cref="Owns"/> always returns
/// <see langword="true"/> -- it owns every file, giving every language a baseline verdict that a
/// later language-specific resolver (e.g. a Roslyn-based one) can override, at call-site identity,
/// for the files it owns more precisely.
///
/// <para>
/// Where a called name has exactly one declaration among <see cref="SymbolFact"/>s, this resolver
/// matches it with <see cref="EdgeConfidence.ByName"/>. Where it has more than one -- or none at all
/// -- it deliberately stays <see cref="EdgeConfidence.Unresolved"/> rather than guess: a spike
/// measured 38-39% of internal call edges as inter-type ambiguous this way (e.g. <c>Equals</c> across
/// seven types, <c>Get</c> across three), so this is the common case, not a rare corner. A wrong
/// attribution would render as a confident link to the wrong concept; an unresolved call renders as
/// honest plain text (§4.5). A name with zero declarations in the repository is the ordinary case for
/// a BCL or NuGet call and is likewise left <see cref="EdgeConfidence.Unresolved"/>.
/// </para>
/// </summary>
public sealed class NameMatchResolver : ISymbolResolver
{
    /// <inheritdoc />
    public bool Owns(string relativePath) => true;

    /// <inheritdoc />
    public IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols)
    {
        // A lookup dictionary, never iterated into the output -- only indexed by each site's called
        // name below. The output's order is entirely determined by `sites`, which is already a list.
        var declarationsByName = new Dictionary<string, List<SymbolFact>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (!declarationsByName.TryGetValue(symbol.Name, out var declarations))
            {
                declarations = [];
                declarationsByName[symbol.Name] = declarations;
            }

            declarations.Add(symbol);
        }

        var edges = new List<ResolvedEdge>(sites.Count);
        foreach (var site in sites)
        {
            if (declarationsByName.TryGetValue(site.CalledName, out var declarations) && declarations.Count == 1)
            {
                var target = declarations[0];
                edges.Add(new ResolvedEdge(site, target.Container, target.Name, EdgeConfidence.ByName));
            }
            else
            {
                edges.Add(new ResolvedEdge(site, TargetContainer: null, TargetName: null, EdgeConfidence.Unresolved));
            }
        }

        return edges;
    }
}

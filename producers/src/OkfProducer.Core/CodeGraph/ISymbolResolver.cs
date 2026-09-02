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

    /// <summary>
    /// Resolves <paramref name="sites"/> against the known <paramref name="symbols"/>.
    ///
    /// <para>
    /// <paramref name="symbols"/> is every symbol the run extracted, NOT the
    /// <see cref="ScopeOptions"/>-filtered subset that ends up in <see cref="CodeGraph.Symbols"/>:
    /// an out-of-scope declaration still competes for a called name here, so an implementation
    /// deciding ambiguity by counting declarations counts the ones the source really has. On the
    /// <see cref="ScopeOptions.IncludeInternal"/> axis that gives a real property -- narrowing
    /// visibility scope cannot turn an unresolved call into a resolved one, because the resolver
    /// sees past that filter. An implementation must therefore NOT treat a target's presence
    /// in <paramref name="symbols"/> as a promise that a concept will exist for it --
    /// <see cref="CodeGraphBuilder.Build"/> degrades an edge whose resolved target falls outside
    /// scope back to <see cref="EdgeConfidence.Unresolved"/> afterwards (§4.5).
    /// </para>
    ///
    /// <para>
    /// <b>That property holds on the visibility axis only, and an implementer must not read it more
    /// widely.</b> "Every symbol the run extracted" is exactly that -- not every symbol the
    /// repository declares. A file that <see cref="FileEligibility.IsEligible"/> rejects is never
    /// opened, so its declarations never enter <paramref name="symbols"/> at all: flipping
    /// <see cref="ScopeOptions.IncludeTests"/> off genuinely removes competitors, and a call that
    /// two declarations made ambiguous CAN become unambiguous, and so resolved, when one of them
    /// lived in a test project. The same is true of every declaration in a file skipped for size,
    /// depth, encoding or unreadability, and of one dropped by an extractor that could not establish
    /// its container (see <c>FileStatus.PartiallyExtracted</c>). Ambiguity counted here is ambiguity
    /// within what this run could see.
    /// </para>
    /// </summary>
    IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols);
}

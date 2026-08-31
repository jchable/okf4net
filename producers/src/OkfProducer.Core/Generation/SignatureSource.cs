// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The mechanical fallback link in <see cref="DescriptionResolver"/>'s chain, reached whenever
/// <see cref="DocCommentSource"/> found nothing. This is the common path, not the rare one: this
/// repository's own near-universal XML-doc coverage is an artefact of it enforcing doc comments, and
/// on an ordinary repository the ratio is inverted, so most generated descriptions come from here.
///
/// Rather than restate <see cref="SymbolFact.Name"/> on its own -- a sentence with nothing in it a
/// reader didn't already know from the concept's title -- this source builds its sentence around
/// <see cref="SymbolFact.Signature"/>: trustworthy for every C# member shape, properties included
/// (fixed and pinned by tests in Task 3), so it can be folded into prose verbatim instead of being
/// re-parsed for a return type or parameter list. The result reads, e.g., "public int Scan(string
/// path), a member of Scanner, declared in Scanner.cs." -- visibility, shape, and location a reader
/// gains something from, not a restatement of the identifier.
///
/// Labels its result <c>generated</c>: the slot a later LLM enrichment step is meant to fill instead
/// (§4.2). Until that step exists, this source is what re-derives that slot on every run.
/// </summary>
public sealed class SignatureSource : IDescriptionSource
{
    /// <summary>The <c>description_source</c> label this source writes.</summary>
    public const string SourceLabel = "generated";

    /// <inheritdoc/>
    /// <remarks>
    /// Never returns <see langword="null"/>: as the chain's terminal, mechanical fallback, it always
    /// has something to say -- falling back to <see cref="SymbolFact.Name"/> itself in the
    /// (currently unreachable, since every extractor populates it) case where
    /// <see cref="SymbolFact.Signature"/> is blank.
    /// </remarks>
    public (string Text, string Source)? Describe(SymbolFact fact)
    {
        var subject = string.IsNullOrWhiteSpace(fact.Signature) ? fact.Name : fact.Signature;

        var kindNoun = fact.Kind switch
        {
            SymbolKind.Type => "type",
            SymbolKind.Namespace => "namespace",
            _ => "member",
        };

        // "of" for a member ("a member of Scanner"); "in" for a type or namespace ("a type in N") --
        // the two prepositions C# usage actually calls for, not a language-neutral compromise.
        var preposition = fact.Kind == SymbolKind.Member ? "of" : "in";
        var owner = string.IsNullOrEmpty(fact.Container) ? string.Empty : $" {preposition} {fact.Container}";

        return ($"{subject}, a {kindNoun}{owner}, declared in {fact.RelativePath}.", SourceLabel);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// The per-language configuration an <see cref="ILanguageExtractor"/> extracts with: which grammar
/// to parse with, which queries pick out declarations and calls, how to recognize a doc comment,
/// and which file extensions this profile applies to. Only Task 3 constructs a real instance (e.g.
/// a C# profile); Task 1 defines the shape and the two language-aware behaviours every later task
/// calls through it.
/// </summary>
public sealed record LanguageProfile(
    string Language,
    string GrammarName,
    string DeclarationQuery,
    string CallQuery,
    string DocCommentPrefix,
    IReadOnlyList<string> FileExtensions)
{
    /// <summary>
    /// Splits a container path into its hierarchical segments: <c>.</c> for the C#/Java namespace
    /// convention, <c>/</c> for a TypeScript/JavaScript module path. An empty container splits to
    /// an empty list rather than a list containing one empty segment.
    /// </summary>
    /// <remarks>
    /// Pinned for C# (<see cref="Language"/> <c>"csharp"</c>) by <c>LanguageProfileTests</c> and, via
    /// <c>CodeConceptIds.For</c>, by <c>CodeConceptIdsTests</c> -- a C# container is always a dotted
    /// namespace/type chain (e.g. <c>N.Outer.Inner</c>), produced by <c>TreeSitterExtractor</c> for
    /// every <see cref="SymbolFact"/> and <c>CallSite</c> it emits. The <c>/</c> branch for
    /// TypeScript/JavaScript-style module paths remains unexercised until a profile for those
    /// languages is built.
    /// </remarks>
    public IReadOnlyList<string> SplitContainer(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return [];
        }

        var separator = Language is "csharp" or "java" ? '.' : '/';
        return container.Split(separator, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Collapses a language's raw modifier text (e.g. <c>"public static"</c>, <c>"export"</c>) to
    /// <see cref="SymbolVisibility"/>'s three tiers, applying C#'s real access rules:
    ///
    /// <list type="bullet">
    /// <item><c>public</c> -&gt; <see cref="SymbolVisibility.Public"/>.</item>
    /// <item><c>protected internal</c> -&gt; <see cref="SymbolVisibility.Public"/>: the union of the
    /// two modifiers, visible to derived types outside the declaring assembly, so it crosses the
    /// same assembly boundary <see cref="SymbolVisibility.Public"/>'s definition names.</item>
    /// <item><c>internal</c> (and the TS/JS <c>export</c> keyword) -&gt;
    /// <see cref="SymbolVisibility.Internal"/>.</item>
    /// <item><c>protected</c> alone -&gt; <see cref="SymbolVisibility.Internal"/>: broader than a
    /// single declaring type (visible to derived types, possibly in another assembly via
    /// inheritance) but not the assembly-wide reach <see cref="SymbolVisibility.Internal"/> names
    /// either -- with only three tiers available, this is the nearer of the two.</item>
    /// <item><c>private protected</c> -&gt; <see cref="SymbolVisibility.Private"/>: the
    /// intersection of <c>private</c> and <c>protected</c>, narrower than plain <c>internal</c> or
    /// plain <c>protected</c>.</item>
    /// <item><c>private</c> -&gt; <see cref="SymbolVisibility.Private"/>.</item>
    /// <item>No explicit access modifier: C#'s default depends on what is being declared -- a
    /// namespace-scoped <paramref name="kind"/> of <see cref="SymbolKind.Type"/> defaults to
    /// <see cref="SymbolVisibility.Internal"/>; any other <paramref name="kind"/> (a type member)
    /// defaults to <see cref="SymbolVisibility.Private"/>. A *nested* type with no modifier is
    /// actually <c>private</c> in real C#, not <c>internal</c> -- this method does not special-case
    /// nesting and applies the namespace-scoped default uniformly to every <see cref="SymbolKind.Type"/>,
    /// which is the rule this method was specified against.</item>
    /// </list>
    ///
    /// Interface members carry no access modifier in C# source yet are implicitly <c>public</c>;
    /// that default is intentionally not applied here, because this method has no way to know
    /// whether <paramref name="kind"/>'s declaring type is an interface -- the caller
    /// (<c>TreeSitterExtractor</c>) resolves that by synthesizing <c>"public"</c> into
    /// <paramref name="modifiers"/> before calling this method for an unmodified interface member.
    /// </summary>
    /// <param name="modifiers">The declaration's raw modifier tokens, space-separated.</param>
    /// <param name="kind">
    /// Which of <see cref="SymbolFact"/>'s three <see cref="SymbolKind"/>s <paramref name="modifiers"/>
    /// was extracted from -- decides which default applies when no access modifier is present.
    /// </param>
    public SymbolVisibility VisibilityOf(string modifiers, SymbolKind kind)
    {
        var words = new HashSet<string>(modifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        var isPublic = words.Contains("public");
        var isPrivate = words.Contains("private");
        var isProtected = words.Contains("protected");
        var isInternal = words.Contains("internal") || words.Contains("export");

        if (isPublic)
        {
            return SymbolVisibility.Public;
        }

        if (isProtected && isInternal)
        {
            return SymbolVisibility.Public;
        }

        if (isPrivate && isProtected)
        {
            return SymbolVisibility.Private;
        }

        if (isInternal)
        {
            return SymbolVisibility.Internal;
        }

        if (isProtected)
        {
            return SymbolVisibility.Internal;
        }

        if (isPrivate)
        {
            return SymbolVisibility.Private;
        }

        return kind == SymbolKind.Type ? SymbolVisibility.Internal : SymbolVisibility.Private;
    }
}

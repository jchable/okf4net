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
    /// Provisional: no profile constructed by Task 1 exercises this beyond the literal
    /// <c>"csharp"</c> string used by <c>CodeGraphBuilderTests</c>. Task 3 (the real C# profile)
    /// and whichever task adds Java/TS/JS profiles are what settle the exact <see cref="Language"/>
    /// string values this switches on; until then, treat the separator choice as a guess, not a
    /// pinned contract.
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
    /// <see cref="SymbolVisibility"/>'s three tiers. Recognizes <c>public</c> as
    /// <see cref="SymbolVisibility.Public"/>; <c>internal</c>, <c>protected internal</c>, and the
    /// TS/JS <c>export</c> keyword as <see cref="SymbolVisibility.Internal"/>; anything else
    /// (including no modifiers at all) as <see cref="SymbolVisibility.Private"/> -- the safer
    /// default when a modifier set isn't recognized.
    /// </summary>
    /// <remarks>
    /// Provisional: this word list is not pinned by any test or spec citation in Task 1. Task 3
    /// (the real C# profile) is what exercises this against actual tree-sitter/Roslyn modifier
    /// text and should replace or extend the vocabulary rather than assume it's settled.
    /// </remarks>
    public SymbolVisibility VisibilityOf(string modifiers)
    {
        var words = modifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Contains("public", StringComparer.Ordinal))
        {
            return SymbolVisibility.Public;
        }

        if (words.Contains("internal", StringComparer.Ordinal) || words.Contains("export", StringComparer.Ordinal))
        {
            return SymbolVisibility.Internal;
        }

        return SymbolVisibility.Private;
    }
}

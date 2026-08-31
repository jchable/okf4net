// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The first link in <see cref="DescriptionResolver"/>'s chain: whenever the extraction stage found a
/// doc comment (<see cref="SymbolFact.DocComment"/> -- already reduced to a C# <c>///</c> comment's
/// <c>&lt;summary&gt;</c> text, or the whole comment when there is no <c>&lt;summary&gt;</c> element,
/// by <c>TreeSitterExtractor</c>; this source does no further parsing of it), it wins outright: the
/// code is the source of truth for what a doc comment says, so an author improving the comment later
/// should have that improvement propagate on the next <c>generate</c> rather than being masked by a
/// stale description. Labels its result <c>doc-comment</c> so <see cref="DescriptionResolver"/>
/// re-derives it on every run instead of treating it as a human edit that must be preserved.
/// </summary>
public sealed class DocCommentSource : IDescriptionSource
{
    /// <summary>The <c>description_source</c> label this source writes.</summary>
    public const string SourceLabel = "doc-comment";

    /// <inheritdoc/>
    public (string Text, string Source)? Describe(SymbolFact fact) =>
        string.IsNullOrWhiteSpace(fact.DocComment) ? null : (fact.DocComment.Trim(), SourceLabel);
}

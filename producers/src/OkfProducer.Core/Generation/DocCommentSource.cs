// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The first link in <see cref="DescriptionResolver"/>'s chain: whenever the extraction stage found a
/// doc comment (<see cref="SymbolFact.DocComment"/> -- already reduced to a C# <c>///</c> comment's
/// <c>&lt;summary&gt;</c> text, or the whole comment when there is no <c>&lt;summary&gt;</c> element,
/// by <c>TreeSitterExtractor</c>), it wins outright: the code is the source of truth for what a doc
/// comment says, so an author improving the comment later should have that improvement propagate on
/// the next <c>generate</c> rather than being masked by a stale description. Labels its result
/// <c>doc-comment</c> so <see cref="DescriptionResolver"/> re-derives it on every run instead of
/// treating it as a human edit that must be preserved.
///
/// <para>The one transformation it applies is <see cref="UnwrapXmlDocTags"/>: the <i>inline</i> XML
/// doc tags that survive the extractor's <c>&lt;summary&gt;</c> unwrapping are reduced to the text
/// they contain. A <c>description</c> is prose a reader sees, and it is indexed by the bundle's
/// full-text search at twice the weight of the body -- so <c>Dependency-free scanner for inline
/// &lt;c&gt;[text](dest)&lt;/c&gt; links</c> is both ugly and slightly harmful, and in a repository
/// that enforces XML docs it is the norm rather than the exception.</para>
/// </summary>
public sealed class DocCommentSource : IDescriptionSource
{
    /// <summary>The <c>description_source</c> label this source writes.</summary>
    public const string SourceLabel = "doc-comment";

    /// <summary>
    /// The attributes a self-closing tag can carry a displayable name in, in the order they are tried.
    /// <c>cref</c> first because <c>&lt;see cref="..."/&gt;</c> is the common case; <c>href</c> last
    /// because a bare <c>&lt;see href="..."/&gt;</c> with no text is the rarest.
    /// </summary>
    private static readonly string[] NameCarryingAttributes = ["cref", "name", "langword", "href"];

    /// <inheritdoc/>
    public (string Text, string Source)? Describe(SymbolFact fact)
    {
        if (string.IsNullOrWhiteSpace(fact.DocComment))
        {
            return null;
        }

        var text = UnwrapXmlDocTags(fact.DocComment).Trim();

        // Unwrapping can empty a comment that was nothing but tags (`<inheritdoc/>` alone is the real
        // case). An empty description is not a description: fall through to the next source in the
        // chain rather than returning one, exactly as a missing doc comment does.
        return text.Length == 0 ? null : (text, SourceLabel);
    }

    /// <summary>
    /// Reduces inline XML doc tags to the text they stand for. <b>Unwraps, never strips:</b> the
    /// content of a tag is usually the subject of the sentence around it, so deleting it would leave
    /// prose that has lost its subject -- a worse outcome than the raw markup this fixes.
    ///
    /// <list type="bullet">
    /// <item><c>&lt;c&gt;x&lt;/c&gt;</c>, and any other paired tag, becomes its inner text.</item>
    /// <item><c>&lt;see cref="T:Foo.Bar"/&gt;</c> becomes <c>Foo.Bar</c> -- the documentation-comment ID
    /// prefix (<c>T:</c>, <c>M:</c>, <c>P:</c>, <c>F:</c>, <c>E:</c>, <c>N:</c>) is dropped, since it is
    /// compiler bookkeeping and not part of the name a reader knows.</item>
    /// <item><c>&lt;paramref name="x"/&gt;</c> and <c>&lt;typeparamref name="x"/&gt;</c> become
    /// <c>x</c>; <c>&lt;see langword="null"/&gt;</c> becomes <c>null</c>.</item>
    /// <item>A self-closing tag with none of those attributes contributes nothing; an unrecognised
    /// paired tag still contributes its inner text.</item>
    /// <item>A <c>&lt;</c> that does not begin a tag -- <c>a &lt; b</c>, or an unterminated tag at the
    /// end of the text -- is emitted verbatim. Doc comments are untrusted input (§2.3), and eating
    /// prose that merely looks like markup is the failure this guards against.</item>
    /// </list>
    ///
    /// <para>Hand-written rather than a regex, for the reason this producer hand-writes its other
    /// parsers: a backtracking pattern over attacker-influenced input is a liability, and the rules
    /// above are clearer as a scan than as an expression. XML entities (<c>&amp;lt;</c>) are
    /// deliberately left encoded: decoding them would put <c>&lt;</c> characters back into the text
    /// this method exists to take them out of.</para>
    ///
    /// <para>This runs <b>before</b> <c>ConceptGenerator</c>'s markdown-link neutralization, and the
    /// order is load-bearing: unwrapping <c>&lt;c&gt;[text](dest)&lt;/c&gt;</c> exposes link syntax
    /// that was previously hidden inside a tag, and it must reach the guard rather than slip past it.
    /// It does, because this runs at description-derivation time and that runs at body-render time.</para>
    /// </summary>
    internal static string UnwrapXmlDocTags(string comment)
    {
        if (!comment.Contains('<', StringComparison.Ordinal))
        {
            return comment;
        }

        var result = new StringBuilder(comment.Length);
        var i = 0;

        while (i < comment.Length)
        {
            var c = comment[i];
            if (c != '<' || !StartsTag(comment, i))
            {
                result.Append(c);
                i++;
                continue;
            }

            var close = comment.IndexOf('>', i + 1);
            if (close < 0)
            {
                // Unterminated: the rest is prose, not markup.
                result.Append(comment, i, comment.Length - i);
                break;
            }

            result.Append(Substitution(comment[(i + 1)..close]));
            i = close + 1;
        }

        return CollapseWhitespaceRuns(result.ToString());
    }

    /// <summary>
    /// Whether the <c>&lt;</c> at <paramref name="index"/> opens something tag-shaped: a name, or a
    /// closing <c>/</c>. Anything else (<c>a &lt; b</c>, <c>&lt;=</c>) is prose.
    /// </summary>
    private static bool StartsTag(string comment, int index)
    {
        if (index + 1 >= comment.Length)
        {
            return false;
        }

        var next = comment[index + 1];
        return next == '/' || char.IsAsciiLetter(next);
    }

    /// <summary>
    /// What one tag's inner markup (everything between <c>&lt;</c> and <c>&gt;</c>) contributes to the
    /// text. Empty for every tag that carries no name of its own; the tag's <i>content</i> is not read
    /// here at all -- it is simply left in the stream, which is what makes an unrecognised paired tag
    /// degrade to its inner text for free.
    /// </summary>
    private static string Substitution(string markup)
    {
        if (markup.StartsWith('/'))
        {
            return string.Empty;
        }

        // Only a self-closing tag stands in for a name: `<see cref="X">text</see>` has text of its own,
        // and emitting X as well would say it twice.
        if (!markup.EndsWith('/'))
        {
            return string.Empty;
        }

        foreach (var attribute in NameCarryingAttributes)
        {
            if (AttributeValue(markup, attribute) is { } value)
            {
                return attribute == "cref" ? StripDocIdPrefix(value) : value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The value of <paramref name="attribute"/> in one tag's markup, or <see langword="null"/> when it
    /// is absent or unquoted. Matched on <c>name="</c> preceded by whitespace or the tag name, so
    /// <c>name</c> does not also match the <c>name</c> inside <c>typeparamname</c>.
    /// </summary>
    private static string? AttributeValue(string markup, string attribute)
    {
        var needle = attribute + "=\"";
        var start = markup.IndexOf(needle, StringComparison.Ordinal);
        while (start > 0)
        {
            if (char.IsWhiteSpace(markup[start - 1]))
            {
                var valueStart = start + needle.Length;
                var end = markup.IndexOf('"', valueStart);
                return end < 0 ? null : markup[valueStart..end];
            }

            start = markup.IndexOf(needle, start + 1, StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>
    /// Drops a documentation-comment ID prefix (<c>T:</c>, <c>M:</c>, ...) from a <c>cref</c> value.
    /// Only those exact one-letter prefixes: a <c>cref</c> such as <c>System.Uri</c> has no prefix, and
    /// cutting at any <c>:</c> would mangle it.
    /// </summary>
    private static string StripDocIdPrefix(string cref) =>
        cref.Length > 2 && cref[1] == ':' && "TMPFEN".Contains(cref[0], StringComparison.Ordinal)
            ? cref[2..]
            : cref;

    /// <summary>
    /// Collapses the whitespace runs unwrapping leaves behind -- a <c>&lt;para&gt;</c> that sat between
    /// two spaces becomes two adjacent spaces once it is gone.
    ///
    /// <para>A no-op on everything else: <c>TreeSitterExtractor</c> already joins a doc comment's lines
    /// and collapses their whitespace before this source ever sees it, so this cleans up only what this
    /// method itself created. It is not a second, competing normalization of the comment.</para>
    /// </summary>
    private static string CollapseWhitespaceRuns(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}

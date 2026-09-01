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

        // Unwrap, THEN decode -- the reverse order is the natural-looking mistake and it is wrong.
        // Decoding first would turn an author's `&lt;c&gt;` (an escaped literal they wrote deliberately,
        // meaning the characters `<c>`) into something the unwrapper would then eat as a tag. In this
        // order a decoded `<` is only ever a character: every tag is already gone.
        var text = DecodeXmlEntities(UnwrapXmlDocTags(fact.DocComment)).Trim();

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
    /// <item>A <c>&lt;</c> that does not begin a tag -- <c>a &lt; b</c>, an unterminated tag at the end
    /// of the text, or an opening tag nothing closes -- is emitted verbatim. Doc comments are untrusted
    /// input (§2.3), and eating prose that merely looks like markup is the failure this guards
    /// against.</item>
    /// <item>A <c>&lt;![CDATA[...]]&gt;</c> section contributes its content, delimiters dropped and the
    /// content copied through <b>unread</b>: that is what a CDATA section means, and it is the one
    /// construct in a doc comment that says "the markup inside me is text".</item>
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

        var tokens = ScanTokens(comment);
        var result = new StringBuilder(comment.Length);
        var next = 0;
        var i = 0;

        while (i < comment.Length)
        {
            if (next < tokens.Count && tokens[next].Start == i)
            {
                var token = tokens[next];
                next++;
                i = token.End + 1;

                if (token.Shape == TokenShape.Cdata)
                {
                    // Content only, copied through unread: everything inside a CDATA section is text by
                    // definition, so it is neither scanned for tags nor eligible to become one.
                    result.Append(comment, token.ContentStart, token.ContentLength);
                    continue;
                }

                if (token.IsTag)
                {
                    result.Append(Substitution(token.Markup));
                }
                else
                {
                    // Tag-shaped but nothing ever closes it, so it is not a tag: `List<T> of results` is
                    // an unescaped generic, invalid XML that no compiler complains about unless doc files
                    // are emitted -- and this producer runs on arbitrary repositories, not only careful
                    // ones. Eating it would delete `T` and leave "a List of results", which is precisely
                    // the "prose that merely looks like markup" failure this method claims not to commit.
                    result.Append(comment, token.Start, token.End - token.Start + 1);
                }

                continue;
            }

            result.Append(comment[i]);
            i++;
        }

        return CollapseWhitespaceRuns(result.ToString());
    }

    /// <summary>
    /// Every tag-shaped span in <paramref name="comment"/>, in order, each already marked as real markup
    /// or as prose that merely looks like it.
    ///
    /// <para><b>Why a pass of its own, rather than deciding tag-by-tag while emitting.</b> Whether an
    /// opening tag is real depends on what comes <i>after</i> it, and the cheap way to ask -- search the
    /// rest of the comment for <c>&lt;/name&gt;</c> -- answers a different question than the one that
    /// matters. An unbounded forward search pairs an earlier unescaped generic against a <b>later,
    /// unrelated</b> tag of the same name: on <c>List&lt;T&gt; and &lt;T&gt;content&lt;/T&gt;</c> it
    /// declares the <c>&lt;T&gt;</c> of <c>List&lt;T&gt;</c> to be markup and deletes it, which is the
    /// failure this whole method exists to avoid, committed in the general case while the simple one
    /// (a name with no closer anywhere) looked fixed.</para>
    ///
    /// <para>So the pairing is done the only way that is actually about <i>this</i> tag: a stack, matched
    /// to the <b>nearest</b> unmatched opener of the same name, exactly as a parser would. Openers left
    /// on the stack at the end are prose. Openers a closer pops <i>over</i> -- crossed tags, which are
    /// malformed either way -- stay markup, so a doc comment that is merely badly nested still degrades
    /// to its inner text rather than sprouting visible angle brackets.</para>
    ///
    /// <para>An unmatched <i>closing</i> tag is still dropped, and the asymmetry is deliberate: a closer
    /// has no content to sever from the sentence around it, so dropping it cannot commit the failure
    /// above, while keeping it would put raw markup back into prose the extractor already unwrapped.</para>
    /// </summary>
    private static List<Token> ScanTokens(string comment)
    {
        var tokens = new List<Token>();
        var open = new List<int>();
        var i = 0;

        while (i < comment.Length)
        {
            if (comment[i] != '<')
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(comment, i, CdataOpen, 0, CdataOpen.Length) == 0)
            {
                var contentStart = i + CdataOpen.Length;
                var end = comment.IndexOf(CdataClose, contentStart, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Unterminated, exactly as an unterminated tag is: with no `]]>` there is no section,
                    // so the rest is prose and is copied through rather than eaten.
                    break;
                }

                tokens.Add(new Token(i, end + CdataClose.Length - 1, string.Empty, TokenShape.Cdata, string.Empty)
                {
                    IsTag = true,
                    ContentStart = contentStart,
                    ContentLength = end - contentStart,
                });

                i = end + CdataClose.Length;
                continue;
            }

            if (!StartsTag(comment, i))
            {
                i++;
                continue;
            }

            var close = comment.IndexOf('>', i + 1);
            if (close < 0)
            {
                // Unterminated: the rest is prose, not markup.
                break;
            }

            var markup = comment[(i + 1)..close];
            var shape = ShapeOf(markup);
            var token = new Token(i, close, markup, shape, NameOf(markup, shape));

            if (shape == TokenShape.SelfClosing)
            {
                token.IsTag = true;
            }
            else if (shape == TokenShape.Closing)
            {
                // Nearest unmatched opener of the same name; everything above it on the stack was
                // implicitly closed by it and stays markup.
                var match = open.FindLastIndex(index => string.Equals(tokens[index].Name, token.Name, StringComparison.Ordinal));
                if (match >= 0)
                {
                    for (var above = match; above < open.Count; above++)
                    {
                        tokens[open[above]].IsTag = true;
                    }

                    open.RemoveRange(match, open.Count - match);
                }

                // Matched or not, a closing tag contributes nothing either way -- see the remarks.
                token.IsTag = true;
            }
            else
            {
                open.Add(tokens.Count);
            }

            tokens.Add(token);
            i = close + 1;
        }

        return tokens;
    }

    /// <summary>The literal opening a CDATA section, which <see cref="StartsTag"/> deliberately does not recognise (<c>!</c> is neither <c>/</c> nor a letter).</summary>
    private const string CdataOpen = "<![CDATA[";

    /// <summary>The literal closing a CDATA section.</summary>
    private const string CdataClose = "]]>";

    /// <summary>What one <c>&lt;...&gt;</c> span is, before pairing decides whether it is markup at all.</summary>
    private enum TokenShape
    {
        /// <summary>An opening tag: real markup only if something closes it.</summary>
        Opening,

        /// <summary>A closing tag.</summary>
        Closing,

        /// <summary>A self-closing tag, which needs no partner.</summary>
        SelfClosing,

        /// <summary>A <c>&lt;![CDATA[...]]&gt;</c> section.</summary>
        Cdata,
    }

    /// <summary>One tag-shaped span: where it sits, what it says, and -- once <see cref="ScanTokens"/> has paired it -- whether it is markup or prose.</summary>
    private sealed class Token(int start, int end, string markup, TokenShape shape, string name)
    {
        /// <summary>Index of the opening <c>&lt;</c>.</summary>
        public int Start { get; } = start;

        /// <summary>Index of the last character of the span (the <c>&gt;</c>, or the <c>&gt;</c> of <c>]]&gt;</c>).</summary>
        public int End { get; } = end;

        /// <summary>Everything between <c>&lt;</c> and <c>&gt;</c>; empty for a CDATA section.</summary>
        public string Markup { get; } = markup;

        /// <summary>Which of the four shapes this span is.</summary>
        public TokenShape Shape { get; } = shape;

        /// <summary>The tag's name, with no attributes and no leading <c>/</c>; empty for a CDATA section.</summary>
        public string Name { get; } = name;

        /// <summary>Whether this span is real markup. False leaves it in the text verbatim.</summary>
        public bool IsTag { get; set; }

        /// <summary>Index of the first character inside a CDATA section.</summary>
        public int ContentStart { get; init; }

        /// <summary>Length of a CDATA section's content.</summary>
        public int ContentLength { get; init; }
    }

    /// <summary>Which shape one tag's inner markup is. Self-closing and closing are decided by shape alone; everything else opens.</summary>
    private static TokenShape ShapeOf(string markup) => markup.StartsWith('/')
        ? TokenShape.Closing
        : markup.EndsWith('/') ? TokenShape.SelfClosing : TokenShape.Opening;

    /// <summary>
    /// The name an opening or closing tag pairs on: everything up to the first whitespace, with a
    /// closing tag's <c>/</c> removed first. Attributes are not part of the name, and
    /// <c>&lt;see cref="X"&gt;</c> pairs with <c>&lt;/see&gt;</c>.
    /// </summary>
    private static string NameOf(string markup, TokenShape shape)
    {
        if (shape == TokenShape.SelfClosing)
        {
            return string.Empty;
        }

        var body = shape == TokenShape.Closing ? markup[1..].Trim() : markup;
        var nameLength = 0;
        while (nameLength < body.Length && !char.IsWhiteSpace(body[nameLength]))
        {
            nameLength++;
        }

        return body[..nameLength];
    }

    /// <summary>
    /// Decodes the five XML predefined entities, in one left-to-right pass. Run <b>after</b>
    /// <see cref="UnwrapXmlDocTags"/>: an <c>&amp;lt;</c> is not markup, it is the author's way of
    /// writing the literal character <c>&lt;</c>, and leaving it encoded means shipping
    /// <c>List&amp;lt;T&amp;gt;</c> in prose a human reads and that <c>ConceptSearch</c> indexes at
    /// twice the body's weight. Generic type names in a summary are ordinary in a repository that
    /// enforces XML docs.
    ///
    /// <para>A single pass, never a repeated one, which is what makes <c>&amp;amp;lt;</c> decode to
    /// <c>&amp;lt;</c> and stop there: the author escaped the ampersand, so the text they meant is
    /// <c>&amp;lt;</c>, and a second pass would silently turn their escaped text into the character it
    /// describes.</para>
    ///
    /// <para><b>No HTML guard here, deliberately.</b> A decoded <c>&lt;div&gt;</c> is raw HTML in a
    /// body, and this repository has already decided where that defense lives: the viewer sanitises the
    /// parsed DOM, and <c>CLAUDE.md</c> records that sanitiser as <i>the whole</i> defense rather than
    /// one layer, having measured and rejected upstream neutralisation as "defense in depth" -- it
    /// stopped nothing the sanitiser did not already stop, while silently deleting benign content. A
    /// second, weaker guard here would contradict a decision that was made with evidence.</para>
    /// </summary>
    private static string DecodeXmlEntities(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '&' && MatchEntity(text, i) is var (character, length))
            {
                result.Append(character);
                i += length;
                continue;
            }

            result.Append(text[i]);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// The character one of the five predefined entities at <paramref name="index"/> stands for and the
    /// length of its markup, or <see langword="null"/> for a bare <c>&amp;</c> that begins none of them
    /// (including a numeric entity, deliberately left alone).
    /// </summary>
    private static (char Character, int Length)? MatchEntity(string text, int index)
    {
        foreach (var (entity, character) in Entities)
        {
            if (string.CompareOrdinal(text, index, entity, 0, entity.Length) == 0)
            {
                return (character, entity.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// The five XML predefined entities. <c>&amp;amp;</c> is listed first so it wins the prefix race
    /// against nothing at all -- the others share no prefix with it -- and the order is fixed rather
    /// than incidental because a longest-match rule is what keeps <c>&amp;amp;lt;</c> from decoding twice.
    /// </summary>
    private static readonly (string Entity, char Character)[] Entities =
    [
        ("&amp;", '&'),
        ("&lt;", '<'),
        ("&gt;", '>'),
        ("&quot;", '"'),
        ("&apos;", '\''),
    ];

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

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
    /// <item>A <c>&lt;</c> that does not begin a tag is emitted verbatim: <c>a &lt; b</c>, an unterminated
    /// tag, an opening tag nothing closes, an opening tag a closer only pops <i>over</i> (the
    /// <c>&lt;T&gt;</c> of <c>&lt;c&gt;List&lt;T&gt;&lt;/c&gt;</c>), a span whose markup is not shaped
    /// like the tag it claims to be (<c>&lt;/b and c &gt;</c>), and a self-closing span whose name is not
    /// an XML name -- an angle-bracketed URL, <c>&lt;https://example.com/&gt;</c>, whose trailing slash
    /// made it a self-closing tag standing for nothing. Doc comments are untrusted input (§2.3), and
    /// eating prose that merely looks like markup is the failure this guards against.</item>
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
    internal static string UnwrapXmlDocTags(string comment) => UnwrapXmlDocTags(comment, out _);

    /// <summary>
    /// <see cref="UnwrapXmlDocTags(string)"/>, additionally reporting how many characters the scan
    /// examined looking for the ends of spans -- the sum, over every <see cref="FindTagEnd"/> call and
    /// every search for a <c>]]&gt;</c>, of the characters that search read.
    ///
    /// <para>It exists so the early-out in <see cref="ScanTokens"/> has a guard that can go red. That
    /// early-out is behaviour-neutral by construction, so no assertion about the <i>output</i> can
    /// distinguish its presence from its absence, and the only evidence it ever had was a wall-clock
    /// measurement in a comment -- which a later refactor deleting it would not disturb. This counts
    /// steps instead: it is a pure function of the input, identical on every machine and every build
    /// configuration, so a test can bound it without the flakiness a millisecond budget would bring.</para>
    /// </summary>
    internal static string UnwrapXmlDocTags(string comment, out long scanCost)
    {
        if (!comment.Contains('<', StringComparison.Ordinal))
        {
            scanCost = 0;
            return comment;
        }

        var tokens = ScanTokens(comment, out scanCost);
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
                    // Tag-shaped, but nothing its own closer ever paired with it, so it is not a tag:
                    // `List<T> of results` is an unescaped generic, invalid XML that no compiler complains
                    // about unless doc files are emitted -- and this producer runs on arbitrary
                    // repositories, not only careful ones. It reads the same whether the generic stands in
                    // open prose or inside `<c>...</c>`. Eating it would delete `T` and leave "a List of
                    // results", which is precisely the "prose that merely looks like markup" failure this
                    // method claims not to commit.
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
    /// to the <b>nearest</b> unmatched opener of the same name, exactly as a parser would. A closer marks
    /// <b>that opener and no other</b>. Openers left on the stack at the end are prose, and so are the
    /// openers a closer pops <i>over</i>.</para>
    ///
    /// <para><b>Why those popped-over openers are prose, which is not the obvious answer.</b> Reading them
    /// as "crossed tags, malformed either way, so keep them markup" tidies a badly nested comment -- and
    /// deletes <c>&lt;T&gt;</c> from <c>&lt;c&gt;List&lt;T&gt;&lt;/c&gt;</c>, because an unescaped generic
    /// inside a matched pair <i>is</i> an opener the closer pops over. That is the same prose-deletion
    /// failure as above, in a strictly more reachable shape: it needs only a generic mentioned inside
    /// <c>&lt;c&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c> or <c>&lt;see&gt;</c>, which is the commonest
    /// way a doc comment names one, where genuinely interleaved
    /// <c>&lt;a&gt;&lt;b&gt;&lt;/a&gt;&lt;/b&gt;</c> is rare. The price is a visible <c>&lt;b&gt;</c> on
    /// input that was malformed anyway, and angle brackets are worth strictly less than a deleted word.
    /// The popped openers do leave the stack, so a later closer cannot reach back and claim one -- that
    /// would be the unbounded forward search again, by another route.</para>
    ///
    /// <para>An unmatched <i>closing</i> tag is still dropped, and the asymmetry is deliberate: a closer
    /// has no content to sever from the sentence around it, so dropping it cannot commit the failure
    /// above, while keeping it would put raw markup back into prose the extractor already unwrapped.
    /// That drop is what makes <see cref="IsTagShaped"/> load-bearing rather than pedantic: a span is
    /// only a tag if its markup is shaped like one, or <c>a &lt;/b and c &gt; d</c> is a closing tag that
    /// contributes nothing and takes <c>and c</c> with it. <see cref="FindTagEnd"/> is the same idea at
    /// the other end -- a <c>&gt;</c> inside a quoted attribute value does not terminate a tag.</para>
    /// </summary>
    private static List<Token> ScanTokens(string comment, out long scanCost)
    {
        var tokens = new List<Token>();
        var open = new List<int>();
        var cost = 0L;

        // Every span this method can recognise -- a tag or a CDATA section alike -- ends at a `>`, so once
        // the last one is behind us nothing ahead can be one. Leaving is not an optimisation of the
        // output (with no `>` left, every `<` from here would fail FindTagEnd and be emitted verbatim
        // anyway, which is exactly what leaving does); it is a bound on the WORK, and the bound is the
        // point: FindTagEnd scans to the end of the string when it fails, and a failure advances by one
        // character rather than abandoning the comment, so a suffix of `<a<a<a...` with no `>` at all
        // re-entered that end-to-end scan once per bracket. That is quadratic in a doc comment's length,
        // on input a hostile repository chooses (§2.3). See the remarks on FindTagEnd for the two shapes
        // this does NOT bound and why.
        //
        // Because it changes no output, deleting this check breaks nothing an assertion about text can
        // see -- so the guard is `scanCost`, and the test is
        // The_scan_stops_re_reading_a_comment_once_no_bracket_can_end_a_tag, which bounds the characters
        // this scan reads at four per character of the comment. That is a step count, not a duration:
        // wall-clock budgets in a suite are their own defect, and the earlier 803ms-to-1ms measurement,
        // true as it was, lived only in this comment and could not go red.
        var lastEnd = comment.LastIndexOf('>');
        var i = 0;

        while (i < comment.Length)
        {
            if (comment[i] != '<')
            {
                i++;
                continue;
            }

            if (lastEnd <= i)
            {
                break;
            }

            if (string.CompareOrdinal(comment, i, CdataOpen, 0, CdataOpen.Length) == 0)
            {
                var contentStart = i + CdataOpen.Length;
                var end = comment.IndexOf(CdataClose, contentStart, StringComparison.Ordinal);
                cost += (end < 0 ? comment.Length : end + CdataClose.Length) - contentStart;
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

            var close = FindTagEnd(comment, i + 1);
            cost += (close < 0 ? comment.Length : close + 1) - (i + 1);
            if (close < 0)
            {
                // No complete span starts here -- nothing terminates it, or a quoted attribute value ran
                // off the end. Prose, and only this one character of it: the scan resumes at the next
                // character rather than abandoning the rest of the comment, so a well-formed tag further
                // on is still unwrapped.
                i++;
                continue;
            }

            var markup = comment[(i + 1)..close];
            var shape = ShapeOf(markup);
            if (!IsTagShaped(markup, shape))
            {
                // Not a tag, so it is emitted verbatim -- and the scan resumes at Start + 1, not at
                // End + 1, so the bytes inside the rejected span are read again and can become tokens of
                // their own. What that rescan is allowed to do is the safety question for the whole
                // IsTagShaped change, and the answer is narrower than the obvious one.
                //
                // Call a span a DELTA span when this predicate rejects it and the pre-IsNameShaped
                // predicate accepted it: exactly a self-closing span whose name is not an XML name, plus a
                // closing span with an empty name (`</>`). WHAT IS GUARANTEED, and the quantifier is the
                // whole of it: if EVERY delta span in the comment has markup containing no `<`, the change
                // is one-way -- every character the old code emitted is still emitted. Read per-span the
                // sentence would say nothing, because one comment can hold a `<`-free delta span next to
                // one that has a `<`, and what that second span is then free to do is the paragraph below.
                // Under the universal the argument runs per span and composes: the only `<` in the span is
                // the one at Start, so the rescan finds no token before End (which is a `>`) and arrives
                // at End + 1 with `open` holding exactly what the old scan held there -- a self-closing
                // token is never pushed, and an empty-name closer's FindLastIndex cannot match, since
                // StartsTag makes every opener's name at least one character -- so from End + 1 the two
                // runs classify every token identically. (Old's token LIST carries one extra entry, the
                // delta span's own; `open` stores indices into that list, but each run indexes its own
                // consistently.) The span's old contribution was
                // Substitution(markup) -- empty, or one attribute value, which is a substring of its own
                // markup -- and its new contribution is the whole span verbatim. `</>` satisfies the
                // condition by construction and so does every angle-bracketed URL, which are the two cases
                // this predicate exists for.
                //
                // WHAT IS NOT GUARANTEED, stated because the stronger claim was made here once and was
                // false. A delta span that DOES contain a `<` can hand the rescan a token that reaches
                // outside the bytes the old code was deleting, in three independent ways:
                //
                //   * an inner CLOSING tag reaches open.FindLastIndex and can mark a surviving OUTER
                //     opener as markup, deleting it: `<b/ x=y>keep<a+</b/>` was `<b/ x=y>keep` and is now
                //     `keep<a+`;
                //   * an inner `<![CDATA[` can pair with a `]]>` lying AFTER End and swallow it:
                //     `<a<![CDATA[x/> keep ]]> tail` was `keep ]]> tail` and is now `<ax/> keep tail`;
                //   * with an odd number of `"` between the inner `<` and End, FindTagEnd skips End and
                //     terminates later, so even the inner token's own bytes fall outside the old span:
                //     `A <a" b=c<d "e/> hello k=1 "x/> tail.` was `A hello k=1 "x/> tail.` and is now
                //     `A <a" b=c tail.`, losing the word `hello`.
                //
                // All three need markup both versions already mangle. How reachable they are depends
                // entirely on what you sample: 3 non-monotone strings in 4,635,516 drawn character by
                // character, but 40,834 in 3,000,000 built by concatenating tag fragments -- which is why
                // "a random sweep found none" is not the argument here. The condition above is, and it
                // held on all 776,797 differing inputs across those sweeps whose delta spans contained no
                // `<`, with no exception. Restoring monotonicity in general would cost the angle-bracketed
                // URL, which is a real doc comment; this is the trade.
                i++;
                continue;
            }

            var token = new Token(i, close, markup, shape, NameOf(markup, shape));

            if (shape == TokenShape.SelfClosing)
            {
                token.IsTag = true;
            }
            else if (shape == TokenShape.Closing)
            {
                // The nearest unmatched opener of the same name, and only it -- see the remarks for why
                // the openers it pops over stay prose.
                var match = open.FindLastIndex(index => string.Equals(tokens[index].Name, token.Name, StringComparison.Ordinal));
                if (match >= 0)
                {
                    tokens[open[match]].IsTag = true;
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

        scanCost = cost;
        return tokens;
    }

    /// <summary>
    /// The index of the <c>&gt;</c> that ends the tag beginning at <paramref name="start"/>, or
    /// <c>-1</c> when none does.
    ///
    /// <para>Double-quoted attribute values are skipped, because a <c>&gt;</c> inside one does not end
    /// anything: stopping at the first <c>&gt;</c> cuts <c>&lt;see cref="a&gt;b"&gt;</c> in half, and the
    /// front half is then an opener that the real <c>&lt;/see&gt;</c> pairs with and deletes, leaving the
    /// back half (<c>b"&gt;</c>) standing in the prose. Only <c>"</c> is tracked, which is the one quote
    /// <see cref="AttributeValue"/> reads and the one XML doc comments use.</para>
    ///
    /// <para><b>Single quotes are legal XML and are still not tracked, so the residual is stated instead
    /// of implied:</b> <c>&lt;see cref='a&gt;b'&gt;tagged&lt;/see&gt;</c> splits the way the double-quoted
    /// form used to, and yields <c>b'&gt;tagged</c>.</para>
    ///
    /// <para><b>What is established about the residual, and what is not.</b> Run span for span against a
    /// fully <c>'</c>-aware <see cref="FindTagEnd"/>, this version deletes a whole span that parse keeps on
    /// 2,781 of 335,923 exhaustive strings over <c>&lt; &gt; / ' = a</c>, on 22,627 of 3,000,000 random
    /// strings, and on 265,448 of 2,000,000 built by concatenating tag fragments and sentences. Every case
    /// inspected was tag-shaped markup, and the only text lost with it was letters standing inside that
    /// markup (<c>see</c>, <c>cref</c>) rather than words of the surrounding sentence -- so the comparative
    /// judgement below holds on the evidence there is. The categorical form of it, "what it costs is never
    /// a deleted word", is <b>not</b> established and is not claimed here: it is the same shape of claim as
    /// the monotonicity argument the rescan comment in <see cref="ScanTokens"/> had to retract.</para>
    ///
    /// <para><b>Why the residual is kept anyway, which is a comparison and not an absolute.</b> The obvious
    /// fix, opening a single-quoted value only on a <c>'</c> that follows an <c>=</c>, was written out and
    /// rejected: it can only move the terminating <c>&gt;</c> LATER, so a span can grow to swallow a whole
    /// nested tag, and if that grown span is then paired it deletes the prose inside it. On
    /// <c>&lt;b x='y&gt; hello &lt;c&gt;z&lt;/c&gt; w'&gt; more &lt;/b&gt;</c> that costs the words
    /// <c>hello</c> and <c>z</c>. The two failures are not symmetric: the residual leaks <c>b'&gt;</c> into
    /// a description that a human reviewing the bundle <b>sees</b>, while the fix deletes words
    /// <b>silently</b>, and a silent deletion is strictly worse than a visible leak.</para>
    ///
    /// <para><b>Cost.</b> A failing search walks to the end of the string, and its caller advances by one
    /// character and comes back, so a run of <c>&lt;</c> can re-enter it once per bracket.
    /// <see cref="ScanTokens"/> bounds the case where no <c>&gt;</c> remains at all -- the plain
    /// <c>&lt;a&lt;a&lt;a...</c> suffix -- to a single check. Two shapes stay quadratic in one comment's
    /// length: a run of <c>&lt;</c> sharing one far <c>&gt;</c> where each span is rejected by
    /// <see cref="IsTagShaped"/> (<c>&lt;a &lt;a &lt;a ... &gt;</c>), and a run whose only <c>&gt;</c> is
    /// quoted relative to each start (<c>&lt;a&lt;a&lt;a...&lt;a"&gt;</c>). Bounding those exactly is not
    /// a scalar memo: whether a <c>&gt;</c> terminates a span depends on the parity of the <c>"</c>
    /// between it and the start, so a result from one <c>&lt;</c> does not transfer to a later one, and
    /// the honest fix is an index of every <c>&gt;</c> bucketed by that parity. The cheap-looking
    /// alternative -- failing at an unquoted <c>&lt;</c>, which a tag's markup may not contain -- is
    /// linear and was also rejected: it changes which openers the stack pairs, and on
    /// <c>&lt;a&gt;keep&lt;a b=c&lt;d&gt;text&lt;/a&gt;</c> it deletes an <c>&lt;a&gt;</c> this version
    /// keeps. Neither is worth a behaviour change to this machine for a cost bounded by one doc comment.</para>
    /// </summary>
    private static int FindTagEnd(string comment, int start)
    {
        var inQuotes = false;

        for (var i = start; i < comment.Length; i++)
        {
            if (comment[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (comment[i] == '>' && !inQuotes)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether <paramref name="markup"/> is shaped like the tag <paramref name="shape"/> says it is: it
    /// carries a name, a closing tag is that name and nothing else, and an opening or self-closing tag is
    /// the name followed by an attribute list in which every attribute carries an <c>=</c>. A
    /// <b>self-closing</b> tag's name must additionally be shaped like an XML name
    /// (<see cref="IsNameShaped"/>).
    ///
    /// <para>The <i>shape</i> rules are <b>necessary</b> conditions of XML's grammar rather than a
    /// validator -- this accepts <c>&lt;b x=y z&gt;</c> -- so no well-formed tag is rejected by them. The
    /// attribute rule is checked because without it <c>a &lt;/b and c &gt; d</c> is one closing tag named
    /// <c>b</c>, a closing tag contributes nothing, and <c>and c</c> is deleted -- prose eaten by a span
    /// that merely starts like markup, which is the failure <see cref="UnwrapXmlDocTags"/> exists to
    /// prevent. The <b>name</b> rule is <i>not</i> a necessary condition and does reject a few well-formed
    /// names; <see cref="IsNameShaped"/> says which, and why that direction is the safe one.</para>
    ///
    /// <para><b>Why the name rule is asked of a self-closing tag alone</b>, which is the one asymmetry
    /// here. A self-closing span is the only shape that can delete itself on its own authority: it needs
    /// no partner anywhere in the comment, and its <see cref="Substitution"/> is empty unless it carries a
    /// name-bearing attribute. So <c>See &lt;https://example.com/&gt; for details.</c> -- an angle-bracketed
    /// URL, ordinary prose in a doc comment -- was whitespace-free, therefore tag-shaped, therefore a
    /// self-closing tag standing for nothing, and became <c>See for details.</c>; drop the trailing slash
    /// and the same URL survived as an unmatched opener, so the loss was inconsistent as well as silent.
    /// The other two shapes cannot commit it: an opener with a nonsense name is harmless, because nothing
    /// closes it and it stays prose, and a closer with one loses only its own brackets. Asking a name of
    /// them would also stop <c>&lt;K,V&gt;that&lt;/K,V&gt;</c> from unwrapping, which is a pair a reader
    /// wrote on purpose.</para>
    ///
    /// <para>An <b>empty</b> name is refused for every shape, which in practice only <c>&lt;/&gt;</c> has:
    /// no opening tag's name is empty (<see cref="StartsTag"/> requires a letter after the <c>&lt;</c>),
    /// so a span with an empty name can never have been anyone's partner.</para>
    /// </summary>
    private static bool IsTagShaped(string markup, TokenShape shape)
    {
        var body = (shape switch
        {
            TokenShape.Closing => markup[1..],
            TokenShape.SelfClosing => markup[..^1],
            _ => markup,
        }).Trim();

        var nameLength = 0;
        while (nameLength < body.Length && !char.IsWhiteSpace(body[nameLength]))
        {
            nameLength++;
        }

        if (nameLength == 0 || (shape == TokenShape.SelfClosing && !IsNameShaped(body, nameLength)))
        {
            return false;
        }

        return nameLength == body.Length
            || (shape != TokenShape.Closing && body.IndexOf('=', nameLength) >= 0);
    }

    /// <summary>
    /// Whether the first <paramref name="nameLength"/> characters of <paramref name="body"/> are all
    /// characters an XML name may contain: a letter or digit, or one of <c>_</c>, <c>-</c>, <c>.</c>,
    /// <c>:</c>.
    ///
    /// <para>Looser than XML's real name production on two points it costs nothing to allow: the first
    /// character is not required to be a letter, and Unicode letters and digits are taken wholesale.</para>
    ///
    /// <para><b>It is also stricter than that production, so "no well-formed tag is rejected by it" -- what
    /// this remark used to say -- is false.</b> XML's <c>NameChar</c> additionally admits U+00B7 and the
    /// ranges U+0300-U+036F and U+203F-U+2040, which are <c>Po</c>, <c>Mn</c> and <c>Pc</c> respectively
    /// and which <c>char.IsLetterOrDigit</c> refuses; a self-closing tag whose name carries a combining
    /// acute (U+0301) is well-formed and is now emitted verbatim rather than unwrapped. Its
    /// <c>NameStartChar</c> range U+10000-U+EFFFF is refused as well, and only half of the obvious reason
    /// applies. <see cref="StartsTag"/> does keep an astral code point out of the name-<b>start</b>
    /// position -- it requires an ASCII letter or <c>/</c> after the <c>&lt;</c>, so a span whose name
    /// begins with one never opens at all -- but it constrains that one character and no other. An
    /// astral code point in any <b>later</b> name position does reach this loop, as the two surrogate
    /// halves it is stored in, and <c>char.IsLetterOrDigit(char)</c> is false for each half on its own, so
    /// the name is refused. A self-closing span named <c>a</c> followed by U+10000 is therefore a
    /// well-formed XML element that this predicate rejects, and a <b>delta</b> span rather than prose: run
    /// against the predicate that shipped before this one it was accepted, <see cref="Substitution"/>
    /// found no name-carrying attribute and returned empty, and the whole span was deleted; run against
    /// this one it is emitted verbatim. Both of those runs were executed, not deduced.</para>
    ///
    /// <para><b>Not widened, deliberately.</b> What this costs is a visible pair of angle brackets on a
    /// tag a reader would rather not see; what widening it would buy is moving spans from "kept verbatim"
    /// to "deleted on their own authority", which is the direction that has produced a Critical in this
    /// file twice. The guarantee is therefore the <i>direction</i> and not the exactness: a span this
    /// rejects is emitted verbatim rather than deleted, whether it rejected it rightly or wrongly.</para>
    /// </summary>
    private static bool IsNameShaped(string body, int nameLength)
    {
        for (var i = 0; i < nameLength; i++)
        {
            var character = body[i];
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
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

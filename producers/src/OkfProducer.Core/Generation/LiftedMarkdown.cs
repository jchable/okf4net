// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Every markdown-text transform this producer applies to text it did not author: neutralizing link
/// syntax, escaping a leading block marker, defusing an unclosed fence, and building a code span or a
/// link label that the text inside cannot break out of.
///
/// <para><b>Lifted out of <see cref="ConceptGenerator"/> because its correctness argument is a
/// different argument.</b> Everything here is a pure function from string to string. Nothing in it
/// knows about a <c>CodeGraph</c>, a <c>Bundle</c>, or a <c>GenerateOptions</c>, and nothing needs to:
/// these transforms are decided entirely against ONE external contract -- <c>LinkScanner</c>'s
/// <c>CodeFreeLines</c>, <c>BlankInlineCode</c> and <c>ParseInlineLink</c> in <c>OKF4net</c>, which is
/// what will read the markdown this producer emits. That is why the escaping here deliberately mirrors
/// the consumer's rules rather than CommonMark's wherever the two differ, and each place it does says
/// so at the site.</para>
///
/// <para>The seam is worth having because of what kept going wrong on the other side of it. The
/// defects recorded in this neighbourhood are, repeatedly, one shape: <i>this code and
/// <c>Links.cs</c> disagree about one character class</i> -- a backslash before a backtick, a bracket
/// inside a code span, whitespace before a fence. Buried among two thousand lines of concept
/// assembly, the two rules could not be read side by side; in a file of their own, against a named
/// contract, they can. No behaviour changed in the move: the golden bundle is byte-identical across
/// it, which is the evidence that claim rests on.</para>
///
/// <para><c>internal</c> rather than public: this is <c>ConceptGenerator</c>'s text layer, not an API
/// this producer offers anyone. It is a seam for reading and for testing, not a published surface.</para>
/// </summary>
internal static class LiftedMarkdown
{
    /// <summary>
    /// Any text this producer <b>lifted from outside the bundle</b> and renders into a body -- a
    /// repository or package name, a README's own <c>#</c> heading, a <c>.csproj</c>
    /// <c>&lt;Description&gt;</c>, a doc comment -- with its markdown link syntax neutralized.
    ///
    /// <para><b>The test is where the text was authored FOR, not who typed it.</b> A NuGet
    /// <c>&lt;Description&gt;</c> and a README heading are written by humans, but for NuGet and for
    /// GitHub; nobody writing either means a link relative to a bundle that did not exist yet. So from
    /// the bundle's point of view they are derived, exactly as a doc comment is, and rendering
    /// <c>[docs](guide)</c> or <c>[Guide](docs/guide.md)</c> from one of them verbatim manufactures a
    /// link the author never wrote. Only text authored <i>in</i> the bundle is exempt, and
    /// <c>description_source</c> is what says so -- see <see cref="BodyDescription"/>.</para>
    ///
    /// <para>A no-op for text that cannot contain the syntax, which is most of it: a C# identifier, a
    /// dotted type name. Applied uniformly all the same, because a per-family exception is exactly how
    /// two concepts with identical text end up behaving differently.</para>
    /// </summary>
    internal static string LiftedBodyText(string text) => NeutralizeMarkdownLinks(text);

    /// <summary>
    /// <see cref="LiftedBodyText"/> for lifted text that <b>begins a paragraph</b> in the body: it
    /// additionally escapes a leading block-construct marker, so a description cannot silently stop
    /// being a paragraph and start being a heading, a list item or a block quote.
    ///
    /// <para><b>Bounded on purpose: one character, at the start of a line.</b> The only thing that can
    /// change a line's block type is its first non-space character -- <c>#</c>, <c>-</c>, <c>*</c>,
    /// <c>+</c>, <c>&gt;</c>, a run of 3+ backticks or tildes (a fenced code block), or a run of digits
    /// followed by <c>.</c> or <c>)</c>. This is deliberately not a general markdown escaper: everything
    /// further into the line is inline markup, which renders as the author's own emphasis and is none of
    /// this producer's business. CommonMark renders <c>\#</c> as <c>#</c>, so the reader sees exactly
    /// what was written.</para>
    ///
    /// <para><b>A marker only counts when a space, a tab or the line's end follows it</b> -- which is
    /// CommonMark's own rule and not a refinement of it. Escaping unconditionally would rewrite ordinary
    /// emphasis: <c>*fast* and small.</c> would become <c>\*fast* and small.</c> and render its asterisks
    /// literally, this guard corrupting the prose it exists to protect. <c>&gt;</c> and a fence are the
    /// exceptions the spec itself makes: <c>&gt;text</c> is a block quote with no space at all, and an
    /// info string may sit right against a fence (<c>```csharp</c>).</para>
    ///
    /// <para><b>An unbalanced fence has a consumer, not just a renderer, to answer to.</b> A leading
    /// ``` or <c>~~~</c> left undefused makes <see cref="OKF4net.LinkScanner"/>.<c>ExtractLinks</c> skip
    /// every line after it as code -- so a description whose fence is never closed (ordinary: it is only
    /// a description, not a whole document) silently hides the rest of this concept's own body, links
    /// included, from the very scanner that resolves them.</para>
    ///
    /// <para><b>Every line, not only the first.</b> A <c>.csproj</c> <c>&lt;Description&gt;</c> reaches
    /// a body with its newlines intact, so a <c>- </c> opening line 2 starts a list exactly as it would
    /// opening line 1 -- a guarantee stated for "the description" has to hold for all of it, or the
    /// documentation and the code disagree about which half is covered.</para>
    ///
    /// <para><b>For an ordered-list marker the delimiter is escaped, not the digit</b>, and the two are
    /// not interchangeable: <c>\1.</c> renders a literal backslash, because a backslash escape is only
    /// defined for ASCII punctuation, while <c>1\.</c> renders <c>1.</c> and forms no list.</para>
    ///
    /// <para>The sharpest case is <c>#</c>. A description that opens a heading is a rendering fault on
    /// its own, and <c>LinkScanner.ExtractCitations</c> reads a heading whose title is exactly
    /// <c>Citations</c> as §13.1's legacy citations marker -- a structural claim about the concept,
    /// made by text its author wrote for a compiler.</para>
    /// </summary>
    internal static string LiftedBodyParagraph(string text) => EscapeLeadingBlockMarker(LiftedBodyText(text));

    /// <summary>
    /// A <see cref="LiftedBodyParagraph(string)"/> that respects <paramref name="preservedSource"/>: text this
    /// producer derived is neutralized, text the author wrote in the bundle is left as written apart from
    /// an unclosed fence. The same rule <see cref="BodyDescription"/> applies to the <c>code/</c> family,
    /// keyed the same way, so two concepts with identical text cannot behave differently.
    /// </summary>
    internal static string LiftedBodyParagraph(string text, string? preservedSource) =>
        preservedSource is null ? LiftedBodyParagraph(text) : DefuseLeadingFenceOnly(text);

    /// <summary>
    /// Escapes the one character that would make <paramref name="text"/> open a markdown block, or
    /// returns it unchanged when it opens none. See <see cref="LiftedBodyParagraph"/> for the bound.
    /// </summary>
    private static string EscapeLeadingBlockMarker(string text)
    {
        // Every line, not only the first: a `.csproj` <Description> reaches a body with its newlines
        // intact, and a `- ` opening line 2 starts a list exactly as it would opening line 1. Splitting
        // on `\n` and keeping any `\r` with the line leaves the text byte-identical apart from the
        // escapes themselves.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = EscapeLineBlockMarker(lines[i]);
        }

        return string.Join('\n', lines);
    }

    private static string EscapeLineBlockMarker(string line)
    {
        var start = LeadingWhitespaceEnd(line);
        if (start >= line.Length)
        {
            return line;
        }

        // A block quote is the one marker CommonMark does not require a space after: `>text` quotes.
        if (line[start] == '>')
        {
            return line.Insert(start, "\\");
        }

        if (TryEscapeLeadingFence(line, start, out var fenced))
        {
            return fenced;
        }

        // A line that is nothing but one repeated marker is a thematic break (`---`, `***`, `___`) or a
        // setext underline (`---`, `===`), and a setext underline retitles the paragraph ABOVE it -- a
        // block-type change made to a line the guard already passed. It is checked before the bullet
        // rule because the space requirement below, correct for `- item`, is exactly what lets these
        // through.
        if (IsMarkerOnlyLine(line, start))
        {
            return line.Insert(start, "\\");
        }

        // A bullet or an ATX heading marker only opens a block when a space (or the line's end) follows
        // it. Without that check this escapes ordinary emphasis: `*fast* and small.` would become
        // `\*fast* and small.`, which renders the asterisks literally -- the method damaging the prose
        // it is here to protect.
        if (line[start] is '-' or '*' or '+')
        {
            return IsBlockMarkerBoundary(line, start + 1) ? line.Insert(start, "\\") : line;
        }

        if (line[start] == '#')
        {
            var hashes = start;
            while (hashes < line.Length && line[hashes] == '#')
            {
                hashes++;
            }

            // Seven or more `#` is not a heading at all, so there is nothing to defuse.
            return hashes - start <= 6 && IsBlockMarkerBoundary(line, hashes)
                ? line.Insert(start, "\\")
                : line;
        }

        if (!char.IsAsciiDigit(line[start]))
        {
            return line;
        }

        // An ordered-list marker: digits, then `.` or `)`, then the same boundary. The DELIMITER is
        // escaped, never the digit -- see this method's caller.
        var delimiter = start;
        while (delimiter < line.Length && char.IsAsciiDigit(line[delimiter]))
        {
            delimiter++;
        }

        return delimiter < line.Length
            && line[delimiter] is '.' or ')'
            && IsBlockMarkerBoundary(line, delimiter + 1)
                ? line.Insert(delimiter, "\\")
                : line;
    }

    /// <summary>
    /// The index of the first non-whitespace character in <paramref name="line"/> (or its length, if
    /// there is none), where <b>whitespace means <see cref="char.IsWhiteSpace(char)"/></b> -- NBSP, form
    /// feed, vertical tab and U+2028 included, not the <c>' '</c>/<c>'\t'</c> pair CommonMark allows
    /// before a block marker.
    ///
    /// <para><b>The consumer's definition is the authoritative one, and the consumer is
    /// <see cref="OKF4net.LinkScanner"/>.</b> Its <c>CodeFreeLines</c> decides a line opens a fenced code
    /// block by <c>TrimStart()</c>, which trims every <c>char.IsWhiteSpace</c>. So a description line
    /// beginning with an NBSP and then ``` was not defused here while it did open a fence there -- after
    /// which the scanner skipped every following line, this concept's own <c>## Contains</c> and
    /// <c>## Calls</c> included. Two definitions of "leading whitespace" is precisely how a branch gets
    /// severed with nothing dangling for <c>okf validate</c> to see, and the same reasoning that makes
    /// <see cref="NeutralizeMarkdownLinks"/> mirror <c>BlankInlineCode</c> rather than CommonMark applies
    /// here: this has to agree with the consumer, not with a spec.</para>
    ///
    /// <para>The wider class is safe for the markers that only affect <i>rendering</i>
    /// (<see cref="EscapeLineBlockMarker"/>'s others), because CommonMark does not admit them behind an
    /// NBSP either: the line was already a paragraph, and the escape it now gets renders identically to
    /// the character it protects (<c>\-</c> is <c>-</c>). It can add a redundant escape; it cannot
    /// change what a reader sees.</para>
    /// </summary>
    private static int LeadingWhitespaceEnd(string line)
    {
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        return start;
    }

    /// <summary>
    /// Escapes a leading run of 3+ backticks or tildes at <paramref name="start"/> -- a fenced code
    /// block opener -- and reports whether it did. CommonMark requires no following space (an info
    /// string may sit right against it, `` ```csharp ``), so this is unconditional, like a block quote.
    ///
    /// <para><b>Why an unbalanced fence is dangerous, not just ugly.</b> Left undefused, a fence with no
    /// matching close anywhere later in the same body -- ordinary when the text is only a description,
    /// not a whole document -- makes <see cref="OKF4net.LinkScanner"/>.<c>ExtractLinks</c> skip every
    /// line after it as code, silently hiding this concept's own outgoing links (<c>## Contains</c>,
    /// <c>## Calls</c>) from the very scanner that resolves them. Nothing dangles, so <c>okf validate</c>
    /// stays silent while the branch is simply severed -- which is why <see cref="BodyDescription"/>
    /// defuses this one marker even on text it otherwise leaves untouched (see its own remarks).</para>
    /// </summary>
    private static bool TryEscapeLeadingFence(string line, int start, out string escaped)
    {
        if (line[start] is '`' or '~')
        {
            var fenceMarker = line[start];
            var run = start;
            while (run < line.Length && line[run] == fenceMarker)
            {
                run++;
            }

            if (run - start >= 3)
            {
                escaped = line.Insert(start, "\\");
                return true;
            }
        }

        escaped = line;
        return false;
    }

    /// <summary>
    /// Whether position <paramref name="index"/> ends a block marker: a space, a tab, or the end of the
    /// line (a bare <c>-</c> on its own line is an empty list item). A <c>\r</c> counts as the end, so a
    /// CRLF-separated line behaves like an LF-separated one.
    /// </summary>
    private static bool IsBlockMarkerBoundary(string line, int index) =>
        index >= line.Length || line[index] is ' ' or '\t' or '\r';

    /// <summary>
    /// Whether every non-blank character from <paramref name="start"/> is the same one of
    /// <c>-</c>, <c>*</c>, <c>_</c>, <c>=</c> -- the shape of a thematic break or a setext underline.
    /// Requiring one repeated character is CommonMark's own rule and is what keeps this from firing on
    /// ordinary prose: <c>-*-</c> is neither construct and is left alone.
    /// </summary>
    private static bool IsMarkerOnlyLine(string line, int start)
    {
        var marker = line[start];
        if (marker is not ('-' or '*' or '_' or '='))
        {
            return false;
        }

        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (c is ' ' or '\t' or '\r')
            {
                continue;
            }

            if (c != marker)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>description</c> as it is rendered <b>into a body</b>: neutralized as
    /// <see cref="LiftedBodyText"/> when this producer derived it, and left exactly as the author wrote
    /// it when the author wrote it in the bundle.
    ///
    /// <para><b>The asymmetry is the whole rule.</b> A <c>[text](dest)</c> inside a C# doc comment is
    /// doc syntax that merely looks like markdown -- <c>LinkScanner</c>'s own summary in this repository
    /// contains the literal <c>&lt;c&gt;[text](dest)&lt;/c&gt;</c> -- and the author never meant a
    /// bundle link. Rendered verbatim it becomes one: measured, that single doc comment was the only
    /// broken link in a 649-concept bundle generated from this repository. The same syntax in a
    /// <c>manual</c> or <c>llm</c> description is a link the author meant <i>for this bundle</i>, and
    /// rewriting it would be editing a human's text.</para>
    ///
    /// <para><b>Keyed on <c>description_source</c>, which makes it consistent with §4.2's preservation
    /// table by construction rather than by coincidence:</b> the set neutralized here --
    /// <see cref="DocCommentSource.SourceLabel"/> and <see cref="SignatureSource.SourceLabel"/> -- is
    /// exactly the set <see cref="DescriptionResolver"/> re-derives, and everything it protects is
    /// left alone. The comparison is <see cref="StringComparison.Ordinal"/> against the two literals,
    /// where the resolver's own comparison is case-insensitive, and that is correct rather than a
    /// mismatch: this value is not read off disk, it is whatever <see cref="DescriptionResolver.Resolve"/>
    /// just returned, and a spelling variant of a derived label can only reach that return as a
    /// <i>preserved</i> value -- which must not be touched, and is not.</para>
    ///
    /// <para>The families with no <c>description_source</c> at all -- <c>packages/*</c>, <c>docs/*</c>,
    /// <c>overview</c> -- never reach this method: nothing in them is bundle-authored, so they go
    /// straight through <see cref="LiftedBodyText"/>.</para>
    ///
    /// <para><b>One exception to "left exactly as the author wrote it": a leading fence is defused
    /// regardless of provenance.</b> Every other marker only changes rendering, which is the author's
    /// choice to make. A fence is different in kind -- <see cref="TryEscapeLeadingFence"/>'s own remarks
    /// explain why -- and the danger it poses to <c>## Contains</c>/<c>## Calls</c> is the same whether
    /// the text came from a doc comment or was hand-typed straight into the bundle's frontmatter on a
    /// prior <c>--update</c>. So a bundle-authored description is defused for a fence and left untouched
    /// for everything else, rather than skipping <see cref="BodyDescription"/> entirely.</para>
    /// </summary>
    internal static string BodyDescription(string description, string descriptionSource) =>
        descriptionSource is DocCommentSource.SourceLabel or SignatureSource.SourceLabel
            ? LiftedBodyParagraph(description)
            : DefuseLeadingFenceOnly(description);

    /// <summary>
    /// Escapes the leading fenced-code-block marker that <b>never closes</b> -- nothing else, including
    /// every other block marker <see cref="EscapeLeadingBlockMarker"/> also handles, and including a
    /// fence that is part of a balanced pair. See <see cref="BodyDescription"/> for why a fence alone is
    /// defused on text this producer otherwise never touches.
    ///
    /// <para><b>Balanced pairs are left alone, and that is a correction rather than a relaxation.</b> The
    /// danger is not a fence, it is a fence <see cref="OKF4net.LinkScanner"/> never closes: on a balanced
    /// pair the scanner opens the block and closes it again, so every line after the close -- this
    /// concept's own <c>## Contains</c> and <c>## Calls</c> -- is scanned exactly as it would be with no
    /// fence at all. Escaping such a pair bought no structural property whatsoever, and it was not free:
    /// <c>\</c> before ``` renders as a literal backtick followed by a stray two-backtick span opener, so
    /// the escape destroyed the author's code block rather than blemishing it -- damage done to text this
    /// method's whole premise is that it must not edit.</para>
    ///
    /// <para><b>Which fence is the unclosed one is decided by replaying the scanner's own state machine,
    /// not by counting.</b> A <c>~~~</c> does not close a ``` block, so parity over all fence-shaped
    /// lines is the wrong question; and the replay has to be repeated until it settles, because escaping
    /// the unclosed opener puts the lines it was hiding back in front of the scanner and one of THEM can
    /// open a fence of its own (``` then <c>~~~</c> is the two-line case). Each pass escapes exactly one
    /// line and strictly reduces the number of fence-shaped lines, so it terminates.</para>
    /// </summary>
    internal static string DefuseLeadingFenceOnly(string text)
    {
        var lines = text.Split('\n');

        while (UnclosedFenceLine(lines) is { } index)
        {
            // The escape always lands: UnclosedFenceLine nominates a line only on the same 3+ run of the
            // same marker that TryEscapeLeadingFence tests for. Guarded all the same, because the loop's
            // termination rests on that agreement and a future edit to either could break it silently --
            // an unescaped fence is the bug this method already has; an infinite loop would be a new one.
            var start = LeadingWhitespaceEnd(lines[index]);
            if (!TryEscapeLeadingFence(lines[index], start, out var fenced))
            {
                break;
            }

            lines[index] = fenced;
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The index of the line that opens a fenced code block <see cref="OKF4net.LinkScanner"/> never
    /// closes, or <see langword="null"/> when every fence in <paramref name="lines"/> is balanced.
    ///
    /// <para>A transcription of <c>LinkScanner.CodeFreeLines</c>'s own loop, and deliberately so: the
    /// question is not what CommonMark makes of these lines, it is what the scanner that resolves this
    /// bundle's links does with them. Both marker characters are tracked separately, and a closer must
    /// repeat the marker its opener used.</para>
    /// </summary>
    private static int? UnclosedFenceLine(IReadOnlyList<string> lines)
    {
        char? fence = null;
        var opener = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var start = LeadingWhitespaceEnd(line);
            var marker = start < line.Length ? line[start] : '\0';
            var run = 0;
            while (start + run < line.Length && line[start + run] == marker)
            {
                run++;
            }

            var isFence = marker is '`' or '~' && run >= 3;

            if (fence is { } open)
            {
                if (isFence && marker == open)
                {
                    fence = null;
                }

                continue;
            }

            if (isFence)
            {
                fence = marker;
                opener = i;
            }
        }

        return fence is null ? null : opener;
    }

    /// <summary>
    /// Escapes every unescaped <c>]</c>, which neutralizes markdown link <i>syntax</i> while leaving
    /// every character the reader sees exactly where it was: CommonMark renders <c>\]</c> as <c>]</c>,
    /// so <c>[text](dest)</c> still reads as <c>[text](dest)</c>.
    ///
    /// <para><b>The closing bracket, and not the opening one</b>, because the opening one does not
    /// work: <c>LinkScanner.ScanLineLinks</c> dispatches on <c>[</c> with no look-back at all, so a
    /// preceding backslash does not stop it and <c>\[text](dest)</c> would render correctly while still
    /// being extracted as a link. Its <c>ParseInlineLink</c> does honour a backslash inside the link
    /// text, where it skips the next character -- so an escaped <c>]</c> never closes the text, the
    /// bracket depth never returns to zero, and the parse fails. Verified against the scanner itself in
    /// <c>CodeConceptGeneratorTests</c> rather than reasoned about here alone.</para>
    ///
    /// <para>An already-escaped character is copied through untouched rather than escaped twice, which
    /// would turn an invisible escape into a visible backslash.</para>
    ///
    /// <para><b>Inside an inline code span nothing is escaped at all</b>, and that is a correctness
    /// requirement rather than a nicety: CommonMark does not process backslash escapes inside a code
    /// span, so the backslash would simply be VISIBLE -- <c>YamlValue.cs</c>'s summary shipped as
    /// <c>A sequence (`[...\]` or block `- ...`)</c>, this method corrupting the very prose it exists to
    /// preserve. It is also provably unnecessary there: <c>LinkScanner.BlankInlineCode</c> blanks code
    /// spans before scanning, so a <c>]</c> inside one could never have produced a link. The backtick
    /// rule here therefore mirrors that method's exactly -- toggle on every backtick, not on runs --
    /// because the two must agree with each other, not with a spec.</para>
    /// </summary>
    private static string NeutralizeMarkdownLinks(string text)
    {
        if (!text.Contains(']', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        var inCodeSpan = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // A backtick toggles a code span, exactly as LinkScanner.BlankInlineCode does -- one
            // backtick, not a run, and with NO backslash awareness, because that is the rule the
            // consumer applies and this has to agree with the consumer, not with CommonMark. Honouring
            // `\` before a backtick desynchronises the two: on ``A \` b ` [text](dest).`` the scanner
            // closes its span at the second backtick and reads the rest as prose, while a producer that
            // skipped the first would still believe it was inside a span and ship a live link.
            if (c == '`')
            {
                inCodeSpan = !inCodeSpan;
                builder.Append(c);
                continue;
            }

            // And the flag resets at every newline, because BlankInlineCode is applied per line: an
            // unclosed backtick cannot make the NEXT line code. Carrying it across `\n` would leave a
            // multi-line description -- a `.csproj` <Description> reaches a body with its newlines
            // intact -- "inside a code span" for the whole rest of the text.
            if (c == '\n')
            {
                inCodeSpan = false;
                builder.Append(c);
                continue;
            }

            if (inCodeSpan)
            {
                builder.Append(c);
                continue;
            }

            // The double-escape guard, but never over a backtick: that character is the consumer's
            // state machine, and consuming it here is what caused the divergence described above.
            if (c == '\\' && i + 1 < text.Length && text[i + 1] != '`')
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == ']')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
    /// <summary>
    /// Wraps <paramref name="text"/> in a markdown code span that survives the text itself: the fence
    /// is one backtick longer than the longest backtick run inside, padded with spaces when the content
    /// starts or ends with a backtick, and control characters (a code span cannot contain a newline)
    /// are flattened to spaces. Signatures and called names come out of source files, which §2.3 treats
    /// as untrusted input -- a naive <c>$"`{text}`"</c> would let one of them close the span early and
    /// corrupt the rest of the document.
    /// </summary>
    internal static string CodeSpan(string text)
    {
        var flat = Flatten(text);
        var fence = new string('`', LongestBacktickRun(flat) + 1);
        var pad = flat.Length == 0 || flat[0] == '`' || flat[^1] == '`' ? " " : string.Empty;

        return fence + pad + flat + pad + fence;
    }

    private static string Flatten(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    /// <summary>
    /// Escapes the display half of a markdown link. Symbol names are identifiers in practice, but they
    /// reach here from untrusted source text (§2.3), and a stray <c>]</c> would end the link text early
    /// and turn the rest of the line into prose.
    ///
    /// <para><b>A backtick is replaced rather than escaped, and it is the link's survival that is at
    /// stake, not its rendering.</b> <see cref="OKF4net.LinkScanner"/>'s <c>BlankInlineCode</c> toggles a
    /// code span on <i>every</i> backtick and blanks to the end of the line, so a bullet whose label
    /// carries an ODD number of them has its own <c>](/id)</c> blanked before <c>ScanLineLinks</c> ever
    /// sees it: the link vanishes, the target file still exists, and nothing dangles, so
    /// <c>okf validate</c> stays silent while the branch is severed -- the same failure shape as an
    /// unbalanced fence, one character wide. Reachable through <c>overview</c>'s own children, since a
    /// doc concept's title is lifted verbatim from the README's <c>#</c> heading and an unbalanced
    /// backtick in one (<c>Migrating to `v2</c>) is an ordinary typo.</para>
    ///
    /// <para><b>Why <c>&amp;#96;</c> and not <c>\`</c>.</b> A backslash would be correct for a renderer --
    /// CommonMark escapes a backtick like any ASCII punctuation -- and useless here, because
    /// <c>BlankInlineCode</c> has no backslash awareness at all, deliberately (see
    /// <see cref="NeutralizeMarkdownLinks"/>, which mirrors that rule rather than CommonMark's for
    /// exactly this reason). Only a form carrying no backtick CHARACTER leaves the consumer's state
    /// machine alone, and a numeric character reference renders as the character the author wrote. The
    /// substitution is not applied to <see cref="CodeSpan"/>, where a backtick is content and the fence
    /// is widened around it instead.</para>
    /// </summary>
    internal static string LinkText(string text) =>
        Flatten(text).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "&#96;", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}

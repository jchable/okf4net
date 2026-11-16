// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using OKF4net.Internal;

namespace OKF4net.Agents.Internal;

/// <summary>
/// The closed set of multi-line values a tool result may carry verbatim
/// (<see cref="ModelTextBuilder.Block"/>). Each is written inside a backtick
/// fence longer than any backtick or tilde run in it
/// (<see cref="ModelTextBuilder.Fence"/>), so no line of the value can close
/// the block, whatever fence that line tries.
/// </summary>
internal enum BlockKind
{
    /// <summary>A concept's markdown body (<c>okf_read_concept</c>): info string <c>markdown</c>.</summary>
    ConceptBody,

    /// <summary>
    /// A computation's sanctioned source (<c>okf_get_computation</c>): no info
    /// string. Three backticks, as before, for a source that holds no fence run.
    /// </summary>
    ComputationSource,

    /// <summary>
    /// A directory's <c>index.md</c> (<c>okf_browse</c>): info string
    /// <c>markdown</c>, after a literal line saying whose index it is, so that
    /// the file can neither pass for the tool's own text nor leave a fence open.
    /// </summary>
    IndexText,
}

/// <summary>
/// The only way an agent tool (<c>OkfBundleTools</c>) or the context provider
/// (<c>OkfContextProvider</c>) builds model-facing text (#122, design
/// <c>docs/superpowers/specs/2026-11-16-model-facing-output-boundary-design.md</c>
/// §2.2).
///
/// <para><b>Holes.</b> The line methods take a <see cref="ModelLineHandler"/>,
/// an interpolated string handler whose literal parts — what the developer
/// wrote — are appended as written, and whose every hole is a VALUE
/// (<see cref="AppendValue"/>): folded by <see cref="ModelText.Fold"/> (a
/// non-string formatted under the invariant culture first), so it occupies
/// the one line it was given; and, when it begins a block (nothing but
/// whitespace and container markers — <c>- </c>, <c>* </c>, <c>+ </c>,
/// <c>1. </c>, <c>&gt; </c> — before it on its line), neutralised
/// (<see cref="Neutralise"/>), so it cannot start Markdown block syntax of its
/// own either. There is no overload taking a plain <see langword="string"/>: a
/// literal line is written <c>$"…"</c>, and a value that fills a line is
/// written <c>$"{value}"</c>, a hole like any other.</para>
///
/// <para>Writing a value raw takes <see cref="Block"/>, visible in a review
/// and limited to the closed <see cref="BlockKind"/> set, always fenced; a line
/// <c>AuditText</c> rendered (golden-locked for the CLI) enters through
/// <see cref="Rendered"/>, as an <see cref="AuditText.AuditLine"/> only
/// <c>AuditText</c> can produce; a context-provider element through
/// <see cref="ContextElement"/>.</para>
///
/// <para>The enumerating <c>ModelFacingBoundaryTests</c> is what proves the
/// boundary holds for every function <c>GetTools</c> exposes and for the
/// injected context, reading each output the way CommonMark does; this type is
/// what makes it hold without a per-site decision.</para>
/// </summary>
internal sealed class ModelTextBuilder
{
    private const string MarkdownInfo = "markdown";
    private const char Backslash = (char)92;

    /// <summary>
    /// The text of a line before a value that begins a block: indentation,
    /// then any number of container markers (a bullet, an ordered-list marker,
    /// a blockquote marker), and nothing else.
    /// </summary>
    private static readonly Regex BlockStartPrefix = new(
        "^[ \t]*(?:(?:[-*+]|[0-9]{1,9}[.)])[ \t]+|>[ \t]?)*$",
        RegexOptions.CultureInvariant);

    private readonly StringBuilder _text = new();

    /// <summary>True while nothing has been written.</summary>
    public bool IsEmpty => _text.Length == 0;

    /// <summary>The buffer the handler writes into.</summary>
    internal StringBuilder Buffer => _text;

    /// <summary>One line: its literals as written, every hole a value (<see cref="AppendValue"/>), then <c>\n</c>.</summary>
    /// <param name="line">The interpolated line.</param>
    public ModelTextBuilder Line([InterpolatedStringHandlerArgument("")] ref ModelLineHandler line)
    {
        _text.Append('\n');
        return this;
    }

    /// <summary>An empty line.</summary>
    public ModelTextBuilder Line()
    {
        _text.Append('\n');
        return this;
    }

    /// <summary>Part of a line, with no terminator: its literals as written, every hole a value.</summary>
    /// <param name="part">The interpolated part.</param>
    public ModelTextBuilder Write([InterpolatedStringHandlerArgument("")] ref ModelLineHandler part) => this;

    /// <summary>
    /// Everything <paramref name="part"/> holds, appended as it is: text
    /// another builder already built under the same rules (a section rendered
    /// apart, then placed).
    /// </summary>
    /// <param name="part">The builder whose text to append.</param>
    public ModelTextBuilder Include(ModelTextBuilder part)
    {
        ArgumentNullException.ThrowIfNull(part);
        _text.Append(part._text);
        return this;
    }

    /// <summary>
    /// A line <c>AuditText</c> rendered, then <c>\n</c>. Its bytes are the
    /// CLI's (golden-locked) and already line-safe (#123); it is folded once
    /// more all the same, which changes nothing but a bidi control a quoted
    /// stored value may still carry (<c>DebugQuote</c> leaves those in place).
    /// It starts with a concept id, a label or a count, never block syntax.
    /// </summary>
    /// <param name="line">The rendered line.</param>
    public ModelTextBuilder Rendered(AuditText.AuditLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _text.Append(ModelText.Fold(line.Text)).Append('\n');
        return this;
    }

    /// <summary>
    /// A multi-line value written raw inside a backtick fence longer than any
    /// backtick or tilde run in it (<see cref="Fence"/>), with the info string
    /// of its <paramref name="kind"/>. The block starts on a line of its own (a
    /// line break is added first if the current line is not empty) and ends
    /// with a line break.
    /// </summary>
    /// <param name="kind">What the value is.</param>
    /// <param name="text">The value, written as is (trailing line feeds dropped).</param>
    public ModelTextBuilder Block(BlockKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var info = kind switch
        {
            BlockKind.ConceptBody or BlockKind.IndexText => MarkdownInfo,
            BlockKind.ComputationSource => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a block kind"),
        };

        if (_text.Length > 0 && _text[^1] != '\n')
        {
            _text.Append('\n');
        }

        AppendFenced(_text, text.TrimEnd('\n'), info);
        _text.Append('\n');
        return this;
    }

    /// <summary>
    /// One <c>&lt;okf-context id="…"&gt;</c> element of the context provider's
    /// injected message (arbitration Q3), with no line break after it: the
    /// opening tag; <paramref name="content"/>, escaped
    /// (<see cref="EscapeContextText"/>) and fenced (<see cref="Fence"/>, over
    /// the escaped text), so that no text inside can close the element or open
    /// another, and no fence opened inside can stay open past the closing tag
    /// into the next element; and the closing tag. The id is folded, and its
    /// <c>&amp;</c>, <c>&lt;</c> and <c>"</c> are escaped, so it cannot leave its
    /// attribute either.
    /// </summary>
    /// <param name="id">The element's id.</param>
    /// <param name="content">The element's content, raw.</param>
    public ModelTextBuilder ContextElement(string id, string content)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(content);
        // A tag alone on its line is a CommonMark HTML block (start condition 7) that
        // runs to the next blank line: the blank lines after the opening tag and before
        // the closing one end it, so the fence opens and closes outside any HTML block
        // (#122 re-review, C-NEW). Nothing else in an element starts an HTML block: the
        // content is fenced, and its '<' are escaped anyway.
        _text.Append("<okf-context id=\"").Append(EscapeContextAttribute(id)).Append("\">\n\n");
        AppendFenced(_text, EscapeContextText(content), string.Empty);
        _text.Append("\n\n</okf-context>");
        return this;
    }

    /// <summary>
    /// <paramref name="text"/> with every <c>&amp;</c> written <c>&amp;amp;</c>
    /// and then every <c>&lt;</c> written <c>&amp;lt;</c>: no literal
    /// <c>&lt;/okf-context&gt;</c> in bundle content can close the element it sits
    /// in, and the escape is reversible (<c>&amp;lt;</c> in the content reads back
    /// as <c>&amp;lt;</c>, not as <c>&lt;</c>). Not idempotent: apply it once.
    /// </summary>
    /// <param name="text">The text to escape.</param>
    public static string EscapeContextText(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    /// <summary>The id of a context element as it is written in its attribute: folded, then escaped (<c>&amp;</c>, <c>&lt;</c>, <c>"</c>).</summary>
    /// <param name="id">The element's id.</param>
    public static string EscapeContextAttribute(string id) =>
        EscapeContextText(ModelText.Fold(id)).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// A single line built from <paramref name="message"/> (literals as
    /// written, every hole a value), with no terminator: for a tool result that
    /// is one message.
    /// </summary>
    /// <param name="message">The interpolated message.</param>
    public static string Message(ref ModelLineHandler message) => message.ToStringAndClear();

    /// <summary>
    /// A single line holding <paramref name="value"/>, with no terminator:
    /// folded, and neutralised since it begins the line (<see cref="Neutralise"/>).
    /// </summary>
    /// <param name="value">The value.</param>
    public static string Message(string value)
    {
        var sb = new StringBuilder();
        AppendValue(sb, value);
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => _text.ToString();

    /// <summary>
    /// Appends one value to <paramref name="text"/>: folded
    /// (<see cref="ModelText.Fold"/>), then, when it begins a block (the text of
    /// its line so far is only whitespace and container markers),
    /// neutralised (<see cref="Neutralise"/>).
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="value">The value, raw.</param>
    internal static void AppendValue(StringBuilder text, string value)
    {
        var folded = ModelText.Fold(value);
        text.Append(EscapePipes(BeginsBlock(text) ? Neutralise(folded) : folded));
    }

    /// <summary>
    /// <paramref name="value"/> with every <c>|</c> escaped, wherever it stands
    /// (#122 re-review, m1): a GFM table needs an unescaped pipe in its header
    /// row and its delimiter row, and the only literal pipes in model-facing text
    /// are the <c>&gt; status: … | trust: … | stale: …</c> line's, which a blank
    /// line follows; so with every pipe a value brings escaped, no two lines can
    /// form a table. A run of k backslashes right before a pipe becomes 2k+1, so
    /// the pipe stays escaped and the run still reads back as written.
    /// </summary>
    /// <param name="value">The value.</param>
    internal static string EscapePipes(string value)
    {
        if (!value.Contains('|', StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == Backslash)
            {
                backslashes++;
                continue;
            }

            sb.Append(Backslash, c == '|' ? (2 * backslashes) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return sb.Append(Backslash, backslashes).ToString();
    }

    /// <summary>Whether the next character appended to <paramref name="text"/> begins a Markdown block.</summary>
    private static bool BeginsBlock(StringBuilder text)
    {
        var start = text.Length;
        while (start > 0 && text[start - 1] != '\n')
        {
            start--;
        }

        return BlockStartPrefix.IsMatch(text.ToString(start, text.Length - start));
    }

    /// <summary>
    /// <paramref name="folded"/>, which begins a Markdown block, made unable to
    /// start block syntax of its own (CommonMark 0.31, GFM tables): leading
    /// spaces and tabs are dropped (they would make indented code, or shift a
    /// marker), and a leading character that would open a block is escaped
    /// with a backslash, which CommonMark renders as that character in plain
    /// text. Escaped: <c>#</c> before an ATX heading (1–6 <c>#</c> then a space,
    /// a tab or the end); <c>-</c>, <c>+</c>, <c>*</c> before a space, a tab or
    /// the end (a list item) or as a thematic break; <c>_</c> as a thematic
    /// break; a value that is only <c>=</c> or only <c>-</c>, any length, with
    /// trailing spaces (a setext underline); <c>&gt;</c> (a blockquote);
    /// <c>`</c> and <c>~</c> before three of them (a fence); <c>&lt;</c> (an HTML
    /// block); <c>[</c> always (a link reference definition: the literal
    /// <c>": "</c> after a frontmatter key can complete one); <c>|</c>; and the
    /// <c>.</c> or <c>)</c> of 1–9 leading digits before a space, a tab or the
    /// end (an ordered-list item). Every <c>|</c> of a value, wherever it stands,
    /// is escaped too, by <see cref="EscapePipes"/> (a GFM table). A value that
    /// starts otherwise — a word, a concept id, a path — is returned as is, so
    /// ordinary values print as they always did.
    /// </summary>
    /// <param name="folded">The folded value.</param>
    internal static string Neutralise(string folded)
    {
        var value = folded.TrimStart(' ', '\t');
        if (value.Length == 0)
        {
            return value;
        }

        if (OrderedListMarker(value) is { } punctuation)
        {
            return string.Concat(value.AsSpan(0, punctuation), Backslash.ToString(), value.AsSpan(punctuation));
        }

        return OpensBlock(value) ? Backslash + value : value;
    }

    private static bool OpensBlock(string value)
    {
        var c = value[0];
        return c switch
        {
            '#' => AtxHeading(value),
            '-' or '+' or '*' => SpaceOrEnd(value, 1) || Thematic(value, c) || (c == '-' && SetextUnderline(value, '-')),
            '_' => Thematic(value, '_'),
            '=' => SetextUnderline(value, '='),
            '>' or '<' or '|' => true,
            '`' or '~' => value.Length >= 3 && value[1] == c && value[2] == c,

            // Always, not only before "]:": a value is often followed by a literal
            // ": " (a frontmatter key), which completes a reference definition.
            '[' => true,
            _ => false,
        };
    }

    private static bool AtxHeading(string value)
    {
        var hashes = value.TakeWhile(ch => ch == '#').Count();
        return hashes <= 6 && SpaceOrEnd(value, hashes);
    }

    private static bool Thematic(string value, char c)
    {
        var count = 0;
        foreach (var ch in value)
        {
            if (ch == c)
            {
                count++;
            }
            else if (ch is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    /// <summary>A setext underline: a run of <paramref name="c"/>, any length, then only spaces or tabs (#122 re-review, m2).</summary>
    private static bool SetextUnderline(string value, char c) =>
        value.TrimEnd(' ', '\t').All(ch => ch == c);

    private static bool SpaceOrEnd(string value, int index) =>
        index >= value.Length || value[index] is ' ' or '\t';

    /// <summary>The index of the <c>.</c> or <c>)</c> that would make <paramref name="value"/> an ordered-list item, if it would.</summary>
    private static int? OrderedListMarker(string value)
    {
        var digits = value.TakeWhile(char.IsAsciiDigit).Count();
        return digits is >= 1 and <= 9 && digits < value.Length && value[digits] is '.' or ')' && SpaceOrEnd(value, digits + 1)
            ? digits
            : null;
    }

    /// <summary>
    /// The opening and closing fence for <paramref name="content"/>: backticks,
    /// at least three and one more than the longest run of backticks OR tildes
    /// anywhere in the content. CommonMark closes a fence only on a run of its
    /// own character at least as long, so no line of the content can close it;
    /// the tilde runs are counted too so that the rule needs no argument about
    /// which character a run is made of.
    /// </summary>
    /// <param name="content">The block's content.</param>
    internal static string Fence(string content)
    {
        var longest = 0;
        var run = 0;
        var previous = '\0';
        foreach (var c in content)
        {
            run = c is '`' or '~' ? (c == previous ? run + 1 : 1) : 0;
            previous = c;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>
    /// <paramref name="text"/>, followed by a line closing the fenced code block
    /// it leaves open, if it leaves one open (CommonMark: a fence opens on 3+
    /// backticks or tildes after at most three spaces, and closes on a run of
    /// the same character at least as long). What a truncation that cut a
    /// block before its closing fence uses to close what was opened.
    /// </summary>
    /// <param name="text">The text, possibly cut.</param>
    internal static string CloseOpenFence(string text)
    {
        char fenceChar = '\0';
        var fenceLength = 0;
        foreach (var line in text.Split('\n'))
        {
            var indent = line.TakeWhile(c => c == ' ').Count();
            if (indent > 3)
            {
                continue;
            }

            var rest = line[indent..];
            if (fenceLength > 0)
            {
                var run = rest.TakeWhile(c => c == fenceChar).Count();
                if (run >= fenceLength && rest[run..].All(c => c is ' ' or '\t' or '\r'))
                {
                    fenceLength = 0;
                }

                continue;
            }

            if (rest.Length >= 3 && rest[0] is '`' or '~')
            {
                var c0 = rest[0];
                var run = rest.TakeWhile(c => c == c0).Count();
                if (run >= 3 && !(c0 == '`' && rest[run..].Contains('`', StringComparison.Ordinal)))
                {
                    fenceChar = c0;
                    fenceLength = run;
                }
            }
        }

        return fenceLength == 0 ? text : text + "\n" + new string(fenceChar, fenceLength);
    }

    private static void AppendFenced(StringBuilder text, string content, string info)
    {
        var fence = Fence(content);
        text.Append(fence).Append(info).Append('\n');
        if (content.Length > 0)
        {
            text.Append(content).Append('\n');
        }

        text.Append(fence);
    }
}

/// <summary>
/// The interpolated string handler behind <see cref="ModelTextBuilder"/>'s line
/// methods: a literal part is appended as written; every hole is a value
/// (<see cref="ModelTextBuilder.AppendValue"/>) — a string as it is, any other
/// value once formatted (an <see cref="IFormattable"/> under the invariant
/// culture, with its format if one is given; anything else through
/// <see cref="object.ToString"/>; <see langword="null"/> as nothing).
/// </summary>
[InterpolatedStringHandler]
internal ref struct ModelLineHandler
{
    private StringBuilder? _text;

    /// <summary>A handler writing into its own buffer (<see cref="ModelTextBuilder.Message(ref ModelLineHandler)"/>).</summary>
    /// <param name="literalLength">The total length of the literal parts.</param>
    /// <param name="formattedCount">The number of holes.</param>
    public ModelLineHandler(int literalLength, int formattedCount) =>
        _text = new StringBuilder(literalLength + (formattedCount * 16));

    /// <summary>A handler writing into <paramref name="builder"/>.</summary>
    /// <param name="literalLength">The total length of the literal parts.</param>
    /// <param name="formattedCount">The number of holes.</param>
    /// <param name="builder">The builder whose line this is.</param>
    public ModelLineHandler(int literalLength, int formattedCount, ModelTextBuilder builder) =>
        _text = builder.Buffer;

    /// <summary>A literal part, as written.</summary>
    /// <param name="value">The literal.</param>
    public readonly void AppendLiteral(string value) => _text!.Append(value);

    /// <summary>A string hole: a value.</summary>
    /// <param name="value">The value.</param>
    public readonly void AppendFormatted(string? value) => ModelTextBuilder.AppendValue(_text!, value ?? string.Empty);

    /// <summary>Any other hole, formatted, then a value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    public readonly void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    /// <summary>Any other hole, formatted with <paramref name="format"/> under the invariant culture, then a value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format string.</param>
    public readonly void AppendFormatted<T>(T value, string? format)
    {
        var formatted = value switch
        {
            null => null,
            IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        ModelTextBuilder.AppendValue(_text!, formatted ?? string.Empty);
    }

    /// <summary>The text written, for a handler with its own buffer.</summary>
    internal string ToStringAndClear()
    {
        var result = _text?.ToString() ?? string.Empty;
        _text = null;
        return result;
    }
}

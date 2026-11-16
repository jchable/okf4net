// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Agents.Internal;

/// <summary>
/// The closed set of multi-line values a tool result may carry verbatim
/// (<see cref="ModelTextBuilder.Block"/>), each with its own delimiting rule,
/// so that the content cannot close the block it sits in.
/// </summary>
internal enum BlockKind
{
    /// <summary>
    /// A concept's markdown body (<c>okf_read_concept</c>): a backtick fence
    /// with the info string <c>markdown</c>, longer than any backtick run in
    /// the body (CommonMark's rule), so no line of the body can close it.
    /// </summary>
    ConceptBody,

    /// <summary>
    /// A computation's sanctioned source (<c>okf_get_computation</c>): a
    /// backtick fence with no info string, longer than any backtick run in the
    /// source. Three backticks, as before, for a source that holds none.
    /// </summary>
    ComputationSource,

    /// <summary>
    /// A directory's <c>index.md</c>, returned by <c>okf_browse</c> as the
    /// WHOLE result: no structure of the tool's own precedes or follows it, so
    /// there is nothing for it to forge and it is not wrapped (its bytes are
    /// the file's, as they always were). The builder refuses it anywhere but
    /// alone: it must be the first thing written, and nothing may follow it.
    /// </summary>
    WholeIndex,
}

/// <summary>
/// The only way an agent tool (<c>OkfBundleTools</c>) or the context provider
/// (<c>OkfContextProvider</c>) builds model-facing text (#122, design
/// <c>docs/superpowers/specs/2026-11-16-model-facing-output-boundary-design.md</c>
/// §2.2). Every value interpolated into a line is folded by construction: the
/// line methods take a <see cref="ModelLineHandler"/>, an interpolated string
/// handler whose literal parts — what the developer wrote — are appended as
/// written, and whose every hole goes through <see cref="ModelText.Fold"/>
/// (a non-string hole is formatted under the invariant culture first). A plain
/// <see langword="string"/> passed to a line method is folded whole. Folding
/// therefore needs no decision at the call site.
///
/// <para>Writing a value raw takes <see cref="Block"/>, which is visible in a
/// review and limited to the closed <see cref="BlockKind"/> set, each delimited
/// so that its content cannot close it; and a line <c>AuditText</c> rendered
/// (golden-locked for the CLI) enters through <see cref="Rendered"/>, as an
/// <see cref="AuditText.AuditLine"/> only <c>AuditText</c> can produce.</para>
///
/// <para>The enumerating <c>ModelFacingBoundaryTests</c> is what proves the
/// boundary holds for every function <c>GetTools</c> exposes and for the
/// injected context; this type is what makes it hold without a per-site
/// decision.</para>
/// </summary>
internal sealed class ModelTextBuilder
{
    private const string ConceptBodyInfo = "markdown";

    private readonly StringBuilder _text = new();
    private bool _closed;

    /// <summary>True while nothing has been written.</summary>
    public bool IsEmpty => _text.Length == 0;

    /// <summary>The buffer the handler writes into; refused once a <see cref="BlockKind.WholeIndex"/> block closed the result.</summary>
    internal StringBuilder Buffer
    {
        get
        {
            if (_closed)
            {
                throw new InvalidOperationException("A whole-result block was written: nothing may follow it.");
            }

            return _text;
        }
    }

    /// <summary>One line: its literals as written, every hole folded, then <c>\n</c>.</summary>
    /// <param name="line">The interpolated line.</param>
    public ModelTextBuilder Line([InterpolatedStringHandlerArgument("")] ref ModelLineHandler line)
    {
        Buffer.Append('\n');
        return this;
    }

    /// <summary>One line holding <paramref name="text"/> folded whole (the identity on a literal), then <c>\n</c>.</summary>
    /// <param name="text">The line's text.</param>
    public ModelTextBuilder Line(string text)
    {
        Buffer.Append(ModelText.Fold(text)).Append('\n');
        return this;
    }

    /// <summary>An empty line.</summary>
    public ModelTextBuilder Line()
    {
        Buffer.Append('\n');
        return this;
    }

    /// <summary>Part of a line, with no terminator: its literals as written, every hole folded.</summary>
    /// <param name="part">The interpolated part.</param>
    public ModelTextBuilder Write([InterpolatedStringHandlerArgument("")] ref ModelLineHandler part) => this;

    /// <summary>Part of a line, with no terminator: <paramref name="text"/> folded whole.</summary>
    /// <param name="text">The text.</param>
    public ModelTextBuilder Write(string text)
    {
        Buffer.Append(ModelText.Fold(text));
        return this;
    }

    /// <summary>
    /// A line <c>AuditText</c> rendered, then <c>\n</c>. Its bytes are the
    /// CLI's (golden-locked) and already line-safe (#123); it is folded once
    /// more all the same, which changes nothing but a bidi control a quoted
    /// stored value may still carry (<c>DebugQuote</c> leaves those in place).
    /// </summary>
    /// <param name="line">The rendered line.</param>
    public ModelTextBuilder Rendered(AuditText.AuditLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Buffer.Append(ModelText.Fold(line.Text)).Append('\n');
        return this;
    }

    /// <summary>
    /// A multi-line value written raw, delimited by the rule of its
    /// <paramref name="kind"/> (see <see cref="BlockKind"/>). A fenced block
    /// starts on a line of its own (a line break is added first if the current
    /// line is not empty) and ends with a line break.
    /// </summary>
    /// <param name="kind">What the value is.</param>
    /// <param name="text">The value, written as is (trailing line feeds dropped for a fenced kind).</param>
    public ModelTextBuilder Block(BlockKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind == BlockKind.WholeIndex)
        {
            if (!IsEmpty)
            {
                throw new InvalidOperationException("A whole-result block must be the only thing written.");
            }

            Buffer.Append(text);
            _closed = true;
            return this;
        }

        var info = kind switch
        {
            BlockKind.ConceptBody => ConceptBodyInfo,
            BlockKind.ComputationSource => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a block kind"),
        };

        var content = text.TrimEnd('\n');
        var fence = Fence(content);
        var buffer = Buffer;
        if (buffer.Length > 0 && buffer[^1] != '\n')
        {
            buffer.Append('\n');
        }

        buffer.Append(fence).Append(info).Append('\n');
        if (content.Length > 0)
        {
            buffer.Append(content).Append('\n');
        }

        buffer.Append(fence).Append('\n');
        return this;
    }

    /// <summary>
    /// A single line built from <paramref name="message"/> (literals as written,
    /// every hole folded), with no terminator: for a tool result that is one
    /// message.
    /// </summary>
    /// <param name="message">The interpolated message.</param>
    public static string Message(ref ModelLineHandler message) => message.ToStringAndClear();

    /// <summary>A single line, <paramref name="text"/> folded whole, with no terminator.</summary>
    /// <param name="text">The text.</param>
    public static string Message(string text) => ModelText.Fold(text);

    /// <inheritdoc/>
    public override string ToString() => _text.ToString();

    /// <summary>
    /// The opening and closing fence for <paramref name="content"/>: backticks,
    /// at least three and one more than the longest run of backticks anywhere
    /// in the content, so that no line of it can be a closing fence
    /// (CommonMark: a closing fence is at least as long as the opening one).
    /// </summary>
    /// <param name="content">The block's content.</param>
    internal static string Fence(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }
}

/// <summary>
/// The interpolated string handler behind <see cref="ModelTextBuilder"/>'s line
/// methods: a literal part is appended as written, every hole is folded by
/// <see cref="ModelText.Fold"/> — a string as it is, any other value once
/// formatted (an <see cref="IFormattable"/> under the invariant culture, with
/// its format if one is given; anything else through
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

    /// <summary>A string hole, folded.</summary>
    /// <param name="value">The value.</param>
    public readonly void AppendFormatted(string? value) => _text!.Append(ModelText.Fold(value ?? string.Empty));

    /// <summary>Any other hole, formatted, then folded.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    public readonly void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    /// <summary>Any other hole, formatted with <paramref name="format"/> under the invariant culture, then folded.</summary>
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

        _text!.Append(ModelText.Fold(formatted ?? string.Empty));
    }

    /// <summary>The text written, for a handler with its own buffer.</summary>
    internal string ToStringAndClear()
    {
        var result = _text?.ToString() ?? string.Empty;
        _text = null;
        return result;
    }
}

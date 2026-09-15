// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Yaml;

/// <summary>
/// Recursive-descent parser for the OKF YAML subset, defining this
/// implementation's grammar, error messages, and 1-based line numbers.
/// </summary>
internal static class YamlParser
{
    /// <summary>
    /// Maximum recursive-descent nesting depth (block and flow alike) before
    /// the parser gives up. A safety guard: pathological input like
    /// "tags: [[[[...]]]]" with thousands of levels of nesting throws a
    /// catchable <see cref="YamlParseException"/> here instead of overflowing
    /// the stack (an uncatchable crash).
    /// </summary>
    private const int MaxNestingDepth = 1000;

    /// <summary>Shared message for every place <see cref="MaxNestingDepth"/> is enforced.</summary>
    private const string NestingDepthExceededMessage = "nesting depth limit exceeded";

    // The YAML features the subset rejects rather than silently reading as
    // plain strings (see README "A documented YAML subset"). Each message names
    // the feature and never quotes the input.
    private const string AnchorMessage = "YAML anchors (&name) are not supported by the OKF YAML subset";
    private const string AliasMessage = "YAML aliases (*name) are not supported by the OKF YAML subset";
    private const string TagMessage = "YAML tags (!name, !!type) are not supported by the OKF YAML subset";
    private const string DirectiveMessage = "YAML directives (%YAML, %TAG) are not supported by the OKF YAML subset";
    private const string DocumentMarkerMessage = "YAML document markers (---, ...) are not supported by the OKF YAML subset";

    /// <summary>
    /// Parses a YAML document (the OKF subset) into a <see cref="YamlValue"/>.
    /// Empty or comment/whitespace-only input parses to <see cref="YamlNull"/>.
    /// Anchors, aliases, tags, directives and document markers are rejected
    /// with a <see cref="YamlParseException"/> naming the feature.
    /// </summary>
    public static YamlValue Parse(string text)
    {
        var lines = LfLines.Split(text);
        RejectDirectivesAndDocumentMarkers(lines);
        var p = new BlockParser(lines);
        p.SkipBlankAndComments();
        if (p.Pos >= p.Lines.Count)
        {
            return YamlNull.Instance;
        }

        var baseIndent = p.CurrentIndent();
        var value = p.ParseNode(baseIndent);
        p.SkipBlankAndComments();
        if (p.Pos < p.Lines.Count)
        {
            throw p.Err("unexpected trailing content");
        }

        return value;
    }

    /// <summary>
    /// Rejects a YAML directive (a line starting with <c>%</c>) or document
    /// marker (<c>---</c> or <c>...</c>, followed by the end of the line, a space
    /// or a tab) at column 0. Only column 0 is checked, and that is exact rather
    /// than approximate: this parser never reads a column-0 line as content. A
    /// block scalar's body and a plain scalar's continuation lines must be
    /// indented deeper than their parent (<see cref="BlockParser.ParseBlockScalar"/>
    /// and <see cref="BlockParser.ParsePlainScalarOrContinuation"/> stop at the
    /// first non-blank line that is not), whose indentation is never negative;
    /// quoted and flow values are single-line. So an indented <c>%</c> or
    /// <c>...</c> line inside a <c>|</c> block stays content, and a column-0 one
    /// is always a structural line, where YAML reserves <c>%</c> for directives
    /// and <c>---</c>/<c>...</c> for document markers.
    /// </summary>
    private static void RejectDirectivesAndDocumentMarkers(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith('%'))
            {
                throw new YamlParseException(i + 1, DirectiveMessage);
            }

            if ((line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("...", StringComparison.Ordinal))
                && (line.Length == 3 || line[3] is ' ' or '\t'))
            {
                throw new YamlParseException(i + 1, DocumentMarkerMessage);
            }
        }
    }

    /// <summary>
    /// Throws when <paramref name="token"/>, the text of an unquoted node with
    /// leading whitespace already removed, starts with an anchor (<c>&amp;</c>),
    /// alias (<c>*</c>) or tag (<c>!</c>) indicator. Only the node's first
    /// character is checked, which is where YAML reads these indicators: a quoted
    /// scalar starts with its quote, an indicator in the middle of a plain scalar
    /// (<c>a &amp; b</c>) is text, and callers never pass block-scalar content or
    /// a plain scalar's continuation lines (YAML restricts only a plain scalar's
    /// first character). <paramref name="line"/> is 0-based, like the other helpers'.
    /// </summary>
    private static void RejectNodeIndicator(string token, int line)
    {
        if (token.Length > 0 && NodeIndicatorMessage(token[0]) is { } message)
        {
            throw new YamlParseException(line + 1, message);
        }
    }

    /// <summary>The rejection message for a node starting with <paramref name="first"/>, or <c>null</c>.</summary>
    private static string? NodeIndicatorMessage(char first) => first switch
    {
        '&' => AnchorMessage,
        '*' => AliasMessage,
        '!' => TagMessage,
        _ => null,
    };

    /// <summary>
    /// If <paramref name="line"/> is a top-level YAML mapping-entry line by
    /// this parser's own rules (<see cref="SplitKeyValue"/>) -- e.g.
    /// <c>key: value</c>, <c>key:</c>, <c>"key": value</c>, <c>'key':</c>, or
    /// <c>key : value</c> (whitespace before the colon) -- returns the
    /// DECODED key name (quoting resolved: exactly what
    /// <see cref="YamlMapping.Get"/> would find this entry under) and whether
    /// the line carries an inline value. A null or comment-only remainder
    /// means it does not, which is exactly the condition under which
    /// <see cref="BlockParser.ParseNested"/> looks for a nested block --
    /// including YAML's indentless block-sequence form -- on the FOLLOWING
    /// lines instead of on this one. Returns <see langword="null"/> when the
    /// line is not a mapping-entry line at all (blank, a comment, a sequence
    /// item, ...).
    ///
    /// Does not itself require or check that <paramref name="line"/> is
    /// unindented; the caller decides what counts as "top-level" for its own
    /// purposes, exactly as <c>BlockParser.ParseMappingCore</c> does by
    /// slicing off <c>indent</c> columns before calling <see cref="SplitKeyValue"/> on
    /// what remains. Never throws: any quote <see cref="SplitKeyValue"/>
    /// accepted as part of the key is, by construction, already closed
    /// within that same substring (its own scan requires <c>quote == null</c>
    /// at the split point), so re-decoding it here cannot hit an unterminated
    /// quote.
    ///
    /// Shared by <see cref="OKF4net.Internal.FrontmatterBlockEdit"/> so it
    /// locates exactly the key line this parser's own real parse would --
    /// see finding #C7-1: a hand-rolled column-0-<c>"key:"</c>-PREFIX check
    /// that did not go through this same key-splitting logic silently missed
    /// <c>"key":</c>/<c>'key':</c>/<c>key :</c> spellings, each of which
    /// <see cref="YamlMapping.Get"/> (first-wins) finds without trouble.
    /// </summary>
    internal static (string? KeyName, bool HasInlineValue)? TryReadTopLevelKeyLine(string line)
    {
        var split = SplitKeyValue(line);
        if (split is null)
        {
            return null;
        }

        return (ParseScalar(split.Value.Key, 0).AsDisplayString(), split.Value.Rest is not null);
    }

    /// <summary>
    /// A single key/optional-rest split of a (left-trimmed) mapping-entry line.
    /// </summary>
    private readonly record struct KeyValueSplit(string Key, string? Rest);

    /// <summary>
    /// Block-context parser: indentation, mappings/sequences, literal (`|`) and
    /// folded (`>`) block scalars, comments.
    /// </summary>
    private sealed class BlockParser(List<string> lines)
    {
        public List<string> Lines { get; } = lines;

        public int Pos { get; set; }

        public YamlParseException Err(string message) => new(Pos + 1, message);

        private static bool IsBlankOrComment(string line)
        {
            var t = line.TrimStart();
            return t.Length == 0 || t.StartsWith('#');
        }

        public void SkipBlankAndComments()
        {
            while (Pos < Lines.Count && IsBlankOrComment(Lines[Pos]))
            {
                Pos++;
            }
        }

        /// <summary>
        /// Indentation (count of leading spaces) of the current line. Throws if
        /// the leading whitespace contains a tab (YAML forbids tab indentation).
        /// </summary>
        public int CurrentIndent() => IndentOf(Lines[Pos]) ?? throw Err("tab character in indentation");

        /// <summary>
        /// Recursion depth guard shared by <see cref="ParseNode"/> and
        /// <see cref="ParseMapping"/> — between them every block-level
        /// recursive path (nested mappings, nested sequences, and the
        /// "- key: value" inline-mapping shortcut that calls
        /// <see cref="ParseMapping"/> directly from <see cref="ParseSequence"/>)
        /// passes through one of the two. See <see cref="MaxNestingDepth"/>.
        /// </summary>
        private int _depth;

        /// <summary>Parses a node whose block items begin at column <paramref name="indent"/>.</summary>
        public YamlValue ParseNode(int indent)
        {
            _depth++;
            if (_depth > MaxNestingDepth)
            {
                throw Err(NestingDepthExceededMessage);
            }

            try
            {
                return ParseNodeCore(indent);
            }
            finally
            {
                _depth--;
            }
        }

        private YamlValue ParseNodeCore(int indent)
        {
            var line = Lines[Pos];
            var content = line[Math.Min(indent, line.Length)..];
            var trimmed = content.TrimStart();

            if (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                return ParseSequence(indent);
            }

            if (SplitKeyValue(trimmed) is not null)
            {
                return ParseMapping(indent);
            }

            // A bare scalar / flow collection on a single line.
            RejectNodeIndicator(trimmed, Pos);
            var v = ParseInlineValue(trimmed, Pos);
            Pos++;
            return v;
        }

        public YamlValue ParseMapping(int indent)
        {
            _depth++;
            if (_depth > MaxNestingDepth)
            {
                throw Err(NestingDepthExceededMessage);
            }

            try
            {
                return ParseMappingCore(indent);
            }
            finally
            {
                _depth--;
            }
        }

        private YamlValue ParseMappingCore(int indent)
        {
            var map = new YamlMapping();
            while (true)
            {
                SkipBlankAndComments();
                if (Pos >= Lines.Count)
                {
                    break;
                }

                var ind = CurrentIndent();
                if (ind < indent)
                {
                    break;
                }

                if (ind > indent)
                {
                    throw Err("unexpected indentation in mapping");
                }

                var line = Lines[Pos];
                var content = line[indent..];
                var trimmed = content.TrimStart();
                if (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    break; // sequence at the same level: not part of this mapping
                }

                var split = SplitKeyValue(trimmed) ?? throw Err("expected 'key: value' mapping entry");

                // The key and the inline value are the two unquoted-node starts
                // on this line. `key: &a` followed by a nested block is caught
                // here too, since `&a` is the inline rest.
                RejectNodeIndicator(split.Key, Pos);
                RejectNodeIndicator(split.Rest ?? string.Empty, Pos);
                var keyValue = ParseScalar(split.Key, Pos);
                var entryLine = Pos;
                Pos++;

                var value = split.Rest switch
                {
                    { } r when r.StartsWith('|') || r.StartsWith('>') => ParseBlockScalar(indent, r),
                    { } r => ParsePlainScalarOrContinuation(r, indent, entryLine),
                    // Nested block on the following more-indented lines, else null.
                    null => ParseNested(indent),
                };

                map.PushRaw(keyValue, value);
            }

            return map;
        }

        public YamlValue ParseSequence(int indent)
        {
            // Guarded independently of ParseNode/ParseMapping: the
            // "indentation-relaxed" block sequence style (list items at the
            // SAME indent as their parent key, e.g. "tags:\n- a\n- b") is
            // parsed by right-recursion — ParseSequence -> ParseNested ->
            // ParseSequence, one stack frame per item — bypassing both
            // ParseNode and ParseMapping entirely. Without this, even a
            // very long flat list (not just deeply *nested* structures)
            // could overflow the stack.
            _depth++;
            if (_depth > MaxNestingDepth)
            {
                throw Err(NestingDepthExceededMessage);
            }

            try
            {
                return ParseSequenceCore(indent);
            }
            finally
            {
                _depth--;
            }
        }

        private YamlValue ParseSequenceCore(int indent)
        {
            var seq = new List<YamlValue>();
            while (true)
            {
                SkipBlankAndComments();
                if (Pos >= Lines.Count)
                {
                    break;
                }

                var ind = CurrentIndent();
                if (ind < indent)
                {
                    break;
                }

                if (ind > indent)
                {
                    throw Err("unexpected indentation in sequence");
                }

                var line = Lines[Pos];
                var content = line[indent..];
                if (!(content == "-" || content.StartsWith("- ", StringComparison.Ordinal)))
                {
                    break;
                }

                // Column at which the item payload starts.
                var dashRest = content[1..]; // after '-'
                var itemOffset = indent + 1 + (dashRest.Length - dashRest.TrimStart().Length);
                var itemText = content[1..].TrimStart();
                var entryLine = Pos;

                // Checked before the "- key: value" branch rewrites the line, so
                // `- &a k: v` is reported as the item's anchor.
                RejectNodeIndicator(itemText, entryLine);

                if (itemText.Length == 0)
                {
                    // Nested block belonging to this item.
                    Pos++;
                    seq.Add(ParseNested(indent));
                }
                else if (itemText.StartsWith('|') || itemText.StartsWith('>'))
                {
                    Pos++;
                    seq.Add(ParseBlockScalar(indent, itemText));
                }
                else if (SplitKeyValue(itemText) is not null)
                {
                    // Inline-started mapping element ("- key: value"). Rewrite the
                    // dash to whitespace so the payload aligns at `itemOffset`,
                    // then parse a mapping at that deeper indent.
                    Lines[entryLine] = new string(' ', itemOffset) + itemText;
                    seq.Add(ParseMapping(itemOffset));
                }
                else
                {
                    Pos++;
                    seq.Add(ParsePlainScalarOrContinuation(itemText, indent, entryLine));
                }
            }

            return new YamlSequence(seq);
        }

        /// <summary>
        /// Parses a nested block following a `key:` with no inline value.
        ///
        /// A nested *mapping* must be indented deeper than <paramref name="parentIndent"/>.
        /// A nested block *sequence*, however, is also permitted at exactly
        /// <paramref name="parentIndent"/> — this is YAML's standard
        /// "indentation-relaxed" block sequence, and it is what PyYAML's
        /// <c>safe_dump</c> (used by the reference implementation) emits for list
        /// values such as <c>tags</c>. Returns <see cref="YamlNull"/> when no
        /// block follows.
        /// </summary>
        public YamlValue ParseNested(int parentIndent)
        {
            SkipBlankAndComments();
            if (Pos >= Lines.Count)
            {
                return YamlNull.Instance;
            }

            var ind = CurrentIndent();
            if (ind > parentIndent)
            {
                return ParseNode(ind);
            }

            if (ind == parentIndent && LineIsSequenceItem(ind))
            {
                return ParseSequence(ind);
            }

            return YamlNull.Instance;
        }

        /// <summary>
        /// Whether the current line, taken from column <paramref name="indent"/>,
        /// begins a block sequence item (`-` alone or `- …`).
        /// </summary>
        public bool LineIsSequenceItem(int indent)
        {
            var line = Lines[Pos];
            var content = line[Math.Min(indent, line.Length)..];
            return content == "-" || content.StartsWith("- ", StringComparison.Ordinal);
        }

        /// <summary>
        /// Parses a `|` (literal) or `&gt;` (folded) block scalar. The header
        /// (<paramref name="header"/>) is the text after the `key:` (e.g. `|`,
        /// `|-`, `&gt;+`).
        /// </summary>
        public YamlValue ParseBlockScalar(int parentIndent, string header)
        {
            var style = header[0]; // '|' or '>'
            char? chomp = null;
            for (var i = 1; i < header.Length; i++)
            {
                if (header[i] is '+' or '-')
                {
                    chomp = header[i];
                    break;
                }
            }

            // Collect body lines: blanks, or lines indented deeper than the parent.
            var body = new List<string>();
            int? blockIndent = null;
            while (Pos < Lines.Count)
            {
                var line = Lines[Pos];
                if (line.Trim().Length == 0)
                {
                    body.Add(string.Empty);
                    Pos++;
                    continue;
                }

                var ind = IndentOf(line) ?? throw Err("tab in block scalar indentation");
                if (ind <= parentIndent)
                {
                    break;
                }

                blockIndent ??= ind;
                var bi = blockIndent.Value;
                body.Add(line.Length >= bi ? line[bi..] : string.Empty);
                Pos++;
            }

            // Drop trailing blank lines for accounting, remember how many there were.
            var trailingBlanks = 0;
            while (body.Count > 0 && body[^1].Length == 0)
            {
                body.RemoveAt(body.Count - 1);
                trailingBlanks++;
            }

            var text = style == '|' ? string.Join('\n', body) : FoldLines(body);

            if (chomp == '-')
            {
                // strip: no trailing newline
            }
            else if (chomp == '+')
            {
                // keep: restore all trailing blank lines + one newline for content
                text += "\n";
                for (var i = 0; i < trailingBlanks; i++)
                {
                    text += "\n";
                }
            }
            else
            {
                // clip: exactly one trailing newline if there was any content
                if (text.Length != 0 || trailingBlanks > 0)
                {
                    text += "\n";
                }
            }

            return new YamlString(text);
        }

        /// <summary>
        /// Parses a mapping/sequence-item value that starts as a plain
        /// (unquoted, non-flow) scalar on <paramref name="entryLine"/>,
        /// folding any subsequent lines indented deeper than
        /// <paramref name="parentIndent"/> into it per YAML's plain-scalar
        /// continuation rule -- the same folding <see cref="ParseBlockScalar"/>
        /// applies to an explicit <c>&gt;</c> block scalar: non-blank runs join
        /// with a single space, a blank line becomes a newline, and any
        /// trailing blank run before the next same-or-lesser-indented line is
        /// dropped (a plain scalar never ends in a trailing newline, unlike a
        /// chomped block scalar). A continuation line beginning with the
        /// block-sequence indicator (<c>-</c> alone, or <c>- </c>) is never
        /// folded in -- that indicator is reserved at the start of a line in
        /// block context, so the caller's own "unexpected indentation" check
        /// fires instead, exactly as it did before this method existed. A
        /// quoted (<c>"</c>/<c>'</c>) or flow (<c>[</c>/<c>{</c>) value is not
        /// a plain scalar; it is delegated to <see cref="ParseInlineValue"/>
        /// unchanged -- multi-line quoted/flow values are a distinct,
        /// unimplemented feature, not this one. A comment-only continuation
        /// line (<see cref="IsBlankOrComment"/>) is treated the same as a
        /// blank one -- indentation-blind, matching every other comment check
        /// in this parser -- which is a safe over-approximation of full YAML
        /// (it folds in a paragraph break rather than vanishing invisibly),
        /// not a precise implementation of comments-inside-plain-scalars.
        /// </summary>
        public YamlValue ParsePlainScalarOrContinuation(string firstLineRest, int parentIndent, int entryLine)
        {
            var t = firstLineRest.Trim();
            if (t.StartsWith('[') || t.StartsWith('{') || t.StartsWith('"') || t.StartsWith('\''))
            {
                return ParseInlineValue(firstLineRest, entryLine);
            }

            var body = new List<string> { StripTrailingComment(t) };
            while (Pos < Lines.Count)
            {
                var line = Lines[Pos];
                if (IsBlankOrComment(line))
                {
                    body.Add(string.Empty);
                    Pos++;
                    continue;
                }

                var ind = IndentOf(line) ?? throw Err("tab character in indentation");
                if (ind <= parentIndent)
                {
                    break;
                }

                var content = line[ind..];
                if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                body.Add(StripTrailingComment(content.TrimEnd()));
                Pos++;
            }

            while (body.Count > 0 && body[^1].Length == 0)
            {
                body.RemoveAt(body.Count - 1);
            }

            return InterpretPlain(FoldLines(body));
        }
    }

    /// <summary>
    /// Folds a literal block's lines per YAML's folded (`&gt;`) rules: runs of
    /// non-empty lines join with a single space; blank lines become newlines.
    /// </summary>
    private static string FoldLines(List<string> lines)
    {
        var sb = new StringBuilder();
        var prevNonEmpty = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                sb.Append('\n');
                prevNonEmpty = false;
            }
            else
            {
                if (prevNonEmpty)
                {
                    sb.Append(' ');
                }

                sb.Append(line);
                prevNonEmpty = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>Leading-space count, or <c>null</c> if the indentation contains a tab.</summary>
    private static int? IndentOf(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                n++;
            }
            else if (c == '\t')
            {
                return null;
            }
            else
            {
                break;
            }
        }

        return n;
    }

    /// <summary>
    /// Splits a (left-trimmed) line into a key and optional rest at the first
    /// top-level `:` that is followed by a space or end-of-line. Returns
    /// <c>null</c> when the line is not a mapping entry.
    /// </summary>
    private static KeyValueSplit? SplitKeyValue(string s)
    {
        var i = 0;
        char? quote = null;
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (quote is { } q)
            {
                if (q == '"' && c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == q)
                {
                    if (q == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    quote = null;
                }

                i += 1;
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '[' or '{':
                    depth += 1;
                    break;
                case ']' or '}':
                    if (depth > 0)
                    {
                        depth -= 1;
                    }

                    break;
                case '#' when depth == 0 && i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t'):
                    // comment region without a preceding separator
                    return null;
                case ':' when depth == 0:
                    {
                        char? next = i + 1 < s.Length ? s[i + 1] : null;
                        if (next is null or ' ' or '\t')
                        {
                            var key = s[..i];
                            var restRaw = s[(i + 1)..].Trim();
                            var rest = restRaw.Length == 0 || restRaw.StartsWith('#') ? null : restRaw;
                            return new KeyValueSplit(key.Trim(), rest);
                        }

                        break;
                    }
            }

            i += 1;
        }

        return null;
    }

    /// <summary>Parses a single-line value: a flow collection or a scalar.</summary>
    private static YamlValue ParseInlineValue(string s, int line)
    {
        var t = s.Trim();
        if (t.StartsWith('[') || t.StartsWith('{'))
        {
            var fp = new FlowParser(t, line);
            var v = fp.ParseValue();
            fp.SkipWs();
            // Allow a trailing comment after the flow collection.
            if (fp.Pos < fp.Chars.Length && fp.Chars[fp.Pos] != '#')
            {
                throw new YamlParseException(line + 1, "unexpected content after flow collection");
            }

            return v;
        }

        return ParseScalar(t, line);
    }

    /// <summary>Interprets a scalar token (possibly quoted) into a typed <see cref="YamlValue"/>.</summary>
    private static YamlValue ParseScalar(string token, int line)
    {
        var t = token.Trim();
        if (t.Length == 0)
        {
            return YamlNull.Instance;
        }

        if (t.StartsWith('"'))
        {
            return new YamlString(ParseDoubleQuoted(t, line));
        }

        if (t.StartsWith('\''))
        {
            return new YamlString(ParseSingleQuoted(t, line));
        }

        // Plain scalar: strip a trailing " #" comment.
        return InterpretPlain(StripTrailingComment(t));
    }

    /// <summary>Strips a trailing ` #...` comment from a plain scalar.</summary>
    private static string StripTrailingComment(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '#' && i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t'))
            {
                return s[..i].TrimEnd();
            }
        }

        return s.TrimEnd();
    }

    /// <summary>
    /// Resolves a plain (unquoted) scalar to null/bool/int/float/string.
    ///
    /// Number resolution is intentionally conservative to avoid silently
    /// coercing identifier-like values: integers must have no redundant leading
    /// zero (so a zero-padded code such as `007` stays a string), and floats
    /// must contain a decimal point (so `1e3` stays a string). The special
    /// float tokens `.inf`, `-.inf`, and `.nan` are recognized so non-finite
    /// floats produced by the emitter round-trip.
    /// </summary>
    private static YamlValue InterpretPlain(string s)
    {
        switch (s)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return YamlNull.Instance;
            case "true" or "True" or "TRUE":
                return new YamlBool(true);
            case "false" or "False" or "FALSE":
                return new YamlBool(false);
            case ".inf" or ".Inf" or ".INF" or "+.inf":
                return new YamlFloat(double.PositiveInfinity);
            case "-.inf" or "-.Inf" or "-.INF":
                return new YamlFloat(double.NegativeInfinity);
            case ".nan" or ".NaN" or ".NAN":
                return new YamlFloat(double.NaN);
        }

        if (IsCanonicalInt(s) && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
        {
            return new YamlInt(i);
        }

        if (IsCanonicalFloat(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            return new YamlFloat(f);
        }

        return new YamlString(s);
    }

    /// <summary>`[-+]?(0|[1-9][0-9]*)` — a decimal integer with no redundant leading zero.</summary>
    private static bool IsCanonicalInt(string s)
    {
        var digits = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? s[1..] : s;
        if (digits.Length == 0 || !digits.All(c => c is >= '0' and <= '9'))
        {
            return false;
        }

        return digits == "0" || !digits.StartsWith('0');
    }

    /// <summary>
    /// A float that contains a decimal point and a digit (optionally with an
    /// exponent), e.g. `0.1`, `-3.5`, `1.0e9`. Bare-exponent forms like `1e3`
    /// are deliberately treated as strings.
    /// </summary>
    private static bool IsCanonicalFloat(string s)
    {
        if (!s.Contains('.') || !s.Any(c => c is >= '0' and <= '9'))
        {
            return false;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static string ParseDoubleQuoted(string s, int line)
    {
        var outSb = new StringBuilder();
        var i = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                return outSb.ToString();
            }

            if (c == '\\')
            {
                i += 1;
                if (i >= s.Length)
                {
                    throw new YamlParseException(line + 1, "dangling escape in double-quoted string");
                }

                var e = s[i];
                switch (e)
                {
                    case 'n':
                        outSb.Append('\n');
                        break;
                    case 't':
                        outSb.Append('\t');
                        break;
                    case 'r':
                        outSb.Append('\r');
                        break;
                    case '"':
                        outSb.Append('"');
                        break;
                    case '\\':
                        outSb.Append('\\');
                        break;
                    case '/':
                        outSb.Append('/');
                        break;
                    case '0':
                        outSb.Append('\0');
                        break;
                    case 'b':
                        outSb.Append('');
                        break;
                    case 'f':
                        outSb.Append('');
                        break;
                    case 'u':
                        {
                            var available = s.Length - (i + 1);
                            var hexLen = Math.Max(0, Math.Min(4, available));
                            var hex = s.Substring(i + 1, hexLen);
                            if (hex.Length == 4)
                            {
                                if (int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var cp))
                                {
                                    AppendUnicodeScalar(outSb, cp);
                                }

                                i += 4;
                            }

                            break;
                        }

                    default:
                        outSb.Append(e);
                        break;
                }

                i += 1;
                continue;
            }

            outSb.Append(c);
            i += 1;
        }

        throw new YamlParseException(line + 1, "unterminated double-quoted string");
    }

    /// <summary>
    /// Appends the character for a Unicode scalar value, silently rejecting
    /// invalid scalars such as surrogate-range or out-of-range code points.
    /// </summary>
    private static void AppendUnicodeScalar(StringBuilder sb, int codePoint)
    {
        if (codePoint is >= 0xD800 and <= 0xDFFF || codePoint is < 0 or > 0x10FFFF)
        {
            return;
        }

        if (codePoint <= 0xFFFF)
        {
            sb.Append((char)codePoint);
        }
        else
        {
            sb.Append(char.ConvertFromUtf32(codePoint));
        }
    }

    private static string ParseSingleQuoted(string s, int line)
    {
        var outSb = new StringBuilder();
        var i = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    outSb.Append('\'');
                    i += 2;
                    continue;
                }

                return outSb.ToString();
            }

            outSb.Append(c);
            i += 1;
        }

        throw new YamlParseException(line + 1, "unterminated single-quoted string");
    }

    /// <summary>A recursive parser for flow collections (`[...]`, `{...}`).</summary>
    private sealed class FlowParser(string chars, int line)
    {
        public string Chars { get; } = chars;

        public int Pos { get; set; }

        public void SkipWs()
        {
            while (Pos < Chars.Length && char.IsWhiteSpace(Chars[Pos]))
            {
                Pos++;
            }
        }

        private YamlParseException Err(string message) => new(line + 1, message);

        /// <summary>
        /// Recursion depth guard: every flow-collection recursive path
        /// (<c>[</c> and <c>{</c> alike) funnels through <see cref="ParseValue"/>
        /// (<see cref="ParseSeq"/> and <see cref="ParseMap"/> both call back
        /// into it for each element), so guarding this single choke point
        /// covers all of it. See <see cref="MaxNestingDepth"/>.
        /// </summary>
        private int _depth;

        public YamlValue ParseValue()
        {
            SkipWs();
            if (Pos >= Chars.Length)
            {
                return YamlNull.Instance;
            }

            _depth++;
            if (_depth > MaxNestingDepth)
            {
                throw Err(NestingDepthExceededMessage);
            }

            try
            {
                return Chars[Pos] switch
                {
                    '[' => ParseSeq(),
                    '{' => ParseMap(),
                    _ => ParseFlowScalar(),
                };
            }
            finally
            {
                _depth--;
            }
        }

        public YamlValue ParseSeq()
        {
            Pos += 1; // consume '['
            var seq = new List<YamlValue>();
            while (true)
            {
                SkipWs();
                if (Pos >= Chars.Length)
                {
                    throw Err("unterminated flow sequence");
                }

                if (Chars[Pos] == ']')
                {
                    Pos += 1;
                    break;
                }

                seq.Add(ParseValue());
                SkipWs();
                if (Pos < Chars.Length && Chars[Pos] == ',')
                {
                    Pos += 1;
                }
                else if (Pos < Chars.Length && Chars[Pos] == ']')
                {
                    Pos += 1;
                    break;
                }
                else
                {
                    throw Err("expected ',' or ']' in flow sequence");
                }
            }

            return new YamlSequence(seq);
        }

        public YamlValue ParseMap()
        {
            Pos += 1; // consume '{'
            var map = new YamlMapping();
            while (true)
            {
                SkipWs();
                if (Pos >= Chars.Length)
                {
                    throw Err("unterminated flow mapping");
                }

                if (Chars[Pos] == '}')
                {
                    Pos += 1;
                    break;
                }

                var key = ParseFlowScalar();
                SkipWs();
                if (Pos >= Chars.Length || Chars[Pos] != ':')
                {
                    throw Err("expected ':' in flow mapping");
                }

                Pos += 1;
                var value = ParseValue();
                map.PushRaw(key, value);
                SkipWs();
                if (Pos < Chars.Length && Chars[Pos] == ',')
                {
                    Pos += 1;
                }
                else if (Pos < Chars.Length && Chars[Pos] == '}')
                {
                    Pos += 1;
                    break;
                }
                else
                {
                    throw Err("expected ',' or '}' in flow mapping");
                }
            }

            return map;
        }

        public YamlValue ParseFlowScalar()
        {
            SkipWs();
            if (Pos >= Chars.Length)
            {
                throw Err("expected scalar");
            }

            var c = Chars[Pos];
            if (c == '"' || c == '\'')
            {
                var start = Pos;
                Pos += 1;
                while (Pos < Chars.Length)
                {
                    var cur = Chars[Pos];
                    if (c == '"' && cur == '\\')
                    {
                        Pos += 2;
                        continue;
                    }

                    if (cur == c)
                    {
                        if (c == '\'' && Pos + 1 < Chars.Length && Chars[Pos + 1] == '\'')
                        {
                            Pos += 2;
                            continue;
                        }

                        Pos += 1;
                        break;
                    }

                    Pos += 1;
                }

                var raw = Chars[start..Math.Min(Pos, Chars.Length)];
                var s = c == '"' ? ParseDoubleQuoted(raw, line) : ParseSingleQuoted(raw, line);
                return new YamlString(s);
            }

            // Plain flow scalar: read until a flow indicator (`,`/`]`/`}`) or a
            // key/value colon. A ':' terminates the scalar only when followed by
            // whitespace, a flow indicator, or end-of-input — mirroring the
            // block-style SplitKeyValue rule — so a bare ':' inside a value
            // (`human:ada`, `https://x`, an ISO time `00:00:00`) is kept.
            // Every flow value and flow-mapping key that is not a collection or a
            // quoted scalar reaches this point with Pos on its first character.
            if (NodeIndicatorMessage(c) is { } indicatorMessage)
            {
                throw Err(indicatorMessage);
            }

            var startPlain = Pos;
            while (Pos < Chars.Length)
            {
                var ch = Chars[Pos];
                if (ch is ',' or ']' or '}')
                {
                    break;
                }

                if (ch == ':')
                {
                    var next = Pos + 1 < Chars.Length ? Chars[Pos + 1] : (char?)null;
                    if (next is null or ' ' or '\t' or ',' or ']' or '}')
                    {
                        break;
                    }
                }

                Pos += 1;
            }

            var rawPlain = Chars[startPlain..Pos];
            return InterpretPlain(rawPlain.Trim());
        }
    }
}

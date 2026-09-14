// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net;

/// <summary>
/// How a link target is interpreted under §6.1.
/// </summary>
public enum LinkKind
{
    /// <summary>Begins with <c>/</c>: resolved relative to the bundle root (§6.1, recommended).</summary>
    Absolute,

    /// <summary>A relative path such as <c>./other.md</c> (§6.1).</summary>
    Relative,

    /// <summary>An external URI (<c>https://…</c>, <c>mailto:…</c>, …).</summary>
    External,

    /// <summary>A pure in-document anchor (<c>#section</c>).</summary>
    Anchor,

    /// <summary>Anything else (e.g. an empty target).</summary>
    Other,
}

/// <summary>
/// A markdown link found in a concept body.
/// </summary>
public sealed record ConceptLink(string Text, string Target, LinkKind Kind)
{
    /// <summary>
    /// Classifies a raw target string per §6.1.
    /// </summary>
    public static LinkKind Classify(string target)
    {
        var t = target.Trim();
        if (t.Length == 0)
        {
            return LinkKind.Other;
        }

        if (t.StartsWith('#'))
        {
            return LinkKind.Anchor;
        }

        if (IsExternal(t))
        {
            return LinkKind.External;
        }

        if (t.StartsWith('/'))
        {
            return LinkKind.Absolute;
        }

        return LinkKind.Relative;
    }

    /// <summary>
    /// Resolves an internal link to the concept id it points at, given the id
    /// of the concept the link appears in.
    ///
    /// Returns <c>null</c> for external links, anchors, links to directories
    /// (targets ending in <c>/</c>), or targets that cannot form a valid
    /// concept id. The result is *not* guaranteed to exist in the bundle —
    /// broken links are permitted by the spec (§6.1).
    /// </summary>
    public ConceptId? Resolve(ConceptId source) => Kind switch
    {
        LinkKind.Absolute => ResolveAbsolute(Target),
        LinkKind.Relative => ResolveRelative(Target, source),
        _ => null,
    };

    private static bool IsExternal(string t)
    {
        var lower = ToAsciiLower(t);
        return lower.StartsWith("//", StringComparison.Ordinal) // protocol-relative URL
            || lower.Contains("://", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal)
            || lower.StartsWith("tel:", StringComparison.Ordinal)
            || lower.StartsWith("data:", StringComparison.Ordinal);
    }

    /// <summary>
    /// ASCII-only lower-casing (non-ASCII characters are left untouched,
    /// unlike culture-aware <c>ToLower</c>).
    /// </summary>
    private static string ToAsciiLower(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(c + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    private static string StripAnchor(string target)
    {
        var idx = target.IndexOf('#');
        return idx >= 0 ? target[..idx] : target;
    }

    private static ConceptId? ResolveAbsolute(string target)
    {
        var t = StripAnchor(target);
        if (t.EndsWith('/'))
        {
            return null; // directory link
        }

        // Normalize `.`/`..` segments relative to the bundle root, consistent
        // with relative-link resolution.
        var segs = new List<string>();
        foreach (var comp in t.TrimStart('/').Split('/'))
        {
            AppendSegment(segs, comp);
        }

        StripMdSuffix(segs);
        return TryNewConceptId(segs);
    }

    private static ConceptId? ResolveRelative(string target, ConceptId source)
    {
        var t = StripAnchor(target);
        if (t.Length == 0 || t.EndsWith('/'))
        {
            return null;
        }

        // Start from the source concept's directory.
        var segs = source.Parent is { } parent ? new List<string>(parent.Segments) : [];
        foreach (var comp in t.Split('/'))
        {
            AppendSegment(segs, comp);
        }

        StripMdSuffix(segs);
        return TryNewConceptId(segs);
    }

    private static void AppendSegment(List<string> segs, string comp)
    {
        switch (comp)
        {
            case "":
            case ".":
                return;
            case "..":
                if (segs.Count > 0)
                {
                    segs.RemoveAt(segs.Count - 1);
                }

                return;
            default:
                segs.Add(comp);
                return;
        }
    }

    private static void StripMdSuffix(List<string> segs)
    {
        if (segs.Count == 0)
        {
            return;
        }

        var last = segs[^1];
        if (last.EndsWith(".md", StringComparison.Ordinal))
        {
            segs[^1] = last[..^3];
        }
    }

    private static ConceptId? TryNewConceptId(List<string> segs)
    {
        try
        {
            return ConceptId.New(segs);
        }
        catch (ConceptIdException)
        {
            return null;
        }
    }
}

/// <summary>
/// A numbered entry under the <c># Citations</c> heading (§13.1, legacy).
/// </summary>
public sealed record Citation(uint Number, string? Text, string? Target, string Raw);

/// <summary>
/// Dependency-free scanner for inline <c>[text](dest)</c> links and
/// numbered <c># Citations</c> entries.
/// </summary>
public static class LinkScanner
{
    /// <summary>
    /// Extracts all inline markdown links from a body, skipping fenced code
    /// blocks and inline code spans.
    /// </summary>
    public static IReadOnlyList<ConceptLink> ExtractLinks(string body)
    {
        var links = new List<ConceptLink>();
        foreach (var line in CodeFreeLines(body))
        {
            ScanLineLinks(line, links);
        }

        return links;
    }

    /// <summary>
    /// Extracts numbered citation entries from the <c># Citations</c>
    /// section (§13.1, legacy).
    /// </summary>
    public static IReadOnlyList<Citation> ExtractCitations(string body)
    {
        var result = new List<Citation>();
        var inSection = false;
        foreach (var line in LfLines.Split(body))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                if (inSection)
                {
                    // A new heading ends the citations section.
                    break;
                }

                var title = trimmed[1..].TrimStart('#').Trim();
                inSection = string.Equals(title, "citations", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || trimmed.Length == 0)
            {
                continue;
            }

            var citation = ParseCitationLine(trimmed);
            if (citation is not null)
            {
                result.Add(citation);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the keys of footnote <b>references</b> in a body — the <c>key</c> in
    /// running text such as <c>claim.[^key]</c> — skipping code blocks, inline code
    /// spans and backslash-escaped brackets, and not counting a footnote <b>definition</b>'s own label
    /// (<c>[^key]: …</c> at the start of a line). Keys are returned once each, in
    /// first-seen order.
    ///
    /// Reuses <see cref="CodeFreeLines"/> so "skip code" has one implementation for
    /// both links and citations. That matters here more than for links: <c>[^a-z]</c>
    /// is a negated character class, ordinary inside a regex or a SQL pattern, and
    /// must never be read as a citation.
    /// </summary>
    internal static IReadOnlyList<string> ExtractFootnoteReferences(string body)
    {
        var keys = new List<string>();
        foreach (var line in CodeFreeLines(body))
        {
            var scanFrom = 0;
            var definition = FootnoteDefinition.Match(line);
            if (definition.Success)
            {
                // The label of a definition is not a citation; its text may still cite.
                scanFrom = definition.Length;
            }

            foreach (System.Text.RegularExpressions.Match m in FootnoteReference.Matches(line, scanFrom))
            {
                // `\[^x]` is a literal bracket; `\\[^x]` is a literal backslash, then a reference.
                var backslashes = 0;
                while (m.Index - backslashes > 0 && line[m.Index - backslashes - 1] == '\\')
                {
                    backslashes++;
                }

                if (backslashes % 2 == 1)
                {
                    continue;
                }

                var key = m.Groups[1].Value;
                if (!keys.Contains(key, StringComparer.Ordinal))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// The body's ATX headings (<c>#</c> through <c>######</c>), in order, as their level
    /// and their text with any closing <c>#</c> sequence removed. Fenced code is skipped,
    /// so a <c># comment</c> in a Python or shell fence is not a heading. Setext headings
    /// (text underlined with <c>===</c>/<c>---</c>) are not recognized.
    /// </summary>
    internal static IReadOnlyList<(int Level, string Text)> ExtractAtxHeadings(string body)
    {
        var headings = new List<(int, string)>();
        foreach (var (raw, _) in CodeFreeLinePairs(body))
        {
            if (TryParseAtxHeading(raw, out var level, out var text))
            {
                headings.Add((level, text));
            }
        }

        return headings;
    }

    /// <summary>
    /// Parses an ATX heading: up to three spaces, one to six <c>#</c>, then the end of the
    /// line or a space or tab and the text, less any closing run of <c>#</c> that follows
    /// whitespace. Hand-written rather than a regex, which is what it replaced: a lazy
    /// <c>(.*?)</c> before an optional closing sequence retried that sequence at every
    /// character, so one long line of spaces took tens of seconds.
    /// </summary>
    private static bool TryParseAtxHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var i = 0;
        while (i < line.Length && i < 4 && line[i] == ' ')
        {
            i++;
        }

        var hashes = i < 4 ? RunLength(line, i, '#') : 0;
        var rest = i + hashes;
        if (hashes is < 1 or > 6 || (rest < line.Length && line[rest] is not (' ' or '\t')))
        {
            return false;
        }

        var s = line.AsSpan(rest).Trim(" \t");
        var end = s.Length;
        while (end > 0 && s[end - 1] == '#')
        {
            end--;
        }

        if (end == 0)
        {
            s = default;
        }
        else if (end < s.Length && s[end - 1] is ' ' or '\t')
        {
            s = s[..end].TrimEnd(" \t");
        }

        level = hashes;
        text = s.ToString();
        return true;
    }

    /// <summary>
    /// Extracts the entries of an <c>index.md</c> body (§8): each list item whose
    /// content begins with an inline link, paired with the description text that
    /// follows the link (a leading <c>-</c>, <c>–</c>, <c>—</c> or <c>:</c> separator
    /// and surrounding whitespace removed; empty when there is none). Fenced code is
    /// skipped, and a list item that does not start with a link is not an entry.
    ///
    /// Structure — the bullet and the link — is read from the code-blanked line, so a
    /// link written inside an inline code span is never mistaken for an entry. The
    /// description is read from the line as written, so an inline code span in it
    /// (<c>— `runtime: python`</c>) counts as the visible text it is.
    /// </summary>
    internal static IReadOnlyList<(ConceptLink Link, string Description)> ExtractIndexEntries(string body)
    {
        var entries = new List<(ConceptLink, string)>();
        foreach (var (link, text, _) in ExtractIndexListItems(body))
        {
            if (link is not null)
            {
                entries.Add((link, text));
            }
        }

        return entries;
    }

    /// <summary>
    /// Every non-empty list item of an <c>index.md</c> body, in order. An item that
    /// begins with an inline link carries that link and the description after it, as
    /// <see cref="ExtractIndexEntries"/> returns them; any other item carries a
    /// <c>null</c> link and its first line's text as written, and says whether a link
    /// appears anywhere in it — a bold link, a link after an icon, a link on a
    /// continuation line. Thematic breaks (<c>* * *</c>, <c>- - -</c>) are not items, and
    /// code is skipped.
    /// </summary>
    internal static IReadOnlyList<(ConceptLink? Link, string Text, bool ContainsLink)> ExtractIndexListItems(string body)
    {
        var items = new List<(ConceptLink?, string, bool)>();
        var lines = CodeFreeLinePairs(body);
        for (var n = 0; n < lines.Count; n++)
        {
            var (raw, blanked) = lines[n];
            if (BulletContentStart(raw, blanked) is not { } i)
            {
                continue;
            }

            if (blanked[i] != '[' || ParseInlineLink(blanked.ToCharArray(), i) is not { } p)
            {
                var containsLink = HasLink(blanked[i..]);
                var start = n;
                while (n + 1 < lines.Count && IsParagraphContinuation(lines[n + 1]))
                {
                    n++;
                    containsLink |= HasLink(lines[n].Blanked);
                }

                // Same offset in both strings (blanking preserves length).
                items.Add((null, lines[start].Raw[i..].Trim(), containsLink));
                continue;
            }

            var target = StripTitle(p.Dest);
            var link = new ConceptLink(p.Text, target, ConceptLink.Classify(target));
            var description = raw[p.Next..].Trim().TrimStart('-', '–', '—', ':').Trim();

            // A list item's paragraph continues onto the following lines, indented or
            // not, until a blank line or the start of another block — so a description
            // wrapped there is still the entry's.
            while (description.Length == 0 && n + 1 < lines.Count && IsParagraphContinuation(lines[n + 1]))
            {
                n++;
                description = lines[n].Raw.Trim().TrimStart('-', '–', '—', ':').Trim();
            }

            items.Add((link, description, true));
        }

        return items;
    }

    private static bool HasLink(string blankedLine)
    {
        var links = new List<ConceptLink>();
        ScanLineLinks(blankedLine, links);
        return links.Count != 0;
    }

    /// <summary>
    /// The offset of a bullet list item's content when the line opens one (<c>*</c>,
    /// <c>-</c> or <c>+</c>, then a space or tab), or <c>null</c> for any other line,
    /// a thematic break, or an item with no content. Whitespace is read on the raw line:
    /// blanking turns an inline code span into spaces, so <c>`code` - text</c> would
    /// otherwise read as a bullet, and <c>* `x` [a](b)</c> as an item opening with a link.
    /// </summary>
    private static int? BulletContentStart(string raw, string blanked)
    {
        var i = 0;
        while (i < raw.Length && raw[i] is ' ' or '\t')
        {
            i++;
        }

        if (i + 1 >= raw.Length || blanked[i] is not ('*' or '-' or '+') || raw[i + 1] is not (' ' or '\t'))
        {
            return null;
        }

        if (ThematicBreak.IsMatch(blanked))
        {
            return null;
        }

        i += 2;
        while (i < raw.Length && raw[i] is ' ' or '\t')
        {
            i++;
        }

        return i < raw.Length ? i : null;
    }

    /// <summary>
    /// Whether a line continues the paragraph above it. Code lines reach here as the empty
    /// placeholders <see cref="CodeFreeLinePairs"/> leaves in their place, so a fence or
    /// indented code block ends a paragraph just as a blank line does.
    /// </summary>
    private static bool IsParagraphContinuation((string Raw, string Blanked) line)
    {
        var content = line.Raw.TrimStart(' ', '\t');
        return content.Length != 0
            && ListItemContentColumn(0, content) is null
            && !TryParseAtxHeading(content, out _, out _)
            && !ThematicBreak.IsMatch(content)
            && OpensFence(content) is null;
    }

    // A CommonMark thematic break: three or more of the same `*`, `-` or `_`, optionally
    // separated by spaces or tabs, after at most three spaces of indentation.
    private static readonly System.Text.RegularExpressions.Regex ThematicBreak =
        new(@"^ {0,3}([*\-_])(?:[ \t]*\1){2,}[ \t]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // A footnote reference: `[^key]`. GFM footnote labels may not contain whitespace
    // or brackets; this is deliberately that permissive rather than [A-Za-z0-9_-]+,
    // so a key the renderer accepts is never silently skipped here.
    private static readonly System.Text.RegularExpressions.Regex FootnoteReference =
        new(@"\[\^([^\]\s\[]+)\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // A footnote definition label at line start, allowing CommonMark's up-to-three
    // spaces of indentation: `[^key]:`.
    private static readonly System.Text.RegularExpressions.Regex FootnoteDefinition =
        new(@"^ {0,3}\[\^[^\]\s\[]+\]:", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the body's lines with fenced code blocks removed and inline
    /// code spans blanked out.
    /// </summary>
    private static List<string> CodeFreeLines(string body) =>
        CodeFreeLinePairs(body).ConvertAll(pair => pair.Blanked);

    /// <summary>
    /// The one implementation of "skip code": every line of the body, paired as it was
    /// written (<c>Raw</c>) and with its inline code spans blanked to spaces
    /// (<c>Blanked</c>) — except a line of a code block, which becomes an empty pair, so
    /// line adjacency survives and code still separates what comes before it from what
    /// comes after. Blanking replaces one character with one space, so both strings have
    /// the same length and every offset means the same position in each — which lets a
    /// caller find structure on <c>Blanked</c> (so nothing inside code is mistaken for a
    /// link) and still read visible text from <c>Raw</c>.
    ///
    /// Code is recognized as CommonMark defines it, within a single line-by-line pass
    /// that tracks the open list items (by the column their content starts at) and
    /// whether a paragraph is open. Indentation is measured from the innermost open list
    /// item's content, not from the margin:
    /// <list type="bullet">
    /// <item>A fence opens on three or more backticks or tildes indented at most three
    /// columns, and closes only on a run of the same character at least as long, indented
    /// at most three columns, with nothing after it but whitespace — so a <c>```</c>
    /// inside a <c>````</c> fence, a <c>```python</c> line, or an over-indented run is
    /// content. A fence opened inside a list item ends when the item does.</item>
    /// <item>A line indented four or more columns while no paragraph is open is indented
    /// code; inside an open paragraph it is continuation text.</item>
    /// <item>A list item ends at a line indented less than its content, unless that line
    /// is lazy continuation text of the item's open paragraph.</item>
    /// <item>A fence may also open on a list marker's own line (<c>* ```</c>), inside
    /// that item.</item>
    /// <item>Inline code spans are blanked by <see cref="BlankInlineCode"/> over a whole
    /// paragraph at once, so a span may cross a line ending but never a block
    /// boundary.</item>
    /// </list>
    /// Not modelled: block quotes and HTML blocks.
    /// </summary>
    private static List<(string Raw, string Blanked)> CodeFreeLinePairs(string body)
    {
        var result = new List<(string, string)>();
        var listContent = new Stack<int>();
        (char Char, int Length, int Container)? fence = null;
        var previousBlank = true;
        var inParagraph = false;

        // The result indices of the open paragraph's lines, blanked together when it ends.
        var paragraph = new List<int>();
        void EndParagraph()
        {
            inParagraph = false;
            if (paragraph.Count == 0)
            {
                return;
            }

            var joined = string.Join('\n', paragraph.Select(k => result[k].Item1));
            var blanked = BlankInlineCode(joined).Split('\n');
            for (var k = 0; k < paragraph.Count; k++)
            {
                result[paragraph[k]] = (result[paragraph[k]].Item1, blanked[k]);
            }

            paragraph.Clear();
        }

        foreach (var line in LfLines.Split(body))
        {
            var (indent, contentStart) = Indentation(line);
            var content = line[contentStart..];
            var blank = content.Length == 0;

            if (fence is { } open)
            {
                if (blank || indent >= open.Container)
                {
                    var run = RunLength(content, 0, open.Char);
                    if (!blank && indent - open.Container <= 3 && run >= open.Length && content.AsSpan(run).IsWhiteSpace())
                    {
                        fence = null;
                    }

                    result.Add((string.Empty, string.Empty));
                    previousBlank = blank;
                    continue;
                }

                // Less indented than the list item that holds the fence: the item has
                // ended, and the fence with it. The line is read as ordinary markdown.
                fence = null;
            }

            if (blank)
            {
                EndParagraph();
                result.Add((line, line));
                previousBlank = true;
                continue;
            }

            var thematicBreak = ThematicBreak.IsMatch(content);
            var markerContent = thematicBreak ? null : ListItem(indent, content);
            var opensFence = OpensFence(content);
            var startsBlock = thematicBreak || markerContent is not null || opensFence is not null
                || TryParseAtxHeading(content, out _, out _);

            var lazy = inParagraph && !previousBlank && !startsBlock;
            while (!lazy && listContent.Count > 0 && indent < listContent.Peek())
            {
                listContent.Pop();
            }

            previousBlank = false;
            var relative = indent - (listContent.Count > 0 ? listContent.Peek() : 0);

            if (relative >= 4 && !inParagraph)
            {
                result.Add((string.Empty, string.Empty));
                continue;
            }

            if (relative <= 3 && opensFence is { } fenceOpen)
            {
                EndParagraph();
                fence = (fenceOpen.Char, fenceOpen.Length, listContent.Count > 0 ? listContent.Peek() : 0);
                result.Add((string.Empty, string.Empty));
                continue;
            }

            if (relative <= 3 && startsBlock && markerContent is null)
            {
                // A heading or thematic break: never part of a paragraph.
                EndParagraph();
                result.Add((line, BlankInlineCode(line)));
                continue;
            }

            if (relative <= 3 && markerContent is { } item)
            {
                EndParagraph();
                listContent.Push(item.Column);
                if (OpensFence(item.Content) is { } itemFence)
                {
                    fence = (itemFence.Char, itemFence.Length, item.Column);
                    result.Add((string.Empty, string.Empty));
                    continue;
                }
            }

            inParagraph = true;
            paragraph.Add(result.Count);
            result.Add((line, line));
        }

        EndParagraph();
        return result;
    }

    /// <summary>
    /// When <paramref name="content"/> (the line past its <paramref name="indent"/>
    /// columns) opens a list item — a bullet (<c>*</c>, <c>-</c>, <c>+</c>) or an ordered
    /// marker (<c>1.</c>, <c>1)</c>, up to nine digits) followed by whitespace or the end
    /// of the line — the column its content starts at and that content on this line;
    /// otherwise <c>null</c>. As CommonMark counts the column: one to four columns after
    /// the marker, or one when there are five or more (the item then opens with indented
    /// code, so no content is returned) or nothing follows.
    /// </summary>
    private static (int Column, string Content)? ListItem(int indent, string content)
    {
        if (ListItemContentColumn(indent, content) is not { } column)
        {
            return null;
        }

        // A marker holds no whitespace, so the first whitespace ends it.
        var markerEnd = content.IndexOfAny([' ', '\t']);
        var afterMarker = markerEnd < 0 ? string.Empty : content[markerEnd..];
        var (spaces, start) = Indentation(afterMarker);
        return (column, spaces >= 5 ? string.Empty : afterMarker[start..]);
    }

    private static int? ListItemContentColumn(int indent, string content)
    {
        int width;
        if (content.Length > 0 && content[0] is '*' or '+' or '-')
        {
            width = 1;
        }
        else
        {
            var digits = 0;
            while (digits < content.Length && digits < 10 && char.IsAsciiDigit(content[digits]))
            {
                digits++;
            }

            if (digits is 0 or > 9 || digits >= content.Length || content[digits] is not ('.' or ')'))
            {
                return null;
            }

            width = digits + 1;
        }

        if (width == content.Length)
        {
            return indent + width + 1;
        }

        if (content[width] is not (' ' or '\t'))
        {
            return null;
        }

        var (spaces, start) = Indentation(content[width..]);
        return start == content.Length - width || spaces >= 5
            ? indent + width + 1
            : indent + width + spaces;
    }

    /// <summary>
    /// A line's indentation in columns (a tab advancing to the next multiple of four, as
    /// CommonMark counts it) and the offset of its first non-whitespace character.
    /// </summary>
    private static (int Columns, int ContentStart) Indentation(string line)
    {
        var columns = 0;
        var i = 0;
        for (; i < line.Length; i++)
        {
            if (line[i] == ' ')
            {
                columns++;
            }
            else if (line[i] == '\t')
            {
                columns += 4 - (columns % 4);
            }
            else
            {
                break;
            }
        }

        return (columns, i);
    }

    private static int RunLength(string text, int start, char c)
    {
        var end = start;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>
    /// The fence character and run length when <paramref name="content"/> opens a fence:
    /// three or more backticks or tildes, where a backtick fence's info string may not
    /// itself contain a backtick (that line is inline code, not a fence).
    /// </summary>
    private static (char Char, int Length)? OpensFence(string content)
    {
        foreach (var c in (ReadOnlySpan<char>)['`', '~'])
        {
            var run = RunLength(content, 0, c);
            if (run >= 3 && (c == '~' || content.IndexOf('`', run) < 0))
            {
                return (c, run);
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces inline code spans, delimiters included, with spaces so nothing inside
    /// them is extracted. As CommonMark defines a span: a run of backticks opens one and
    /// the next run of exactly the same length closes it (so <c>`` a ` b ``</c> is one
    /// span); a run with no such closer is literal text; and a backslash-escaped backtick
    /// opens nothing. Spans are matched across all of <paramref name="line"/>, which may
    /// be a whole paragraph joined with <c>\n</c>; a line ending inside a span is kept.
    /// </summary>
    private static string BlankInlineCode(string line)
    {
        if (line.IndexOf('`') < 0)
        {
            return line;
        }

        // Every backtick run, in order. A backslash escapes only outside a span, where it
        // matters for an OPENER alone: an odd number of backslashes right before a run
        // makes its first backtick literal, so it opens with one fewer. Inside a span a
        // backslash is literal, so a CLOSER is matched on its full length (`C:\` closes).
        // The backslashes right before a would-be opener are never inside a span: if a
        // span enclosed them, it would enclose the run too, which is then no opener.
        var runs = new List<(int Start, int Length)>();
        for (var i = 0; i < line.Length;)
        {
            if (line[i] != '`')
            {
                i++;
                continue;
            }

            var run = RunLength(line, i, '`');
            runs.Add((i, run));
            i += run;
        }

        var escaped = new bool[runs.Count];
        for (var k = 0; k < runs.Count; k++)
        {
            var backslashes = 0;
            while (runs[k].Start - backslashes > 0 && line[runs[k].Start - backslashes - 1] == '\\')
            {
                backslashes++;
            }

            escaped[k] = backslashes % 2 == 1;
        }

        // For each run as an opener, the index of the next run whose full length equals
        // the opener's: one backward pass, so matching stays linear however many
        // unclosable runs a hostile line holds.
        var closer = new int[runs.Count];
        var lastSeen = new Dictionary<int, int>();
        for (var k = runs.Count - 1; k >= 0; k--)
        {
            var openLength = runs[k].Length - (escaped[k] ? 1 : 0);
            closer[k] = openLength > 0 && lastSeen.TryGetValue(openLength, out var j) ? j : -1;
            lastSeen[runs[k].Length] = k;
        }

        var chars = line.ToCharArray();
        for (var k = 0; k < runs.Count;)
        {
            if (closer[k] < 0)
            {
                k++; // no closer: the run is literal text
                continue;
            }

            var open = runs[k].Start + (escaped[k] ? 1 : 0);
            var close = runs[closer[k]];
            for (var c = open; c < close.Start + close.Length; c++)
            {
                if (chars[c] != '\n')
                {
                    chars[c] = ' '; // line endings survive, so a caller can split lines back out
                }
            }

            k = closer[k] + 1;
        }

        return new string(chars);
    }

    // The characters a backslash escapes in CommonMark (§2.4).
    private static bool IsAsciiPunctuation(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// <summary>
    /// Scans a single (code-free) line for <c>[text](dest)</c> links.
    /// </summary>
    private static void ScanLineLinks(string line, List<ConceptLink> output)
    {
        var chars = line.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '[')
            {
                var parsed = ParseInlineLink(chars, i);
                if (parsed is { } p)
                {
                    var target = StripTitle(p.Dest);
                    output.Add(new ConceptLink(p.Text, target, ConceptLink.Classify(target)));
                    i = p.Next;
                    continue;
                }
            }

            i++;
        }
    }

    /// <summary>
    /// Attempts to parse <c>[text](dest)</c> starting at <paramref name="start"/>
    /// (the <c>[</c>). Returns the text, destination, and index just past the
    /// closing <c>)</c>.
    /// </summary>
    private static InlineLinkMatch? ParseInlineLink(char[] chars, int start)
    {
        // Match the link text up to a balanced `]`.
        var i = start + 1;
        var depth = 1;
        var textStart = i;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c == '\\')
            {
                i++; // skip escaped char
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }

            i++;
        }

        if (depth != 0 || i >= chars.Length)
        {
            return null;
        }

        var text = new string(chars, textStart, i - textStart);

        // Next non-space char must be '('.
        var j = i + 1;
        if (j >= chars.Length || chars[j] != '(')
        {
            return null;
        }

        j++;
        var destStart = j;
        var paren = 1;
        while (j < chars.Length)
        {
            var c = chars[j];
            if (c == '\\')
            {
                j++;
            }
            else if (c == '(')
            {
                paren++;
            }
            else if (c == ')')
            {
                paren--;
                if (paren == 0)
                {
                    break;
                }
            }

            j++;
        }

        if (paren != 0 || j >= chars.Length)
        {
            return null;
        }

        var dest = new string(chars, destStart, j - destStart);
        return new InlineLinkMatch(text, dest, j + 1);
    }

    /// <summary>
    /// Removes an optional <c>"title"</c> (or <c>'title'</c>) suffix from a
    /// link destination.
    /// </summary>
    private static string StripTitle(string dest)
    {
        var d = dest.Trim();
        var idx = d.IndexOfAny([' ', '\t']);
        if (idx >= 0)
        {
            var url = d[..idx];
            var rest = d[idx..].TrimStart();
            if (rest.StartsWith('"') || rest.StartsWith('\''))
            {
                return url;
            }
        }

        return d;
    }

    /// <summary>
    /// Parses a single <c>[n] …</c> citation line.
    /// </summary>
    private static Citation? ParseCitationLine(string line)
    {
        if (!line.StartsWith('['))
        {
            return null;
        }

        var rest = line[1..];
        var close = rest.IndexOf(']');
        if (close < 0)
        {
            return null;
        }

        // Unsigned parse rule: a single leading '+' is stripped before
        // parsing digits, but a leading '-' is never valid for an unsigned
        // value -- any leading '-' is rejected outright (including "-0"). Done
        // by hand rather than via NumberStyles.AllowLeadingSign, which would
        // uniquely accept "-0" for uint.
        var numberText = rest[..close].Trim();
        if (numberText.StartsWith('-'))
        {
            return null;
        }

        var digits = numberText.StartsWith('+') ? numberText[1..] : numberText;
        if (!uint.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var after = rest[(close + 1)..].Trim();

        // If the remainder is itself a markdown link, capture its text and target.
        string? text = null;
        string? target = null;
        var chars = after.ToCharArray();
        var openIdx = Array.IndexOf(chars, '[');
        if (openIdx >= 0)
        {
            var parsed = ParseInlineLink(chars, openIdx);
            if (parsed is { } p)
            {
                text = p.Text;
                target = StripTitle(p.Dest);
            }
        }

        return new Citation(number, text, target, after);
    }

    private readonly record struct InlineLinkMatch(string Text, string Dest, int Next);
}

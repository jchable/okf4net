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
    /// Blocks are recognized as CommonMark defines them, in one line-by-line pass that
    /// follows its block-parsing strategy (§5): each line first matches the open
    /// containers — a block quote by its <c>&gt;</c>, a list item by indentation reaching
    /// its content column — then may open new ones, and what is left is read as a leaf
    /// block, with indentation measured from the innermost container's content:
    /// <list type="bullet">
    /// <item>A container that a line does not match closes, and every block inside it
    /// with it — unless the line is lazy continuation text of an open paragraph.</item>
    /// <item>A fence opens on three or more backticks or tildes, and closes only on a run
    /// of the same character at least as long, indented at most three columns, with
    /// nothing after it but whitespace.</item>
    /// <item>A line indented four or more columns while no paragraph is open is indented
    /// code; inside an open paragraph it is continuation text.</item>
    /// <item>An HTML block (§4.6) is raw HTML: a comment, <c>&lt;script&gt;</c>,
    /// <c>&lt;pre&gt;</c>, <c>&lt;style&gt;</c> or <c>&lt;textarea&gt;</c>, a processing
    /// instruction, a declaration or CDATA runs to the line holding its end marker; a
    /// block-level tag, or any complete tag alone on its line, runs to the next blank line
    /// (and the latter cannot interrupt a paragraph).</item>
    /// <item>Inline code spans and raw inline HTML are blanked by <see cref="BlankInline"/>
    /// over a whole paragraph at once, so either may cross a line ending but never a
    /// block boundary.</item>
    /// </list>
    /// Code and HTML block lines become empty pairs. What is not modelled is what no
    /// consumer here reads: setext headings, link reference definitions, and the
    /// interruption rules that only decide how a paragraph ends, not whether text is code.
    /// </summary>
    private static List<(string Raw, string Blanked)> CodeFreeLinePairs(string body)
    {
        var result = new List<(string, string)>();
        var containers = new List<BlockContainer>();
        var opened = new List<BlockContainer>();

        // Open leaf blocks, each with the number of containers open when it began: it
        // ends as soon as one of them closes.
        (char Char, int Length, int Depth)? fence = null;
        (int Kind, int Depth)? html = null;
        var paragraph = new List<int>();
        var paragraphDepth = 0;

        void EndParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            var joined = string.Join('\n', paragraph.Select(k => result[k].Item1));
            var blanked = BlankInline(joined).Split('\n');
            for (var k = 0; k < paragraph.Count; k++)
            {
                result[paragraph[k]] = (result[paragraph[k]].Item1, blanked[k]);
            }

            paragraph.Clear();
        }

        foreach (var line in LfLines.Split(body))
        {
            // Every per-container step below looks at a bounded stretch of the line, so a
            // line holding thousands of nested markers stays linear; only this is per line.
            var lastText = line.Length - 1;
            while (lastText >= 0 && line[lastText] is ' ' or '\t')
            {
                lastText--;
            }

            (int Star, int Dash, int Underscore)? thematicFacts = null;

            // 1. Match the open containers.
            var pos = 0;
            var matched = 0;
            foreach (var container in containers)
            {
                if (container.Quote)
                {
                    var (indent, start) = Indentation(line, pos, 4);
                    if (indent > 3 || start >= line.Length || line[start] != '>')
                    {
                        break;
                    }

                    pos = QuoteContentStart(line, start);
                }
                else if (pos <= lastText)
                {
                    var end = Advance(line, pos, container.Column);
                    if (Indentation(line, pos, container.Column).Columns < container.Column)
                    {
                        break;
                    }

                    pos = end;
                }

                // A blank remainder keeps a list item open; only a quote needs its marker.
                matched++;
            }

            var allMatched = matched == containers.Count;

            // 2. Inside a fence or an HTML block whose containers all continue.
            if (allMatched && fence is { } open)
            {
                var (indent, start) = Indentation(line, pos, 4);
                var run = RunLength(line, start, open.Char);
                if (start < line.Length && indent <= 3 && run >= open.Length && line.AsSpan(start + run).IsWhiteSpace())
                {
                    fence = null;
                }

                result.Add((string.Empty, string.Empty));
                continue;
            }

            if (allMatched && html is { } block)
            {
                var rest = line.AsSpan(pos);
                if (block.Kind >= 6 && rest.IsWhiteSpace())
                {
                    html = null;
                    result.Add((line, line));
                    continue;
                }

                if (block.Kind <= 5 && EndsHtmlBlock(block.Kind, rest))
                {
                    html = null;
                }

                result.Add((string.Empty, string.Empty));
                continue;
            }

            // 3. Open new containers.
            opened.Clear();
            var inner = pos;
            while (true)
            {
                var (indent, start) = Indentation(line, inner, 4);
                if (indent > 3 || start > lastText)
                {
                    break;
                }

                if (line[start] == '>')
                {
                    opened.Add(new BlockContainer(true, 0));
                    inner = QuoteContentStart(line, start);
                    continue;
                }

                if (!IsThematicBreakAt(line, start, lastText, ref thematicFacts)
                    && ListItemMarkerAt(line, start, lastText) is { } item)
                {
                    opened.Add(new BlockContainer(false, indent + item.Column));
                    inner = item.ContentOffset;
                    continue;
                }

                break;
            }

            var (leafIndent, leafStart) = Indentation(line, inner, 4);
            var leaf = line[leafStart..];
            var leafBlank = inner > lastText;
            var paragraphOpen = paragraph.Count > 0;

            // 4. Close what this line did not continue — unless it is lazy continuation.
            if (!allMatched)
            {
                if (opened.Count == 0 && paragraphOpen && !leafBlank
                    && (leafIndent > 3 || !StartsLeafBlock(leaf, paragraphOpen)))
                {
                    paragraph.Add(result.Count);
                    result.Add((line, line));
                    continue;
                }

                containers.RemoveRange(matched, containers.Count - matched);
                fence = null;
                html = null;
                if (paragraphDepth > matched)
                {
                    EndParagraph();
                }
            }

            if (opened.Count > 0)
            {
                EndParagraph();
                containers.AddRange(opened);
            }

            paragraphOpen = paragraph.Count > 0;

            // 5. The leaf block.
            if (leafBlank)
            {
                EndParagraph();
                result.Add((line, line));
                continue;
            }

            if (leafIndent >= 4)
            {
                if (!paragraphOpen)
                {
                    result.Add((string.Empty, string.Empty));
                    continue;
                }
            }
            else if (OpensFence(leaf) is { } fenceOpen)
            {
                EndParagraph();
                fence = (fenceOpen.Char, fenceOpen.Length, containers.Count);
                result.Add((string.Empty, string.Empty));
                continue;
            }
            else if (HtmlBlockStart(leaf, paragraphOpen) is var kind and > 0)
            {
                EndParagraph();
                html = kind <= 5 && EndsHtmlBlock(kind, leaf) ? null : (kind, containers.Count);
                result.Add((string.Empty, string.Empty));
                continue;
            }
            else if (TryParseAtxHeading(leaf, out _, out _) || ThematicBreak.IsMatch(leaf))
            {
                EndParagraph();
                result.Add((line, BlankInline(line)));
                continue;
            }

            if (paragraph.Count == 0)
            {
                paragraphDepth = containers.Count;
            }

            paragraph.Add(result.Count);
            result.Add((line, line));
        }

        EndParagraph();
        return result;
    }

    /// <summary>An open block quote (<see cref="Quote"/>) or list item, whose content starts <see cref="Column"/> columns past its parent's.</summary>
    private readonly record struct BlockContainer(bool Quote, int Column);

    /// <summary>The offset just past a block quote marker at <paramref name="marker"/> and the one optional space after it.</summary>
    private static int QuoteContentStart(string line, int marker) =>
        marker + 1 < line.Length && line[marker + 1] is ' ' or '\t' ? marker + 2 : marker + 1;

    /// <summary>
    /// When a list item marker starts at <paramref name="start"/>, the column its content
    /// starts at, counted from <paramref name="start"/> as
    /// <see cref="ListItemContentColumn"/> counts it, and the offset of that content:
    /// past the marker and the whitespace after it — past one space only when five or
    /// more follow (the content is then indented code), and the end of the line when
    /// nothing does. Otherwise <c>null</c>. Reads a bounded stretch of the line.
    /// </summary>
    private static (int Column, int ContentOffset)? ListItemMarkerAt(string line, int start, int lastText)
    {
        int width;
        if (line[start] is '*' or '+' or '-')
        {
            width = 1;
        }
        else
        {
            var digits = 0;
            while (start + digits < line.Length && digits < 10 && char.IsAsciiDigit(line[start + digits]))
            {
                digits++;
            }

            if (digits is 0 or > 9 || start + digits >= line.Length || line[start + digits] is not ('.' or ')'))
            {
                return null;
            }

            width = digits + 1;
        }

        var after = start + width;
        if (after > lastText)
        {
            return (width + 1, line.Length);
        }

        if (line[after] is not (' ' or '\t'))
        {
            return null;
        }

        var (spaces, content) = Indentation(line, after, 5);
        return spaces >= 5 ? (width + 1, after + 1) : (width + spaces, content);
    }

    /// <summary>
    /// Whether the rest of <paramref name="line"/> from <paramref name="start"/> is a
    /// thematic break: three or more of one of <c>*</c>, <c>-</c>, <c>_</c>, with only
    /// spaces or tabs besides. <paramref name="facts"/> caches, per line, the last offset
    /// holding anything else for each of the three, so a line of many markers is not
    /// rescanned at each one.
    /// </summary>
    private static bool IsThematicBreakAt(string line, int start, int lastText, ref (int Star, int Dash, int Underscore)? facts)
    {
        var c = line[start];
        if (c is not ('*' or '-' or '_'))
        {
            return false;
        }

        if (facts is null)
        {
            int star = -1, dash = -1, underscore = -1;
            for (var p = 0; p <= lastText; p++)
            {
                var ch = line[p];
                if (ch is ' ' or '\t')
                {
                    continue;
                }

                star = ch == '*' ? star : p;
                dash = ch == '-' ? dash : p;
                underscore = ch == '_' ? underscore : p;
            }

            facts = (star, dash, underscore);
        }

        var lastOther = c switch { '*' => facts.Value.Star, '-' => facts.Value.Dash, _ => facts.Value.Underscore };
        if (lastOther > start)
        {
            return false;
        }

        // Only `c` and whitespace remain, so this count runs at most a few times per line:
        // with three or more the loop that called it stops here.
        var count = 0;
        for (var p = start; p <= lastText && count < 3; p++)
        {
            count += line[p] == c ? 1 : 0;
        }

        return count >= 3;
    }

    /// <summary>The offset reached by consuming <paramref name="columns"/> columns of whitespace from <paramref name="pos"/>.</summary>
    private static int Advance(string line, int pos, int columns)
    {
        var consumed = 0;
        while (consumed < columns && pos < line.Length && line[pos] is ' ' or '\t')
        {
            consumed += line[pos] == '\t' ? 4 - (consumed % 4) : 1;
            pos++;
        }

        return pos;
    }

    /// <summary>Whether <paramref name="leaf"/> (indented at most three columns) starts a block that ends an open paragraph rather than continuing it lazily.</summary>
    private static bool StartsLeafBlock(string leaf, bool paragraphOpen) =>
        OpensFence(leaf) is not null
        || TryParseAtxHeading(leaf, out _, out _)
        || ThematicBreak.IsMatch(leaf)
        || HtmlBlockStart(leaf, paragraphOpen) > 0;

    // The tag names that open an HTML block of kind 1 (raw text, up to its closing tag)
    // and of kind 6 (block-level, up to a blank line), from CommonMark §4.6.
    private static readonly string[] RawTextTags = ["script", "pre", "style", "textarea"];

    private static readonly HashSet<string> BlockLevelTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center",
        "col", "colgroup", "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5",
        "h6", "head", "header", "hr", "html", "iframe", "legend", "li", "link", "main", "menu",
        "menuitem", "nav", "noframes", "ol", "optgroup", "option", "p", "param", "search", "section",
        "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul",
    };

    /// <summary>
    /// The CommonMark HTML block kind (1–7) that <paramref name="leaf"/> opens, or 0. Kind
    /// 7 — a complete tag alone on its line — cannot interrupt a paragraph.
    /// </summary>
    private static int HtmlBlockStart(string leaf, bool paragraphOpen)
    {
        if (leaf.Length < 2 || leaf[0] != '<')
        {
            return 0;
        }

        var closing = leaf[1] == '/';
        var nameStart = closing ? 2 : 1;
        var nameEnd = nameStart;
        while (nameEnd < leaf.Length && (char.IsAsciiLetterOrDigit(leaf[nameEnd]) || leaf[nameEnd] == '-'))
        {
            nameEnd++;
        }

        var name = leaf[nameStart..nameEnd];
        var afterName = nameEnd < leaf.Length ? leaf[nameEnd] : ' ';

        if (!closing && afterName is ' ' or '\t' or '>' && RawTextTags.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (leaf.StartsWith("<!--", StringComparison.Ordinal))
        {
            return 2;
        }

        if (leaf.StartsWith("<?", StringComparison.Ordinal))
        {
            return 3;
        }

        if (leaf.StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            return 5;
        }

        if (leaf[1] == '!' && leaf.Length > 2 && char.IsAsciiLetter(leaf[2]))
        {
            return 4;
        }

        if (name.Length > 0 && BlockLevelTags.Contains(name)
            && (afterName is ' ' or '\t' or '>' || leaf.AsSpan(nameEnd).StartsWith("/>")))
        {
            return 6;
        }

        if (!paragraphOpen && name.Length > 0 && !RawTextTags.Contains(name, StringComparer.OrdinalIgnoreCase)
            && new InlineHtml(leaf).TokenEnd(0) is var end and > 0 && leaf.AsSpan(end).IsWhiteSpace())
        {
            return 7;
        }

        return 0;
    }

    /// <summary>Whether <paramref name="line"/> holds the end marker of an HTML block of kind 1–5.</summary>
    private static bool EndsHtmlBlock(int kind, ReadOnlySpan<char> line) => kind switch
    {
        1 => line.Contains("</script>", StringComparison.OrdinalIgnoreCase)
            || line.Contains("</pre>", StringComparison.OrdinalIgnoreCase)
            || line.Contains("</style>", StringComparison.OrdinalIgnoreCase)
            || line.Contains("</textarea>", StringComparison.OrdinalIgnoreCase),
        2 => line.Contains("-->", StringComparison.Ordinal),
        3 => line.Contains("?>", StringComparison.Ordinal),
        4 => line.Contains('>'),
        _ => line.Contains("]]>", StringComparison.Ordinal),
    };

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
    private static (int Columns, int ContentStart) Indentation(string line) => Indentation(line, 0);

    /// <summary>
    /// As <see cref="Indentation(string)"/>, counting from offset <paramref name="from"/>
    /// and stopping once <paramref name="cap"/> columns are reached — callers only compare
    /// against a small bound, and a line of huge indentation must not be rescanned for each
    /// container it holds.
    /// </summary>
    private static (int Columns, int ContentStart) Indentation(string line, int from, int cap = int.MaxValue)
    {
        var columns = 0;
        var i = from;
        for (; i < line.Length && columns < cap; i++)
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
    /// Replaces inline code spans and raw inline HTML, delimiters included, with spaces so
    /// nothing inside them is extracted; line endings are kept, since
    /// <paramref name="line"/> may be a whole paragraph joined with <c>\n</c>. The two are
    /// read left to right and whichever starts first wins, so <c>`&lt;!--`</c> is code and
    /// the backtick inside <c>&lt;!-- ` --&gt;</c> is HTML.
    ///
    /// A code span, as CommonMark defines it (§6.1): a run of backticks opens one and the
    /// next run of exactly the same length closes it (so <c>`` a ` b ``</c> is one span);
    /// a run with no such closer is literal text; and a backslash-escaped backtick opens
    /// nothing. Raw HTML (§6.6): a comment, processing instruction, declaration, CDATA
    /// section, or open or closing tag — see <see cref="InlineHtml"/>.
    /// </summary>
    private static string BlankInline(string line)
    {
        var hasCode = line.IndexOf('`') >= 0;
        var hasHtml = line.IndexOf('<') >= 0;
        if (!hasCode && !hasHtml)
        {
            return line;
        }

        var chars = line.ToCharArray();
        var (runs, escaped, closer) = hasCode ? CodeSpanRuns(line) : ([], [], []);
        var html = hasHtml ? new InlineHtml(line) : null;
        var k = 0;
        for (var i = 0; i < line.Length;)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] != '`' && IsAsciiPunctuation(line[i + 1]))
            {
                i += 2;
            }
            else if (c == '`')
            {
                // Runs consumed inside an earlier span or HTML token are skipped here.
                while (runs[k].Start + runs[k].Length <= i)
                {
                    k++;
                }

                if (closer[k] < 0)
                {
                    i = runs[k].Start + runs[k].Length; // no closer: literal
                    k++;
                    continue;
                }

                var close = runs[closer[k]];
                Blank(chars, runs[k].Start + (escaped[k] ? 1 : 0), close.Start + close.Length);
                i = close.Start + close.Length;
                k = closer[k] + 1;
            }
            else if (c == '<' && html!.TokenEnd(i) is var end and > 0)
            {
                Blank(chars, i, end);
                i = end;
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int from, int to)
    {
        for (var c = from; c < to; c++)
        {
            if (chars[c] != '\n')
            {
                chars[c] = ' '; // line endings survive, so a caller can split lines back out
            }
        }
    }

    /// <summary>
    /// Raw HTML tokens in a text (CommonMark §6.6), found in time linear in the text:
    /// every search for an end marker or closing quote is a lookup in a table of next
    /// occurrences, built once on first use, so no attempt rescans what another did.
    /// </summary>
    private sealed class InlineHtml(string text)
    {
        private readonly Dictionary<string, int[]> next = [];

        /// <summary>
        /// The offset just past the raw HTML token starting at the <c>&lt;</c> at
        /// <paramref name="i"/>, or <c>-1</c> when none does: <c>&lt;!--&gt;</c>,
        /// <c>&lt;!---&gt;</c> or a comment to <c>--&gt;</c>; <c>&lt;?</c> to <c>?&gt;</c>;
        /// CDATA to <c>]]&gt;</c>; <c>&lt;!</c> and a letter to <c>&gt;</c>; a closing tag;
        /// or an open tag with well-formed attributes. Whitespace inside a tag may include
        /// line endings.
        /// </summary>
        public int TokenEnd(int i)
        {
            var s = text.AsSpan(i);
            if (s.StartsWith("<!-->"))
            {
                return i + 5;
            }

            if (s.StartsWith("<!--->"))
            {
                return i + 6;
            }

            if (s.StartsWith("<!--"))
            {
                return After(Next("-->", i + 4), 3);
            }

            if (s.StartsWith("<?"))
            {
                return After(Next("?>", i + 2), 2);
            }

            if (s.StartsWith("<![CDATA["))
            {
                return After(Next("]]>", i + 9), 3);
            }

            if (s.Length > 2 && s[1] == '!' && char.IsAsciiLetter(s[2]))
            {
                return After(Next(">", i + 2), 1);
            }

            return s.Length > 1 && s[1] == '/' ? ClosingTagEnd(i + 2) : OpenTagEnd(i + 1);
        }

        private static int After(int match, int length) => match < 0 ? -1 : match + length;

        private int TagNameEnd(int j)
        {
            if (j >= text.Length || !char.IsAsciiLetter(text[j]))
            {
                return -1;
            }

            j++;
            while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '-'))
            {
                j++;
            }

            return j;
        }

        private int SkipWhitespace(int j)
        {
            while (j < text.Length && text[j] is ' ' or '\t' or '\n')
            {
                j++;
            }

            return j;
        }

        private int ClosingTagEnd(int j)
        {
            j = TagNameEnd(j);
            if (j < 0)
            {
                return -1;
            }

            j = SkipWhitespace(j);
            return j < text.Length && text[j] == '>' ? j + 1 : -1;
        }

        private int OpenTagEnd(int j)
        {
            j = TagNameEnd(j);
            if (j < 0)
            {
                return -1;
            }

            while (true)
            {
                var afterSpace = SkipWhitespace(j);
                if (afterSpace < text.Length && text[afterSpace] == '>')
                {
                    return afterSpace + 1;
                }

                if (afterSpace + 1 < text.Length && text[afterSpace] == '/' && text[afterSpace + 1] == '>')
                {
                    return afterSpace + 2;
                }

                // An attribute must follow whitespace, and starts with a letter, `_` or `:`.
                if (afterSpace == j || afterSpace >= text.Length
                    || !(char.IsAsciiLetter(text[afterSpace]) || text[afterSpace] is '_' or ':'))
                {
                    return -1;
                }

                j = afterSpace + 1;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] is '_' or '.' or ':' or '-'))
                {
                    j++;
                }

                var equals = SkipWhitespace(j);
                if (equals >= text.Length || text[equals] != '=')
                {
                    continue; // no value
                }

                var value = SkipWhitespace(equals + 1);
                if (value >= text.Length)
                {
                    return -1;
                }

                if (text[value] is '"' or '\'')
                {
                    var quote = Next(text[value] == '"' ? "\"" : "'", value + 1);
                    if (quote < 0)
                    {
                        return -1;
                    }

                    j = quote + 1;
                }
                else
                {
                    j = value;
                    while (j < text.Length && text[j] is not (' ' or '\t' or '\n' or '"' or '\'' or '=' or '<' or '>' or '`'))
                    {
                        j++;
                    }

                    if (j == value)
                    {
                        return -1;
                    }
                }
            }
        }

        /// <summary>The first offset at or after <paramref name="from"/> where <paramref name="marker"/> occurs, or <c>-1</c>.</summary>
        private int Next(string marker, int from)
        {
            if (from > text.Length - marker.Length)
            {
                return -1;
            }

            if (!next.TryGetValue(marker, out var table))
            {
                table = new int[text.Length + 1];
                table[text.Length] = -1;
                for (var p = text.Length - 1; p >= 0; p--)
                {
                    table[p] = text.AsSpan(p).StartsWith(marker) ? p : table[p + 1];
                }

                next[marker] = table;
            }

            return table[from];
        }
    }

    /// <summary>
    /// Every backtick run of <paramref name="line"/> in order, whether a backslash
    /// escapes its first backtick, and for each run as an opener the index of its closing
    /// run, or <c>-1</c>.
    /// </summary>
    private static (List<(int Start, int Length)> Runs, bool[] Escaped, int[] Closer) CodeSpanRuns(string line)
    {
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

        return (runs, escaped, closer);
    }

    // The characters a backslash escapes in CommonMark (§2.4).
    private static bool IsAsciiPunctuation(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// <summary>
    /// Scans a single (code-free) line for <c>[text](dest)</c> links: at each <c>[</c>,
    /// exactly what <see cref="ParseInlineLink"/> would match there, in linear time.
    /// Calling <see cref="ParseInlineLink"/> at every <c>[</c> rescans to the end of the
    /// line each time an opener never closes, which made a line of unclosed brackets
    /// quadratic — on untrusted bundle content. The closer each opener would reach is
    /// precomputed instead (<see cref="BalancedCloses"/>).
    /// </summary>
    private static void ScanLineLinks(string line, List<ConceptLink> output)
    {
        if (line.IndexOf('[') < 0)
        {
            return;
        }

        var chars = line.ToCharArray();
        var closeBracket = BalancedCloses(chars, '[', ']');
        var closeParen = BalancedCloses(chars, '(', ')');
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '['
                && closeBracket[i] is var textEnd and >= 0
                && textEnd + 1 < chars.Length
                && chars[textEnd + 1] == '('
                && closeParen[textEnd + 1] is var destEnd and >= 0)
            {
                var target = StripTitle(new string(chars, textEnd + 2, destEnd - textEnd - 2));
                output.Add(new ConceptLink(new string(chars, i + 1, textEnd - i - 1), target, ConceptLink.Classify(target)));
                i = destEnd + 1;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    /// For each <paramref name="open"/> character, the index of the <paramref name="close"/>
    /// that <see cref="ParseInlineLink"/>'s balanced, escape-aware scan starting there
    /// stops at, or <c>-1</c> when it never closes; other entries are meaningless.
    ///
    /// One left-to-right pass suffices because a scan started just past any opener
    /// visits exactly the positions a scan from the start of the line visits from there
    /// on (a backslash skips the next character either way, and an opener is never a
    /// backslash). So relative depth is global balance: an opener leaving the balance at
    /// <c>L</c> — counted or, when backslash-escaped, not — closes at the first
    /// unescaped closer that brings it to <c>L - 1</c>. Openers wait by level and are
    /// resolved together, each once.
    /// </summary>
    private static int[] BalancedCloses(char[] chars, char open, char close)
    {
        var closes = new int[chars.Length];
        var waiting = new Dictionary<int, List<int>>();
        var balance = 0;

        void Wait(int opener, int level)
        {
            closes[opener] = -1;
            if (!waiting.TryGetValue(level, out var openers))
            {
                waiting[level] = openers = [];
            }

            openers.Add(opener);
        }

        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] == '\\')
            {
                // The escaped character is skipped by every scan, but a scan may still
                // START at it, so an escaped opener waits at the unchanged balance.
                if (k + 1 < chars.Length && chars[k + 1] == open)
                {
                    Wait(k + 1, balance);
                }

                k++;
            }
            else if (chars[k] == open)
            {
                balance++;
                Wait(k, balance);
            }
            else if (chars[k] == close)
            {
                balance--;
                if (waiting.Remove(balance + 1, out var resolved))
                {
                    foreach (var opener in resolved)
                    {
                        closes[opener] = k;
                    }
                }
            }
        }

        return closes;
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

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
    /// running text such as <c>claim.[^key]</c> — skipping fenced code blocks and
    /// inline code spans, and not counting a footnote <b>definition</b>'s own label
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
        foreach (var (raw, blanked) in CodeFreeLinePairs(body))
        {
            var i = 0;
            while (i < blanked.Length && (blanked[i] == ' ' || blanked[i] == '\t'))
            {
                i++;
            }

            if (i + 1 >= blanked.Length || blanked[i] is not ('*' or '-' or '+') || blanked[i + 1] != ' ')
            {
                continue;
            }

            i += 2;
            while (i < blanked.Length && blanked[i] == ' ')
            {
                i++;
            }

            if (i >= blanked.Length || blanked[i] != '[')
            {
                continue;
            }

            if (ParseInlineLink(blanked.ToCharArray(), i) is not { } p)
            {
                continue;
            }

            var target = StripTitle(p.Dest);
            var link = new ConceptLink(p.Text, target, ConceptLink.Classify(target));

            // Same offset in both strings (blanking preserves length).
            var description = raw[p.Next..].Trim().TrimStart('-', '–', '—', ':').Trim();
            entries.Add((link, description));
        }

        return entries;
    }

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
    /// The one implementation of "skip code": each non-fence line of the body,
    /// paired as it was written (<c>Raw</c>) and with its inline code spans blanked
    /// to spaces (<c>Blanked</c>). Blanking replaces one character with one space, so
    /// both strings have the same length and every offset means the same position in
    /// each — which lets a caller find structure on <c>Blanked</c> (so nothing inside
    /// code is mistaken for a link) and still read visible text from <c>Raw</c>.
    /// </summary>
    private static List<(string Raw, string Blanked)> CodeFreeLinePairs(string body)
    {
        var result = new List<(string, string)>();
        char? fence = null;
        foreach (var line in LfLines.Split(body))
        {
            var trimmed = line.TrimStart();
            if (fence is { } f)
            {
                // Inside a fence; look for the closing marker.
                if (trimmed.StartsWith(new string(f, 3), StringComparison.Ordinal))
                {
                    fence = null;
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                fence = '`';
                continue;
            }

            if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = '~';
                continue;
            }

            result.Add((line, BlankInlineCode(line)));
        }

        return result;
    }

    /// <summary>
    /// Replaces inline code spans (backtick-delimited) with spaces so links
    /// inside them are not extracted.
    /// </summary>
    private static string BlankInlineCode(string line)
    {
        var sb = new StringBuilder(line.Length);
        var inCode = false;
        foreach (var c in line)
        {
            if (c == '`')
            {
                inCode = !inCode;
                sb.Append(' ');
            }
            else if (inCode)
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

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

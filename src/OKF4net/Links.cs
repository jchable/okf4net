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
    /// Returns the body's lines with fenced code blocks removed and inline
    /// code spans blanked out.
    /// </summary>
    private static List<string> CodeFreeLines(string body)
    {
        var result = new List<string>();
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

            result.Add(BlankInlineCode(line));
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

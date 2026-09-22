// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// A single, correct line-splitter shared by every place in this codebase
/// that needs <c>'\n'</c>-based line splitting (previously four
/// near-identical, and in one case divergent, private copies).
///
/// Semantics: splits only on <c>'\n'</c>. If the character immediately
/// preceding a <c>'\n'</c> is <c>'\r'</c>, that single <c>'\r'</c> is
/// stripped (i.e. <c>"\r\n"</c> is understood as one line terminator). A
/// lone <c>'\r'</c> NOT immediately followed by <c>'\n'</c> is not a line
/// terminator at all and stays embedded in the line content. A trailing
/// <c>'\n'</c> does not produce a trailing empty line, and the empty
/// string produces no lines.
/// </summary>
internal static class LfLines
{
    internal static List<string> Split(string text)
    {
        var spans = SplitSpans(text);
        var result = new List<string>(spans.Count);
        foreach (var span in spans)
        {
            result.Add(text.Substring(span.Start, span.ContentEnd - span.Start));
        }

        return result;
    }

    /// <summary>
    /// Like <see cref="Split"/>, but returns each line's character span
    /// (relative to <paramref name="text"/>) instead of a copied substring,
    /// plus the offset right after its own terminator -- <c>TerminatorEnd ==
    /// ContentEnd</c> means no terminator (only possible for the FINAL span,
    /// e.g. a file with no trailing newline). The terminator's own text is
    /// <c>text[ContentEnd..TerminatorEnd]</c>: empty, <c>"\n"</c>, or
    /// <c>"\r\n"</c> -- never a bare <c>"\r"</c>, matching this type's
    /// documented "a lone '\r' not immediately followed by '\n' is not a
    /// line terminator at all" rule.
    ///
    /// <see cref="Split"/> is defined in terms of this method (same scan, one
    /// implementation) specifically so a caller that needs to preserve each
    /// line's OWN original terminator byte-for-byte -- <see cref="OKF4net.Internal.FrontmatterBlockEdit"/>,
    /// which must not normalize a document's line endings while editing only
    /// one frontmatter key's block -- can splice the untouched prefix/suffix
    /// verbatim instead of re-joining every line under one chosen separator.
    /// </summary>
    internal static List<LineSpan> SplitSpans(string text)
    {
        var result = new List<LineSpan>();
        if (text.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i;
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            result.Add(new LineSpan(start, end, i + 1));
            start = i + 1;
        }

        if (start < text.Length)
        {
            result.Add(new LineSpan(start, text.Length, text.Length));
        }

        return result;
    }
}

/// <summary>
/// One line's content span <c>[Start, ContentEnd)</c> plus <see cref="TerminatorEnd"/>,
/// the offset right after its terminator. See <see cref="LfLines.SplitSpans"/>.
/// </summary>
internal readonly record struct LineSpan(int Start, int ContentEnd, int TerminatorEnd);

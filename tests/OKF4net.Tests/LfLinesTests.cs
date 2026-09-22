// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="LfLines.SplitSpans"/> (and, by extension,
/// <see cref="LfLines.Split"/>, which is now defined in terms of it): the
/// offset/terminator arithmetic that <see cref="FrontmatterBlockEdit"/>
/// relies on to preserve every untouched line's own terminator byte-for-byte
/// (finding #C7-3). This file did not exist before that refactor -- flagged
/// as a self-review gap in the C7 fix report -- since <see cref="Split"/>
/// itself had no dedicated tests either, only indirect coverage through
/// <see cref="OkfDocument"/>/<see cref="Bundle"/>.
/// </summary>
public class LfLinesTests
{
    private static string Terminator(string text, LineSpan span) =>
        text.Substring(span.ContentEnd, span.TerminatorEnd - span.ContentEnd);

    /// <summary>
    /// The final line of a file with no trailing newline has NO terminator:
    /// <c>TerminatorEnd == ContentEnd</c>. This is the one case
    /// <c>FrontmatterBlockEdit.TerminatorOf</c> has to fall back from (a
    /// closing fence that is also the file's last line, with nothing to copy
    /// a terminator from).
    /// </summary>
    [Fact]
    public void The_final_line_has_no_terminator_when_the_text_does_not_end_in_a_newline()
    {
        var spans = LfLines.SplitSpans("a\nb");

        Assert.Equal(2, spans.Count);
        Assert.Equal("\n", Terminator("a\nb", spans[0]));
        Assert.Equal("a", "a\nb".Substring(spans[0].Start, spans[0].ContentEnd - spans[0].Start));
        Assert.Equal("", Terminator("a\nb", spans[1]));
        Assert.Equal(spans[1].ContentEnd, spans[1].TerminatorEnd);
        Assert.Equal("b", "a\nb".Substring(spans[1].Start, spans[1].ContentEnd - spans[1].Start));
    }

    /// <summary>
    /// A lone <c>\r</c> NOT immediately followed by <c>\n</c> is not a line
    /// terminator at all (per <see cref="LfLines"/>'s documented contract) --
    /// it stays embedded in the line's own content span, and the
    /// terminator recovered from the span is just the trailing <c>\n</c>.
    /// </summary>
    [Fact]
    public void A_lone_CR_not_followed_by_LF_stays_embedded_in_the_content_span()
    {
        var text = "a\rb\n";
        var spans = LfLines.SplitSpans(text);

        var span = Assert.Single(spans);
        Assert.Equal("a\rb", text.Substring(span.Start, span.ContentEnd - span.Start));
        Assert.Equal("\n", Terminator(text, span));
    }

    /// <summary>
    /// <c>"\r\r\n"</c>: only the <c>\r</c> IMMEDIATELY preceding the <c>\n</c>
    /// is part of the terminator -- the earlier one stays embedded in the
    /// content, and the recovered terminator text is the full 2-character
    /// <c>"\r\n"</c> (the second <c>\r</c> plus the <c>\n</c>), never a bare
    /// <c>"\r"</c>.
    /// </summary>
    [Fact]
    public void Only_the_immediately_preceding_CR_is_part_of_a_CRCRLF_lines_terminator()
    {
        var text = "a\r\r\nb";
        var spans = LfLines.SplitSpans(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal("a\r", text.Substring(spans[0].Start, spans[0].ContentEnd - spans[0].Start));
        Assert.Equal("\r\n", Terminator(text, spans[0]));
        Assert.Equal("b", text.Substring(spans[1].Start, spans[1].ContentEnd - spans[1].Start));
        Assert.Equal("", Terminator(text, spans[1]));
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests for the viewer's HTML-safe JSON string escaping. The generated page
/// embeds its payload inside a &lt;script&gt; element, so escaping here is a
/// security boundary, not a formatting detail (design spec §8.1).
/// </summary>
public class HtmlSafeJsonTests
{
    [Fact]
    public void Quote_wraps_the_value_in_double_quotes()
        => Assert.Equal("\"hello\"", HtmlSafeJson.Quote("hello"));

    [Fact]
    public void Quote_escapes_quotes_and_backslashes()
        => Assert.Equal("\"a\\\"b\\\\c\"", HtmlSafeJson.Quote("a\"b\\c"));

    [Fact]
    public void Quote_escapes_control_characters()
        => Assert.Equal("\"a\\nb\\tc\"", HtmlSafeJson.Quote("a\nb\tc"));

    // --- security: script-container breakout (spec §8.1) ---

    [Fact]
    public void Quote_escapes_a_closing_script_tag_so_it_cannot_break_out()
    {
        var hostile = "</script><img src=x onerror=alert(1)>";
        var quoted = HtmlSafeJson.Quote(hostile);

        Assert.DoesNotContain("</script", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_escapes_angle_brackets_and_ampersands()
    {
        var quoted = HtmlSafeJson.Quote("<&>");
        Assert.Equal("\"\\u003c\\u0026\\u003e\"", quoted);
    }

    [Fact]
    public void Quote_escapes_the_line_and_paragraph_separators_that_break_js_string_literals()
    {
        var quoted = HtmlSafeJson.Quote("a\u2028b\u2029c");
        Assert.Equal("\"a\\u2028b\\u2029c\"", quoted);
    }

    // --- robustness: lone UTF-16 surrogates cannot be emitted raw ---

    [Fact]
    public void Quote_escapes_a_lone_high_surrogate()
    {
        var quoted = HtmlSafeJson.Quote("a\ud800b");
        Assert.Equal("\"a\\ud800b\"", quoted);
    }

    [Fact]
    public void Quote_escapes_a_lone_low_surrogate()
    {
        var quoted = HtmlSafeJson.Quote("a\udc00b");
        Assert.Equal("\"a\\udc00b\"", quoted);
    }

    [Fact]
    public void Quote_escapes_a_high_surrogate_at_end_of_string()
    {
        var quoted = HtmlSafeJson.Quote("a\ud800");
        Assert.Equal("\"a\\ud800\"", quoted);
    }

    [Fact]
    public void Quote_leaves_a_valid_surrogate_pair_untouched()
    {
        var quoted = HtmlSafeJson.Quote("\ud83d\ude00");
        Assert.Equal("\"\ud83d\ude00\"", quoted);
    }

    [Fact]
    public void Quote_leaves_ordinary_markdown_untouched()
        => Assert.Equal("\"# Title\\n\\n- item\"", HtmlSafeJson.Quote("# Title\n\n- item"));
}

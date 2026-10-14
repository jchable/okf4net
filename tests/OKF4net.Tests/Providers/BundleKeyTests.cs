// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyTests
{
    [Theory]
    [InlineData("a.md", new[] { "a.md" })]
    [InlineData("tables/orders.md", new[] { "tables", "orders.md" })]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("%2e%2e/x.md", new[] { "%2e%2e", "x.md" })]           // never percent-decoded
    [InlineData("tables/index.md/x.md", new[] { "tables", "index.md", "x.md" })]
    [InlineData("sub\u2028dir/log.md", new[] { "sub\u2028dir", "log.md" })] // U+2028 is a separator, not a control: layer 1 keeps it (decision 7)
    [InlineData("café.md", new[] { "café.md" })]
    public void Parses_valid_keys_into_segments(string raw, string[] segments)
    {
        Assert.True(BundleKey.TryParse(raw, out var key, out var reason), reason);
        Assert.Equal(segments, key!.Segments);
        Assert.Equal(raw, key.ToString());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/a.md", "leading")]
    [InlineData("a.md/", "trailing")]
    [InlineData("a//b.md", "empty segment")]
    [InlineData("./a.md", "'.' segment")]
    [InlineData("a/../b.md", "'..' segment")]
    [InlineData("a\\b.md", "backslash")]
    [InlineData("a\0.md", "control")]
    [InlineData("a\u001f.md", "control")]
    [InlineData("a\u007f.md", "control")]
    [InlineData("a\u0085.md", "control")]
    [InlineData("a\u009f.md", "control")]
    public void Rejects_base_rule_violations(string raw, string reasonFragment)
    {
        Assert.False(BundleKey.TryParse(raw, out var key, out var reason));
        Assert.Null(key);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Length_bound_is_1024_utf8_bytes_inclusive()
    {
        var ascii1024 = new string('a', 1021) + ".md"; // 1024 bytes
        Assert.True(BundleKey.TryParse(ascii1024, out var key, out _));
        Assert.Equal(1024, key!.Utf8ByteCount);
        var ascii1025 = new string('a', 1022) + ".md";
        Assert.False(BundleKey.TryParse(ascii1025, out _, out var reason));
        Assert.Contains("1024", reason);
        // 'é' is 2 bytes in UTF-8: 512 of them = 1024 bytes, 513 = 1026.
        Assert.True(BundleKey.TryParse(new string('é', 512), out _, out _));
        Assert.False(BundleKey.TryParse(new string('é', 513), out _, out _));
    }

    [Fact]
    public void Equality_and_ordering_are_ordinal_and_case_sensitive()
    {
        var a = BundleKey.Parse("A/x.md");
        var b = BundleKey.Parse("a/x.md");
        Assert.NotEqual(a, b);
        Assert.True(a.CompareTo(b) < 0); // 'A' (0x41) < 'a' (0x61)
        Assert.Equal(BundleKey.Parse("a/x.md"), b);
        Assert.Equal(BundleKey.Parse("a/x.md").GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Name_parent_markdown_and_prefix_are_segment_bounded()
    {
        var key = BundleKey.Parse("tables/orders.md");
        Assert.Equal("orders.md", key.Name);
        Assert.Equal(BundleKey.Parse("tables"), key.Parent);
        Assert.Null(BundleKey.Parse("a.md").Parent);
        Assert.True(key.IsMarkdown);
        Assert.False(BundleKey.Parse("tables/orders.MD").IsMarkdown); // ordinal, like MarkdownPaths
        Assert.True(key.StartsWith(BundleKey.Parse("tables")));
        Assert.False(BundleKey.Parse("tablesx/orders.md").StartsWith(BundleKey.Parse("tables")));
        Assert.Equal(BundleKey.Parse("tables/orders.md/extra"), key.Append("extra"));
        Assert.Throws<BundleKeyException>(() => key.Append("a/b"));
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for link-scanning and citation-extraction behaviour:
/// <c>LinkScanner.ExtractLinks</c>/<c>LinkScanner.ExtractCitations</c> and
/// <c>ConceptLink.Classify</c>/<c>ConceptLink.Resolve</c>.
/// </summary>
public class LinksTests
{
    [Fact]
    public void Classify_link_kinds()
    {
        Assert.Equal(LinkKind.Absolute, ConceptLink.Classify("/tables/users.md"));
        Assert.Equal(LinkKind.Relative, ConceptLink.Classify("./other.md"));
        Assert.Equal(LinkKind.Relative, ConceptLink.Classify("../sibling.md"));
        Assert.Equal(LinkKind.External, ConceptLink.Classify("https://example.com"));
        Assert.Equal(LinkKind.External, ConceptLink.Classify("mailto:a@b.com"));
        Assert.Equal(LinkKind.Anchor, ConceptLink.Classify("#section"));
    }

    [Fact]
    public void Extract_inline_links()
    {
        var body = "See [customers](/tables/customers.md) and [docs](https://example.com \"title\").";
        var links = LinkScanner.ExtractLinks(body);
        Assert.Equal(2, links.Count);
        Assert.Equal("customers", links[0].Text);
        Assert.Equal("/tables/customers.md", links[0].Target);
        Assert.Equal(LinkKind.Absolute, links[0].Kind);
        // Title stripped from the second link.
        Assert.Equal("https://example.com", links[1].Target);
    }

    [Fact]
    public void Links_inside_code_are_ignored()
    {
        var body = "Real [a](/a.md).\n\n```\nNot a [link](/b.md) in code.\n```\n\nInline `[c](/c.md)` ignored.\n";
        var links = LinkScanner.ExtractLinks(body);
        var targets = links.Select(l => l.Target).ToList();
        Assert.Equal(new[] { "/a.md" }, targets);
    }

    [Fact]
    public void Resolve_absolute_link()
    {
        var source = ConceptId.Parse("tables/orders");
        var link = new ConceptLink("customers", "/tables/customers.md", LinkKind.Absolute);
        Assert.Equal(ConceptId.Parse("tables/customers"), link.Resolve(source));
    }

    [Fact]
    public void Resolve_relative_link()
    {
        var source = ConceptId.Parse("tables/orders");
        var link = new ConceptLink("neighbor", "./customers.md", LinkKind.Relative);
        Assert.Equal(ConceptId.Parse("tables/customers"), link.Resolve(source));

        var up = new ConceptLink("up", "../datasets/sales.md", LinkKind.Relative);
        Assert.Equal(ConceptId.Parse("datasets/sales"), up.Resolve(source));
    }

    [Fact]
    public void Protocol_relative_url_is_external()
    {
        Assert.Equal(LinkKind.External, ConceptLink.Classify("//cdn.example.com/x.js"));
    }

    [Fact]
    public void Absolute_link_normalizes_dot_segments()
    {
        var source = ConceptId.Parse("a/b");
        var link = new ConceptLink("x", "/tables/../datasets/sales.md", LinkKind.Absolute);
        Assert.Equal(ConceptId.Parse("datasets/sales"), link.Resolve(source));
    }

    [Fact]
    public void External_links_do_not_resolve()
    {
        var source = ConceptId.Parse("a");
        var link = new ConceptLink("x", "https://example.com", LinkKind.External);
        Assert.Null(link.Resolve(source));
    }

    [Fact]
    public void Citations_section_parsed()
    {
        var body = "Prose.\n\n# Citations\n\n[1] [BigQuery schema](https://bq.example/schema)\n[2] [Runbook](https://wiki.acme.internal/runbook)\n";
        var citations = LinkScanner.ExtractCitations(body);
        Assert.Equal(2, citations.Count);
        Assert.Equal(1u, citations[0].Number);
        Assert.Equal("BigQuery schema", citations[0].Text);
        Assert.Equal("https://bq.example/schema", citations[0].Target);
        Assert.Equal(2u, citations[1].Number);
    }

    [Fact]
    public void Citations_stop_at_next_heading()
    {
        var body = "# Citations\n[1] [a](https://a)\n\n# Other\n[2] [b](https://b)\n";
        var citations = LinkScanner.ExtractCitations(body);
        Assert.Single(citations);
    }

    [Fact]
    public void Citation_number_accepts_a_leading_plus_but_rejects_a_leading_minus()
    {
        // The unsigned citation-number parse strips one leading '+' before
        // parsing digits, but never strips a leading '-' -- for an unsigned
        // value that's simply an invalid digit, so ANY leading '-' is
        // rejected (including "-0", unlike .NET's NumberStyles.AllowLeadingSign,
        // which uniquely accepts "-0" for uint -- verified empirically and
        // avoided below).
        var plus = LinkScanner.ExtractCitations("# Citations\n[+3] Src\n");
        Assert.Single(plus);
        Assert.Equal(3u, plus[0].Number);

        var minus = LinkScanner.ExtractCitations("# Citations\n[-3] Src\n");
        Assert.Empty(minus);
    }

    [Fact]
    public void Document_links_and_citations_integration()
    {
        var doc = OkfDocument.Parse(
            "---\ntype: BigQuery Table\n---\n\nJoined with [customers](/tables/customers.md).\n\n# Citations\n[1] [BQ](https://bq)\n");
        // links() returns every body link, including the one in the citation list.
        Assert.Equal(2, doc.Links().Count);
        var internalLinks = doc.Links().Where(l => l.Kind == LinkKind.Absolute).ToList();
        Assert.Single(internalLinks);
        Assert.Single(doc.Citations());
    }

    /// <summary>
    /// Bundle content is untrusted input, and link scanning used to restart a balanced
    /// scan to the end of the line at every <c>[</c>: a line of unclosed brackets was
    /// quadratic (~11 s for a 200 KB <c>[a[a[a…</c> in <c>okf validate</c>). Both halves
    /// of a link are covered — brackets that never close, and link text followed by a
    /// <c>(</c> that never closes. The bound is deliberately loose: the quadratic scan
    /// takes many seconds on these inputs, a linear one milliseconds.
    /// </summary>
    [Theory]
    [InlineData("[a")]
    [InlineData("[a](")]
    public void Unclosed_link_syntax_is_scanned_in_linear_time(string unit)
    {
        var body = string.Concat(Enumerable.Repeat(unit, 300_000 / unit.Length)) + " [b](/b.md)\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        LinkScanner.ExtractLinks(body);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// The linear scan must find exactly the links the original restart-at-every-bracket
    /// scan found — `okf graph` output is golden-locked, and every link feeds validation.
    /// <see cref="ReferenceScan"/> is that original algorithm, kept here as the oracle,
    /// and the two are compared on random lines over the characters that steer it:
    /// brackets, parentheses, backslash escapes, spaces and quotes (titles).
    /// </summary>
    [Fact]
    public void Linear_link_scan_matches_the_original_scan_on_random_lines()
    {
        var alphabet = "[]()\\ a\"".ToCharArray();
        var random = new Random(20260914);
        for (var n = 0; n < 20_000; n++)
        {
            var chars = new char[random.Next(0, 24)];
            for (var k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[random.Next(alphabet.Length)];
            }

            var line = new string(chars);
            var expected = ReferenceScan(line);
            var actual = LinkScanner.ExtractLinks(line).Select(l => (l.Text, l.Target)).ToList();
            Assert.True(expected.SequenceEqual(actual), $"line {line}: expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
        }
    }

    /// <summary>The link scan as it was before it was made linear, verbatim in behaviour.</summary>
    private static List<(string Text, string Target)> ReferenceScan(string line)
    {
        var output = new List<(string, string)>();
        var chars = line.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '[' && Parse(chars, i) is { } p)
            {
                output.Add(p.Link);
                i = p.Next;
                continue;
            }

            i++;
        }

        return output;

        static ((string, string) Link, int Next)? Parse(char[] chars, int start)
        {
            var i = start + 1;
            var depth = 1;
            while (i < chars.Length)
            {
                if (chars[i] == '\\')
                {
                    i++;
                }
                else if (chars[i] == '[')
                {
                    depth++;
                }
                else if (chars[i] == ']' && --depth == 0)
                {
                    break;
                }

                i++;
            }

            if (depth != 0 || i >= chars.Length || i + 1 >= chars.Length || chars[i + 1] != '(')
            {
                return null;
            }

            var text = new string(chars, start + 1, i - start - 1);
            var j = i + 2;
            var paren = 1;
            while (j < chars.Length)
            {
                if (chars[j] == '\\')
                {
                    j++;
                }
                else if (chars[j] == '(')
                {
                    paren++;
                }
                else if (chars[j] == ')' && --paren == 0)
                {
                    break;
                }

                j++;
            }

            if (paren != 0 || j >= chars.Length)
            {
                return null;
            }

            var dest = new string(chars, i + 2, j - i - 2).Trim();
            var space = dest.IndexOfAny([' ', '\t']);
            if (space >= 0 && dest[space..].TrimStart() is var rest && (rest.StartsWith('"') || rest.StartsWith('\'')))
            {
                dest = dest[..space];
            }

            return ((text, dest), j + 1);
        }
    }
}

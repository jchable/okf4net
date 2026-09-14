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

    private static List<string> Targets(string body) =>
        LinkScanner.ExtractLinks(body).Select(l => l.Target).ToList();

    /// <summary>
    /// A fence closes only on a run of the same character at least as long as the one
    /// that opened it (CommonMark §4.5). A four-backtick fence is how markdown about
    /// markdown shows a three-backtick one, so the inner run is content.
    /// </summary>
    [Fact]
    public void A_shorter_fence_inside_a_longer_one_does_not_close_it()
    {
        Assert.Equal(["/after.md"], Targets("````markdown\n```\n[in](/in.md)\n```\n````\n\n[after](/after.md)\n"));
    }

    /// <summary>A closing fence carries no info string, so <c>```python</c> inside a fence is content.</summary>
    [Fact]
    public void A_fence_line_with_an_info_string_does_not_close_a_fence()
    {
        Assert.Equal(["/after.md"], Targets("```\n```python\n[in](/in.md)\n```\n\n[after](/after.md)\n"));
    }

    /// <summary>
    /// A code span opens on a backtick run and closes on the next run of the SAME length
    /// (CommonMark §6.1), so a double-backtick span can hold a single backtick.
    /// </summary>
    [Fact]
    public void A_double_backtick_code_span_is_code_throughout()
    {
        Assert.Equal(["/after.md"], Targets("Use `` a ` [in](/in.md) `` then [after](/after.md).\n"));
    }

    /// <summary>
    /// Bundle content is untrusted input. Searching for each opener's closer from scratch
    /// is quadratic: a line of backtick runs of distinct lengths (none closable) took 14 s
    /// to validate at 1.4 MB. Matching must stay linear. The bound is deliberately loose —
    /// the quadratic scan takes tens of seconds on this input, a linear one milliseconds.
    /// </summary>
    [Fact]
    public void Unclosable_backtick_runs_are_scanned_in_linear_time()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 1400; i++)
        {
            sb.Append('`', i).Append(' ');
        }

        sb.Insert(sb.Length, " x", 200_000).Append(" [a](/a.md)");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var targets = Targets(sb.ToString());
        watch.Stop();

        Assert.Equal(["/a.md"], targets);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// Raised in review of #99: the ATX heading regex's lazy <c>(.*?)</c> retried its
    /// optional closing sequence at every character, so this very input — <c># a</c>,
    /// 150 000 spaces, then <c>x</c> — took 25 s to scan, and the scan runs on every line
    /// outside code. Same loose bound as above.
    /// </summary>
    [Fact]
    public void A_long_heading_line_is_scanned_in_linear_time()
    {
        var body = "# a" + new string(' ', 150_000) + "x\n[a](/a.md)\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var targets = Targets(body);
        watch.Stop();

        Assert.Equal(["/a.md"], targets);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// Raised in review of #99. A closing fence may be indented at most three columns
    /// past its container (CommonMark §4.5); a further-indented run is content.
    /// </summary>
    [Fact]
    public void A_closing_fence_indented_four_columns_is_content()
    {
        Assert.Equal(["/after.md"], Targets("```\n    ```\n[in](/in.md)\n```\n[after](/after.md)\n"));
    }

    /// <summary>
    /// Raised in review of #99. Four columns of indent inside a paragraph is paragraph
    /// continuation: neither an indented code block (which cannot interrupt a paragraph)
    /// nor a fence (indented too far) — so it must not hide the rest of the document.
    /// </summary>
    [Fact]
    public void An_indented_fence_line_inside_a_paragraph_is_paragraph_text()
    {
        Assert.Equal(["/a.md"], Targets("Run this:\n    ```\nSee [a](/a.md).\n"));
    }

    /// <summary>
    /// Raised in review of #99. A fence opened inside a list item ends with the item; an
    /// unclosed one used to hide everything to the end of the document.
    /// </summary>
    [Fact]
    public void A_fence_inside_a_list_item_ends_with_the_item()
    {
        Assert.Equal(["/x.md"], Targets("- a\n\n  ```\n  [in](/in.md)\n\nafter [x](/x.md)\n"));
    }

    /// <summary>
    /// Raised in review of #99. An indented code block needs only that no paragraph is
    /// open — a heading or a closing fence ends one as surely as a blank line — and
    /// indentation counts from the list item's content, so a list ended by an unindented
    /// fence, or by a line indented less than a <c>10.</c> item's content, no longer
    /// shields what follows. The last case is code inside an item: four columns past its
    /// content.
    /// </summary>
    [Theory]
    [InlineData("# Example\n    [in](/in.md)\n")]
    [InlineData("```\nx\n```\n    [in](/in.md)\n")]
    [InlineData("- item\n\n```\nx\n```\n\n    [in](/in.md)\n")]
    [InlineData("10. item\n\n  para\n\n    [in](/in.md)\n")]
    [InlineData("* a\n\n      [in](/in.md)\n")]
    public void Indented_code_is_recognized_wherever_no_paragraph_is_open(string body)
    {
        Assert.Empty(Targets(body));
    }

    /// <summary>
    /// Raised by Copilot on #99. A list item's content can open a fence on the marker's
    /// own line; the fence's container is then that item.
    /// </summary>
    [Fact]
    public void A_fence_opened_on_a_list_marker_line_is_code()
    {
        Assert.Equal(["/after.md"], Targets("* ```\n  [in](/in.md)\n  ```\n\n[after](/after.md)\n"));
    }

    /// <summary>
    /// Raised by Copilot on #99. A code span may cross a line ending inside its paragraph
    /// (CommonMark §6.1), so a link on the span's second line is code.
    /// </summary>
    [Fact]
    public void A_code_span_crossing_a_line_ending_is_code()
    {
        Assert.Equal(["/after.md"], Targets("Use `a\n[in](/in.md)` here, then [after](/after.md).\n"));
    }

    /// <summary>
    /// A span never crosses a block boundary: a blank line, or a list item interrupting
    /// the paragraph, leaves each backtick unmatched and literal.
    /// </summary>
    [Theory]
    [InlineData("A stray ` here.\n\n[a](/a.md) and ` there.\n")]
    [InlineData("A stray ` here.\n* [a](/a.md) and ` there.\n")]
    [InlineData("# A stray ` here\n[a](/a.md) and ` there.\n")]
    public void A_code_span_does_not_cross_a_block_boundary(string body)
    {
        Assert.Equal(["/a.md"], Targets(body));
    }

    /// <summary>A backtick run with no closing run of the same length is literal text, not code to the end of the line.</summary>
    [Fact]
    public void An_unmatched_backtick_is_literal_text()
    {
        Assert.Equal(["/a.md"], Targets("A stray ` backtick, then [a](/a.md).\n"));
    }

    /// <summary>
    /// A line indented four spaces after a blank line, outside any list, is an indented
    /// code block (CommonMark §4.4).
    /// </summary>
    [Fact]
    public void Links_in_an_indented_code_block_are_ignored()
    {
        Assert.Equal(["/after.md"], Targets("Para.\n\n    [in](/in.md)\n\n[after](/after.md)\n"));
    }

    /// <summary>
    /// Inside a list, four spaces of indentation is the item's own content — a nested
    /// item or a continuation paragraph — not code. Index files nest exactly like this.
    /// </summary>
    [Fact]
    public void Indented_content_inside_a_list_is_not_code()
    {
        Assert.Equal(
            ["/a.md", "/b.md", "/c.md"],
            Targets("* [a](/a.md)\n    * [b](/b.md)\n\n    More about it, see [c](/c.md).\n"));
    }

    /// <summary>
    /// A block quote is a container (CommonMark §5.1): what follows its <c>&gt;</c> is
    /// read as a document of its own, so a fence or indented code inside it is code, and
    /// that code ends with the quote. Nested with list items either way round.
    /// </summary>
    [Theory]
    [InlineData("> ```\n> [in](/in.md)\n> ```\n\n[after](/after.md)\n")]
    [InlineData("> Quote.\n>\n>     [in](/in.md)\n\n[after](/after.md)\n")]
    [InlineData("> ```\n> [in](/in.md)\n\n[after](/after.md)\n")]
    [InlineData("> ```\n[after](/after.md)\n")]
    [InlineData("> - item\n>   ```\n>   [in](/in.md)\n>   ```\n\n[after](/after.md)\n")]
    [InlineData("- item\n  > ```\n  > [in](/in.md)\n  > ```\n\n[after](/after.md)\n")]
    [InlineData(">> ```\n>> [in](/in.md)\n>> ```\n\n[after](/after.md)\n")]
    [InlineData("> Use `a\n> [in](/in.md)` here, [after](/after.md)\n")]
    public void Code_inside_a_block_quote_is_code(string body)
    {
        Assert.Equal(["/after.md"], Targets(body));
    }

    /// <summary>
    /// What a quote renders is still read: links in quoted prose, including a lazy
    /// continuation line that omits the <c>&gt;</c>.
    /// </summary>
    [Theory]
    [InlineData("> See [a](/a.md).\n> And [b](/b.md).\n")]
    [InlineData("> See [a](/a.md)\ncontinued lazily, [b](/b.md).\n")]
    [InlineData("> - [a](/a.md)\n> - [b](/b.md)\n")]
    public void Links_inside_a_block_quote_are_links(string body)
    {
        Assert.Equal(["/a.md", "/b.md"], Targets(body));
    }

    /// <summary>
    /// An HTML block (CommonMark §4.6) is raw HTML, not markdown, so nothing inside it is
    /// a link: comments, <c>&lt;script&gt;</c>/<c>&lt;pre&gt;</c> and the like up to their
    /// closing tag (a blank line does not end them), processing instructions,
    /// declarations, CDATA, and — up to the next blank line — a block-level tag or any
    /// complete tag alone on its line. An HTML block inside a list item ends with the item.
    /// </summary>
    [Theory]
    [InlineData("<!--\n[in](/in.md)\n-->\n[after](/after.md)\n")]
    [InlineData("<!-- [in](/in.md) -->\n[after](/after.md)\n")]
    [InlineData("<script>\nx = '[in](/in.md)'\n\n[in](/in.md)\n</script>\n[after](/after.md)\n")]
    [InlineData("<PRE>\n[in](/in.md)\n</pre>\n[after](/after.md)\n")]
    [InlineData("<?php\n[in](/in.md)\n?>\n[after](/after.md)\n")]
    [InlineData("<!DOCTYPE html [in](/in.md)>\n[after](/after.md)\n")]
    [InlineData("<![CDATA[\n[in](/in.md)\n]]>\n[after](/after.md)\n")]
    [InlineData("<div>\n[in](/in.md)\n</div>\n\n[after](/after.md)\n")]
    [InlineData("<span class=\"x\">\n[in](/in.md)\n\n[after](/after.md)\n")]
    [InlineData("- <pre>\n  [in](/in.md)\n\n[after](/after.md)\n")]
    [InlineData("> <!--\n> [in](/in.md)\n\n[after](/after.md)\n")]
    [InlineData("</pre>\n[in](/in.md)\n\n[after](/after.md)\n")]
    public void Nothing_inside_an_html_block_is_a_link(string body)
    {
        Assert.Equal(["/after.md"], Targets(body));
    }

    /// <summary>
    /// The limits of an HTML block: markdown after the blank line that ends a block-level
    /// tag is markdown again (the usual <c>&lt;details&gt;</c> pattern); a tag alone on
    /// its line cannot interrupt a paragraph, so the paragraph goes on; and a block-level
    /// tag can, so the paragraph ends before it.
    /// </summary>
    [Theory]
    [InlineData("<details>\n<summary>S</summary>\n\n[a](/a.md) and [b](/b.md)\n\n</details>\n", "/a.md,/b.md")]
    [InlineData("Para [a](/a.md)\n<span>\n[b](/b.md)\n", "/a.md,/b.md")]
    [InlineData("Para [a](/a.md)\n<div>\n[in](/in.md)\n", "/a.md")]
    public void An_html_block_ends_where_commonmark_ends_it(string body, string expected)
    {
        Assert.Equal(expected.Split(','), Targets(body));
    }

    /// <summary>
    /// Raw HTML inline in a paragraph (CommonMark §6.6) is not markdown either: a comment
    /// (also across a line ending), a tag's attribute values. Whichever of a code span or
    /// raw HTML starts first wins.
    /// </summary>
    [Theory]
    [InlineData("Text <!-- [in](/in.md) --> then [after](/after.md).\n")]
    [InlineData("Text <!-- start\n[in](/in.md) -->\nthen [after](/after.md).\n")]
    [InlineData("An <a title=\"[in](/in.md)\">x</a> and [after](/after.md).\n")]
    [InlineData("Use `<!--` then [after](/after.md) -->.\n")]
    [InlineData("Text <!-- `x --> [after](/after.md) `\n")]
    public void Raw_inline_html_is_not_markdown(string body)
    {
        Assert.Equal(["/after.md"], Targets(body));
    }

    /// <summary>
    /// What only looks like HTML stays text: an unclosed comment, a bare <c>&lt;</c>, an
    /// email autolink, and an escaped <c>\&lt;</c>.
    /// </summary>
    [Theory]
    [InlineData("Text <!-- never closed [a](/a.md)\n")]
    [InlineData("A < b and <not a tag [a](/a.md)\n")]
    [InlineData("Mail <someone@example.com> and [a](/a.md)\n")]
    [InlineData("\\<!-- [a](/a.md) -->\n")]
    public void Html_lookalikes_are_text(string body)
    {
        Assert.Equal(["/a.md"], Targets(body));
    }

    /// <summary>
    /// Raised in review of dbbd3b1. Tabs count to the next multiple of four from the start
    /// of the line, and a container that needs only part of a tab leaves the rest as
    /// indentation (CommonMark §2.2): here the two list items consume the first tab, so
    /// the second makes the last line indented code; after a quote marker and its space,
    /// a tab reaches only column 4, so the line stays prose.
    /// </summary>
    [Theory]
    [InlineData("- a\n  - b\n\n\t\tSELECT [in](/in.md)\n\n[after](/after.md)\n", "/after.md")]
    [InlineData("> \tMore [a](/a.md)\n", "/a.md")]
    [InlineData("1.\tStep\n\n        code [in](/in.md)\n\n[after](/after.md)\n", "/after.md")]
    public void Tabs_count_from_the_start_of_the_line_and_may_be_partly_consumed(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// Raised in review of dbbd3b1: the paragraph rules that decide where code spans and
    /// inline HTML may reach. A setext underline ends its paragraph, so a backtick above it
    /// cannot pair with one below; an ordered list not starting at 1 cannot interrupt a
    /// paragraph, so <c>2) &lt;div&gt;</c> is paragraph text, not an HTML block; and a
    /// quote's <c>&gt;</c> markers are not part of the paragraph's text, so a tag's
    /// attribute can continue on the next quoted line.
    /// </summary>
    [Theory]
    [InlineData("Setup `x\n===\n[a](/a.md) and `y`\n", "/a.md")]
    [InlineData("Note\n2) <div>[a](/a.md)\n", "/a.md")]
    [InlineData("> Note\n2) <div>[in](/in.md)\n\n[after](/after.md)\n", "/after.md")]
    [InlineData("> A `span\n===\n[in](/in.md)` end [after](/after.md)\n", "/after.md")]
    [InlineData("> Use <b\n> title=\"[in](/in.md)\">x</b> [after](/after.md)\n", "/after.md")]
    public void Paragraph_boundaries_follow_commonmark(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// Raised in review of dbbd3b1. A link reference definition (CommonMark §4.7) at the
    /// start of a paragraph is not rendered, so its destination and title are not text;
    /// what follows it in the paragraph is. A <c>[^label]:</c> line is a footnote
    /// definition (GFM), whose text is rendered, and a definition cannot interrupt a
    /// paragraph.
    /// </summary>
    [Theory]
    [InlineData("[ref]: /url \"[in](/in.md)\"\n[after](/after.md)\n", "/after.md")]
    [InlineData("  [ref]: <[in](/in.md)>\n\n[after](/after.md)\n", "/after.md")]
    [InlineData("[^note]: See [a](/a.md).\n", "/a.md")]
    [InlineData("Prose.\n[ref]: /url \"[a](/a.md)\"\n", "/a.md")]
    public void Link_reference_definitions_are_not_text(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// Raised in review of dbbd3b1. A list item that begins with a blank line ends at the
    /// next blank line (CommonMark §5.2), so indentation after that is measured from the
    /// margin again and four columns is indented code.
    /// </summary>
    [Fact]
    public void An_empty_list_item_ends_at_a_blank_line()
    {
        Assert.Equal(["/after.md"], Targets("-\n\n    [in](/in.md)\n\n[after](/after.md)\n"));
    }

    /// <summary>
    /// Raised in review of dbbd3b1: every line matched every open container, so deep
    /// nesting followed by many blank lines was quadratic (~20 s for 80 000 nested items
    /// and 160 000 blank lines). Nesting is capped, as markdown-it caps it.
    /// </summary>
    [Fact]
    public void Deep_nesting_followed_by_many_lines_is_scanned_in_linear_time()
    {
        var body = string.Concat(Enumerable.Repeat("* - ", 80_000)) + new string('\n', 160_000) + "[a](/a.md)\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Targets(body);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// The block and inline scanners work on offsets into untrusted text, where one
    /// off-by-one throws instead of warning. Random bodies built from every character that
    /// steers them — container markers, fences, list markers, HTML delimiters, escapes,
    /// tabs, line endings — must never make an extractor throw. Nothing is asserted about
    /// what they return: the behaviour tests above pin that.
    /// </summary>
    [Fact]
    public void No_extractor_throws_on_random_markdown()
    {
        var alphabet = "> -*+1.)`~<>!-?[]()\\ \t\n\na\"'/=#_".ToCharArray();
        var tokens = new[] { "<!--", "-->", "<?", "?>", "<![CDATA[", "]]>", "<div>", "</pre>", "<a b=\"", "```", "~~~", "    ", "> ", "- ", "10. ", "[^k]" };
        var random = new Random(20260915);
        for (var n = 0; n < 5_000; n++)
        {
            var sb = new System.Text.StringBuilder();
            for (var k = random.Next(0, 60); k > 0; k--)
            {
                sb.Append(random.Next(4) == 0 ? tokens[random.Next(tokens.Length)] : alphabet[random.Next(alphabet.Length)].ToString());
            }

            var body = sb.ToString();
            var ex = Record.Exception(() =>
            {
                LinkScanner.ExtractLinks(body);
                LinkScanner.ExtractFootnoteReferences(body);
                LinkScanner.ExtractAtxHeadings(body);
                LinkScanner.ExtractIndexListItems(body);
            });
            Assert.True(ex is null, $"body {System.Text.Json.JsonSerializer.Serialize(body)} threw {ex}");
        }
    }

    /// <summary>
    /// Untrusted input again: every HTML and container construct above, repeated until
    /// it never closes, must scan in linear time. Same loose bound as the other tests.
    /// </summary>
    [Theory]
    [InlineData("x <!--")]
    [InlineData("x <?")]
    [InlineData("x <![CDATA[")]
    [InlineData("x <!D")]
    [InlineData("x <a b=\"")]
    [InlineData("x <a b ")]
    [InlineData("> ")]
    [InlineData("- ")]
    [InlineData("> - ")]
    [InlineData("<a b=\"\n")]
    [InlineData("<!--\n")]
    [InlineData("> > > > ```\n")]
    [InlineData("[x]: /u\n")]
    [InlineData("[x]: /u '")]
    [InlineData("[x]: /u '\n")]
    public void Html_and_container_constructs_are_scanned_in_linear_time(string unit)
    {
        var body = string.Concat(Enumerable.Repeat(unit, 300_000 / unit.Length)) + "\n\n[a](/a.md)\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Targets(body);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }
}

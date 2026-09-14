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
    /// The table-driven scan must find exactly what the same bracket algorithm finds when
    /// every end is searched for by hand — the tables are an optimization, not a second
    /// definition. <see cref="ReferenceScan"/> is that straightforward version, kept here as
    /// the oracle, and the two are compared on random lines over the characters that steer
    /// inline links: brackets, image bangs, parentheses, backslash escapes, spaces, quotes.
    /// (It replaced an oracle of the pre-CommonMark scan, whose semantics were changed on
    /// purpose: escaped brackets, links inside link text, destinations with spaces.)
    /// </summary>
    [Fact]
    public void Linear_link_scan_matches_a_straightforward_scan_on_random_lines()
    {
        var alphabet = "[]()\\ a\"'!".ToCharArray();
        var random = new Random(20260914);
        for (var n = 0; n < 20_000; n++)
        {
            var chars = new char[random.Next(0, 24)];
            for (var k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[random.Next(alphabet.Length)];
            }

            // A leading letter keeps the line an ordinary paragraph (four leading spaces
            // would make it indented code).
            var line = "z" + new string(chars);
            var expected = ReferenceScan(line);
            var actual = LinkScanner.ExtractLinks(line).Select(l => (l.Text, l.Target)).ToList();
            Assert.True(expected.SequenceEqual(actual), $"line {line}: expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
        }
    }

    /// <summary>
    /// CommonMark's bracket algorithm for inline links and images on one line, with every
    /// search written out: no tables, no reference links (the random lines define none).
    /// </summary>
    private static List<(string Text, string Target)> ReferenceScan(string s)
    {
        static bool Punct(char c) => c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

        var found = new List<(int Start, string Text, string Target)>();
        var openers = new List<(int Index, bool Image, int Order)>();
        var order = 0;
        var inactiveBelow = 0;
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '\\' && i + 1 < s.Length && Punct(s[i + 1]))
            {
                i += 2;
                continue;
            }

            var image = s[i] == '!' && i + 1 < s.Length && s[i + 1] == '[';
            if (s[i] == '[' || image)
            {
                openers.Add((image ? i + 1 : i, image, order++));
                i += image ? 2 : 1;
                continue;
            }

            if (s[i] != ']' || openers.Count == 0)
            {
                i++;
                continue;
            }

            var opener = openers[^1];
            openers.RemoveAt(openers.Count - 1);
            if ((opener.Image || opener.Order >= inactiveBelow) && Inline(s, i + 1) is { } m)
            {
                found.Add((opener.Image ? opener.Index - 1 : opener.Index, s[(opener.Index + 1)..i], m.Target));
                if (!opener.Image)
                {
                    inactiveBelow = order;
                }

                i = m.End;
                continue;
            }

            i++;
        }

        return found.OrderBy(f => f.Start).Select(f => (f.Text, f.Target)).ToList();

        static (string Target, int End)? Inline(string s, int open)
        {
            if (open >= s.Length || s[open] != '(')
            {
                return null;
            }

            var p = open + 1;
            while (p < s.Length && s[p] == ' ')
            {
                p++;
            }

            var dest = p;
            var target = new System.Text.StringBuilder();
            if (p < s.Length && s[p] == '<')
            {
                p++;
                while (p < s.Length && s[p] is not ('>' or '<'))
                {
                    if (s[p] == '\\' && p + 1 < s.Length)
                    {
                        p++;
                    }

                    target.Append(s[p]);
                    p++;
                }

                if (p >= s.Length || s[p] != '>')
                {
                    return null;
                }

                p++;
            }
            else
            {
                var depth = 0;
                while (p < s.Length && s[p] != ' ')
                {
                    if (s[p] == '\\' && p + 1 < s.Length && Punct(s[p + 1]))
                    {
                        target.Append(s[p + 1]);
                        p += 2;
                        continue;
                    }

                    if (s[p] == '(')
                    {
                        depth++;
                    }
                    else if (s[p] == ')' && --depth < 0)
                    {
                        break;
                    }

                    target.Append(s[p]);
                    p++;
                }

                if (depth > 0 || (p == dest && (p >= s.Length || s[p] != ')')))
                {
                    return null;
                }
            }

            var afterDest = p;
            while (p < s.Length && s[p] == ' ')
            {
                p++;
            }

            if (p > afterDest && p < s.Length && s[p] is '"' or '\'' or '(')
            {
                var close = s[p] == '(' ? ')' : s[p];
                var q = p + 1;
                while (q < s.Length && s[q] != close && !(close == ')' && s[q] == '('))
                {
                    q += s[q] == '\\' && q + 1 < s.Length ? 2 : 1;
                }

                if (q < s.Length && s[q] == close)
                {
                    p = q + 1;
                    while (p < s.Length && s[p] == ' ')
                    {
                        p++;
                    }
                }
            }

            return p < s.Length && s[p] == ')' ? (target.ToString(), p + 1) : null;
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

    /// <summary>
    /// The four reference forms (CommonMark §6.3), each resolved against a link reference
    /// definition (§4.7) anywhere in the body: full <c>[text][label]</c>, collapsed
    /// <c>[label][]</c>, shortcut <c>[label]</c>, and an image <c>![alt][label]</c> —
    /// extracted like an inline image. The link's target is the definition's destination,
    /// its title dropped as an inline link's is. Expected results are commonmark.js's.
    /// </summary>
    [Theory]
    [InlineData("See [the table][tbl].\n\n[tbl]: /tables/orders.md\n", "/tables/orders.md")]
    [InlineData("See [tbl][].\n\n[tbl]: /tables/orders.md \"Orders\"\n", "/tables/orders.md")]
    [InlineData("See [tbl].\n\n[tbl]: /tables/orders.md\n", "/tables/orders.md")]
    [InlineData("A chart: ![chart][c]\n\n[c]: /img/chart.png\n", "/img/chart.png")]
    [InlineData("[tbl]: /tables/orders.md\n\nDefined before use: [tbl].\n", "/tables/orders.md")]
    [InlineData("> [q]: /quoted.md\n\nA definition in a quote counts: [q]\n", "/quoted.md")]
    [InlineData("A claim.[^k][r]\n\n[r]: /x.md\n", "/x.md")]
    public void Reference_links_resolve_to_their_definition(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// How labels match (§4.7): case-insensitively, with runs of whitespace collapsed; the
    /// first definition of a label wins; and a label is read raw, escapes included.
    /// </summary>
    [Theory]
    [InlineData("[Foo  Bar][]\n\n[foo bar]: /x.md\n", "/x.md")]
    [InlineData("[r]\n\n[r]: /first.md\n[R]: /second.md\n", "/first.md")]
    [InlineData("[x\\]y][r]\n\n[r]: /esc.md\n", "/esc.md")]
    public void Reference_labels_match_as_commonmark_normalizes_them(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// The sequencing rules, as commonmark.js applies them: a full reference whose label is
    /// undefined is no link, and its text cannot fall back to a shortcut; a link label that
    /// failed can still be the text of the next reference; and text and label must touch.
    /// </summary>
    [Theory]
    [InlineData("[foo][bar]\n\n[foo]: /f.md\n", "")]
    [InlineData("[foo][bar][baz]\n\n[baz]: /url1\n[bar]: /url2\n", "/url2,/url1")]
    [InlineData("[foo][bar][baz]\n\n[baz]: /url\n", "/url")]
    [InlineData("[foo] [bar]\n\n[bar]: /b.md\n", "/b.md")]
    [InlineData("[r][]x[r]\n\n[r]: /r.md\n", "/r.md,/r.md")]
    [InlineData("[undefined] and [also][missing]\n", "")]
    public void Reference_links_follow_commonmark_sequencing(string body, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), Targets(body));
    }

    /// <summary>
    /// A decision, not CommonMark: OKF cites sources with footnotes keyed to
    /// <c>sources[].id</c> (§4.2, §5.1), so a bracket starting with <c>^</c> is a footnote,
    /// never a reference link — commonmark.js, which has no footnotes, would read
    /// <c>[^k][r]</c> as one link with text <c>^k</c>; here <c>[^k]</c> is the footnote and
    /// <c>[r]</c> a shortcut of its own, as GitHub renders it. And a definition inside code
    /// defines nothing.
    /// </summary>
    [Theory]
    [InlineData("See [text][^k].\n\n[^k]: /not-a-definition.md\n")]
    [InlineData("A claim.[^r]\n\n[r]: /x.md\n")]
    [InlineData("```\n[r]: /x.md\n```\n\n[r]\n")]
    public void Footnotes_and_code_never_form_reference_links(string body)
    {
        Assert.Empty(Targets(body));
    }

    /// <summary>
    /// Found by comparing against commonmark.js on random bodies: an escaped <c>\[</c>
    /// opens no reference; a following <c>[^k]</c> is a footnote, so the text before it is
    /// still a shortcut; and an angle-bracket destination holding a <c>&lt;</c> is invalid
    /// even when that <c>&lt;</c> starts a tag the code pass blanked.
    /// </summary>
    [Theory]
    [InlineData("\\[r] and [r]\n\n[r]: /r.md\n", "/r.md")]
    [InlineData("See [r][^k].\n\n[r]: /r.md\n", "/r.md")]
    [InlineData("[t](<a<b>) then [after](/after.md)\n", "/after.md")]
    [InlineData("[t](<!--<[in](/in.md)-->) [after](/after.md)\n", "/after.md")]
    public void Reference_and_angle_bracket_edge_cases_follow_commonmark(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// Found by review of #105: a definition's destination has its backslash escapes
    /// resolved, as an inline link's has, so the same file is reached either way.
    /// </summary>
    [Theory]
    [InlineData("[a][r]\n\n[r]: a\\_b.md\n", "a_b.md")]
    [InlineData("[a][r]\n\n[r]: <my\\_file.md>\n", "my_file.md")]
    [InlineData("[a][r]\n\n[r]: a\\(b.md \"t\"\n", "a(b.md")]
    [InlineData("[a][r]\n\n[r]: a\\b.md\n", "a\\b.md")]
    public void Reference_definition_destinations_resolve_escapes(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// A setext underline does not make a heading of a paragraph that holds only link
    /// reference definitions — there is no text left to head (commonmark.js) — so the
    /// underline continues that paragraph, and a definition after it is no longer at the
    /// paragraph's start: it defines nothing.
    /// </summary>
    [Fact]
    public void A_setext_underline_under_definitions_only_continues_the_paragraph()
    {
        Assert.Equal(["/s.md"], Targets("[s]: /s.md\n-\n[r]: /r.md\n[t][r] and [s]\n"));
    }

    /// <summary>
    /// A destination in angle brackets (CommonMark §6.3) may hold spaces and parentheses;
    /// the brackets are not part of it. It used to be extracted with them, so the link
    /// never resolved — a false broken link, and a dead <c>.md</c> link in the viewer.
    /// </summary>
    [Theory]
    [InlineData("[a](</angle.md>)\n", "/angle.md")]
    [InlineData("[b](<my file.md> \"title\")\n", "my file.md")]
    [InlineData("[c](<x)y.md>)\n", "x)y.md")]
    [InlineData("[r]\n\n[r]: </x y.md>\n", "/x y.md")]
    public void Angle_bracket_destinations_lose_their_brackets(string body, string expected)
    {
        Assert.Equal([expected], Targets(body));
    }

    /// <summary>
    /// Linear on hostile input: openers followed by unclosed or undefined labels, many
    /// shortcuts against many definitions, and unclosed angle-bracket destinations.
    /// </summary>
    [Theory]
    [InlineData("[a][", "[a]: /a.md\n\n")]
    [InlineData("[a][b", "[a]: /a.md\n\n")]
    [InlineData("[a] ", "[a]: /a.md\n\n")]
    [InlineData("[a][]", "[a]: /a.md\n\n")]
    [InlineData("[a](<", "")]
    [InlineData("[x](<y", "")]
    [InlineData("[", "[a]: /a.md\n\n")]
    [InlineData("[[]", "[a]: /a.md\n\n")]
    [InlineData("\\[a]", "[a]: /a.md\n\n")]
    [InlineData("](<", "")]
    public void Reference_and_angle_bracket_links_are_scanned_in_linear_time(string unit, string prefix)
    {
        var body = prefix + string.Concat(Enumerable.Repeat(unit, 300_000 / unit.Length)) + "\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Targets(body);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// An inline link destination as CommonMark defines it (§6.3): no spaces, parentheses
    /// only when balanced or escaped, backslash escapes resolved, and a title only after
    /// whitespace. Anything else after <c>](</c> is no link — the scan used to accept any
    /// balanced parentheses and strip a trailing title. Expected results are commonmark.js's.
    /// </summary>
    [Theory]
    [InlineData("[a](not a link)\n", null)]
    [InlineData("[a](x y)\n", null)]
    [InlineData("[a](x \"t\")\n", "x")]
    [InlineData("[a](x 't' )\n", "x")]
    [InlineData("[a](x (t))\n", "x")]
    [InlineData("[a](x \"t\" y)\n", null)]
    [InlineData("[a](x (t(u)))\n", null)]
    [InlineData("[a](x(y))\n", "x(y)")]
    [InlineData("[a](x(y)\n", null)]
    [InlineData("[a](x\\)y)\n", "x)y")]
    [InlineData("[a](\\(x)\n", "(x")]
    [InlineData("[a]()\n", "")]
    [InlineData("[a](  x  )\n", "x")]
    public void Inline_link_destinations_follow_commonmark(string body, string? expected)
    {
        Assert.Equal(expected is null ? [] : [expected], Targets(body));
    }

    /// <summary>
    /// A link never contains a link (§6.3): once an inner link forms, the brackets opened
    /// before it can no longer form one — the scan used to keep the outer link and skip the
    /// inner. An image is the exception, and may hold a link.
    /// </summary>
    [Theory]
    [InlineData("[a [b](/x.md) c](/y.md)\n", "/x.md")]
    [InlineData("[[a](/x.md)](/y.md)\n", "/x.md")]
    [InlineData("![a [b](/x.md) c](/y.png)\n", "/y.png,/x.md")]
    [InlineData("![[a](/x.md)](/y.png)\n", "/y.png,/x.md")]
    public void A_link_inside_link_text_suppresses_the_outer_link(string body, string expected)
    {
        Assert.Equal(expected.Split(','), Targets(body));
    }

    /// <summary>
    /// A link's text, destination and title may cross a line ending inside their paragraph
    /// (§6.3), and never a paragraph boundary; the text is reported on one line.
    /// </summary>
    [Theory]
    [InlineData("[a\nb](/x.md)\n", "/x.md")]
    [InlineData("[a]( /x.md\n\"a title\")\n", "/x.md")]
    [InlineData("[a](\n /x.md\n )\n", "/x.md")]
    [InlineData("> [a\n> b](/x.md)\n", "/x.md")]
    [InlineData("[a](/x\ny.md)\n", "")]
    [InlineData("[a]\n(/x.md)\n", "")]
    [InlineData("[a\n\nb](/x.md)\n", "")]
    public void A_link_may_span_a_line_ending_within_its_paragraph(string body, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : [expected], Targets(body));
    }

    [Fact]
    public void A_link_text_spanning_a_line_ending_is_reported_on_one_line()
    {
        var link = Assert.Single(LinkScanner.ExtractLinks("See [the orders\ntable](/tables/orders.md).\n"));
        Assert.Equal("the orders table", link.Text);
    }

    /// <summary>
    /// A backslash-escaped bracket opens or closes nothing (§2.4) — the scan used to let
    /// <c>\[</c> open an inline link — while an escaped backslash leaves the bracket live.
    /// </summary>
    [Theory]
    [InlineData("\\[a](/x.md)\n", "")]
    [InlineData("\\\\[a](/x.md)\n", "/x.md")]
    [InlineData("[a\\](/x.md)\n", "")]
    [InlineData("![a\\](/x.png) [b](/y.md)\n", "/y.md")]
    public void Escaped_brackets_open_and_close_nothing(string body, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : [expected], Targets(body));
    }

    /// <summary>
    /// Raw HTML and code spans are resolved in the same left-to-right pass as links, as
    /// commonmark.js does: what follows a <c>]</c> that closes a link is its destination,
    /// never a tag or a span, while after a <c>]</c> that closes nothing a tag hides what
    /// it holds. Raised by Copilot on #105: a <c>&lt;</c> after any <c>](</c> used to be
    /// read as a destination, so a link inside that tag's attribute was extracted.
    /// Expected targets are commonmark.js 0.31.2's, separated by commas.
    /// </summary>
    [Theory]
    [InlineData("foo](<a title=\"[in](/in.md)\">)\n", "")]
    [InlineData("[a [b](c) ](<i title=\"[in](/in.md)\">)\n", "c")]
    [InlineData("[x](<a title=\"[in](/in.md)\">)\n", "a title=\"[in](/in.md)\"")]
    [InlineData("[x](<a b='x> \"t\") [y](/y.md)'>\n", "a b='x,/y.md")]
    [InlineData("[a](x`y) [b](/b.md)`\n", "x`y,/b.md")]
    [InlineData("[t][`a]`]\n\n[`a]: /b.md\n", "/b.md")]
    [InlineData("[t][<b>]\n\n[<b>]: /b.md\n", "/b.md")]
    public void Links_raw_html_and_code_spans_resolve_in_one_pass(string body, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), Targets(body));
    }

    /// <summary>
    /// What a link consumes after its text is not text, so a <c>[^k]</c> in its destination
    /// is no citation; nor is one inside a tag that follows a <c>]</c> closing no link. A
    /// footnote beside a link, or one followed by parentheses, still is.
    /// </summary>
    [Theory]
    [InlineData("[t](a[^k]b)\n", "")]
    [InlineData("[t](#<!X[^k])\n", "")]
    [InlineData("foo](<a title=\"[^k]\">)\n", "")]
    [InlineData("[t](/x.md) [^k]\n", "k")]
    [InlineData("A claim.[^k](/i.md)\n", "k")]
    public void Footnotes_in_link_destinations_are_not_citations(string body, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : [expected], LinkScanner.ExtractFootnoteReferences(body));
    }

    /// <summary>
    /// Linear on hostile input shaped for the bracket algorithm: destinations that never
    /// balance or never end, and long runs of openers each followed by a link.
    /// </summary>
    [Theory]
    [InlineData("[a](")]
    [InlineData("[a](x(")]
    [InlineData("[a]([a](")]
    [InlineData("[[a](b)")]
    [InlineData("![")]
    [InlineData("[a](x \"")]
    [InlineData("[a]( x\n")]
    [InlineData("[a](<")]
    [InlineData("`[a](<b>`")]
    [InlineData("[a](<b c='")]
    public void Bracket_algorithm_inputs_are_scanned_in_linear_time(string unit)
    {
        var body = "z" + string.Concat(Enumerable.Repeat(unit, 300_000 / unit.Length)) + "](y)\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Targets(body);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }
}

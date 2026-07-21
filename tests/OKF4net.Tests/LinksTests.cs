namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust link-scanning and citation-extraction tests
/// (tests/links.rs), guaranteeing behavioural parity with the reference
/// implementation's <c>extract_links</c>/<c>extract_citations</c> and
/// <c>Link::classify</c>/<c>Link::resolve</c>.
/// </summary>
public class LinksTests
{
    [Fact]
    public void Classify_link_kinds()
    {
        // tests/links.rs:8-15
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
        // tests/links.rs:18-27
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
        // tests/links.rs:30-35
        var body = "Real [a](/a.md).\n\n```\nNot a [link](/b.md) in code.\n```\n\nInline `[c](/c.md)` ignored.\n";
        var links = LinkScanner.ExtractLinks(body);
        var targets = links.Select(l => l.Target).ToList();
        Assert.Equal(new[] { "/a.md" }, targets);
    }

    [Fact]
    public void Resolve_absolute_link()
    {
        // tests/links.rs:38-49
        var source = ConceptId.Parse("tables/orders");
        var link = new ConceptLink("customers", "/tables/customers.md", LinkKind.Absolute);
        Assert.Equal(ConceptId.Parse("tables/customers"), link.Resolve(source));
    }

    [Fact]
    public void Resolve_relative_link()
    {
        // tests/links.rs:52-68
        var source = ConceptId.Parse("tables/orders");
        var link = new ConceptLink("neighbor", "./customers.md", LinkKind.Relative);
        Assert.Equal(ConceptId.Parse("tables/customers"), link.Resolve(source));

        var up = new ConceptLink("up", "../datasets/sales.md", LinkKind.Relative);
        Assert.Equal(ConceptId.Parse("datasets/sales"), up.Resolve(source));
    }

    [Fact]
    public void Protocol_relative_url_is_external()
    {
        // tests/links.rs:71-74
        Assert.Equal(LinkKind.External, ConceptLink.Classify("//cdn.example.com/x.js"));
    }

    [Fact]
    public void Absolute_link_normalizes_dot_segments()
    {
        // tests/links.rs:77-88
        var source = ConceptId.Parse("a/b");
        var link = new ConceptLink("x", "/tables/../datasets/sales.md", LinkKind.Absolute);
        Assert.Equal(ConceptId.Parse("datasets/sales"), link.Resolve(source));
    }

    [Fact]
    public void External_links_do_not_resolve()
    {
        // tests/links.rs:91-99
        var source = ConceptId.Parse("a");
        var link = new ConceptLink("x", "https://example.com", LinkKind.External);
        Assert.Null(link.Resolve(source));
    }

    [Fact]
    public void Citations_section_parsed()
    {
        // tests/links.rs:102-112
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
        // tests/links.rs:115-120
        var body = "# Citations\n[1] [a](https://a)\n\n# Other\n[2] [b](https://b)\n";
        var citations = LinkScanner.ExtractCitations(body);
        Assert.Single(citations);
    }

    [Fact]
    public void Document_links_and_citations_integration()
    {
        // tests/links.rs:123-135
        var doc = OkfDocument.Parse(
            "---\ntype: BigQuery Table\n---\n\nJoined with [customers](/tables/customers.md).\n\n# Citations\n[1] [BQ](https://bq)\n");
        // links() returns every body link, including the one in the citation list.
        Assert.Equal(2, doc.Links().Count);
        var internalLinks = doc.Links().Where(l => l.Kind == LinkKind.Absolute).ToList();
        Assert.Single(internalLinks);
        Assert.Single(doc.Citations());
    }
}

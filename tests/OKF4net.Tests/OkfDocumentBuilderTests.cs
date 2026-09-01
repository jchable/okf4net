// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

public class OkfDocumentBuilderTests
{
    [Fact]
    public void Build_produces_expected_frontmatter_and_body_in_canonical_key_order()
    {
        var doc = OkfDocumentBuilder
            .ForType("CLI Tool")
            .Title("okfgen")
            .Description("Generates OKF bundles for repositories")
            .Resource("https://example.com/okfgen")
            .Tags("cli", "okf")
            .AddSource(resource: "README.md", title: "README")
            .AddSource(resource: "package.json")
            .Extension("custom_field", new YamlString("custom_value"))
            .Body("# Summary\n")
            .Build();

        Assert.Equal("# Summary\n", doc.Body);
        Assert.Equal(
            new[] { "type", "title", "description", "resource", "tags", "sources", "custom_field" },
            doc.Frontmatter.AsMapping().Keys.ToList());
        Assert.Equal("CLI Tool", doc.Frontmatter.Type);
        Assert.Equal("okfgen", doc.Frontmatter.Title);
        Assert.Equal("https://example.com/okfgen", doc.Frontmatter.Resource);
        Assert.Equal(2, doc.Frontmatter.Sources.Count);
        Assert.Equal("README.md", doc.Frontmatter.Sources[0].Resource);
        Assert.Equal("README", doc.Frontmatter.Sources[0].Title);
        Assert.Equal("package.json", doc.Frontmatter.Sources[1].Resource);
    }

    [Fact]
    public void Build_without_title_or_description_fails_strict_validate_but_passes_conformance()
    {
        var doc = OkfDocumentBuilder.ForType("CLI Tool").Body("body").Build();

        Assert.Throws<DocumentValidationException>(() => doc.Validate());
        doc.ValidateConformance(); // does not throw: §11 requires only `type`
    }

    [Fact]
    public void Build_without_body_throws()
    {
        var builder = OkfDocumentBuilder.ForType("CLI Tool");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Tags_overwrites_a_previous_Tags_call()
    {
        var doc = OkfDocumentBuilder.ForType("t").Tags("a", "b").Tags("c").Body("").Build();

        Assert.Equal(new[] { "c" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void AddTags_accumulates_across_calls()
    {
        var doc = OkfDocumentBuilder.ForType("t").AddTags("a").AddTags("b").Body("").Build();

        Assert.Equal(new[] { "a", "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Tags_after_AddTags_replaces_everything_regardless_of_call_order()
    {
        var doc = OkfDocumentBuilder.ForType("t").AddTags("a").Tags("b").Body("").Build();

        Assert.Equal(new[] { "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void AddTags_after_Tags_accumulates_on_top_of_the_base_list()
    {
        var doc = OkfDocumentBuilder.ForType("t").Tags("a").AddTags("b").Body("").Build();

        Assert.Equal(new[] { "a", "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Tags_never_called_omits_the_tags_key()
    {
        var doc = OkfDocumentBuilder.ForType("t").Body("").Build();

        Assert.False(doc.Frontmatter.AsMapping().ContainsKey("tags"));
    }

    [Fact]
    public void AddSource_never_called_omits_the_sources_key()
    {
        var doc = OkfDocumentBuilder.ForType("t").Body("").Build();

        Assert.False(doc.Frontmatter.AsMapping().ContainsKey("sources"));
    }

    [Fact]
    public void AddSource_usage_window_reaches_the_built_document_s_frontmatter()
    {
        var window = new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z");
        var doc = OkfDocumentBuilder.ForType("t")
            .AddSource(resource: "README.md", usageWindow: window)
            .Body("")
            .Build();

        Assert.Equal(window, doc.Frontmatter.Sources[0].UsageWindow);
    }

    [Fact]
    public void Extension_targeting_a_well_known_key_wins_over_the_typed_setter_regardless_of_call_order()
    {
        var doc = OkfDocumentBuilder.ForType("t")
            .Tags("a", "b")
            .Extension("tags", new YamlSequence([new YamlString("override")]))
            .Body("")
            .Build();

        Assert.Equal(new[] { "override" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Extension_targeting_a_well_known_key_wins_even_when_called_before_the_typed_setter()
    {
        var doc = OkfDocumentBuilder.ForType("t")
            .Extension("tags", new YamlSequence([new YamlString("override")]))
            .Tags("a", "b")
            .Body("")
            .Build();

        Assert.Equal(new[] { "override" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Build_key_order_is_fixed_regardless_of_setter_call_order()
    {
        var doc = OkfDocumentBuilder
            .ForType("t")
            .Resource("r")
            .Description("d")
            .Title("ti")
            .Body("")
            .Build();

        Assert.Equal(
            new[] { "type", "title", "description", "resource" },
            doc.Frontmatter.AsMapping().Keys.ToList());
    }

    [Fact]
    public void Tags_with_a_null_element_throws_ArgumentException_at_Build_instead_of_the_emitter()
    {
        var builder = OkfDocumentBuilder.ForType("t").Tags("a", null!).Body("");

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Fact]
    public void AddTags_with_a_null_element_throws_ArgumentException_at_Build_instead_of_the_emitter()
    {
        var builder = OkfDocumentBuilder.ForType("t").AddTags("a", null!).Body("");

        Assert.Throws<ArgumentException>(() => builder.Build());
    }

    [Fact]
    public void Build_is_idempotent_and_non_destructive()
    {
        var builder = OkfDocumentBuilder.ForType("t").Title("x").Body("body");

        var first = builder.Build();
        var second = builder.Build();

        Assert.Equal(first.Frontmatter, second.Frontmatter);
        Assert.Equal(first.Body, second.Body);
    }
}

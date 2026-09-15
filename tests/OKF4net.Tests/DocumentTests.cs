// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// A port of the document parse/serialize/validate tests, plus the v0.2
/// <c>Sources()</c> fallback: <c>Sources_falls_back_to_legacy_citations_when_frontmatter_absent</c>
/// below exercises the legacy <c>Citations()</c> path.
/// </summary>
public class DocumentTests
{
    [Fact]
    public void Roundtrip_preserves_frontmatter_and_body()
    {
        var src =
            "---\ntype: BigQuery Table\ntitle: Sample\ndescription: A sample table.\n" +
            "tags: [a, b]\ntimestamp: 2026-05-27T00:00:00+00:00\n---\n\n" +
            "# Sample\n\nBody text.\n";

        var doc = OkfDocument.Parse(src);
        Assert.Equal("BigQuery Table", doc.Frontmatter.Type);
        Assert.Equal(new[] { "a", "b" }, doc.Frontmatter.Tags);
        Assert.StartsWith("# Sample", doc.Body);

        var serialized = doc.Serialize();
        var reparsed = OkfDocument.Parse(serialized);
        Assert.Equal(doc.Frontmatter.AsMapping(), reparsed.Frontmatter.AsMapping());
        Assert.Equal(doc.Body.Trim(), reparsed.Body.Trim());

        // F11: structural equality over frontmatter + body.
        Assert.True(doc.Equals(reparsed));
        Assert.Equal(doc, reparsed);
        Assert.Equal(doc.GetHashCode(), reparsed.GetHashCode());
    }

    [Fact]
    public void Equality_is_structural_and_sensitive_to_body_and_frontmatter()
    {
        // F11: document equality is componentwise over frontmatter + body.
        var a = OkfDocument.Parse("---\ntype: X\n---\nbody\n");
        var sameContent = OkfDocument.Parse("---\ntype: X\n---\nbody\n");
        Assert.Equal(a, sameContent);

        var differentBody = OkfDocument.Parse("---\ntype: X\n---\nother body\n");
        Assert.NotEqual(a, differentBody);
        Assert.False(a.Equals(differentBody));

        var differentFrontmatter = OkfDocument.Parse("---\ntype: Y\n---\nbody\n");
        Assert.NotEqual(a, differentFrontmatter);
        Assert.False(a.Equals(differentFrontmatter));

        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Parse_no_frontmatter_treats_all_as_body()
    {
        var src = "# Hello\n\nNo frontmatter here.\n";
        var doc = OkfDocument.Parse(src);
        Assert.True(doc.Frontmatter.IsEmpty);
        Assert.Contains("Hello", doc.Body);
    }

    [Fact]
    public void Unterminated_frontmatter_raises()
    {
        var src = "---\ntype: X\nstill in frontmatter\n";
        var ex = Assert.Throws<DocumentParseException>(() => OkfDocument.Parse(src));
        Assert.Equal("Unterminated YAML frontmatter block", ex.Message);
    }

    // ---- §4 fence: "`---` on its own line" -----------------------------------

    [Theory]
    [InlineData("---\ntype: X\n---\nbody\n")]
    [InlineData("---  \ntype: X\n---  \nbody\n")]
    [InlineData("---\t\ntype: X\n---\t\nbody\n")]
    [InlineData("--- \t\ntype: X\n---\nbody\n")]
    public void A_column_0_fence_with_optional_trailing_spaces_or_tabs_opens_and_closes(string src)
    {
        var doc = OkfDocument.Parse(src);
        Assert.Equal("X", doc.Frontmatter.Type);
        Assert.Equal("body", doc.Body);
    }

    [Fact]
    public void A_CRLF_fence_opens_and_closes()
    {
        var doc = OkfDocument.Parse("---\r\ntype: X\r\n---\r\nbody\r\n");
        Assert.Equal("X", doc.Frontmatter.Type);
        Assert.Equal("body", doc.Body);
    }

    /// <summary>
    /// §4: an indented <c>---</c> is not "<c>---</c> on its own line", so it no
    /// longer closes the frontmatter. With no other fence, the block is unterminated.
    /// </summary>
    [Theory]
    [InlineData("---\ntype: X\n  ---\nbody\n")]
    [InlineData("---\ntype: X\n\t---\nbody\n")]
    [InlineData("---\ntype: X\n ---\nbody\n")]
    public void An_indented_dash_line_does_not_close_the_frontmatter(string src)
    {
        var ex = Assert.Throws<DocumentParseException>(() => OkfDocument.Parse(src));
        Assert.Equal("Unterminated YAML frontmatter block", ex.Message);
    }

    /// <summary>
    /// Once it is not a fence, the indented line is ordinary YAML: here the
    /// continuation of a multi-line plain scalar, with the real fence after it.
    /// </summary>
    [Fact]
    public void An_indented_dash_line_before_the_real_fence_is_yaml_content()
    {
        var doc = OkfDocument.Parse("---\ntype: X\n  ---\n---\nbody\n");
        Assert.Equal("X ---", doc.Frontmatter.Type);
        Assert.Equal("body", doc.Body);
    }

    [Theory]
    [InlineData("  ---\ntype: X\n---\nbody\n")]
    [InlineData("\t---\ntype: X\n---\nbody\n")]
    [InlineData("----\ntype: X\n----\nbody\n")]
    [InlineData("--- x\ntype: X\n---\nbody\n")]
    [InlineData("---x\ntype: X\n---\nbody\n")]
    public void A_first_line_that_is_not_a_column_0_fence_means_no_frontmatter(string src)
    {
        var doc = OkfDocument.Parse(src);
        Assert.True(doc.Frontmatter.IsEmpty);
        Assert.Equal(src, doc.Body);
    }

    /// <summary>
    /// Only spaces and tabs may follow the three dashes. Other whitespace that
    /// <c>string.Trim</c> used to strip (NO-BREAK SPACE, vertical tab, form feed,
    /// a lone carriage return not followed by <c>\n</c>) leaves the line ordinary text.
    /// </summary>
    [Theory]
    [InlineData("---\ntype: X\n--- \nbody\n")]
    [InlineData("---\ntype: X\n---\v\nbody\n")]
    [InlineData("---\ntype: X\n---\f\nbody\n")]
    [InlineData("---\ntype: X\n---\r")]
    [InlineData("---\ntype: X\n----\n--- x\n")]
    public void A_dash_line_with_anything_but_spaces_or_tabs_after_it_does_not_close(string src)
    {
        var ex = Assert.Throws<DocumentParseException>(() => OkfDocument.Parse(src));
        Assert.Equal("Unterminated YAML frontmatter block", ex.Message);
    }

    /// <summary>
    /// A leading UTF-8 BOM (U+FEFF) is not stripped by <see cref="OkfDocument.Parse"/>:
    /// the first line is then not a fence and the whole text is body. The old
    /// <c>Trim()</c>-based predicate behaved the same, since U+FEFF is not
    /// <c>char.IsWhiteSpace</c>; pinned so the new predicate keeps it.
    /// </summary>
    [Fact]
    public void A_leading_BOM_means_no_frontmatter_as_before()
    {
        const string src = "﻿---\ntype: X\n---\nbody\n";
        var doc = OkfDocument.Parse(src);
        Assert.True(doc.Frontmatter.IsEmpty);
        Assert.Equal(src, doc.Body);
    }

    /// <summary>
    /// The case that used to truncate silently: an indented <c>---</c> inside a
    /// <c>|</c> block scalar was taken as the closing fence, cutting the block and
    /// pushing the rest of the frontmatter into the body. It is block content now.
    /// </summary>
    [Fact]
    public void An_indented_dash_line_inside_a_block_scalar_round_trips()
    {
        const string src = "---\ntype: X\ndescription: |\n  Intro\n  ---\n  Details\ntitle: T\n---\nbody\n";
        var doc = OkfDocument.Parse(src);
        Assert.Equal("Intro\n---\nDetails\n", doc.Frontmatter.Description);
        Assert.Equal("T", doc.Frontmatter.Title);
        Assert.Equal("body", doc.Body);

        var reparsed = OkfDocument.Parse(doc.Serialize());
        Assert.Equal(doc, reparsed);
        Assert.Equal("Intro\n---\nDetails\n", reparsed.Frontmatter.Description);
    }

    /// <summary>
    /// A rejected YAML feature surfaces from <see cref="OkfDocument.Parse"/> as a
    /// <see cref="DocumentParseException"/> naming it, with the line counted from
    /// the first frontmatter line, like every other YAML error.
    /// </summary>
    [Fact]
    public void A_rejected_yaml_feature_is_a_document_parse_error()
    {
        var ex = Assert.Throws<DocumentParseException>(() => OkfDocument.Parse("---\ntype: X\nk: *a\n---\nbody\n"));
        Assert.StartsWith("Invalid YAML in frontmatter: YAML error at line 2: YAML aliases", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_missing_required_keys()
    {
        var doc = OkfDocument.Parse("---\ntype: X\ntitle: Y\n---\n");
        var ex = Assert.Throws<DocumentValidationException>(() => doc.Validate());
        Assert.Contains("description", ex.Message);
        Assert.Equal(new[] { "description" }, ex.MissingKeys);
    }

    [Fact]
    public void Validate_accepts_full_frontmatter()
    {
        var doc = OkfDocument.Parse(
            "---\ntype: X\ntitle: Y\ndescription: Z\ntimestamp: 2026-05-27T00:00:00+00:00\n---\n");
        doc.Validate(); // does not throw
    }

    [Fact]
    public void Conformance_requires_only_type()
    {
        var doc = OkfDocument.Parse("---\ntype: Metric\n---\nbody\n");
        doc.ValidateConformance(); // does not throw
        Assert.Throws<DocumentValidationException>(() => doc.Validate()); // strict producer validation still fails

        var noType = OkfDocument.Parse("---\ntitle: X\n---\n");
        Assert.Throws<DocumentValidationException>(() => noType.ValidateConformance());
    }

    [Fact]
    public void Empty_type_is_not_conformant()
    {
        var doc = OkfDocument.Parse("---\ntype: \"\"\n---\n");
        Assert.Throws<DocumentValidationException>(() => doc.ValidateConformance());
    }

    [Fact]
    public void Unknown_keys_are_preserved_on_roundtrip()
    {
        var src = "---\ntype: X\ncustom_key: custom value\nnested:\n  a: 1\n  b: 2\n---\nbody\n";
        var doc = OkfDocument.Parse(src);
        Assert.NotNull(doc.Frontmatter.Get("custom_key"));
        var extensions = doc.Frontmatter.ExtensionKeys;
        Assert.Contains("custom_key", extensions);
        Assert.Contains("nested", extensions);

        var reparsed = OkfDocument.Parse(doc.Serialize());
        Assert.Equal(doc.Frontmatter.AsMapping(), reparsed.Frontmatter.AsMapping());
        Assert.Equal(YamlValue.Parse("{a: 1, b: 2}"), reparsed.Frontmatter.Get("nested"));
    }

    [Fact]
    public void Empty_frontmatter_block_is_empty_mapping()
    {
        var doc = OkfDocument.Parse("---\n---\nbody\n");
        Assert.True(doc.Frontmatter.IsEmpty);
        // The trailing newline is dropped on parse (splitlines/join
        // semantics); serialize restores it.
        Assert.Equal("body", doc.Body);
        Assert.EndsWith("body\n", doc.Serialize());
    }

    [Fact]
    public void Sources_reads_frontmatter_sources_when_present()
    {
        var doc = OkfDocument.Parse("---\ntype: T\nsources:\n  - resource: https://a\n---\nbody\n");
        var s = doc.Sources();
        Assert.Single(s);
        Assert.Equal("https://a", s[0].Resource);
        Assert.False(doc.UsesLegacyCitations());
    }

    [Fact]
    public void Sources_falls_back_to_legacy_citations_when_frontmatter_absent()
    {
        var doc = OkfDocument.Parse("---\ntype: T\n---\n\n# Citations\n\n[1] [Schema](https://a)\n");
        var s = doc.Sources();
        Assert.Single(s);
        Assert.Equal("https://a", s[0].Resource);
        Assert.Equal("Schema", s[0].Title);
        Assert.True(doc.UsesLegacyCitations());
    }

    [Fact]
    public void Sources_is_empty_with_neither_field_nor_citations()
        => Assert.Empty(OkfDocument.Parse("---\ntype: T\n---\nbody\n").Sources());

    [Fact]
    public void Validate_requires_type_title_description_but_not_timestamp()
    {
        // v0.2: timestamp is no longer required by the producer-side check.
        OkfDocument.Parse("---\ntype: T\ntitle: X\ndescription: D\n---\nbody\n").Validate();

        var ex = Assert.Throws<DocumentValidationException>(
            () => OkfDocument.Parse("---\ntype: T\ntitle: X\n---\nbody\n").Validate());
        Assert.Contains("description", ex.Message);
    }
}

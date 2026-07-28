// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Tests for OKF document parse/serialize/validate behaviour. Link/citation
/// extraction (§8) is deferred to Task 7 (<c>ConceptLink</c>/<c>Citation</c>
/// do not exist yet), so <c>OkfDocument</c> has no <c>Links()</c>/<c>Citations()</c>
/// members in this task — none of the tests below exercise them.
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
}

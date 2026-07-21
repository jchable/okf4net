using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust document parse/serialize/validate tests
/// (tests/document.rs), guaranteeing behavioural parity with the reference
/// implementation's <c>OKFDocument</c>. Link/citation extraction (§8) is
/// deferred to Task 7 (<c>ConceptLink</c>/<c>Citation</c> do not exist yet),
/// so <c>OkfDocument</c> has no <c>Links()</c>/<c>Citations()</c> members in
/// this task — none of the ported tests below exercise them.
/// </summary>
public class DocumentTests
{
    [Fact]
    public void Roundtrip_preserves_frontmatter_and_body()
    {
        // Exact literal from tests/document.rs:11-21 (Rust `\`-continued
        // string literal, which joins lines and strips leading whitespace).
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
    }

    [Fact]
    public void Parse_no_frontmatter_treats_all_as_body()
    {
        // tests/document.rs:35
        var src = "# Hello\n\nNo frontmatter here.\n";
        var doc = OkfDocument.Parse(src);
        Assert.True(doc.Frontmatter.IsEmpty);
        Assert.Contains("Hello", doc.Body);
    }

    [Fact]
    public void Unterminated_frontmatter_raises()
    {
        // tests/document.rs:43-45
        var src = "---\ntype: X\nstill in frontmatter\n";
        var ex = Assert.Throws<DocumentParseException>(() => OkfDocument.Parse(src));
        // error.rs:22-24
        Assert.Equal("Unterminated YAML frontmatter block", ex.Message);
    }

    [Fact]
    public void Validate_rejects_missing_required_keys()
    {
        // tests/document.rs:49-55
        var doc = OkfDocument.Parse("---\ntype: X\ntitle: Y\n---\n");
        var ex = Assert.Throws<DocumentValidationException>(() => doc.Validate());
        Assert.Contains("description", ex.Message);
        Assert.Contains("timestamp", ex.Message);
        // Structured MissingKeys (brief's addition over the Rust API, which
        // only exposes the joined Display string): order follows
        // REQUIRED_FRONTMATTER_KEYS (frontmatter.rs:16).
        Assert.Equal(new[] { "description", "timestamp" }, ex.MissingKeys);
    }

    [Fact]
    public void Validate_accepts_full_frontmatter()
    {
        // tests/document.rs:57-64
        var doc = OkfDocument.Parse(
            "---\ntype: X\ntitle: Y\ndescription: Z\ntimestamp: 2026-05-27T00:00:00+00:00\n---\n");
        doc.Validate(); // does not throw
    }

    [Fact]
    public void Conformance_requires_only_type()
    {
        // tests/document.rs:66-74
        var doc = OkfDocument.Parse("---\ntype: Metric\n---\nbody\n");
        doc.ValidateConformance(); // does not throw
        Assert.Throws<DocumentValidationException>(() => doc.Validate()); // strict producer validation still fails

        var noType = OkfDocument.Parse("---\ntitle: X\n---\n");
        Assert.Throws<DocumentValidationException>(() => noType.ValidateConformance());
    }

    [Fact]
    public void Empty_type_is_not_conformant()
    {
        // tests/document.rs:76-80
        var doc = OkfDocument.Parse("---\ntype: \"\"\n---\n");
        Assert.Throws<DocumentValidationException>(() => doc.ValidateConformance());
    }

    [Fact]
    public void Unknown_keys_are_preserved_on_roundtrip()
    {
        // tests/document.rs:82-97
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
        // tests/document.rs:99-107
        var doc = OkfDocument.Parse("---\n---\nbody\n");
        Assert.True(doc.Frontmatter.IsEmpty);
        // The trailing newline is dropped on parse (matching the reference's
        // splitlines/join); serialize restores it.
        Assert.Equal("body", doc.Body);
        Assert.EndsWith("body\n", doc.Serialize());
    }
}

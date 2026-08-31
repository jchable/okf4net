// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Yaml;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

public class DescriptionTests
{
    private static readonly DescriptionResolver Resolver = new([new DocCommentSource(), new SignatureSource()]);

    [Fact]
    public void A_doc_comment_wins_and_is_labelled_doc_comment()
    {
        var (text, source) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing: null);

        Assert.Equal("Scans a body.", text);
        Assert.Equal("doc-comment", source);
    }

    [Fact]
    public void Without_a_doc_comment_a_sentence_is_derived_from_the_signature()
    {
        var (text, source) = Resolver.Resolve(Member("Scanner", "Scan", doc: null), existing: null);

        Assert.Equal("generated", source);
        Assert.Contains("Scan", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manual_description_is_never_overwritten()
    {
        // §4.2: without field-level preservation, a hand-written description
        // disappears on the next generate and the bundle is a throwaway
        // artefact rather than an editable knowledge base.
        var existing = FrontmatterWith(description: "Hand written.", descriptionSource: "manual");

        var (text, source) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing);

        Assert.Equal("Hand written.", text);
        Assert.Equal("manual", source);
    }

    [Theory]
    [InlineData("doc-comment")]
    [InlineData("generated")]
    public void A_generated_description_is_re_derived(string previousSource)
    {
        var existing = FrontmatterWith(description: "Stale text.", descriptionSource: previousSource);

        Assert.Equal("Scans a body.", Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing).Text);
    }

    [Fact]
    public void An_llm_description_is_preserved_like_a_manual_one()
        => Assert.Equal("From a model.",
            Resolver.Resolve(Member("T", "Scan", doc: "d"), FrontmatterWith("From a model.", "llm")).Text);

    [Fact]
    public void A_signature_derived_sentence_mentions_the_container_and_the_file_not_just_the_name()
    {
        // A restatement of the identifier alone ("Scan is a member.") gives a reader nothing they
        // didn't already know from the concept's title -- the mechanical fallback must do better.
        var fact = Member("Scanner", "Scan", doc: null, path: "src/Scanner.cs");

        var (text, _) = Resolver.Resolve(fact, existing: null);

        Assert.Contains("Scanner", text, StringComparison.Ordinal);
        Assert.Contains("src/Scanner.cs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_s_generated_sentence_uses_a_type_appropriate_preposition()
    {
        var fact = new SymbolFact(SymbolKind.Type, "csharp", "N", "Scanner", "public class Scanner",
            SymbolVisibility.Public, "Scanner.cs", 0, 10, 1, 1, null);

        var (text, source) = Resolver.Resolve(fact, existing: null);

        Assert.Equal("generated", source);
        Assert.Contains("type in N", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_manual_description_with_no_prior_frontmatter_derives_normally()
    {
        // existing: null (no concept this producer has written before) is not a fifth preserved
        // state -- it must always fall through to the chain, doc comment first.
        var (text, source) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing: null);

        Assert.Equal("Scans a body.", text);
        Assert.Equal("doc-comment", source);
    }

    [Fact]
    public void Resolve_is_deterministic_for_identical_input()
    {
        var fact = Member("Scanner", "Scan", doc: null);

        var first = Resolver.Resolve(fact, existing: null);
        var second = Resolver.Resolve(fact, existing: null);

        Assert.Equal(first, second);
    }

    private static SymbolFact Member(string container, string name, string? doc = null, string path = "A.cs") =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            SymbolVisibility.Public, path, 0, 10, 1, 1, doc);

    private static Frontmatter FrontmatterWith(string description, string descriptionSource) =>
        OkfDocumentBuilder.ForType("Member")
            .Description(description)
            .Extension(DescriptionResolver.DescriptionSourceKey, new YamlString(descriptionSource))
            .Body("body\n")
            .Build()
            .Frontmatter;
}

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

    [Theory]
    [InlineData("Manual")]
    [InlineData("MANUAL")]
    [InlineData(" manual")]
    [InlineData("hand-edited")]
    public void An_unrecognized_or_differently_cased_description_source_is_still_preserved(string descriptionSource)
    {
        // §4.2, inverted default: only the two labels this producer writes for a *derived*
        // description (doc-comment, generated) -- or an absent key -- are re-derived. Everything
        // else is protected, however it is spelled or cased: guessing wrong must cost a stale
        // description, never a deleted one. "manual"/"llm" are the documented canonical spellings,
        // but the check does not special-case them -- a human's own convention ("hand-edited") is
        // preserved exactly the same way a case or whitespace variant of "manual" is.
        var existing = FrontmatterWith(description: "Hand written.", descriptionSource: descriptionSource);

        var (text, _) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing);

        Assert.Equal("Hand written.", text);
    }

    [Fact]
    public void An_empty_chain_with_nothing_preserved_throws_the_documented_exception()
    {
        var emptyResolver = new DescriptionResolver([]);

        Assert.Throws<InvalidOperationException>(() =>
            emptyResolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing: null));
    }

    [Fact]
    public void A_signature_derived_sentence_mentions_the_container_not_just_the_name()
    {
        // A restatement of the identifier alone ("Scan is a member.") gives a reader nothing they
        // didn't already know from the concept's title -- the mechanical fallback must do better.
        var fact = Member("Scanner", "Scan", doc: null);

        var (text, _) = Resolver.Resolve(fact, existing: null);

        Assert.Contains("Scanner", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_derived_sentence_omits_the_file_path_so_a_rename_does_not_churn_it()
    {
        // This result is labelled "generated" and re-derived on every run. If it named the file,
        // renaming or moving that file with zero code changes would rewrite the description of every
        // symbol it declares -- exactly the churn Tasks 10/12 exist to bound for concepts whose code
        // did not change. The path is also already recorded structurally via Resource/AddSource once
        // a code concept is wired through OkfDocumentBuilder (Task 8), so restating it here would
        // only duplicate that field in unstructured, churn-prone form.
        var fact = Member("Scanner", "Scan", doc: null, path: "src/very/specific/Scanner.cs");

        var (text, _) = Resolver.Resolve(fact, existing: null);

        Assert.DoesNotContain("very/specific/Scanner.cs", text, StringComparison.Ordinal);
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

    // -- inline XML doc tags ----------------------------------------------------------------------
    //
    // A `description` is prose a reader sees, and ConceptSearch weights it above the body -- so raw
    // `<c>`/`<see>` markup is both ugly and slightly harmful. This repository enforces XML docs, so it
    // is the norm rather than an edge case. Unwrap, never strip: the tag's content is usually the
    // subject of the sentence around it.

    [Theory]
    [InlineData("Scans a <c>body</c> for links.", "Scans a body for links.")]
    [InlineData("See <see cref=\"T:OKF4net.LinkScanner\"/> for details.", "See OKF4net.LinkScanner for details.")]
    [InlineData("See <see cref=\"M:Foo.Bar(System.String)\"/>.", "See Foo.Bar(System.String).")]
    [InlineData("A <see cref=\"System.Uri\"/> with no doc-id prefix.", "A System.Uri with no doc-id prefix.")]
    [InlineData("Whether <paramref name=\"path\"/> exists.", "Whether path exists.")]
    [InlineData("Of <typeparamref name=\"T\"/>.", "Of T.")]
    [InlineData("Returns <see langword=\"null\"/> when absent.", "Returns null when absent.")]
    [InlineData("See <see cref=\"Foo\">the helper</see> instead.", "See the helper instead.")]
    [InlineData("An <unknown>inner</unknown> tag keeps its text.", "An inner tag keeps its text.")]
    public void Inline_xml_doc_tags_are_unwrapped_to_the_text_they_stand_for(string doc, string expected)
        => Assert.Equal(expected, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);

    [Theory]
    [InlineData("A value where a < b holds.")]
    [InlineData("An unterminated <tag that never closes")]
    [InlineData("Compares with <= and >= operators.")]
    public void Text_that_merely_looks_like_markup_is_left_alone(string doc)
        => Assert.Equal(doc, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);

    [Fact]
    public void A_doc_comment_that_is_nothing_but_tags_falls_through_to_the_next_source()
    {
        // `<inheritdoc/>` alone unwraps to nothing, and an empty description is not a description --
        // returning one would put an empty required field into the bundle. Fall through as a missing
        // doc comment does.
        var (text, source) = Resolver.Resolve(Member("Scanner", "Scan", doc: "<inheritdoc/>"), existing: null);

        Assert.Equal("generated", source);
        Assert.Contains("Scan", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Returns a <c>List&lt;T&gt;</c> of results.", "Returns a List<T> of results.")]
    [InlineData("Splits on &quot;,&quot; only.", "Splits on \",\" only.")]
    [InlineData("Reads A &amp; B.", "Reads A & B.")]
    [InlineData("The &apos;name&apos; field.", "The 'name' field.")]
    public void Xml_entities_are_decoded_once_the_tags_are_gone(string doc, string expected)
        => Assert.Equal(expected, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);

    [Fact]
    public void An_escaped_ampersand_is_decoded_once_and_not_twice()
    {
        // The author escaped the ampersand, so the text they meant is the six characters `&lt;`, not the
        // character it names. One left-to-right pass gives that; a repeated pass would silently rewrite
        // their escaped text into `<`.
        Assert.Equal("Write &lt; for a less-than.",
            Resolver.Resolve(Member("T", "Scan", "Write &amp;lt; for a less-than."), existing: null).Text);
    }

    [Fact]
    public void A_numeric_entity_is_left_alone()
        => Assert.Equal("Code point &#60; here.",
            Resolver.Resolve(Member("T", "Scan", "Code point &#60; here."), existing: null).Text);

    [Fact]
    public void Unwrapping_a_tag_does_not_leave_a_double_space_behind()
        => Assert.Equal("Scans a body.", Resolver.Resolve(Member("T", "Scan", "Scans a <para></para> body."), existing: null).Text);

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

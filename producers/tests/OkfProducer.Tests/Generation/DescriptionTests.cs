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

    [Theory]
    [InlineData("Returns a List<T> of results.")]
    [InlineData("A Dictionary<K,V> keyed by name.")]
    [InlineData("Compares Foo<Bar> with Foo<Baz>.")]
    public void An_unpaired_pseudo_tag_is_literal_text_and_keeps_its_content(string doc)
    {
        // `List<T>` is tag-shaped and nothing closes it, so it is not a tag. Eating it would delete `T`
        // and leave "a List of results" -- the exact "prose that merely looks like markup" failure this
        // unwrapper claims to guard against. Unescaped generics are invalid XML that no compiler
        // complains about unless doc files are emitted, and this producer runs on arbitrary repositories.
        Assert.Equal(doc, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Theory]
    [InlineData("Returns a List<T> and <T>content</T>.", "Returns a List<T> and content.")]
    [InlineData("Wraps <c>x</c>, unlike List<c> and <c>y</c>.", "Wraps x, unlike List<c> and y.")]
    [InlineData("A Dictionary<K,V> before <c>this</c> and <K,V>that</K,V>.", "A Dictionary<K,V> before this and that.")]
    public void An_unpaired_generic_stays_literal_even_when_a_later_unrelated_tag_shares_its_name(string doc, string expected)
    {
        // The theory above only covers names with NO closer ANYWHERE in the comment, which an unbounded
        // `IndexOf("</name>")` passes for free. The discriminating shape is an unescaped generic FOLLOWED
        // by a genuinely paired tag of the same name: a forward search that is not scoped to this tag's
        // own partner pairs `List<T>` against the LATER `</T>`, decides it is markup, and deletes `T` --
        // the exact "prose that merely looks like markup" failure the unwrapper claims to guard against,
        // shipped in its general form while only the simple form was fixed.
        Assert.Equal(expected, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Fact]
    public void Properly_nested_tags_of_the_same_name_are_both_unwrapped()
    {
        // The other side of the pairing rule: matching to the NEAREST unmatched opener must not make a
        // legitimately nested pair look unpaired.
        Assert.Equal("An outer inner pair here.",
            Resolver.Resolve(Member("T", "Scan", "An <c>outer <c>inner</c> pair</c> here."), existing: null).Text);
    }

    [Theory]
    [InlineData("Returns a <c>List<T></c> of items.", "Returns a List<T> of items.")]
    [InlineData("A <see cref=\"Foo\">List<T> holder</see> here.", "A List<T> holder here.")]
    [InlineData("Badly <a>nested <b>tags</a> here</b>.", "Badly nested <b>tags here.")]
    public void An_unmatched_opener_INSIDE_a_matched_pair_is_still_literal_text(string doc, string expected)
    {
        // The third side of the pairing rule, and the one that shipped wrong. A closer used to mark every
        // opener at or above its match -- "crossed tags are malformed either way, so keep them markup" --
        // which makes `<c>List<T></c>` a matched `<c>` pair with `<T>` marked markup INSIDE it, and `T` is
        // deleted. That is the same prose-deletion failure the two theories above exist to prevent, in a
        // strictly more reachable shape: those need a later genuinely-paired tag sharing the name, this
        // needs only an unescaped generic inside `<c>`/`<b>`/`<i>`/`<see>`, which is the commonest way a
        // doc comment names a generic. So a closer marks its own match and nothing else.
        //
        // The third row is what that costs: a genuinely crossed `<b>` becomes visible rather than being
        // tidied away. Angle brackets on input that was malformed anyway are worth strictly less than a
        // deleted word, and the crossed shape is the rarer of the two by a wide margin.
        Assert.Equal(expected, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Theory]
    [InlineData("Everything </b and c > survives.")]
    [InlineData("Divides <a and b/> evenly.")]
    public void A_span_whose_markup_is_not_tag_shaped_is_prose(string doc)
    {
        // `</b and c >` parses as a closing tag named `b` if nothing looks past the name, and a closing tag
        // contributes nothing -- so `and c` was deleted, prose eaten by a span that only starts like
        // markup. A closer is a name and nothing else; an opener or self-closing tag is a name followed by
        // attributes, and every attribute carries an `=`. Both are NECESSARY conditions of XML's grammar,
        // so nothing well-formed is rejected by them.
        Assert.Equal(doc, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Fact]
    public void A_bracket_inside_a_quoted_attribute_value_does_not_end_the_tag()
    {
        // Ending the tag at the first `>` cuts `<see cref="a>b">` in half: the front becomes an opener that
        // `</see>` pairs with and deletes, and the back, `b">`, is left standing in the prose.
        Assert.Equal("A tagged mention.",
            Resolver.Resolve(Member("T", "Scan", "A <see cref=\"a>b\">tagged</see> mention."), existing: null).Text);
    }

    [Fact]
    public void A_single_quoted_attribute_value_still_splits_the_tag_it_belongs_to()
    {
        // Pins a KNOWN residual, not a guarantee. `'` is legal XML and FindTagEnd tracks only `"`, so this
        // splits exactly the way the double-quoted form used to, and what it costs HERE is leaked markup
        // (`b'>`). That the cost is never anything worse -- never a deleted word -- is NOT established:
        // measured against a fully `'`-aware FindTagEnd the shipped version deletes a span that parse keeps
        // on hundreds of thousands of soup inputs, and while every case inspected was markup rather than
        // prose, "every case inspected" is the whole of the evidence. FindTagEnd's remarks carry the
        // counts. What IS established is the comparison that keeps the residual: the obvious fix (open a
        // quote on a `'` that follows an `=`) pushes the terminating `>` later, lets a span swallow a
        // nested tag, and deletes the prose inside it SILENTLY, where the residual leaks `b'>` into a
        // description a human reviewing the bundle sees. If this ever returns "A tagged mention." the
        // residual is closed and this test should be deleted, not adjusted.
        Assert.Equal("A b'>tagged mention.",
            Resolver.Resolve(Member("T", "Scan", "A <see cref='a>b'>tagged</see> mention."), existing: null).Text);
    }

    [Theory]
    [InlineData("See <https://example.com/> for details.")]
    [InlineData("See <https://example.com> for details.")]
    [InlineData("Adds <a+b/> together.")]
    [InlineData("Closes </> nothing.")]
    public void A_self_closing_span_whose_name_is_not_a_name_is_prose(string doc)
    {
        // An angle-bracketed URL is ordinary prose in a doc comment, and the trailing slash turned it into
        // a SELF-CLOSING tag: whitespace-free, so tag-shaped, and carrying no `cref`/`name`/`langword`, so
        // its substitution was empty and the whole span was deleted -- `See for details.`. Without the
        // slash the same URL survived as an unmatched opener, so the loss was inconsistent as well as
        // silent.
        //
        // A self-closing span is the ONLY shape that can delete itself on its own authority, with no
        // partner anywhere in the comment, so it is the one shape whose name has to be an XML name. An
        // opener with a nonsense name is harmless (nothing closes it, so it stays prose) and a closer with
        // one loses only its own brackets -- and requiring a name of either would stop `<K,V>that</K,V>`
        // from unwrapping, which the pairing theory above deliberately pins. The empty name of `</>` is
        // rejected for every shape: no opener can have one, so it can never have been a partner.
        //
        // The rows are not equally load-bearing, and saying so is the point of this note. Rows 1, 3 and 4
        // discriminate: dropping the name half of IsTagShaped's condition reddens rows 1 and 3, dropping
        // its empty-name half reddens row 4. Row 2 -- the same URL WITHOUT the trailing slash -- cannot be
        // reddened by any edit to this
        // predicate at all, because rejecting a non-name opener and accepting one that never finds a
        // partner both leave it verbatim. It is here as documentation of the slash/no-slash symmetry that
        // made the original loss inconsistent as well as silent; the invariant it rests on is pinned by
        // An_unpaired_pseudo_tag_is_literal_text_and_keeps_its_content, not by this row.
        Assert.Equal(doc, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Theory]
    [InlineData("Matches <![CDATA[a < b]]> exactly.", "Matches a < b exactly.")]
    [InlineData("Emits <![CDATA[<c>x</c>]]> literally.", "Emits <c>x</c> literally.")]
    public void A_cdata_section_contributes_its_content_without_its_delimiters(string doc, string expected)
    {
        // `<![CDATA[` is not tag-shaped (`!` is neither `/` nor a letter), so before this the delimiters
        // leaked into the description as literal text AND the content between them was scanned for tags
        // like any other prose -- which is precisely what a CDATA section says not to do.
        Assert.Equal(expected, Resolver.Resolve(Member("T", "Scan", doc), existing: null).Text);
    }

    [Fact]
    public void An_unterminated_cdata_section_is_emitted_verbatim()
    {
        // Same rule as an unterminated tag: with no `]]>` there is no section, so the text is prose and
        // is copied through rather than eaten to the end of the comment.
        Assert.Equal("Matches <![CDATA[a < b",
            Resolver.Resolve(Member("T", "Scan", "Matches <![CDATA[a < b"), existing: null).Text);
    }

    [Fact]
    public void A_paired_tag_is_still_unwrapped_even_when_its_name_looks_generic()
    {
        // The discriminator is the matching close tag and nothing else -- `<T>` and `<summary>` are
        // indistinguishable by shape.
        Assert.Equal("inner", Resolver.Resolve(Member("T", "Scan", "<T>inner</T>"), existing: null).Text);
    }

    [Fact]
    public void The_scan_stops_re_reading_a_comment_once_no_bracket_can_end_a_tag()
    {
        // The guard on ScanTokens' early-out, and it counts STEPS rather than milliseconds on purpose: a
        // wall-clock budget in a suite is its own defect, red on a loaded CI agent and green on a fast one,
        // where a step count is a pure function of the input and identical everywhere.
        //
        // What is being guarded. FindTagEnd walks to the end of the comment when it fails and its caller
        // then advances by ONE character and calls it again, so a run of `<` with no `>` anywhere after it
        // re-reads the whole tail once per bracket -- quadratic, on input a hostile repository chooses.
        // ScanTokens leaves the loop once no `>` remains at or after the current `<`. That changes no
        // output whatsoever (with no `>` left, every one of those `<` would fail FindTagEnd and be emitted
        // verbatim, which is exactly what leaving does), which is precisely why no assertion about the text
        // can see it: delete the `lastEnd` check and every other test in this file stays green.
        //
        // First, the control: a comment with real tags must cost something, or the bound below would hold
        // just as well against a meter that had been gutted to return zero.
        DocCommentSource.UnwrapXmlDocTags("Wraps <c>x</c> and <see cref=\"T:A.B\"/> here.", out var tagged);
        Assert.True(tagged > 0, $"the scan cost meter reported {tagged} on a comment full of tags");

        // Then the bound. 4,000 brackets, no `>` at all: guarded this reads nothing, unguarded it reads
        // about 16,000,000 characters -- 500 times the linear budget asserted here, so the margin is not
        // delicate.
        var hostile = "Summary. " + string.Concat(Enumerable.Repeat("<a", 4_000));
        var text = DocCommentSource.UnwrapXmlDocTags(hostile, out var cost);

        Assert.Equal(hostile, text);
        Assert.True(cost <= 4L * hostile.Length, $"scan cost {cost:N0} exceeds the linear budget {4L * hostile.Length:N0} for {hostile.Length:N0} characters");
    }

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

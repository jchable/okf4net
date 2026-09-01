// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Yaml;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

// `CodeGraph` alone would bind to the sibling namespace OkfProducer.Tests.CodeGraph, not to the type
// (CS0118) -- see the same alias, and the same reason, at the top of ConceptGenerator.cs.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §4: the shape of a generated code concept, and the first markdown links this producer ever emits.
/// Before this, <c>ConceptGenerator</c> emitted no links at all, so <c>okf graph</c> on a generated
/// bundle displayed nothing -- a flat list of unrelated concepts rather than a graph.
/// </summary>
public class CodeConceptGeneratorTests
{
    [Fact]
    public void A_member_concept_carries_the_frontmatter_shape_of_section_4_1()
    {
        var concept = Single(Generate(), "code/csharp/n/scanner/scan");
        var fm = concept.Document.Frontmatter;

        Assert.Equal("C# Member", fm.Type);
        Assert.Equal("Scanner.Scan", fm.Title);
        Assert.Equal("doc-comment", fm.Get("description_source")?.AsDisplayString());
        Assert.Contains("csharp", fm.Tags);
        Assert.Null(fm.Get("generated")?.AsMapping()?.Get("at"));   // §4.4: `at` is on overview only
    }

    [Fact]
    public void Resolved_calls_become_absolute_markdown_links()
    {
        // §4.5 / §6.1: absolute so the generator does no relative-path
        // arithmetic, and so `okf graph` resolves them.
        Assert.Contains("[Other.Callee](/code/csharp/n/other/callee)", Single(Generate(), "code/csharp/n/scanner/scan").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_calls_are_code_spans_not_links()
    {
        // 54-58% of call sites have no declaration in the repo. Linking them
        // would emit that many BrokenLink diagnostics and drown `validate`.
        var body = Single(Generate(), "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("## Calls (unresolved)", body, StringComparison.Ordinal);
        Assert.Contains("`string.Substring`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[string.Substring]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Overloads_are_one_concept_listing_every_signature()
    {
        var body = Single(Generate(), "code/csharp/n/t/validate").Document.Body;

        Assert.Contains("public void Validate()", body, StringComparison.Ordinal);
        Assert.Contains("public void Validate(int x)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void With_repo_url_the_resource_is_a_url_and_earns_no_path_warning()
    {
        // §4.3: a bare relative resource resolves against the CONCEPT directory,
        // not the bundle root, so it would miss for every code concept.
        var fm = Single(Generate(repoUrl: "https://github.com/o/r", rev: "main"), "code/csharp/n/scanner/scan").Document.Frontmatter;

        Assert.StartsWith("https://github.com/o/r/blob/main/", fm.Resource, StringComparison.Ordinal);
        Assert.Contains("#L", fm.Resource, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_repo_url_no_resource_is_emitted_rather_than_a_broken_path()
        => Assert.Null(Single(Generate(repoUrl: null), "code/csharp/n/scanner/scan").Document.Frontmatter.Resource);

    [Fact]
    public void There_is_no_called_by_section()
        => Assert.DoesNotContain("## Called by", Single(Generate(), "code/csharp/n/other/callee").Document.Body, StringComparison.Ordinal);

    // -- beyond the plan's own list ---------------------------------------------------------------

    [Fact]
    public void A_by_name_edge_is_linked_just_like_an_exact_one()
    {
        // Only Unresolved falls back to text. A ByName match is a real declaration in this repository
        // -- it just was not resolved with full type information -- so it has a concept to point at,
        // and dropping it to text would lose an edge the graph legitimately has.
        Assert.Contains("[Other.Helper](/code/csharp/n/other/helper)",
            Single(Generate(), "code/csharp/n/t/validate").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_carries_by_and_only_by()
    {
        var generated = Single(Generate(), "code/csharp/n/scanner/scan").Document.Frontmatter.Get("generated")?.AsMapping();

        Assert.NotNull(generated);
        Assert.Equal(ConceptGenerator.ProducerActor, generated.Get("by")?.AsDisplayString());
        Assert.Equal(["by"], generated.Entries.Select(e => e.Key.AsString()));
    }

    [Fact]
    public void A_type_concept_and_its_member_concept_coexist()
    {
        var concepts = Generate();

        Assert.Equal("C# Type", Single(concepts, "code/csharp/n/scanner").Document.Frontmatter.Type);
        Assert.Equal("C# Member", Single(concepts, "code/csharp/n/scanner/scan").Document.Frontmatter.Type);
    }

    [Fact]
    public void A_signature_line_names_the_file_with_forward_slashes_and_a_line_span()
    {
        var body = Single(Generate(), "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("## Signatures", body, StringComparison.Ordinal);
        Assert.Contains("`src/Scanner.cs#L10-L20`", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resource_points_at_the_first_declaration_of_a_merged_overload_set()
    {
        // §3.2 merges overloads into one concept, so `resource` has to pick one span. It picks the
        // Ordinal-first (path, offset) declaration, which is stable under adding an overload later in
        // the file -- the churn the merge exists to prevent.
        var fm = Single(Generate(), "code/csharp/n/t/validate").Document.Frontmatter;

        Assert.Equal("https://github.com/o/r/blob/main/src/T.cs#L5-L6", fm.Resource);
    }

    [Fact]
    public void A_rev_with_a_slash_keeps_its_separator_and_a_space_is_escaped()
    {
        // A branch name is a path in a forge's blob URL, so its separators must survive; everything
        // else in it must not (§4.3: built segment by segment with encoding, never concatenated raw).
        var fm = Single(Generate(rev: "feature/a b"), "code/csharp/n/scanner/scan").Document.Frontmatter;

        Assert.StartsWith("https://github.com/o/r/blob/feature/a%20b/src/Scanner.cs", fm.Resource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repo_url_that_is_not_an_absolute_url_yields_no_resource()
    {
        // The validator classifies `resource` by shape: without a `scheme://` it is a PATH, resolved
        // against the concept's own directory. Emitting one would be the warning-per-concept outcome
        // §4.3 rules out, so a malformed --repo-url degrades to no field, not to a path.
        Assert.Null(Single(Generate(repoUrl: "github.com/o/r"), "code/csharp/n/scanner/scan").Document.Frontmatter.Resource);
    }

    [Fact]
    public void Without_a_rev_no_resource_is_emitted_either()
        => Assert.Null(Single(Generate(rev: null), "code/csharp/n/scanner/scan").Document.Frontmatter.Resource);

    [Fact]
    public void A_manual_description_in_the_existing_bundle_survives_regeneration()
    {
        // §4.2 through the generator, not just through DescriptionResolver: without this wiring the
        // resolver would always see null, preservation would be dead code, and a hand-written
        // description would be destroyed on the next `generate`.
        var options = Options() with
        {
            ExistingFrontmatter = id => id.ToString() == "code/csharp/n/scanner/scan"
                ? ExistingFrontmatter("Hand written.", "manual")
                : null,
        };

        var fm = Single(Generate(options), "code/csharp/n/scanner/scan").Document.Frontmatter;

        Assert.Equal("Hand written.", fm.Description);
        Assert.Equal("manual", fm.Get("description_source")?.AsDisplayString());
        Assert.Contains("Hand written.", Single(Generate(options), "code/csharp/n/scanner/scan").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doc_comment_description_in_the_existing_bundle_is_re_derived()
    {
        var options = Options() with
        {
            ExistingFrontmatter = _ => ExistingFrontmatter("Stale text.", "doc-comment"),
        };

        Assert.Equal("Scans a body.", Single(Generate(options), "code/csharp/n/scanner/scan").Document.Frontmatter.Description);
    }

    [Fact]
    public void A_member_named_index_does_not_shadow_the_reserved_index_file()
    {
        // BundleConceptWriter rejects `index`/`log`; a property named Index is perfectly plausible.
        var graph = GraphOf(Member("N.Scanner", "Index", "public int Index()"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/scanner/index-2", ids);
        Assert.DoesNotContain("code/csharp/n/scanner/index", ids);
    }

    [Fact]
    public void A_case_only_collision_is_broken_by_ordinal_order_of_the_original_name()
    {
        // §3.3: the tie-break keys off the Ordinal order of the symbols' own NAMES, so it survives a
        // file move or a line shift rather than depending on which file the scanner reached first.
        //
        // Asserting that both ids merely EXIST would not test that: input order, path order and name
        // order all produce the same two-id set. So the fixture makes all three orders disagree --
        // lowercase `parse` comes first in input order and first in path order (src/a.cs), while
        // Ordinal name order puts `Parse` first ('P' is 0x50, 'p' is 0x70) -- and the assertion is on
        // WHICH declaration ended up under the unsuffixed id.
        var graph = GraphOf(
            Member("N.Scanner", "parse", "public void parse()", path: "src/a.cs"),
            Member("N.Scanner", "Parse", "public void Parse()", path: "src/z.cs"));

        var concepts = new ConceptGenerator().Generate(Snapshot(), graph, Options());

        Assert.Contains("public void Parse()", Single(concepts, "code/csharp/n/scanner/parse").Document.Body, StringComparison.Ordinal);
        Assert.Contains("public void parse()", Single(concepts, "code/csharp/n/scanner/parse-2").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_concepts_come_out_in_a_pinned_order_shallowest_first_then_ordinal()
    {
        // The group sort decides registration order (which is what §3.3's tie-break rides on) and the
        // order concepts leave this method in. The fixture's symbols are deliberately NOT in this
        // order, so deleting the sort chain changes this sequence rather than passing by luck.
        //
        // `code/csharp/n` leads because Task 9's containment spine gives every namespace a concept of
        // its own (§5.1) and emits it at its own depth: the sequence is still depth-first-then-Ordinal,
        // with the synthesized containers of a level following that level's real declarations.
        var ids = Ids(Generate()).Where(id => id.StartsWith("code/", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            [
                "code/csharp/n",
                "code/csharp/n/other",
                "code/csharp/n/scanner",
                "code/csharp/n/t",
                "code/csharp/n/other/callee",
                "code/csharp/n/other/helper",
                "code/csharp/n/scanner/scan",
                "code/csharp/n/t/validate",
            ],
            ids);
    }

    [Fact]
    public void Signature_bullets_are_ordered_by_declaration_site_not_by_input_order()
    {
        // The within-group sort, pinned on the body rather than only through `resource`: the fixture
        // lists the offset-20 overload first, so input order would put `Validate(int x)` first.
        var body = Single(Generate(), "code/csharp/n/t/validate").Document.Body;

        Assert.True(
            body.IndexOf("public void Validate()", StringComparison.Ordinal)
            < body.IndexOf("public void Validate(int x)", StringComparison.Ordinal),
            body);
    }

    [Fact]
    public void The_registry_spans_the_code_family_as_well_as_packages_and_docs()
    {
        // §3.4: one registry, one allocation record for the whole run. Being exact about what that
        // means, because the tempting claim is false: the four families use disjoint prefixes, so a doc
        // titled "overview" lands on `docs/overview` and CANNOT collide with the bare `overview` id --
        // they coexist, which is what this asserts. What the shared registry buys is that `code/` is in
        // the same record as the rest (the old Generate-local usedIds never covered it) and that
        // `overview` is allocated rather than assumed.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("O.md", "overview")]);

        var ids = Ids(new ConceptGenerator().Generate(snapshot, GraphOf(), Options()));

        Assert.Equal("overview", ids[0]);
        Assert.Contains("docs/overview", ids);
    }

    [Fact]
    public void A_type_whose_name_is_reserved_keeps_its_members_underneath_it()
    {
        // §3.3's invariant is that a type becomes BOTH `log.md` AND `log/`, and Task 9's containment
        // spine is built on that correspondence. Applying the reserved-segment escape to the leaf only
        // would register the type at `log-2` while its members kept the raw container segment and
        // landed under `log/` -- a concept file beside a directory that is not its own, which
        // IndexGenerator would then list as a child of the namespace. Both names are ordinary: Index
        // ships in the BCL as System.Index.
        var graph = GraphOf(
            Type("N", "Log", path: "src/Log.cs"),
            Member("N.Log", "Write", "public void Write()", path: "src/Log.cs"),
            Type("N", "Index", path: "src/Index.cs"),
            Member("N.Index", "Read", "public void Read()", path: "src/Index.cs"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/log-2", ids);
        Assert.Contains("code/csharp/n/log-2/write", ids);
        Assert.Contains("code/csharp/n/index-2", ids);
        Assert.Contains("code/csharp/n/index-2/read", ids);
        Assert.DoesNotContain("code/csharp/n/log/write", ids);
        Assert.DoesNotContain("code/csharp/n/index/read", ids);
    }

    [Fact]
    public void A_member_hangs_off_its_type_s_registered_id_even_when_that_id_was_disambiguated()
    {
        // The same rule as above, reached through §3.3's numeric tie-break rather than the reserved
        // list: whatever made the parent's id differ from its raw name, the child follows the id.
        var graph = GraphOf(
            Type("N", "Thing", path: "src/a.cs"),
            Type("N", "thing", path: "src/b.cs"),
            Member("N.thing", "Go", "public void Go()", path: "src/b.cs"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/thing-2/go", ids);
        Assert.DoesNotContain("code/csharp/n/thing/go", ids);
    }

    [Fact]
    public void A_parent_is_registered_before_its_child_even_when_container_order_says_otherwise()
    {
        // Pins the depth key in the group sort, which was otherwise unobservable -- the exact defect
        // fix round 2 closed one level up, one level down.
        //
        // In the canonical case the ThenBy on Container already orders parents first for free: a
        // child's container is its parent's container with the parent's name appended, so the parent's
        // container is a proper Ordinal prefix of the child's and sorts ahead of it. Deleting the depth
        // key therefore changes nothing -- until two container spellings denote the same structural
        // path. SplitContainer drops empty entries, so `.N.Log` and `N.Log` split identically, and
        // `.N.Log` sorts BEFORE the parent's plain `N` ('.' is 0x2E, 'N' is 0x4E). Without the depth
        // key the member registers first, finds no parent, falls back to its raw container, and lands
        // in `n/log/` while the type -- registered afterwards, and escaped because `log` is reserved --
        // takes `n/log-2`: the severed-member defect all over again.
        var graph = GraphOf(
            Type("N", "Log", path: "src/Log.cs"),
            Member(".N.Log", "Write", "public void Write()", path: "src/Log.cs"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/log-2/write", ids);
        Assert.DoesNotContain("code/csharp/n/log/write", ids);
    }

    [Fact]
    public void A_symbol_whose_name_cannot_form_an_id_still_gets_a_concept()
    {
        // A C# identifier may legally be entirely non-ASCII, and ConceptId.Slugify rejects the empty
        // slug that falls out of it. §2.3: unusual input degrades the output, it never aborts the run.
        var graph = GraphOf(Member("N.Scanner", "概要", "public void 概要()"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/scanner/member", ids);
    }

    [Fact]
    public void A_language_tag_that_is_not_a_valid_id_segment_is_slugified_not_collapsed()
    {
        // Unreachable with the shipped csharp profile, and owned by whoever writes the next one:
        // `c++` and `f#` carry characters ValidateSegment rejects. Left raw, every id built from them
        // would fail to parse, all four fallback rungs would throw, and every symbol of that language
        // would pile into one generic bucket.
        //
        // Containers are deliberately dot-free here. SplitContainer cuts on `.` only for csharp/java
        // and on `/` for everything else, so a dotted container under a `c++` profile stays one
        // segment -- that is SplitContainer's documented rule, not a defect, and mixing it into this
        // test would only obscure the one thing being pinned: the language segment itself.
        var graph = GraphOf(
            Type("N", "Scanner", path: "a.cpp") with { Language = "c++" },
            Member("Scanner", "Scan", "void Scan()", path: "a.cpp") with { Language = "c++" });

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/c-/n/scanner", ids);
        Assert.Contains("code/c-/scanner/scan", ids);
    }

    [Fact]
    public void A_language_that_yields_no_segment_is_skipped_not_collapsed_into_the_fallback_bucket()
    {
        // The honest scope of this test, because its previous name claimed more than it could observe:
        // it pins that an unusable language segment is SKIPPED. Slugifying it instead throws, which
        // sends the fallback ladder to its generic bucket and piles every symbol of that language into
        // `code/member`, `code/member-2` -- trading the desync for the very collapse the c++ case above
        // condemns. Leaving it raw yields `code//n.scanner/scan`, whose registry key would not equal
        // the id ConceptId.Parse returns; that desync is NOT observable from here, because the
        // single-language guard makes the language segment uniform across a run, so no two ids can
        // collide through it. It is pinned where it lives instead, on the registry itself, by
        // CodeConceptIdsTests.The_registry_keys_on_the_id_it_returns_not_on_the_string_it_composed.
        var graph = GraphOf(
            Member("N.Scanner", "Scan", "void Scan()", path: "a.x") with { Language = "" },
            Member("N.Other", "Scan", "void Scan()", path: "b.x") with { Language = "" });

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/n.scanner/scan", ids);
        Assert.Contains("code/n.other/scan", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("code/member", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repo_url_carrying_a_query_or_fragment_does_not_leak_it_into_the_middle_of_the_link()
    {
        // Trimming the raw string yields `https://github.com/o/r?x=1/blob/main/...`, which the
        // validator still classifies as a Url and still passes with no warning -- a silently wrong
        // link. The parsed Uri is already at hand, so use it.
        var fm = Single(Generate(repoUrl: "https://github.com/o/r?x=1#frag"), "code/csharp/n/scanner/scan").Document.Frontmatter;

        Assert.Equal("https://github.com/o/r/blob/main/src/Scanner.cs#L10-L20", fm.Resource);
    }

    [Fact]
    public void A_signature_carrying_a_backtick_cannot_break_out_of_its_code_span()
    {
        // Signatures come out of source files, which §2.3 treats as untrusted: a naive $"`{sig}`" would
        // let one close the span early and corrupt the rest of the document.
        var graph = GraphOf(Member("N.Scanner", "Odd", "public void Odd(string s = \"`\")"));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/odd").Document.Body;

        Assert.Contains("``public void Odd(string s = \"`\")``", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_call_sites_to_the_same_target_collapse_to_one_bullet()
    {
        var body = Single(Generate(), "code/csharp/n/scanner/scan").Document.Body;
        var occurrences = body.Split("[Other.Callee](/code/csharp/n/other/callee)").Length - 1;

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_concept_with_no_calls_carries_neither_calls_section()
    {
        var body = Single(Generate(), "code/csharp/n/other/callee").Document.Body;

        Assert.DoesNotContain("## Calls", body, StringComparison.Ordinal);
        Assert.Contains("## Signatures", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_language_fails_loudly_because_the_call_join_carries_no_language()
    {
        // CallSite names its caller and its target as (container, name) with no language, so both joins
        // are language-agnostic -- unambiguous only while v1 ships one profile. Left as a comment, the
        // day a second profile lands two languages sharing a container and name would attribute the same
        // call to BOTH concepts, silently: a confidently wrong edge, which is worse than a missing one.
        // The guard is the specification of what to fix, so it must name the languages and the join.
        var graph = GraphOf(
            Member("N.Scanner", "Scan", "public void Scan()"),
            new SymbolFact(SymbolKind.Member, "typescript", "src/lib/scanner", "scan", "function scan()",
                SymbolVisibility.Public, "src/lib/scanner.ts", 0, 1, 1, 2, null));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("csharp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("typescript", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CallSite", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BuildCodeConcepts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_language_and_no_language_at_all_both_generate_without_the_guard_firing()
    {
        // The guard must key off "more than one", not "not exactly one": an empty graph is the ordinary
        // no-code-found case and must still produce the non-code families.
        Assert.Contains("code/csharp/n/scanner/scan", Ids(Generate()));
        Assert.Equal(["overview"], Ids(new ConceptGenerator().Generate(Snapshot(), GraphOf(), Options())));
    }

    [Fact]
    public void A_link_this_producer_derived_is_neutralized_before_it_reaches_a_body()
    {
        // Found by the end-to-end run, not by reasoning: LinkScanner's own XML summary in this
        // repository contains the literal `<c>[text](dest)</c>`, DocCommentSource carries it into the
        // description, and rendering it verbatim made it the only broken link in a 648-concept bundle.
        // The author wrote doc syntax that merely looks like markdown; they did not write a bundle link.
        //
        // Asserted against OKF4net's own LinkScanner, which is what the validator uses -- not against
        // the escaped text, which would only pin the spelling of the fix. Escaping `[` instead would
        // pass a text assertion and fail this one: ScanLineLinks dispatches on `[` with no look-back.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "See [text](dest) for more."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Empty(LinkScanner.ExtractLinks(body));
        Assert.Contains("[text\\](dest)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_a_human_wrote_is_left_exactly_as_written()
    {
        // The other half of the asymmetry, and the half that makes the rule a rule rather than a blanket
        // escape: a `[text](dest)` in a manual description is a link the author meant. Keyed on
        // `description_source`, so the set neutralized is exactly the set §4.2 re-derives.
        var options = Options() with
        {
            ExistingFrontmatter = _ => ExistingFrontmatter("See [Other](/code/csharp/n/other) for more.", "manual"),
        };

        var body = Single(Generate(options), "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("[Other](/code/csharp/n/other)", body, StringComparison.Ordinal);
        Assert.Contains(LinkScanner.ExtractLinks(body), link => link.Target == "/code/csharp/n/other");
    }

    [Fact]
    public void A_bundle_authored_description_still_has_its_leading_fence_defused()
    {
        // Fix round 1 on Task 10: the ruling above ("a manual description is left exactly as written")
        // held for every OTHER marker, but not for a fence -- a fence is not a rendering choice, it is
        // structural. LinkScanner.ExtractLinks skips every line after an UNBALANCED one as code, so a
        // fence surviving in a preserved description (the shape a real bundle's frontmatter takes after
        // a prior --update) would still silently sever this concept's own `## Calls` link, exactly the
        // carry-over-B bug, just reachable through the OTHER route into BodyDescription.
        var options = Options() with
        {
            ExistingFrontmatter = _ => ExistingFrontmatter("```\n- a bullet the fence would otherwise hide.", "manual"),
        };

        var concept = Single(Generate(options), "code/csharp/n/scanner/scan");

        // Frontmatter keeps the author's exact bytes, as every preserved field does.
        Assert.Equal("```\n- a bullet the fence would otherwise hide.", concept.Document.Frontmatter.Description);

        // The fence is defused, so the concept's own `## Calls` link (present in the fixture graph --
        // see Resolved_calls_become_absolute_markdown_links) is not swallowed as code.
        Assert.Contains(LinkScanner.ExtractLinks(concept.Document.Body), link => link.Target == "/code/csharp/n/other/callee");

        // And ONLY the fence: the bullet marker on line 2 is left exactly as written, proving this is a
        // narrow fence-only exception rather than BodyDescription silently escaping again.
        Assert.Contains("\\```\n- a bullet the fence would otherwise hide.", concept.Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tagged_link_is_unwrapped_first_and_then_neutralized()
    {
        // The two fixes meeting, in the exact shape that produced the original defect: LinkScanner's own
        // summary contains the literal `<c>[text](dest)</c>`. Unwrapping runs first and EXPOSES link
        // syntax that was hidden inside a tag, so the order matters -- the exposed syntax has to reach
        // the guard rather than slip past it. The reader ends up seeing `[text](dest)`, tags gone, and
        // the scanner sees no link.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()",
            doc: "Scanner for inline <c>[text](dest)</c> links."));

        var concept = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc");

        Assert.Equal("Scanner for inline [text](dest) links.", concept.Document.Frontmatter.Description);
        Assert.Contains("Scanner for inline [text\\](dest) links.", concept.Document.Body, StringComparison.Ordinal);
        Assert.Empty(LinkScanner.ExtractLinks(concept.Document.Body));
    }

    [Theory]
    [InlineData("# Citations are the point.", "\\# Citations are the point.")]
    [InlineData("- a dashed opening.", "\\- a dashed opening.")]
    [InlineData("* a starred opening.", "\\* a starred opening.")]
    [InlineData("+ a plus opening.", "\\+ a plus opening.")]
    [InlineData("> a quoted opening.", "\\> a quoted opening.")]
    [InlineData("1. an ordered opening.", "1\\. an ordered opening.")]
    [InlineData("12) another ordered opening.", "12\\) another ordered opening.")]
    [InlineData("```csharp\nvar x = 1;", "\\```csharp\nvar x = 1;")]
    [InlineData("~~~\nfenced.", "\\~~~\nfenced.")]
    public void A_description_that_would_open_a_block_is_escaped_at_that_one_character(string doc, string expected)
    {
        // A description is rendered as a paragraph; its first non-space character is the only thing that
        // can change that. `\#` renders as `#`, so the reader is unaffected -- and for an ordered list it
        // is the DELIMITER that is escaped, since `\1.` would render a literal backslash.
        //
        // A text assertion, and stated as one: nothing in OKF4net parses block structure, so unlike the
        // link and citation cases there is no consumer to assert through. See the report for why the
        // `# Citations` case is a rendering fault today rather than a LegacyCitations warning. The fence
        // cases below get their OWN LinkScanner-backed test, since an unbalanced fence has a real
        // consumer to answer to, not just a renderer.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: doc));

        var concept = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc");

        Assert.Equal(doc, concept.Document.Frontmatter.Description);
        Assert.Contains("\n\n" + expected + "\n", concept.Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unbalanced_fence_in_a_description_does_not_swallow_the_rest_of_the_body()
    {
        // Task 9's cap parked this one rather than fixing it (Ruling R44): EscapeLineBlockMarker defused
        // every other block marker but not a leading ``` or ~~~, and LinkScanner.ExtractLinks skips
        // every line after an UNBALANCED fence -- so a doc comment containing one hides this concept's
        // own outgoing `## Contains` links from the very scanner that resolves them. Nothing dangles, so
        // validation stays silent; the branch is simply severed. A doc comment ending mid-fence, without
        // a closing pair, is ordinary -- it is only a description, not a whole document.
        //
        // Asserted through LinkScanner.ExtractLinks, not by eye, as the earlier fixes in this file are.
        var graph = GraphOf(
            Type("N", "Scanner", "src/Scanner.cs", doc: "```\nvar unterminated = fence;"),
            Member("N.Scanner", "Scan", "public void Scan()"));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner").Document.Body;

        Assert.Contains(LinkScanner.ExtractLinks(body), link => link.Target == "/code/csharp/n/scanner/scan");
    }

    [Theory]
    [InlineData("A normal opening.")]
    [InlineData("2026 was the year.")]
    [InlineData("v1.0 shipped.")]
    public void A_description_that_opens_no_block_is_left_alone(string doc)
    {
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: doc));

        Assert.Contains("\n\n" + doc + "\n",
            Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_bracket_inside_a_code_span_is_left_exactly_as_written()
    {
        // Live on this repository before the fix: YamlValue.cs's summary shipped as
        // ``A sequence (`[...\]` or block `- ...`).`` -- CommonMark does not process backslash escapes
        // inside a code span, so the backslash is VISIBLE and the guard corrupts the prose it exists to
        // protect. It is also provably unnecessary there: LinkScanner blanks code spans before scanning,
        // so a `]` inside one could never have produced a link. Both halves are asserted.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()",
            doc: "A sequence (`[...]` or block `- ...`)."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Contains("A sequence (`[...]` or block `- ...`).", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\]", body, StringComparison.Ordinal);
        Assert.Empty(LinkScanner.ExtractLinks(body));
    }

    [Fact]
    public void A_bracket_outside_a_code_span_is_still_escaped_when_a_span_is_present()
    {
        // The toggle must not swallow the rest of the text: a `]` after a closed code span is back in
        // prose and still has to be neutralised.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()",
            doc: "See `code` then [text](dest)."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Contains("See `code` then [text\\](dest).", body, StringComparison.Ordinal);
        Assert.Empty(LinkScanner.ExtractLinks(body));
    }

    [Fact]
    public void An_escaped_backtick_does_not_desynchronise_the_producer_from_the_scanner()
    {
        // The toggle must mirror LinkScanner.BlankInlineCode LITERALLY, because that method is what
        // decides whether a link exists. It has no backslash awareness: it closes its span at the second
        // backtick and reads the rest as prose. A producer that treated the first backtick as escaped
        // would still believe it was inside a span there and stop escaping -- and ship a live link.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "A \\` b ` [text](dest)."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Empty(LinkScanner.ExtractLinks(body));
    }

    [Fact]
    public void A_code_span_does_not_leak_across_a_newline()
    {
        // BlankInlineCode runs per line, so an unclosed backtick cannot make the NEXT line code. A
        // producer that carried the flag across `\n` would treat every later line as code and stop
        // escaping -- and a `.csproj` <Description> reaches a body with its newlines intact.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "Opens ` here.\nThen [text](dest)."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Empty(LinkScanner.ExtractLinks(body));
    }

    [Theory]
    [InlineData("Summary line.\n---\nMore prose.", "\\---")]
    [InlineData("Summary line.\n***\nMore prose.", "\\***")]
    [InlineData("Summary line.\n===\nMore prose.", "\\===")]
    [InlineData("Summary line.\n___\nMore prose.", "\\___")]
    [InlineData("Summary line.\n--\nMore prose.", "\\--")]
    public void A_line_that_is_nothing_but_markers_is_defused(string doc, string expected)
    {
        // Requiring a space after `-` let thematic breaks and setext underlines through: `---` on its
        // own line turns the paragraph ABOVE it into a heading, which is precisely the block-type change
        // this guard promises to prevent. A line whose every non-space character is the same marker is
        // escaped at its first character.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: doc));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_emphasis_is_not_mistaken_for_a_bullet()
    {
        // A bullet marker requires a following space; without that check `*fast* and small.` becomes
        // `\*fast* and small.` and renders its asterisks literally.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "*fast* and small."));

        Assert.Contains("\n\n*fast* and small.\n",
            Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_marker_on_a_later_line_is_escaped_too()
    {
        // A multi-line description reaches the body with its newlines intact, so a `- ` opening line 2
        // starts a list exactly as it would opening line 1. The guarantee has to cover all of it.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "First line.\n- second line."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Contains("First line.\n\\- second line.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Neutralizing_never_doubles_an_escape_the_author_already_wrote()
    {
        // An already-escaped bracket must be copied through, not escaped again: `\\]` renders a visible
        // backslash, which would be this fix corrupting the very text it exists to preserve.
        var graph = GraphOf(Member("N.Scanner", "Doc", "public void Doc()", doc: "Keep [a\\] and [b](c)."));

        var body = Single(new ConceptGenerator().Generate(Snapshot(), graph, Options()), "code/csharp/n/scanner/doc").Document.Body;

        Assert.Empty(LinkScanner.ExtractLinks(body));
        Assert.DoesNotContain("\\\\]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_generated_concept_passes_the_strict_producer_validation()
    {
        foreach (var concept in Generate())
        {
            concept.Document.Validate();
        }
    }

    [Fact]
    public void Two_runs_over_the_same_graph_produce_identical_ids_and_bodies()
    {
        var first = Generate();
        var second = Generate();

        Assert.Equal(Ids(first), Ids(second));
        Assert.Equal(
            first.Select(c => c.Document.Body).ToList(),
            second.Select(c => c.Document.Body).ToList());
    }

    [Fact]
    public void Without_a_code_graph_the_output_is_unchanged()
    {
        var snapshot = Snapshot();

        var ids = Ids(new ConceptGenerator().Generate(snapshot));

        Assert.Equal(["overview"], ids);
    }

    // -- fixture ----------------------------------------------------------------------------------

    private static IReadOnlyList<GeneratedConcept> Generate(
        string? repoUrl = "https://github.com/o/r",
        string? rev = "main")
        => Generate(Options(repoUrl, rev));

    private static IReadOnlyList<GeneratedConcept> Generate(GenerateOptions options)
        => new ConceptGenerator().Generate(Snapshot(), Graph(), options);

    private static GenerateOptions Options(string? repoUrl = "https://github.com/o/r", string? rev = "main")
        => new() { RepoUrl = repoUrl, Rev = rev, Profiles = [CSharp] };

    private static RepositorySnapshot Snapshot() => new("/repo", "my-repo", [], []);

    private static GeneratedConcept Single(IReadOnlyList<GeneratedConcept> concepts, string id)
        => concepts.Single(c => c.Id.ToString() == id);

    private static List<string> Ids(IReadOnlyList<GeneratedConcept> concepts)
        => concepts.Select(c => c.Id.ToString()).ToList();

    private static Frontmatter ExistingFrontmatter(string description, string descriptionSource) =>
        OkfDocumentBuilder.ForType("C# Member")
            .Description(description)
            .Extension(DescriptionResolver.DescriptionSourceKey, new YamlString(descriptionSource))
            .Body("body\n")
            .Build()
            .Frontmatter;

    /// <summary>
    /// The fixture graph: <c>N.Scanner.Scan</c> calls <c>N.Other.Callee</c> exactly (twice, from two
    /// sites, so the dedup is exercised) and <c>string.Substring</c> not at all; <c>N.T.Validate</c> is
    /// two overloads on one concept and reaches <c>N.Other.Helper</c> by name only.
    /// </summary>
    private static CodeGraphModel Graph() => new(
        [
            Type("N", "Scanner", path: "src/Scanner.cs"),
            Member("N.Scanner", "Scan", "public void Scan()", path: "src/Scanner.cs", startLine: 10, endLine: 20,
                doc: "Scans a body."),
            Type("N", "Other", path: "src/Other.cs"),
            Member("N.Other", "Callee", "public void Callee()", path: "src/Other.cs"),
            Member("N.Other", "Helper", "public void Helper()", path: "src/Other.cs"),
            Type("N", "T", path: "src/T.cs"),
            // Deliberately listed later-offset-first, so input order and sorted order DISAGREE: every
            // assertion about which overload comes first would pass by luck if this pair were already
            // in order, since GroupBy preserves input order when the sort is removed.
            Member("N.T", "Validate", "public void Validate(int x)", path: "src/T.cs", startLine: 8, endLine: 9, offset: 20),
            Member("N.T", "Validate", "public void Validate()", path: "src/T.cs", startLine: 5, endLine: 6, offset: 10),
        ],
        [
            new ResolvedEdge(new CallSite("N.Scanner", "Scan", "Other.Callee", "src/Scanner.cs", 100),
                "N.Other", "Callee", EdgeConfidence.Exact),
            new ResolvedEdge(new CallSite("N.Scanner", "Scan", "Other.Callee", "src/Scanner.cs", 140),
                "N.Other", "Callee", EdgeConfidence.Exact),
            new ResolvedEdge(new CallSite("N.Scanner", "Scan", "string.Substring", "src/Scanner.cs", 180),
                null, null, EdgeConfidence.Unresolved),
            new ResolvedEdge(new CallSite("N.T", "Validate", "Helper", "src/T.cs", 60),
                "N.Other", "Helper", EdgeConfidence.ByName),
        ],
        RunStatus.Complete);

    private static CodeGraphModel GraphOf(params SymbolFact[] symbols) => new(symbols, [], RunStatus.Complete);

    private static SymbolFact Type(string container, string name, string path, string? doc = null) =>
        new(SymbolKind.Type, "csharp", container, name, $"public class {name}",
            SymbolVisibility.Public, path, 0, 1, 1, 2, doc);

    private static SymbolFact Member(
        string container,
        string name,
        string signature,
        string path = "src/Scanner.cs",
        int startLine = 3,
        int endLine = 4,
        int offset = 0,
        string? doc = null) =>
        new(SymbolKind.Member, "csharp", container, name, signature,
            SymbolVisibility.Public, path, offset, offset + 1, startLine, endLine, doc);

    private static readonly LanguageProfile CSharp = new(
        Language: "csharp",
        GrammarName: "c_sharp",
        DeclarationQuery: string.Empty,
        CallQuery: string.Empty,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);
}

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
        // §3.3: the tie-break keys off the Ordinal order of the symbols' own names, so it survives a
        // file move or a line shift rather than depending on which file the scanner reached first.
        var graph = GraphOf(
            Member("N.Scanner", "parse", "public void parse()", path: "src/z.cs"),
            Member("N.Scanner", "Parse", "public void Parse()", path: "src/a.cs"));

        var ids = Ids(new ConceptGenerator().Generate(Snapshot(), graph, Options()));

        Assert.Contains("code/csharp/n/scanner/parse", ids);
        Assert.Contains("code/csharp/n/scanner/parse-2", ids);
    }

    [Fact]
    public void The_registry_spans_the_code_family_as_well_as_packages_and_docs()
    {
        // §3.4: one registry for all four families. A doc whose title slugifies to "overview" must not
        // be able to take the reserved-in-practice `overview` id, and neither must anything under code/.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("O.md", "overview")]);

        var ids = Ids(new ConceptGenerator().Generate(snapshot, GraphOf(), Options()));

        Assert.Equal("overview", ids[0]);
        Assert.Contains("docs/overview", ids);
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
            Member("N.T", "Validate", "public void Validate()", path: "src/T.cs", startLine: 5, endLine: 6, offset: 10),
            Member("N.T", "Validate", "public void Validate(int x)", path: "src/T.cs", startLine: 8, endLine: 9, offset: 20),
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

    private static SymbolFact Type(string container, string name, string path) =>
        new(SymbolKind.Type, "csharp", container, name, $"public class {name}",
            SymbolVisibility.Public, path, 0, 1, 1, 2, null);

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

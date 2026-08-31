// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

// `CodeGraph` alone would bind to the sibling namespace OkfProducer.Tests.CodeGraph, not to the type
// (CS0118) -- see the same alias, and the same reason, at the top of ConceptGenerator.cs.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §5: the containment spine -- the descending links that make a generated bundle navigable from
/// <c>overview</c> down to a single member -- and the namespace concepts that spine needs to exist at
/// all. Before this, <c>okf graph</c> on a generated bundle showed call edges between members and
/// nothing above them: no way in from a package, and no concept for a namespace to be a way in to.
/// </summary>
public class ContainmentTests
{
    [Fact]
    public void Every_namespace_gets_a_real_concept()
    {
        // A link to a directory's index.md would be a BrokenLink: index.md is a reserved file, not a
        // concept (§5.1). So a namespace needs a concept of its own, coexisting with its directory
        // exactly as a type does (`n.md` beside `n/`).
        //
        // Typed "C# Container" and not "C# Namespace": what this pass identifies is a level of the path
        // tree no extracted declaration claims, which is a namespace most of the time and measurably not
        // always -- 8 of the ~31 synthesized on this repository are private nested types whose members
        // outlived the visibility scope filter. See ConceptGenerator.ContainerToken.
        var concept = Single(Generate(), "code/csharp/n");

        Assert.Equal("C# Container", concept.Document.Frontmatter.Type);
        Assert.Equal("N", concept.Document.Frontmatter.Title);
        Assert.Contains("container", concept.Document.Frontmatter.Tags);

        // No `resource`: a namespace is not declared in one file, and §4.3 admits only a URL there.
        Assert.Null(concept.Document.Frontmatter.Resource);
    }

    [Fact]
    public void Each_level_links_exactly_one_level_down()
    {
        // §5.2, and it is churn control, not cosmetics: if overview listed all 480 concepts, adding one
        // type would rewrite overview. With one level, adding a type touches its namespace alone.
        Assert.Contains("(/code/csharp/n)", Single(Generate(), "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.Contains("(/code/csharp/n/scanner)", Single(Generate(), "code/csharp/n").Document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/code/csharp/n/scanner/scan", Single(Generate(), "code/csharp/n").Document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/code/csharp/n/scanner", Single(Generate(), "packages/lib").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_owns_the_namespaces_declared_by_its_Compile_items()
    {
        // §5.1: NOT "the files in its folder" -- MSBuild lets a project add, remove and link sources
        // across directories. The fixture's only C# file is `linked/Scanner.cs`, which is not under the
        // project's directory at all: directory containment attributes nothing here, and only the
        // `Compile` item set the composition root read out of MSBuild can produce this link.
        Assert.Contains("(/code/csharp/n)", Single(Generate(), "packages/lib").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_claimed_by_two_projects_is_attached_once_to_the_first_ordinal_project()
    {
        var concepts = Generate(sharedFile: true);

        var linkCount = concepts.Count(c => c.Document.Body.Contains("(/code/csharp/shared)", StringComparison.Ordinal));

        Assert.Equal(1, linkCount);
        Assert.Contains("(/code/csharp/shared)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_claimants_are_named_rather_than_duplicated()
    {
        // §5.1's second half: the concept mentions the others. As text, never as a link -- a link would
        // give the namespace a second incoming containment edge, which is the duplication the
        // Ordinal-first rule exists to prevent.
        var body = Single(Generate(sharedFile: true), "code/csharp/shared").Document.Body;

        Assert.Contains("## Also compiled by", body, StringComparison.Ordinal);
        Assert.Contains("`lib2`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("(/packages/lib2)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_ownership_map_no_package_link_is_emitted_and_the_run_says_so()
    {
        // The degradation §5.1 demands. A missing link leaves the spine incomplete, which is visible and
        // costs a reader one hop; a link guessed from the directory tree attributes a namespace to the
        // wrong package, which is a confident lie. So: no link, and a note saying why.
        var notes = new List<string>();
        var concepts = new ConceptGenerator().Generate(Snapshot(), Graph(), Options() with { SourceOwnership = null, Note = notes.Add });

        Assert.DoesNotContain("(/code/csharp/n)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.Contains(concepts, c => c.Id.ToString() == "code/csharp/n");
        Assert.Contains(notes, note => note.Contains("Compile", StringComparison.Ordinal) && note.Contains("directory", StringComparison.Ordinal));
    }

    [Fact]
    public void A_namespace_no_project_claims_is_reported_rather_than_attributed_anyway()
    {
        // The same rule one level down: a map that simply does not mention this file attributes nothing,
        // and the run says how many containers came out of the pass unattached to a package.
        var notes = new List<string>();
        var ownership = SourceOwnershipMap.From(RepoRoot,
            [new ProjectCompileItems("src/A/A.csproj", "net10.0", ["src/A/Unrelated.cs"])]);

        var concepts = new ConceptGenerator().Generate(Snapshot(), Graph(), Options() with { SourceOwnership = ownership, Note = notes.Add });

        Assert.DoesNotContain("(/code/csharp/n)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.Contains(notes, note => note.Contains("not attributed to a package", StringComparison.Ordinal));
    }

    [Fact]
    public void A_claimant_that_is_not_a_detected_package_does_not_win_the_tie()
    {
        // "The first .csproj in Ordinal order" is a rule for choosing between PACKAGES. A test project
        // or a tool outside the solution has no package concept to attach to, so letting it win the tie
        // would drop the link entirely while a real package compiles the very same file. `build/...`
        // sorts before `src/...`, so this fixture fails without that distinction rather than by luck.
        var ownership = SourceOwnershipMap.From(RepoRoot,
            [
                new ProjectCompileItems("src/A/A.csproj", "net10.0", ["linked/Scanner.cs"]),
                new ProjectCompileItems("build/Tool/Tool.csproj", "net10.0", ["linked/Scanner.cs"]),
            ]);

        var concepts = new ConceptGenerator().Generate(Snapshot(), Graph(), Options() with { SourceOwnership = ownership });

        Assert.Contains("(/code/csharp/n)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("## Also compiled by", Single(concepts, "code/csharp/n").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbol_absent_from_a_target_framework_says_so_in_its_body()
    {
        // §5.1's multi-TFM rule: the symbols are the union across frameworks, and a symbol missing from
        // one says so, rather than a concept per TFM multiplying the bundle for information nobody asks
        // for at that level.
        var ownership = SourceOwnershipMap.From(RepoRoot,
            [
                new ProjectCompileItems("src/A/A.csproj", "net10.0", ["linked/Scanner.cs"]),
                new ProjectCompileItems("src/A/A.csproj", "net8.0", []),
            ]);

        var body = Single(
            new ConceptGenerator().Generate(Snapshot(), Graph(), Options() with { SourceOwnership = ownership }),
            "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("## Target frameworks", body, StringComparison.Ordinal);
        Assert.Contains("Absent from `net8.0`", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbol_every_framework_compiles_carries_no_framework_section()
    {
        Assert.DoesNotContain("## Target frameworks", Single(Generate(), "code/csharp/n/scanner/scan").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_links_to_its_packages_and_docs_and_nothing_deeper()
    {
        var body = Single(Generate(), "overview").Document.Body;

        Assert.Contains("(/packages/lib)", body, StringComparison.Ordinal);
        Assert.Contains("(/docs/readme)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/code/", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Containment_and_calls_are_two_families_that_do_not_mix()
    {
        // §5.2: `okf graph` sees both; it is the body section that says which is which. A member is a
        // leaf of the containment tree and still the source of call edges.
        var type = Single(Generate(), "code/csharp/n/scanner").Document.Body;
        var member = Single(Generate(), "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("## Contains", type, StringComparison.Ordinal);
        Assert.DoesNotContain("## Calls", type, StringComparison.Ordinal);
        Assert.DoesNotContain("## Contains", member, StringComparison.Ordinal);
        Assert.Contains("[Other.Callee](/code/csharp/n/other/callee)", member, StringComparison.Ordinal);
    }

    [Fact]
    public void A_namespace_whose_name_is_reserved_is_escaped_and_keeps_its_children()
    {
        // `index`/`log` collide with the bundle's own reserved files, and a namespace is as free to be
        // called `Index` as a type is. The escape has to carry the children with it, or the namespace's
        // file would sit beside a directory that is not its own (§3.3).
        var graph = new CodeGraphModel(
            [
                Type("Index", "Thing", "src/A/Thing.cs"),
                Member("Index.Thing", "Go", "public void Go()", "src/A/Thing.cs"),
            ],
            [],
            RunStatus.Complete);

        var ids = new ConceptGenerator().Generate(Snapshot(), graph, Options()).Select(c => c.Id.ToString()).ToList();

        Assert.Contains("code/csharp/index-2", ids);
        Assert.Contains("code/csharp/index-2/thing", ids);
        Assert.Contains("code/csharp/index-2/thing/go", ids);
        Assert.DoesNotContain("code/csharp/index", ids);
    }

    [Fact]
    public void A_nested_namespace_hangs_off_its_parent_and_not_off_the_package()
    {
        // §5.2 again, at the one place it is easy to get wrong: a package must link to the namespaces it
        // declares into, minus those already reachable from another of its own links. `N.Sub` is listed
        // by `N`, so the package names `N` alone.
        var graph = new CodeGraphModel(
            [
                Type("N", "Scanner", "linked/Scanner.cs"),
                Type("N.Sub", "Deep", "linked/Scanner.cs"),
            ],
            [],
            RunStatus.Complete);

        var concepts = new ConceptGenerator().Generate(Snapshot(), graph, Options());

        Assert.Contains("(/code/csharp/n)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("(/code/csharp/n/sub)", Single(concepts, "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.Contains("(/code/csharp/n/sub)", Single(concepts, "code/csharp/n").Document.Body, StringComparison.Ordinal);
        Assert.Contains("(/code/csharp/n/sub/deep)", Single(concepts, "code/csharp/n/sub").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manual_description_on_a_namespace_survives_regeneration()
    {
        // §4.2 reaches the namespace family too: a container concept is a real concept a human can edit,
        // so it goes through the same DescriptionResolver rather than a second copy of the rule.
        var options = Options() with
        {
            ExistingFrontmatter = id => id.ToString() == "code/csharp/n"
                ? OkfDocumentBuilder.ForType("C# Container")
                    .Description("Hand written.")
                    .Extension(DescriptionResolver.DescriptionSourceKey, new OKF4net.Yaml.YamlString("manual"))
                    .Body("body\n")
                    .Build()
                    .Frontmatter
                : null,
        };

        var fm = Single(new ConceptGenerator().Generate(Snapshot(), Graph(), options), "code/csharp/n").Document.Frontmatter;

        Assert.Equal("Hand written.", fm.Description);
        Assert.Equal("manual", fm.Get("description_source")?.AsDisplayString());
    }

    [Fact]
    public void Every_generated_concept_passes_the_strict_producer_validation()
    {
        foreach (var concept in Generate(sharedFile: true))
        {
            concept.Document.Validate();
        }
    }

    [Fact]
    public void Two_runs_over_the_same_inputs_produce_identical_ids_and_bodies()
    {
        var first = Generate(sharedFile: true);
        var second = Generate(sharedFile: true);

        Assert.Equal(first.Select(c => c.Id.ToString()), second.Select(c => c.Id.ToString()));
        Assert.Equal(first.Select(c => c.Document.Body), second.Select(c => c.Document.Body));
    }

    [Fact]
    public void The_bundle_that_comes_out_validates_clean()
    {
        using var tmp = new TempDir();
        Write(Generate(sharedFile: true, repoUrl: "https://github.com/o/r"), tmp.Path);

        var outcome = new BundleValidationRunner().Validate(tmp.Path, new FixedClock(new DateOnly(2026, 8, 31)));

        Assert.True(outcome.IsConformant, string.Join("\n", outcome.DiagnosticLines));

        // Not `DiagnosticLines.Contains("BrokenLink")`: Diagnostic.ToString() renders
        // `[severity] path: message` and never the DiagnosticCode, so that assertion would pass on any
        // bundle whatsoever, including one where every containment link dangles. The check that can
        // actually fail is the bundle's own link resolution -- which is the whole point of this lot,
        // since a dangling containment link is exactly what would make `okf graph` show nothing.
        Assert.Empty(Bundle.Load(tmp.Path).BrokenLinks());
    }

    // -- fixture ----------------------------------------------------------------------------------

    private const string RepoRoot = "/repo";

    private static IReadOnlyList<GeneratedConcept> Generate(bool sharedFile = false, string? repoUrl = null)
        => new ConceptGenerator().Generate(
            Snapshot(sharedFile),
            Graph(sharedFile),
            Options(repoUrl) with { SourceOwnership = Ownership(sharedFile) });

    private static GenerateOptions Options(string? repoUrl = null) => new()
    {
        RepoUrl = repoUrl,
        Rev = repoUrl is null ? null : "main",
        Profiles = [CSharp],
        SourceOwnership = Ownership(sharedFile: false),
    };

    /// <summary>
    /// One package whose <c>.csproj</c> sits in <c>src/A/</c> while its only source file lives in
    /// <c>linked/</c> -- so no directory rule could attribute the namespace -- plus, on the shared
    /// fixture, a second package that claims the same shared file as the first.
    /// </summary>
    private static RepositorySnapshot Snapshot(bool sharedFile = false) => new(
        RepoRoot,
        "my-repo",
        sharedFile
            ?
            [
                new PackageManifest("nuget", "src/A/A.csproj", "lib", null),
                new PackageManifest("nuget", "src/B/B.csproj", "lib2", null),
            ]
            : [new PackageManifest("nuget", "src/A/A.csproj", "lib", null)],
        [new DocFile("README.md", "Readme")]);

    private static SourceOwnershipMap Ownership(bool sharedFile) => SourceOwnershipMap.From(
        RepoRoot,
        sharedFile
            ?
            [
                new ProjectCompileItems("src/A/A.csproj", "net10.0", ["linked/Scanner.cs", "src/shared/Thing.cs"]),
                new ProjectCompileItems("src/B/B.csproj", "net10.0", ["src/shared/Thing.cs"]),
            ]
            : [new ProjectCompileItems("src/A/A.csproj", "net10.0", ["linked/Scanner.cs"])]);

    private static CodeGraphModel Graph(bool sharedFile = false) => new(
        sharedFile
            ? [.. CoreSymbols, Type("Shared", "Thing", "src/shared/Thing.cs")]
            : CoreSymbols,
        [
            new ResolvedEdge(new CallSite("N.Scanner", "Scan", "Other.Callee", "linked/Scanner.cs", 100),
                "N.Other", "Callee", EdgeConfidence.Exact),
        ],
        RunStatus.Complete);

    private static readonly SymbolFact[] CoreSymbols =
    [
        Type("N", "Scanner", "linked/Scanner.cs"),
        Member("N.Scanner", "Scan", "public void Scan()", "linked/Scanner.cs"),
        Type("N", "Other", "linked/Other.cs"),
        Member("N.Other", "Callee", "public void Callee()", "linked/Other.cs"),
    ];

    private static GeneratedConcept Single(IReadOnlyList<GeneratedConcept> concepts, string id)
        => concepts.Single(c => c.Id.ToString() == id);

    private static void Write(IReadOnlyList<GeneratedConcept> concepts, string path)
    {
        var result = new BundleWriter().Write(path, concepts, WritePolicy.RequireEmpty, System.IO.Path.GetTempPath());

        Assert.Empty(result.Failures);
    }

    private static SymbolFact Type(string container, string name, string path) =>
        new(SymbolKind.Type, "csharp", container, name, $"public class {name}",
            SymbolVisibility.Public, path, 0, 1, 1, 2, null);

    private static SymbolFact Member(string container, string name, string signature, string path) =>
        new(SymbolKind.Member, "csharp", container, name, signature,
            SymbolVisibility.Public, path, 10, 11, 3, 4, null);

    private static readonly LanguageProfile CSharp = new(
        Language: "csharp",
        GrammarName: "c_sharp",
        DeclarationQuery: string.Empty,
        CallQuery: string.Empty,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-containment-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

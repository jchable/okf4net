// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

// `CodeGraph` alone would bind to the sibling namespace OkfProducer.Tests.CodeGraph, not to the type
// (CS0118) -- see the same alias, and the same reason, at the top of ConceptGenerator.cs.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Tests.Generation;

public class ConceptGeneratorTests
{
    [Fact]
    public void Generate_always_includes_one_overview_concept_first()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var overview = Assert.Single(concepts);
        Assert.Equal("overview", overview.Id.ToString());
        Assert.Equal("Repository", overview.Document.Frontmatter.Type);
        Assert.Equal("my-repo", overview.Document.Frontmatter.Title);
        Assert.Contains("repository", overview.Document.Frontmatter.Tags);
    }

    [Fact]
    public void Generate_creates_one_concept_per_package_under_packages_prefix()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "my-lib", "A little library.")],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = concepts.Single(c => c.Id.ToString() == "packages/my-lib");
        Assert.Equal("Package", packageConcept.Document.Frontmatter.Type);
        Assert.Equal("my-lib", packageConcept.Document.Frontmatter.Title);
        Assert.Equal("A little library.", packageConcept.Document.Frontmatter.Description);
        Assert.Contains("npm", packageConcept.Document.Frontmatter.Tags);
        Assert.Equal("package.json", packageConcept.Document.Frontmatter.Resource);

        // No `sources` block: its one entry used to repeat `resource` verbatim, which is the §4.5 rule
        // the `code/` family already applies -- and it cost a second FrontmatterPathMissing warning per
        // concept for a fact already stated one line above.
        Assert.Empty(packageConcept.Document.Frontmatter.Sources);
    }

    [Fact]
    public void Generate_package_without_description_falls_back_to_a_generated_one()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", null)],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = concepts.Single(c => c.Id.ToString() == "packages/foo");
        Assert.Equal("nuget package Foo.", packageConcept.Document.Frontmatter.Description);
    }

    [Fact]
    public void Generate_slugifies_package_names_for_the_concept_id()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "@scope/My Package!", null)],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "packages");
        Assert.Equal("scope-my-package-", packageConcept.Id.Segments[1]);
    }

    [Fact]
    public void Generate_disambiguates_two_packages_that_slugify_to_the_same_segment()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [
                new PackageManifest("npm", "a/package.json", "My Package", null),
                new PackageManifest("nuget", "b/My.Package.csproj", "My Package", null),
            ],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageIds = concepts.Where(c => c.Id.Segments[0] == "packages").Select(c => c.Id.ToString()).ToList();
        Assert.Equal(["packages/my-package", "packages/my-package-2"], packageIds);
    }

    [Fact]
    public void Generate_does_not_collide_a_package_and_a_doc_that_slugify_to_the_same_bare_name()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "Foo", null)],
            [new DocFile("Foo.md", "Foo")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        Assert.Contains(concepts, c => c.Id.ToString() == "packages/foo");
        Assert.Contains(concepts, c => c.Id.ToString() == "docs/foo");
    }

    [Fact]
    public void Generate_creates_one_concept_per_doc_under_docs_prefix()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "My Great Tool")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = concepts.Single(c => c.Id.ToString() == "docs/my-great-tool");
        Assert.Equal("Documentation", docConcept.Document.Frontmatter.Type);
        Assert.Equal("My Great Tool", docConcept.Document.Frontmatter.Title);
        Assert.Contains("documentation", docConcept.Document.Frontmatter.Tags);
        Assert.Equal("README.md", docConcept.Document.Frontmatter.Resource);
        Assert.Empty(docConcept.Document.Frontmatter.Sources);
    }

    [Fact]
    public void Generate_gives_packages_and_docs_a_forge_url_resource_when_a_repo_url_and_a_rev_are_supplied()
    {
        // The defect this pins. `--repo-url` reached the `code/` family only: these two builders never
        // took GenerateOptions at all, so on a real run of this repository WITH `--repo-url` -- where
        // every code concept's `resource` becomes a resolving permalink -- the ten `packages/*` and
        // `docs/*` concepts still emitted a bare repo-relative path the validator resolves against the
        // concept's own directory and misses. Measured before the fix: 20 of the 55 remaining warnings.
        //
        // No line span, unlike a code concept's: the concept is about the whole file, and there is no
        // declaration to point a reader at inside it.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "src/Fixture.csproj", "Fixture", "A package.")],
            [new DocFile("docs/guide.md", "Guide")]);

        var options = GenerateOptions.Default with { RepoUrl = "https://github.com/o/r", Rev = "main" };
        var concepts = new ConceptGenerator().Generate(snapshot, codeGraph: null, options);

        Assert.Equal(
            "https://github.com/o/r/blob/main/src/Fixture.csproj",
            concepts.Single(c => c.Id.ToString() == "packages/fixture").Document.Frontmatter.Resource);
        Assert.Equal(
            "https://github.com/o/r/blob/main/docs/guide.md",
            concepts.Single(c => c.Id.ToString() == "docs/guide").Document.Frontmatter.Resource);
    }

    [Fact]
    public void Generate_escapes_a_packages_resource_url_segment_by_segment_and_builds_it_from_the_parsed_uri()
    {
        // The two properties BlobUrl carries that a bare concatenation would lose, now that a second
        // family reaches it: a space in a path becomes %20 rather than breaking the URL, and a query
        // string on --repo-url is dropped instead of landing mid-path (`.../r?x=1/blob/main/...`),
        // which the validator would still classify as a Url and pass with no warning -- a silently
        // wrong link. Asserted here as well as on the code family precisely because the shared helper
        // is what makes both true, and one family's test alone would not notice it being bypassed.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "src/my dir/Fixture.csproj", "Fixture", "A package.")],
            []);

        var options = GenerateOptions.Default with { RepoUrl = "https://github.com/o/r?x=1", Rev = "feature/a b" };
        var concepts = new ConceptGenerator().Generate(snapshot, codeGraph: null, options);

        Assert.Equal(
            "https://github.com/o/r/blob/feature/a%20b/src/my%20dir/Fixture.csproj",
            concepts.Single(c => c.Id.ToString() == "packages/fixture").Document.Frontmatter.Resource);
    }

    [Fact]
    public void Generate_falls_back_to_a_generic_slug_when_a_package_name_is_entirely_non_ascii()
    {
        // "概要" normalizes to nothing under ConceptId.Slugify (every character maps to '-', which then
        // collapses and strips away) -- Generate must not throw (Finding 2), and must still produce a
        // valid, unique concept id.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "概要", null)],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "packages");
        Assert.Equal("packages/package", packageConcept.Id.ToString());
    }

    [Fact]
    public void Generate_falls_back_to_a_generic_slug_when_a_doc_title_is_entirely_non_ascii()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "概要")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/doc", docConcept.Id.ToString());
    }

    [Fact]
    public void Generate_disambiguates_two_packages_that_are_both_entirely_non_ascii()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [
                new PackageManifest("npm", "a/package.json", "概要", null),
                new PackageManifest("nuget", "b/Pkg.csproj", "Привет", null),
            ],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageIds = concepts.Where(c => c.Id.Segments[0] == "packages").Select(c => c.Id.ToString()).ToList();
        Assert.Equal(["packages/package", "packages/package-2"], packageIds);
    }

    [Fact]
    public void Generate_disambiguates_a_doc_titled_Index_instead_of_producing_a_reserved_id()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("INDEX.md", "Index")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/index-2", docConcept.Id.ToString());
    }

    [Fact]
    public void Generate_strips_a_trailing_dot_md_from_a_doc_slug_to_avoid_a_double_extension()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "README.md")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/readme", docConcept.Id.ToString());
    }

    [Fact]
    public void Generate_does_not_strip_a_trailing_dot_md_from_a_package_slug()
    {
        // Finding 2 (final review): the ".md"-strip only makes sense for docs, whose id is derived
        // from a human-facing title. A package literally named "Foo.Md" (e.g. a dotted assembly-style
        // NuGet PackageId) must keep the ".md" in its slug -- stripping it would silently collide it
        // with an unrelated sibling package named "Foo" (invisible in the resulting id).
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [
                new PackageManifest("nuget", "a/Foo.Md.csproj", "Foo.Md", null),
                new PackageManifest("nuget", "b/Foo.csproj", "Foo", null),
            ],
            [new DocFile("Foo.md", "Foo.md")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageIds = concepts.Where(c => c.Id.Segments[0] == "packages").Select(c => c.Id.ToString()).ToList();
        Assert.Equal(["packages/foo.md", "packages/foo"], packageIds);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/foo", docConcept.Id.ToString());
    }

    [Fact]
    public void A_nuget_description_never_manufactures_a_bundle_link()
    {
        // A `.csproj` <Description> is written by a human -- for NuGet, not for this bundle. Nobody
        // writing one means a link relative to a bundle that did not exist yet, so from the bundle's
        // point of view it is lifted text exactly as a doc comment is. `[docs](guide)` in one is
        // ordinary, and rendered verbatim it is a broken link the author never wrote.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", "See [docs](guide) to get started.")],
            []);

        var concept = new ConceptGenerator().Generate(snapshot).Single(c => c.Id.ToString() == "packages/foo");

        Assert.Equal("See [docs](guide) to get started.", concept.Document.Frontmatter.Description);
        Assert.Empty(LinkScanner.ExtractLinks(concept.Document.Body));
    }

    [Fact]
    public void A_readme_heading_that_is_itself_a_link_never_becomes_a_bundle_link()
    {
        // `# [Guide](docs/guide.md)` is an ordinary way to open a README, and the doc title is lifted
        // straight out of that heading -- the one place a relative link reaches a body through a TITLE
        // rather than through a description.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "[Guide](docs/guide.md)")]);

        var concept = new ConceptGenerator().Generate(snapshot).Single(c => c.Id.Segments[0] == "docs");

        Assert.Empty(LinkScanner.ExtractLinks(concept.Document.Body));
    }

    [Fact]
    public void A_documentation_path_containing_a_backtick_cannot_close_its_own_code_span()
    {
        // A backtick is legal in a filename on both platforms. A hand-written pair of backticks lets the
        // path close the span early, after which the rest of it is prose again -- and a lifted string
        // has manufactured a real link. CodeSpan exists precisely to fence content that contains
        // backticks, so the doc family uses it rather than a second, naive copy.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("odd`[x](y).md", "Odd")]);

        var concept = new ConceptGenerator().Generate(snapshot).Single(c => c.Id.Segments[0] == "docs");

        Assert.Empty(LinkScanner.ExtractLinks(concept.Document.Body));
    }

    [Fact]
    public void A_documentation_title_containing_a_backtick_does_not_blank_its_own_containment_link()
    {
        // The other half of the backtick door, through the LABEL rather than the path. `LinkScanner`'s
        // `BlankInlineCode` toggles on EVERY backtick and blanks to the end of the line, so a `## Contains`
        // bullet whose label carries an ODD number of backticks has its own `](/id)` blanked before the
        // link scanner ever sees it: the link vanishes, the target file still exists, and nothing dangles,
        // so `okf validate` is silent while the branch is severed. A doc title is lifted verbatim from the
        // README's `# ` heading, where an unbalanced backtick (`# Migrating to \`v2`) is an ordinary typo.
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("guide.md", "Don`t Panic")]);

        var concepts = new ConceptGenerator().Generate(snapshot);
        var docId = concepts.Single(c => c.Id.Segments[0] == "docs").Id.ToString();
        var overviewBody = concepts[0].Document.Body;

        Assert.Contains(LinkScanner.ExtractLinks(overviewBody), link => link.Target == "/" + docId);

        // And the character still reaches the reader: `&#96;` renders as a backtick, which a backslash
        // escape would too -- but `BlankInlineCode` has no backslash awareness, so only a form carrying
        // no backtick CHARACTER keeps the link.
        Assert.Contains("Don&#96;t Panic", overviewBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manual_description_on_a_package_survives_regeneration()
    {
        // §4.2 was wired into the `code/` family alone: `packages/*` re-derived its description
        // unconditionally and the writer rewrote the file wholesale, so a hand-written description on the
        // concept a human is MOST likely to edit -- the derived text is only the `.csproj` <Description> --
        // died on the next `generate --update`, and `description_source: manual` did not help because the
        // key was never read for this family.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", "From the csproj.")],
            []);

        var concept = Generate(snapshot, "packages/foo", Preserved("Hand written."));

        Assert.Equal("Hand written.", concept.Document.Frontmatter.Description);
        Assert.Contains("Hand written.", concept.Document.Body, StringComparison.Ordinal);

        // The label is written back out, or the next run would find no `description_source` and re-derive
        // over the text it just preserved -- preservation that lasts exactly one run is not preservation.
        Assert.Equal("manual", concept.Document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());
    }

    [Fact]
    public void A_manual_description_on_a_documentation_concept_survives_regeneration()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "Readme")]);

        var concept = Generate(snapshot, "docs/readme", Preserved("Hand written."));

        Assert.Equal("Hand written.", concept.Document.Frontmatter.Description);
        Assert.Equal("manual", concept.Document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());
    }

    [Fact]
    public void A_manual_package_description_keeps_the_link_its_author_wrote()
    {
        // "On the same terms as `code/*`": a link in a description the producer DERIVED is doc syntax
        // nobody meant for this bundle and is neutralized, while a link in a description a human wrote in
        // the bundle is a link they meant. Keyed on `description_source`, exactly as `BodyDescription` is.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", "From the csproj.")],
            []);

        var body = Generate(snapshot, "packages/foo", Preserved("See [the overview](/overview).")).Document.Body;

        Assert.Contains(LinkScanner.ExtractLinks(body), link => link.Target == "/overview");
    }

    [Fact]
    public void A_manual_description_on_the_overview_survives_regeneration()
    {
        // One concept beyond the ruling's `packages/*` and `docs/*`, and for the same reason: the derived
        // text is a head-count of detected packages, so `overview` is a concept a human plainly may
        // rewrite -- and it was re-derived and overwritten wholesale exactly as the other two were.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", null)],
            []);

        var concept = Generate(snapshot, "overview", Preserved("What this repository is for."));

        Assert.Equal("What this repository is for.", concept.Document.Frontmatter.Description);
        Assert.Equal("manual", concept.Document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());

        // And the one level of containment below it is still there: `overview` is the way in.
        Assert.Contains(LinkScanner.ExtractLinks(concept.Document.Body), link => link.Target == "/packages/foo");
    }

    [Theory]
    [InlineData("repository-metadata")]
    [InlineData("Repository-Metadata")]
    public void A_description_source_spelt_like_the_derivation_sentinel_still_survives_the_NEXT_run(string label)
    {
        // `repository-metadata` is LiftedMetadataSource's label -- this producer's private sentinel for
        // "this text was derived here, write no key". It was told apart from a preserved label by
        // comparing the RESOLVER's answer to it, so a human who happens to type that exact string was
        // read as a derivation: their description was kept, its key was dropped, and the next run found
        // no `description_source` and re-derived over it. Preservation that lasts exactly one run, with
        // nothing said anywhere. The second row is the same defect from the other side -- an ordinal
        // comparison sent it down the OTHER branch, contradicting the deliberately case-insensitive check
        // in DescriptionResolver that decided the text was preserved in the first place.
        //
        // So this asserts the second run, not the key: the key is only the mechanism, and asserting the
        // mechanism would let a fix that writes the key but re-derives anyway pass.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", "From the csproj.")],
            []);

        var first = Generate(snapshot, "packages/foo", Preserved("Hand written.", label));
        var second = Generate(snapshot, "packages/foo", first.Document.Frontmatter);

        Assert.Equal("Hand written.", first.Document.Frontmatter.Description);
        Assert.Equal("Hand written.", second.Document.Frontmatter.Description);
        Assert.Equal(label, second.Document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());
    }

    [Fact]
    public void A_derived_package_or_doc_description_carries_no_description_source_key()
    {
        // The asymmetry preservation rests on, pinned so it cannot drift: these families write NO
        // `description_source` when the text is derived, which is what keeps "absent => derive normally"
        // meaning "refresh from the manifest" here. A label of their own would be read back on the next
        // run as a value DescriptionResolver does not recognise as derived -- and preserved for ever,
        // freezing a description that is supposed to track the `.csproj`.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", "From the csproj.")],
            [new DocFile("README.md", "Readme")]);

        foreach (var concept in new ConceptGenerator().Generate(snapshot))
        {
            Assert.Null(concept.Document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey));
        }
    }

    [Fact]
    public void Only_the_code_family_carries_a_generated_block()
    {
        // ProducerActor's own summary claimed it was written into "every generated concept's
        // `generated.by`"; `packages/*` and `docs/*` carry no `generated` block at all.
        //
        // The run carries a code graph on purpose. Without one there is no `code/*` concept in it at all,
        // so the half of this name that says "the code family" pinned nothing: deleting the `generated`
        // extension from BuildCodeConcept AND from BuildContainerConcept left the assertion green, since
        // every concept present took the `Assert.Null` branch. The graph below produces both kinds -- two
        // DECLARED concepts (the type `code/csharp/n/scanner`, its member `.../scan`) and exactly one
        // SYNTHESIZED container above them, `code/csharp/n`.
        //
        // All four ids are asserted present, and `code/csharp/n` is the one that carries its weight
        // twice. It is the only concept `BuildContainerConcept` builds here, so it is the only thing
        // standing between that method's `generated` block and a green test; and container synthesis is
        // conditional -- a change to MinimumContainerDepth or to the `claimed` set stops it -- so without
        // this line the coverage could evaporate while every remaining concept still took the branch it
        // was already taking. Asserting only the declared pair is the same defect this comment describes,
        // one level down.
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", null)],
            [new DocFile("README.md", "Readme")]);

        var concepts = new ConceptGenerator().Generate(snapshot, GraphWithOneMember(), GenerateOptions.Default);
        var ids = concepts.Select(c => c.Id.ToString()).ToList();

        Assert.Contains("code/csharp/n/scanner/scan", ids);
        Assert.Contains("code/csharp/n/scanner", ids);
        Assert.Contains("code/csharp/n", ids);
        Assert.Contains("packages/foo", ids);
        Assert.Contains("docs/readme", ids);

        foreach (var concept in concepts)
        {
            var id = concept.Id.ToString();
            var generated = concept.Document.Frontmatter.Get("generated");
            if (id == "overview" || id.StartsWith("code/", StringComparison.Ordinal))
            {
                Assert.NotNull(generated);
            }
            else
            {
                Assert.Null(generated);
            }
        }
    }

    [Fact]
    public void The_overview_body_does_not_manufacture_a_link_from_a_repository_name()
    {
        var snapshot = new RepositorySnapshot("/repo", "[odd](name)", [], []);

        Assert.Empty(LinkScanner.ExtractLinks(new ConceptGenerator().Generate(snapshot)[0].Document.Body));
    }

    [Fact]
    public void Generate_every_concept_passes_strict_Validate()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "my-lib", "A little library.")],
            [new DocFile("README.md", "My Great Tool")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        foreach (var concept in concepts)
        {
            concept.Document.Validate();
        }
    }

    /// <summary>The one concept <paramref name="id"/> names, generated with <paramref name="existing"/> standing in for its on-disk frontmatter.</summary>
    private static GeneratedConcept Generate(RepositorySnapshot snapshot, string id, Frontmatter existing)
    {
        var options = GenerateOptions.Default with
        {
            ExistingFrontmatter = candidate => candidate.ToString() == id ? existing : null,
        };

        return new ConceptGenerator().Generate(snapshot, codeGraph: null, options).Single(c => c.Id.ToString() == id);
    }

    /// <summary>Frontmatter as a human would leave it behind: a description of their own, marked <c>manual</c> unless <paramref name="source"/> says otherwise.</summary>
    private static Frontmatter Preserved(string description, string source = DescriptionResolver.ManualLabel) =>
        OkfDocumentBuilder.ForType("Package")
            .Description(description)
            .Extension(DescriptionResolver.DescriptionSourceKey, new OKF4net.Yaml.YamlString(source))
            .Body("body\n")
            .Build()
            .Frontmatter;

    /// <summary>
    /// The smallest graph that produces both kinds of <c>code/*</c> concept: a type and its member, so
    /// <c>BuildCodeConcept</c> runs, and the namespace above them -- unclaimed by any declaration -- so
    /// <c>BuildContainerConcept</c> does too. <c>CodeConceptGeneratorTests</c> owns the rich fixture; this
    /// one exists only so the families this file compares are all actually present in the run.
    ///
    /// <para>Its caller passes <see cref="GenerateOptions.Default"/>, which carries no C# profile, and
    /// that is on purpose rather than an omission. A helper supplying one was tried and removed: it was
    /// inert, because <c>ConceptGenerator.ProfileFor</c> synthesizes a fallback profile from the language
    /// name alone and <c>LanguageProfile.SplitContainer</c> is documented as a pure function of
    /// <c>Language</c>, so the ids came out identical either way and the helper's doc claimed a necessity
    /// that did not exist. Taking the fallback path instead makes the ids asserted above a live check on
    /// that documented invariant: if container splitting ever consults another field, the fallback -- whose
    /// every other field is empty -- stops reproducing the real profile's ids, and these assertions go red
    /// where a supplied profile would have hidden it.</para>
    /// </summary>
    private static CodeGraphModel GraphWithOneMember() => new(
        [
            new SymbolFact(SymbolKind.Type, "csharp", "N", "Scanner", "public class Scanner",
                SymbolVisibility.Public, "src/Scanner.cs", 0, 1, 1, 2, null),
            new SymbolFact(SymbolKind.Member, "csharp", "N.Scanner", "Scan", "public void Scan()",
                SymbolVisibility.Public, "src/Scanner.cs", 2, 3, 3, 4, null),
        ],
        [],
        RunStatus.Complete);
}

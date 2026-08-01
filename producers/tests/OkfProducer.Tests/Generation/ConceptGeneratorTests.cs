// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

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
        Assert.Single(packageConcept.Document.Frontmatter.Sources);
        Assert.Equal("package.json", packageConcept.Document.Frontmatter.Sources[0].Resource);
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
        Assert.Single(docConcept.Document.Frontmatter.Sources);
        Assert.Equal("README.md", docConcept.Document.Frontmatter.Sources[0].Resource);
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
}

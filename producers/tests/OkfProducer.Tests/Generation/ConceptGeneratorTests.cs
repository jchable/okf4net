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
        Assert.Equal(ConceptId.Slugify("@scope/My Package!"), packageConcept.Id.Segments[1]);
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
    public void Generate_creates_one_concept_per_doc_under_docs_prefix()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "My Great Tool")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = concepts.Single(c => c.Id.ToString() == "docs/my-great-tool");
        Assert.Equal("Documentation", docConcept.Document.Frontmatter.Type);
        Assert.Equal("My Great Tool", docConcept.Document.Frontmatter.Title);
        Assert.Single(docConcept.Document.Frontmatter.Sources);
        Assert.Equal("README.md", docConcept.Document.Frontmatter.Sources[0].Resource);
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

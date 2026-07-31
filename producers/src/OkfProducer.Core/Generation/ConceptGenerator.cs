// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Maps a <see cref="RepositorySnapshot"/> to concepts via <see cref="OkfDocumentBuilder"/>: one
/// repository overview (fixed id <c>overview</c>), one <c>packages/&lt;slug&gt;</c> concept per
/// detected package, and one <c>docs/&lt;slug&gt;</c> concept per detected doc. A concept id
/// collision (two names slugifying to the same segment) is disambiguated with a numeric suffix
/// (<c>-2</c>, <c>-3</c>, ...) -- <see cref="ConceptId.Slugify"/> itself never deduplicates, that
/// responsibility belongs to its caller (this class).
/// </summary>
public sealed class ConceptGenerator : IConceptGenerator
{
    /// <inheritdoc/>
    public IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot)
    {
        var results = new List<GeneratedConcept>
        {
            new(ConceptId.Parse("overview"), BuildOverview(snapshot)),
        };

        var usedIds = new HashSet<string>(StringComparer.Ordinal) { "overview" };

        foreach (var package in snapshot.Packages)
        {
            var id = UniqueConceptId("packages", package.Name, usedIds);
            results.Add(new GeneratedConcept(id, BuildPackageConcept(package)));
        }

        foreach (var doc in snapshot.Docs)
        {
            var id = UniqueConceptId("docs", doc.Title, usedIds);
            results.Add(new GeneratedConcept(id, BuildDocConcept(doc)));
        }

        return results;
    }

    private static ConceptId UniqueConceptId(string prefix, string name, HashSet<string> usedIds)
    {
        string baseSlug;
        try
        {
            baseSlug = ConceptId.Slugify(name);
        }
        catch (ConceptIdException)
        {
            // `name` normalized to nothing (e.g. entirely non-ASCII, or empty) -- fall back to a
            // generic slug derived from the prefix; the collision loop below still disambiguates
            // multiple equally-unnameable entries under the same prefix with a numeric suffix.
            baseSlug = prefix switch
            {
                "packages" => "package",
                "docs" => "doc",
                _ => prefix,
            };
        }

        var candidate = $"{prefix}/{baseSlug}";
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = $"{prefix}/{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse(candidate);
    }

    private static OkfDocument BuildOverview(RepositorySnapshot snapshot)
    {
        var description = snapshot.Packages.Count switch
        {
            0 => $"Repository {snapshot.RepoName}.",
            1 => $"Repository {snapshot.RepoName}, containing 1 detected package.",
            var n => $"Repository {snapshot.RepoName}, containing {n} detected packages.",
        };

        return OkfDocumentBuilder
            .ForType("Repository")
            .Title(snapshot.RepoName)
            .Description(description)
            .Tags("repository")
            .Body($"# {snapshot.RepoName}\n\n{description}\n")
            .Build();
    }

    private static OkfDocument BuildPackageConcept(PackageManifest package)
    {
        var description = package.Description ?? $"{package.Ecosystem} package {package.Name}.";

        return OkfDocumentBuilder
            .ForType("Package")
            .Title(package.Name)
            .Description(description)
            .Tags(package.Ecosystem)
            .Resource(package.RelativePath)
            .AddSource(resource: package.RelativePath)
            .Body($"# {package.Name}\n\n{description}\n")
            .Build();
    }

    private static OkfDocument BuildDocConcept(DocFile doc)
    {
        return OkfDocumentBuilder
            .ForType("Documentation")
            .Title(doc.Title)
            .Description($"Repository documentation file {doc.RelativePath}.")
            .Tags("documentation")
            .Resource(doc.RelativePath)
            .AddSource(resource: doc.RelativePath)
            .Body($"# {doc.Title}\n\nSee `{doc.RelativePath}` in the repository.\n")
            .Build();
    }
}

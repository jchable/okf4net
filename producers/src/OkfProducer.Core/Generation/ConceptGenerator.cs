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

        // A concept id segment ending in ".md" would double up when BundleConceptWriter appends its
        // own ".md" extension to serialize the file (e.g. a doc literally titled "README.md" would
        // otherwise become "docs/readme.md.md"). Scoped to docs only: for a doc, the id is derived
        // straight from a human-facing title, so trimming a redundant ".md" is a harmless, expected
        // normalization. For a package, the id is derived from an ecosystem identifier (e.g. a NuGet
        // PackageId such as "Foo.Md") where ".md" can be a meaningful, distinguishing part of the
        // name -- silently stripping it would make the strip invisible in the id and could collide an
        // unrelated sibling package named "Foo" into "packages/foo-2". A package whose id ends in
        // ".md" still risks the same double-extension filename on write, but that's the lesser, more
        // honest failure mode: the id itself stays a faithful, non-colliding representation of the
        // package name.
        if (prefix == "docs" && baseSlug.Length > ".md".Length
            && baseSlug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            baseSlug = baseSlug[..^".md".Length];
        }

        // "index"/"log" are reserved concept ids (BundleConceptWriter.WriteConcept rejects them --
        // they'd collide with the bundle's own index.md/log.md). Treat a name that slugifies to one of
        // these the same as an ordinary collision: fall through to the numeric-suffix loop below
        // instead of producing an id that write time would reject.
        var segment = baseSlug;
        var suffix = 2;
        while (IsReservedSegment(segment) || !usedIds.Add($"{prefix}/{segment}"))
        {
            segment = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse($"{prefix}/{segment}");
    }

    /// <summary>
    /// True when <paramref name="segment"/> would collide with a concept id
    /// <see cref="OKF4net.BundleConceptWriter"/> reserves for the bundle's own
    /// <c>index.md</c>/<c>log.md</c>. Internal (not private) so <see cref="ConceptIdRegistry"/> --
    /// the single registry spanning all four id families -- can reuse this rule instead of forking a
    /// second copy of it.
    /// </summary>
    internal static bool IsReservedSegment(string segment) =>
        string.Equals(segment, "index", StringComparison.OrdinalIgnoreCase)
        || string.Equals(segment, "log", StringComparison.OrdinalIgnoreCase);

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

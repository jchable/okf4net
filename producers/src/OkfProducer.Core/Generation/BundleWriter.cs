// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <inheritdoc cref="IBundleWriter"/>
public sealed class BundleWriter : IBundleWriter
{
    /// <inheritdoc/>
    public WriteResult Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy, string repoPath)
    {
        if (policy == WritePolicy.Reset && Directory.Exists(outPath))
        {
            var fullOut = Path.GetFullPath(outPath);
            var fullRepo = Path.GetFullPath(repoPath);
            if (IsSameOrAncestor(fullOut, fullRepo))
            {
                throw new InvalidOperationException(
                    $"Refusing to reset '{outPath}': it is the same as, or an ancestor of, the repository being scanned ('{repoPath}'). Choose a different --out.");
            }

            Directory.Delete(outPath, recursive: true);
        }

        if (policy == WritePolicy.RequireEmpty && Directory.Exists(outPath) && Directory.EnumerateFileSystemEntries(outPath).Any())
        {
            throw new InvalidOperationException(
                $"Output directory '{outPath}' is not empty. Use --update or --reset.");
        }

        Directory.CreateDirectory(outPath);

        var writer = new BundleConceptWriter(outPath);
        var failures = new List<(ConceptId, string)>();
        var written = 0;

        foreach (var concept in concepts)
        {
            var result = writer.WriteConcept(concept.Id.ToString(), concept.Document.Frontmatter, concept.Document.Body);
            if (result.StartsWith("Error:", StringComparison.Ordinal))
            {
                failures.Add((concept.Id, result));
            }
            else
            {
                written++;
            }
        }

        IndexGenerator.RegenerateIndexes(outPath);

        return new WriteResult(written, failures);
    }

    /// <summary>
    /// True if <paramref name="ancestorCandidate"/> (already <see cref="Path.GetFullPath(string)"/>-resolved)
    /// equals <paramref name="path"/> (likewise resolved), or is one of its ancestor directories.
    /// Compares path components, not raw string prefixes (so <c>/repo</c> is not mistaken for an
    /// ancestor of <c>/repository</c>).
    /// </summary>
    private static bool IsSameOrAncestor(string ancestorCandidate, string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var ancestor = ancestorCandidate.TrimEnd(separators);
        var target = path.TrimEnd(separators);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return string.Equals(ancestor, target, comparison)
            || target.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }
}

// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <inheritdoc cref="IBundleWriter"/>
public sealed class BundleWriter : IBundleWriter
{
    /// <inheritdoc/>
    public WriteResult Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy)
    {
        if (policy == WritePolicy.Reset && Directory.Exists(outPath))
        {
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
}

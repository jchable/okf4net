// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Hostile-input guards a single extraction run must respect. Task 1 only carries these values
/// through; Task 4 is what makes an <c>ILanguageExtractor</c> actually enforce them.
/// </summary>
public sealed record ExtractionLimits(long MaxFileBytes, int MaxDepth, TimeSpan Timeout)
{
    /// <summary>2 MB per file, a directory depth of 512, and a 10-minute overall timeout.</summary>
    public static ExtractionLimits Default { get; } = new(2 * 1024 * 1024, 512, TimeSpan.FromMinutes(10));
}

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// How extraction went for one file. Only <see cref="Extracted"/> is produced by Task 1's
/// <c>CodeGraphBuilder</c> itself; the rest are hostile-input outcomes a real
/// <c>ILanguageExtractor</c> reports once Task 4 adds the policy that produces them.
/// </summary>
public enum FileStatus
{
    /// <summary>The file was read and parsed in full.</summary>
    Extracted,

    /// <summary>The file was read and parsed, but extraction stopped early (e.g. a timeout).</summary>
    PartiallyExtracted,

    /// <summary>The file exceeded the configured maximum size and was not read.</summary>
    SkippedTooLarge,

    /// <summary>The file's bytes could not be decoded as text.</summary>
    SkippedEncoding,

    /// <summary>The file's path exceeded the configured maximum directory depth.</summary>
    SkippedDepth,

    /// <summary>The file could not be opened for reading.</summary>
    SkippedUnreadable,

    /// <summary>The path is a symlink and was not followed.</summary>
    SkippedSymlink,
}

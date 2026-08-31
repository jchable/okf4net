// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Whether a whole extraction run succeeded, and what it could not read.
/// <see cref="IsComplete"/> is the gate Task 11's pruning keys off: "absent
/// from this run" has two indistinguishable causes -- the symbol is gone, or
/// the file could not be read -- so only a complete run may delete anything.
/// </summary>
public sealed record RunStatus(bool IsComplete, IReadOnlyList<(string Path, FileStatus Status)> Skipped)
{
    /// <summary>A run in which every eligible file extracted cleanly.</summary>
    public static RunStatus Complete { get; } = new(true, []);
}

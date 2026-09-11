// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The outcome of a <see cref="IBundleWriter.Write"/> call. A per-concept write failure (e.g. a
/// permission error on one specific file) is reported in <see cref="Failures"/>, not thrown --
/// it does not stop the rest of the concepts from being written.
/// </summary>
public sealed record WriteResult(int Written, IReadOnlyList<(ConceptId Id, string Error)> Failures)
{
    /// <summary>
    /// The concepts this run deleted from the bundle, sorted <see cref="StringComparer.Ordinal"/> by
    /// id -- always a subset of the ids the previous run's <see cref="GenerationManifest"/> claimed,
    /// and empty for every run that was not allowed to prune.
    /// </summary>
    public IReadOnlyList<ConceptId> Pruned { get; init; } = [];

    /// <summary>
    /// What the run could not do, or chose not to do, in plain sentences with no severity prefix, so
    /// the caller decides how to render them (the CLI prefixes <c>note: </c> and writes to stderr).
    ///
    /// <para>This is where §6.3's "a partial or degraded run writes its concepts but deletes nothing
    /// <b>and says so</b>" is discharged, and where the report distinguishes "symbol deleted" from
    /// "file not analysed": a run that declined to prune says why, a run that held an id back names
    /// the file that was not read, and a markdown file sitting under the owned prefix that no manifest
    /// claims is reported as not owned rather than silently deleted.</para>
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

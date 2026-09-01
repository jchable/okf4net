// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>Writes generated concepts to an OKF bundle directory and regenerates its index.</summary>
public interface IBundleWriter
{
    /// <summary>
    /// Writes <paramref name="concepts"/> to <paramref name="outPath"/> under <paramref name="policy"/>,
    /// prunes what this run is allowed to prune, then regenerates the bundle's index files.
    ///
    /// <para><b>Transactional (§6.3 rule 1).</b> Every concept is written into a staging directory
    /// beside <paramref name="outPath"/> first; the bundle is only touched once the whole set has been
    /// produced. A run that fails while generating -- including one whose
    /// <paramref name="concepts"/> sequence throws part-way through -- leaves the bundle exactly as it
    /// was. <see cref="WritePolicy.Reset"/> is inside that guarantee and not an exception to it: its
    /// deletion happens at the commit boundary, after the whole set exists, so a reset run that dies
    /// while generating leaves the old bundle rather than an empty directory. What the guarantee does
    /// <b>not</b> cover, for any policy, is a failure during the commit itself -- the staged files are
    /// moved one at a time, so an interruption there leaves a mix of new and old concepts, which the
    /// next run corrects.</para>
    ///
    /// <para><b>Pruning is opt-in and it is the caller who opts in</b>, by supplying both
    /// <paramref name="manifest"/> and <paramref name="status"/> under
    /// <see cref="WritePolicy.Update"/>. Omit either and this method behaves as it always did: it
    /// writes, it preserves, it deletes nothing. That default is deliberate -- a caller that has not
    /// yet been taught what this run covered must not be able to delete on its behalf.</para>
    /// </summary>
    /// <param name="repoPath">
    /// Root of the repository that was scanned to produce <paramref name="concepts"/>. Two uses, both
    /// of them guard rails: it is what a <see cref="WritePolicy.Reset"/> refuses to delete (see below),
    /// and pruning checks a candidate's owning file against it before treating "this run never visited
    /// that file" as "that file is gone".
    /// </param>
    /// <param name="manifest">
    /// What <b>this</b> run produced and analysed (see <see cref="GenerationManifest.ForRun"/>). It is
    /// merged with the manifest the previous run left in <paramref name="outPath"/> and written back
    /// there; the previous one is what bounds the deletions.
    /// </param>
    /// <param name="status">
    /// This run's extraction outcome. <see cref="RunStatus.TraversalComplete"/> is the gate --
    /// <b>not</b> <see cref="RunStatus.IsComplete"/>, which is false on ordinary modern C# because the
    /// vendored grammar mis-parses an empty collection expression, and gating on it would make pruning
    /// dead code. Per file, only <see cref="FileStatus.Extracted"/> counts as an owner whose absence
    /// this run can vouch for.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="policy"/> is <see cref="WritePolicy.RequireEmpty"/> and <paramref name="outPath"/>
    /// already exists and is non-empty; or <paramref name="policy"/> is <see cref="WritePolicy.Reset"/>
    /// and <paramref name="outPath"/>'s resolved absolute path equals, or is an ancestor directory of,
    /// <paramref name="repoPath"/>'s resolved absolute path -- refusing to delete the very repository
    /// being scanned. Nothing is written in either case.
    /// </exception>
    WriteResult Write(
        string outPath,
        IReadOnlyList<GeneratedConcept> concepts,
        WritePolicy policy,
        string repoPath,
        GenerationManifest? manifest = null,
        RunStatus? status = null);
}

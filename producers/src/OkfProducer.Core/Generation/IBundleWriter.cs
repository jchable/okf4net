// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>Writes generated concepts to an OKF bundle directory and regenerates its index.</summary>
public interface IBundleWriter
{
    /// <summary>
    /// Writes <paramref name="concepts"/> to <paramref name="outPath"/> under <paramref name="policy"/>,
    /// then regenerates the bundle's index files.
    /// </summary>
    /// <param name="repoPath">
    /// Root of the repository that was scanned to produce <paramref name="concepts"/> -- used only as
    /// a guard rail against a <see cref="WritePolicy.Reset"/> deleting it (see below); not otherwise
    /// read.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="policy"/> is <see cref="WritePolicy.RequireEmpty"/> and <paramref name="outPath"/>
    /// already exists and is non-empty; or <paramref name="policy"/> is <see cref="WritePolicy.Reset"/>
    /// and <paramref name="outPath"/>'s resolved absolute path equals, or is an ancestor directory of,
    /// <paramref name="repoPath"/>'s resolved absolute path -- refusing to delete the very repository
    /// being scanned. Nothing is written in either case.
    /// </exception>
    WriteResult Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy, string repoPath);
}

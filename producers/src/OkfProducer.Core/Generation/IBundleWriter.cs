// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>Writes generated concepts to an OKF bundle directory and regenerates its index.</summary>
public interface IBundleWriter
{
    /// <summary>
    /// Writes <paramref name="concepts"/> to <paramref name="outPath"/> under <paramref name="policy"/>,
    /// then regenerates the bundle's index files.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="policy"/> is <see cref="WritePolicy.RequireEmpty"/> and <paramref name="outPath"/>
    /// already exists and is non-empty. Nothing is written in this case.
    /// </exception>
    WriteResult Write(string outPath, IReadOnlyList<Generation.GeneratedConcept> concepts, WritePolicy policy);
}

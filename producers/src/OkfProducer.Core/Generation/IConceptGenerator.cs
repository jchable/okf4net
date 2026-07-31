// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.Generation;

/// <summary>Turns a <see cref="RepositorySnapshot"/> into the OKF concepts describing it.</summary>
public interface IConceptGenerator
{
    /// <summary>Generates the concepts for <paramref name="snapshot"/>, each paired with its concept id.</summary>
    IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot);
}

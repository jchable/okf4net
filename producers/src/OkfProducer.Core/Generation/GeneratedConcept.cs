// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>A generated concept, paired with the id it will be written under.</summary>
public sealed record GeneratedConcept(ConceptId Id, OkfDocument Document);

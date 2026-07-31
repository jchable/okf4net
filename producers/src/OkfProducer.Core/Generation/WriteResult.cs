// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The outcome of a <see cref="IBundleWriter.Write"/> call. A per-concept write failure (e.g. a
/// permission error on one specific file) is reported in <see cref="Failures"/>, not thrown --
/// it does not stop the rest of the concepts from being written.
/// </summary>
public sealed record WriteResult(int Written, IReadOnlyList<(ConceptId Id, string Error)> Failures);

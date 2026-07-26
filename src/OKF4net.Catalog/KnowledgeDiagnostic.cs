// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A single non-fatal condition surfaced by a knowledge search as data (see
/// <see cref="KnowledgeDiagnosticCode"/>).
/// </summary>
/// <param name="Code">The specific condition.</param>
/// <param name="SourceId">
/// The <see cref="KnowledgeCatalogSource.Id"/> the diagnostic is about, for a
/// per-source condition (<see cref="KnowledgeDiagnosticCode.SourceUnavailable"/>);
/// <see langword="null"/> for a catalog-wide condition
/// (<see cref="KnowledgeDiagnosticCode.NoEnabledSources"/>,
/// <see cref="KnowledgeDiagnosticCode.NoMatches"/>).
/// </param>
/// <param name="Message">A human-readable description, for logs and diagnostics.</param>
public sealed record KnowledgeDiagnostic(KnowledgeDiagnosticCode Code, string? SourceId, string Message);

// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A single reason a <c>catalog.json</c> manifest was rejected by
/// <see cref="CatalogManifestParser.TryParse"/>.
/// </summary>
/// <param name="Code">The specific reject rule that was violated.</param>
/// <param name="Message">A human-readable description of the violation, for logs and diagnostics.</param>
public sealed record CatalogDiagnostic(CatalogDiagnosticCode Code, string Message);

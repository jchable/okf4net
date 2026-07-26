// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Thrown when a catalog manifest fails validation at a point where there is
/// no last-known-good snapshot to fall back to -- specifically, construction
/// of <see cref="FileKnowledgeCatalog"/> from an invalid initial
/// <c>catalog.json</c> (fail-fast, so a caller never silently gets an empty
/// or partial catalog). Runtime reloads never throw this or anything else:
/// see <see cref="IKnowledgeCatalog.LastReloadDiagnostics"/> for the
/// errors-as-data surface used once a catalog has loaded successfully once.
/// </summary>
public sealed class CatalogException : OkfException
{
    /// <summary>Creates the exception with a descriptive message aggregating the underlying diagnostics.</summary>
    public CatalogException(string message)
        : base(message)
    {
    }
}

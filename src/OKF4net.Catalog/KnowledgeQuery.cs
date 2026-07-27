// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A knowledge-search request against an <see cref="IKnowledgeResolver"/> or
/// <see cref="IKnowledgeSource"/>.
/// </summary>
/// <param name="Text">
/// The search text, forwarded verbatim to the core <c>ConceptSearch.Search</c>
/// scorer (whitespace-separated, case-insensitive substring terms). Required
/// to be non-blank -- <see cref="DefaultKnowledgeResolver.SearchAsync"/>
/// throws <see cref="ArgumentException"/> for a blank <see cref="Text"/>,
/// since that is a caller/programming error rather than a data condition
/// (contrast <see cref="KnowledgeDiagnosticCode.NoMatches"/>, which is a
/// legitimate zero-result outcome for a well-formed query).
/// </param>
/// <param name="Tag">
/// Optional tag filter (<see cref="System.StringComparison.OrdinalIgnoreCase"/>),
/// reusing <c>ConceptSearch</c>'s own tag-filter semantics.
/// </param>
/// <remarks>
/// Deliberately V1-scoped: no user/tenant/path fields. Those are identity and
/// routing concerns the OKF spec (§8) keeps orthogonal to a search query, and
/// adding them here would be premature surface before an actual multi-tenant
/// consumer exists.
/// </remarks>
public sealed record KnowledgeQuery(string Text, string? Tag = null);

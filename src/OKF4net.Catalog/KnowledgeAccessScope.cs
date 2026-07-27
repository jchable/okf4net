// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// An immutable, host-authenticated access scope: opaque tenant/user/session
/// identifiers, each validated via <see cref="OKF4net.ConceptId.ValidateSegment"/>
/// so a scope is a path-safe key by construction. All-null is the degenerate
/// "local" (desktop/CLI) single-scope case. Never derived from a message.
/// </summary>
public sealed class KnowledgeAccessScope
{
    /// <summary>The shared all-null "local" scope.</summary>
    public static KnowledgeAccessScope Local { get; } = new();

    /// <summary>Creates a scope, validating every non-null segment.</summary>
    /// <exception cref="ArgumentException">A non-null segment is not a valid concept-id segment.</exception>
    public KnowledgeAccessScope(string? tenantId = null, string? userId = null, string? sessionId = null)
    {
        TenantId = Validate(tenantId, nameof(tenantId));
        UserId = Validate(userId, nameof(userId));
        SessionId = Validate(sessionId, nameof(sessionId));
    }

    /// <summary>The tenant identifier, or <see langword="null"/>.</summary>
    public string? TenantId { get; }

    /// <summary>The user identifier, or <see langword="null"/>.</summary>
    public string? UserId { get; }

    /// <summary>The session identifier, or <see langword="null"/>.</summary>
    public string? SessionId { get; }

    /// <summary><see langword="true"/> when every segment is <see langword="null"/> (the "local" case).</summary>
    public bool IsLocal => TenantId is null && UserId is null && SessionId is null;

    private static string? Validate(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            OKF4net.ConceptId.ValidateSegment(value);
        }
        catch (OKF4net.ConceptIdException ex)
        {
            throw new ArgumentException($"{paramName} must be a valid concept-id segment: {ex.Message}", paramName, ex);
        }

        return value;
    }
}

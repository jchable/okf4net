// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Maps a <see cref="MemoryTier"/> + <see cref="KnowledgeAccessScope"/> to a
/// readable-prefix, '/'-joined concept-path prefix beneath a memory source's
/// root. The single point that decides scope-key storage form — switching to
/// hashed keys later changes only this function. A null scope segment renders
/// as the <see cref="LocalSentinel"/>, so cross-tenant collision is impossible
/// by construction (user memory nests under tenant) and the all-null "local"
/// scope is a valid path for every tier.
/// </summary>
public static class MemoryPath
{
    /// <summary>The sentinel segment substituted for a null scope segment (desktop/CLI).</summary>
    public const string LocalSentinel = "_local";

    /// <summary>
    /// The '/'-joined concept-path prefix for <paramref name="tier"/> under
    /// <paramref name="scope"/> (e.g. <c>memory-user/acme/alice</c>).
    /// </summary>
    public static string For(MemoryTier tier, KnowledgeAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tenant = scope.TenantId ?? LocalSentinel;
        var user = scope.UserId ?? LocalSentinel;
        var session = scope.SessionId ?? LocalSentinel;

        return tier switch
        {
            MemoryTier.Tenant => $"memory-tenant/{tenant}",
            MemoryTier.User => $"memory-user/{tenant}/{user}",
            MemoryTier.Session => $"memory-session/{session}",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
        };
    }
}

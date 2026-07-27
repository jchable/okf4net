// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace OKF4net.Catalog;

/// <summary>
/// Maps a <see cref="MemoryTier"/> + <see cref="KnowledgeAccessScope"/> to a
/// readable-prefix, '/'-joined concept-path prefix beneath a memory source's
/// root. The single point that decides scope-key storage form — switching to a
/// different key scheme later changes only this function.
/// </summary>
/// <remarks>
/// <para>
/// Each non-null tenant/user/session segment is <b>encoded</b> as
/// <c>{lowercased-raw}-{hash}</c>, where <c>hash</c> is a 64-bit (16 lowercase
/// hex chars) truncation of the SHA-256 of the <b>case-sensitive</b> raw bytes.
/// This is deliberate isolation on a <b>case-insensitive filesystem</b>
/// (Windows/macOS): the raw segment is only validated to be ordinally distinct
/// (<c>[A-Za-z0-9_][A-Za-z0-9_.-]*</c>), so tenant <c>"Acme"</c> and
/// <c>"acme"</c> — different scopes — would otherwise map to the <i>same</i>
/// directory and cross-read/enumerate/delete each other. The readable
/// lowercased prefix is a human-facing <i>hint</i> only; the hash suffix (of
/// the case-sensitive bytes) is the actual discriminator, so two case-variant
/// segments land in case-<i>insensitively</i> distinct directories. The suffix
/// also guarantees the segment never ends in a trailing dot/space (safe as a
/// non-final path component on every OS). The lowercased raw is FS-safe by the
/// same validation.
/// </para>
/// <para>
/// A null scope segment renders as the bare <see cref="LocalSentinel"/>
/// (<c>"_local"</c>, <b>no hash</b>). Because every encoded real segment
/// carries a <c>-{hash}</c> suffix, the bare sentinel is provably distinct from
/// any encoded value (and <see cref="KnowledgeAccessScope"/> additionally
/// rejects <c>"_local"</c> as an explicit segment). User memory nests under
/// tenant, so cross-tenant collision is impossible by construction, and the
/// all-null "local" scope is a valid path for every tier.
/// </para>
/// </remarks>
public static class MemoryPath
{
    /// <summary>The sentinel segment substituted for a null scope segment (desktop/CLI); bare, never encoded.</summary>
    public const string LocalSentinel = "_local";

    /// <summary>
    /// The '/'-joined concept-path prefix for <paramref name="tier"/> under
    /// <paramref name="scope"/> (e.g. <c>memory-user/acme-1a2b…/alice-3c4d…</c>).
    /// Each non-null scope segment is <see cref="Encode(string)">encoded</see>;
    /// a null segment is the bare <see cref="LocalSentinel"/>. The fixed tier
    /// prefixes (<c>memory-tenant</c>/<c>memory-user</c>/<c>memory-session</c>)
    /// are literals.
    /// </summary>
    public static string For(MemoryTier tier, KnowledgeAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Encode BEFORE substituting the sentinel for nulls: a present segment
        // is always encoded (readable + hash); a null segment is the bare
        // sentinel, provably distinct from every encoded value.
        var tenant = scope.TenantId is { } t ? Encode(t) : LocalSentinel;
        var user = scope.UserId is { } u ? Encode(u) : LocalSentinel;
        var session = scope.SessionId is { } s ? Encode(s) : LocalSentinel;

        return tier switch
        {
            MemoryTier.Tenant => $"memory-tenant/{tenant}",
            MemoryTier.User => $"memory-user/{tenant}/{user}",
            MemoryTier.Session => $"memory-session/{session}",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
        };
    }

    /// <summary>
    /// Encodes a validated scope segment as <c>{lowercased-raw}-{hash}</c>,
    /// injective under case-folding: the <c>hash</c> is a 64-bit (16 lowercase
    /// hex chars) truncation of the SHA-256 of the <b>case-sensitive</b> raw
    /// bytes, so two case-variant segments produce case-insensitively distinct
    /// results. See the type remarks for why (case-insensitive-FS isolation).
    /// </summary>
    private static string Encode(string raw)
    {
        var readable = raw.ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        return $"{readable}-{hash}";
    }
}

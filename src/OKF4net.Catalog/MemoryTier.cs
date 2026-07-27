// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The tier a <see cref="SourceRole.Memory"/> catalog source stores memory at.
/// Session and tenant tiers are recognized by the manifest parser this lot;
/// only the user tier's storage is implemented (see <c>FileMemoryStore</c>).
/// </summary>
public enum MemoryTier
{
    /// <summary>Per-session memory (contract only this lot; storage staged).</summary>
    Session,

    /// <summary>Per-user memory (durable; implemented this lot).</summary>
    User,

    /// <summary>Per-tenant memory (contract only this lot; storage staged).</summary>
    Tenant,
}

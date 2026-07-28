// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The tier a <see cref="SourceRole.Memory"/> catalog source stores memory at.
/// All three tiers are backed by durable storage in <c>FileMemoryStore</c>.
/// </summary>
public enum MemoryTier
{
    /// <summary>Per-session memory, nested under tenant and user (see <c>MemoryPath.For</c>).</summary>
    Session,

    /// <summary>Per-user memory, nested under tenant.</summary>
    User,

    /// <summary>Per-tenant memory.</summary>
    Tenant,
}

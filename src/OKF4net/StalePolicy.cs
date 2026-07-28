// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>How a consumer treats a stale concept (§5.5). <see cref="Use"/> is the zero value, so an unset policy admits everything.</summary>
public enum StaleMode
{
    /// <summary>Admit stale concepts (surface a stale flag; never drop). The safe default.</summary>
    Use,

    /// <summary>Admit stale concepts only within <see cref="StalePolicy.GraceDays"/> of <c>stale_after</c>.</summary>
    Tolerate,

    /// <summary>Exclude stale concepts.</summary>
    Strict,
}

/// <summary>A consumer-side policy for stale concepts (§5.5). Lives in the core so both Agents and Catalog share one implementation.</summary>
public readonly record struct StalePolicy(StaleMode Mode, int GraceDays)
{
    /// <summary>Admit every concept, stale or not (default).</summary>
    public static StalePolicy Use => new(StaleMode.Use, 0);

    /// <summary>Exclude stale concepts.</summary>
    public static StalePolicy Strict => new(StaleMode.Strict, 0);

    /// <summary>Admit stale concepts up to <paramref name="graceDays"/> days past <c>stale_after</c>.</summary>
    public static StalePolicy Tolerate(int graceDays) => new(StaleMode.Tolerate, graceDays);

    /// <summary>Whether a concept with lifecycle <paramref name="lc"/> should be surfaced as of <paramref name="today"/>.</summary>
    public bool Admits(Lifecycle lc, DateOnly today) => Mode switch
    {
        StaleMode.Use => true,
        StaleMode.Strict => !lc.IsStale(today),
        StaleMode.Tolerate => lc.StaleAfter is not { } d || today <= d.AddDays(GraceDays),
        _ => true,
    };
}

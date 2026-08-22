// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Supplies the current date, so staleness checks (§5.5) are testable and deterministic.</summary>
public interface IOkfClock
{
    /// <summary>Today's date (UTC for <see cref="SystemClock"/>).</summary>
    DateOnly Today { get; }
}

/// <summary>The real wall-clock, in UTC.</summary>
public sealed class SystemClock : IOkfClock
{
    /// <inheritdoc/>
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);
}

/// <summary>
/// An <see cref="IOkfClock"/> pinned to one date. Every API that takes a
/// clock — <see cref="BundleValidator.Validate"/>, <see cref="ConceptAudit.Run"/> —
/// exists to make staleness (§5.5) reproducible; without a shipped pinned
/// clock every caller wanting that has to write this same four-line type.
/// </summary>
/// <param name="today">The date <see cref="Today"/> returns.</param>
public sealed class FixedClock(DateOnly today) : IOkfClock
{
    /// <inheritdoc/>
    public DateOnly Today { get; } = today;
}

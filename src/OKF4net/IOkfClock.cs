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

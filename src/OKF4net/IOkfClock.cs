// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Supplies the current date and instant, so staleness checks (§5.5) are testable and deterministic.</summary>
public interface IOkfClock
{
    /// <summary>Today's date (UTC for <see cref="SystemClock"/>).</summary>
    DateOnly Today { get; }

    /// <summary>
    /// The current instant, in UTC. §5 makes <c>stale_after</c> an instant, so
    /// staleness is an instant comparison. Defaults to midnight UTC on
    /// <see cref="Today"/> so that an implementer written before this member
    /// existed keeps working unchanged; implementations that know the time of
    /// day should override it.
    /// </summary>
    DateTimeOffset Now => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>The real wall-clock, in UTC.</summary>
public sealed class SystemClock : IOkfClock
{
    /// <inheritdoc/>
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    /// <inheritdoc/>
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>
/// An <see cref="IOkfClock"/> pinned to one instant. Every API that takes a
/// clock — <see cref="BundleValidator.Validate"/>, <see cref="ConceptAudit.Run"/> —
/// exists to make staleness (§5.5) reproducible; without a shipped pinned
/// clock every caller wanting that has to write this same small type.
/// </summary>
public sealed class FixedClock : IOkfClock
{
    /// <summary>Pins the clock to <paramref name="instant"/>.</summary>
    /// <param name="instant">The instant <see cref="Now"/> returns; normalized to UTC.</param>
    public FixedClock(DateTimeOffset instant) => Now = instant.ToUniversalTime();

    /// <summary>Pins the clock to midnight UTC on <paramref name="today"/>.</summary>
    /// <param name="today">The date <see cref="Today"/> returns.</param>
    public FixedClock(DateOnly today)
        : this(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
    {
    }

    /// <inheritdoc/>
    public DateTimeOffset Now { get; }

    /// <inheritdoc/>
    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
}

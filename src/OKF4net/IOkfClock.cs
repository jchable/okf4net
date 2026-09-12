// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Supplies the current date and instant, so staleness checks (§5.5) are testable and deterministic.</summary>
public interface IOkfClock
{
    /// <summary>
    /// The current instant, in UTC. §5 makes <c>stale_after</c> an instant, so
    /// staleness is an instant comparison — <c>Now</c>, not <c>Today</c>, is
    /// the primitive a clock exists to supply. **Breaking (0.x):** before this
    /// member was required, an implementer could supply only <see cref="Today"/>
    /// and silently evaluate every §5.5 comparison at midnight UTC, with no
    /// compile-time hint that its instant resolution was wrong.
    /// </summary>
    DateTimeOffset Now { get; }

    /// <summary>Today's date, derived from <see cref="Now"/> (UTC).</summary>
    DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
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
    /// <param name="today">The date <see cref="IOkfClock.Today"/> returns.</param>
    public FixedClock(DateOnly today)
        : this(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
    {
    }

    /// <inheritdoc/>
    public DateTimeOffset Now { get; }
}

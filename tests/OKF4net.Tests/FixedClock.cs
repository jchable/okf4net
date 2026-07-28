// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>Deterministic clock for tests: returns a fixed date.</summary>
public sealed class FixedClock(DateOnly today) : IOkfClock
{
    public DateOnly Today { get; } = today;
}

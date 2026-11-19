// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// A clock the test moves by hand, timers included: a deadline built on it (<c>new CancellationTokenSource(delay, clock)</c>)
/// fires exactly when <see cref="Advance"/> reaches it, on the thread that advances, so a timing test never sleeps.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now = TimeSpan.TicksPerSecond;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and runs, in due order, every timer that is then due.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by.Ticks;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.Due <= _now).OrderBy(t => t.Due).FirstOrDefault();
                if (due is null)
                {
                    return;
                }

                if (due.Period > TimeSpan.Zero)
                {
                    due.Due += due.Period.Ticks;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Callback(due.State);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                Due = clock._now + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                clock._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

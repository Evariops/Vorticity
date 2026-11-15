// A clock a test moves by hand, whose timers fire as it passes their time: code that paces itself against time, a budget
// of requests a second, a calm before a rate grows back, is checked at the instants that matter without waiting for them.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Tests;

internal sealed class StepClock : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new object();
    private readonly List<StepTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        StepTimer timer = new StepTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock by <paramref name="by"/>, firing each timer whose time it passes, in their order, the clock at their time.</summary>
    internal void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            StepTimer? next = null;
            lock (_gate)
            {
                foreach (StepTimer timer in _timers)
                {
                    if (timer.Due <= target && (next is null || timer.Due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, next.Due);
                if (next.Period > 0)
                {
                    next.Due += next.Period;
                }
                else
                {
                    _timers.Remove(next);
                }
            }

            next.Fire();
        }
    }

    private sealed class StepTimer(StepClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; set; }

        internal long Period { get; private set; }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                Due = clock._ticks + Math.Max(dueTime.Ticks, 0);
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
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

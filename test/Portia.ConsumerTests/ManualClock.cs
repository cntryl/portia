using System.Threading.Channels;

namespace Cntryl.Portia.Consumer;

sealed class ManualClock : TimeProvider
{
    readonly Lock _gate = new();
    readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();
    readonly List<ClockTimer> _timers = [];
    DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    public async Task<TimeSpan> WaitForDelayAsync(CancellationToken ct = default) =>
        await _scheduled.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);

    public void Advance(TimeSpan duration)
    {
        List<ClockTimer> ready;
        lock (_gate)
        {
            _now += duration;
            ready = [.. _timers.Where(timer => timer.Due <= _now)];
            foreach (var timer in ready)
                timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
        }

        foreach (var timer in ready)
            timer.Callback(timer.State);
    }

    sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        bool _disposed;
        public TimerCallback Callback => callback;
        public object? State => state;
        public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                Due = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock._now + dueTime;
                Period = period;
                if (dueTime >= TimeSpan.Zero)
                {
                    _ = clock._scheduled.Writer.TryWrite(dueTime);
                }

                return true;
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                _ = clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

using System.Threading.Channels;

namespace LXST.Tests;

internal sealed class ManualAudioClock : TimeProvider
{
    private sealed class Timer(ManualAudioClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period;
        public bool Disposed;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner._gate) { Disposed = true; Due = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private readonly Channel<TimeSpan> _timerCreations = Channel.CreateUnbounded<TimeSpan>();
    private long _ticks;
    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() { lock (_gate) return DateTimeOffset.FromUnixTimeSeconds(1000).AddTicks(_ticks); }
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        _timerCreations.Writer.TryWrite(dueTime);
        TimerCreated.TrySetResult();
        return timer;
    }

    public async Task WaitForTimerAsync(TimeSpan dueTime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await _timerCreations.Reader.ReadAsync(timeout.Token) != dueTime) { }
    }

    public void Advance(TimeSpan amount)
    {
        List<Timer> ready = [];
        lock (_gate)
        {
            _ticks += amount.Ticks;
            foreach (var timer in _timers)
            {
                if (timer.Due > _ticks) continue;
                ready.Add(timer);
                timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
            }
        }
        foreach (var timer in ready) timer.Fire();
    }
}

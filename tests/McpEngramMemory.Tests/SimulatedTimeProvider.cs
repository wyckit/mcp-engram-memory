namespace McpEngramMemory.Tests;

/// <summary>
/// Advances a worker to its next scheduled deadline, then waits for it to finish
/// and arm its next delay. No wall-clock sleeps or accelerated production intervals.
/// One worker owns each clock; real time is used only to bound a broken test.
/// </summary>
internal sealed class SimulatedTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<SimulatedTimer> _timers = [];
    private readonly SemaphoreSlim _scheduled = new(0);
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public int PendingCount { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SimulatedTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public async Task WaitForScheduledAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (PendingCount == 0) await _scheduled.WaitAsync(deadline.Token);
    }

    public async Task<DateTimeOffset> RunNextCycleAsync()
    {
        await WaitForScheduledAsync();
        SimulatedTimer timer;
        lock (_gate)
        {
            timer = _timers.Where(t => t.Due is not null).MinBy(t => t.Due)!;
            _now = timer.Due!.Value;
            timer.Due = null;
        }
        timer.Invoke();
        // Task.Delay continuations may be asynchronous. Registration of the next
        // delay is the completion handshake, after work and telemetry are committed.
        await WaitForScheduledAsync();
        return GetUtcNow();
    }

    private sealed class SimulatedTimer(SimulatedTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Tests drive one-shot worker delays.");
            lock (clock._gate)
            {
                if (_disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }
            clock._scheduled.Release();
            return true;
        }

        public void Invoke() => callback(state);
        public void Dispose()
        {
            lock (clock._gate) { _disposed = true; Due = null; clock._timers.Remove(this); }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

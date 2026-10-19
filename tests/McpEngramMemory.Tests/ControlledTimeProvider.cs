namespace McpEngramMemory.Tests;

/// <summary>
/// Delivers timer callbacks explicitly, including callbacks queued before disposal.
/// No thread-pool or elapsed-time assumptions are needed to test debounce ownership.
/// </summary>
internal sealed class ControlledTimeProvider : TimeProvider
{
    public List<ControlledTimer> Timers { get; } = new();
    public ControlledTimer Latest => Timers[^1];

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(callback, state, dueTime);
        Timers.Add(timer);
        return timer;
    }

    internal sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public bool Disposed { get; private set; }
        public int RearmCount { get; private set; }
        public TimeSpan DueTime { get; private set; } = dueTime;

        // System.Threading.Timer.Dispose does not retract an already queued callback.
        public void DeliverQueuedCallback() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            DueTime = dueTime;
            RearmCount++;
            return true;
        }

        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

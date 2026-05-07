using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Records per-cycle telemetry for every background maintenance worker.
/// Thread-safe: uses a dedicated lock per worker slot.
/// </summary>
public interface IBackgroundWorkerStatusTracker
{
    /// <summary>
    /// Record a completed maintenance cycle for the named worker.
    /// </summary>
    void RecordCycle(string worker, DateTime utcNow, long durationMs, long entriesProcessed, string? errorMessage);

    /// <summary>
    /// Return a point-in-time snapshot of all worker statuses.
    /// </summary>
    EngramStatusOutput GetSnapshot();
}

/// <summary>
/// Default in-memory implementation of <see cref="IBackgroundWorkerStatusTracker"/>.
/// </summary>
public sealed class BackgroundWorkerStatusTracker : IBackgroundWorkerStatusTracker
{
    private readonly WorkerSlot _decay        = new("decay");
    private readonly WorkerSlot _consolidation = new("consolidation");
    private readonly WorkerSlot _autoLink      = new("auto_link");
    private readonly WorkerSlot _accretion     = new("accretion");

    public void RecordCycle(string worker, DateTime utcNow, long durationMs, long entriesProcessed, string? errorMessage)
    {
        var slot = Resolve(worker);
        lock (slot)
        {
            slot.LastRunUtc = utcNow;
            slot.LastDurationMs = durationMs;
            slot.CyclesCompleted++;
            slot.TotalEntriesProcessed += entriesProcessed;
            slot.LastErrorMessage = errorMessage;
        }
    }

    public EngramStatusOutput GetSnapshot()
    {
        return new EngramStatusOutput(
            Snapshot(_decay),
            Snapshot(_consolidation),
            Snapshot(_autoLink),
            Snapshot(_accretion));
    }

    private static EngramWorkerStatus Snapshot(WorkerSlot slot)
    {
        lock (slot)
        {
            return new EngramWorkerStatus(
                slot.Worker,
                slot.LastRunUtc,
                slot.LastDurationMs,
                slot.CyclesCompleted,
                slot.TotalEntriesProcessed,
                slot.LastErrorMessage);
        }
    }

    private WorkerSlot Resolve(string worker) => worker switch
    {
        "decay"         => _decay,
        "consolidation" => _consolidation,
        "auto_link"     => _autoLink,
        "accretion"     => _accretion,
        _               => throw new ArgumentException($"Unknown worker: '{worker}'", nameof(worker))
    };

    /// <summary>Mutable per-worker state bucket (locked via itself).</summary>
    private sealed class WorkerSlot(string worker)
    {
        public string  Worker               { get; } = worker;
        public DateTime? LastRunUtc         { get; set; }
        public long    LastDurationMs       { get; set; }
        public long    CyclesCompleted      { get; set; }
        public long    TotalEntriesProcessed { get; set; }
        public string? LastErrorMessage     { get; set; }
    }
}

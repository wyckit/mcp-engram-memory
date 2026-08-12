using System.Collections.Concurrent;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>Thread-safe append-only audit store suitable for embedding and deterministic tests.</summary>
public sealed class InMemoryConstitutionAuditStore : IConstitutionAuditStore
{
    private readonly ConcurrentQueue<ConstitutionAuditRecord> _records = new();
    private long _nextSequence;

    public ValueTask<ConstitutionAuditRecord> AppendAsync(
        ConstitutionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        long sequence = Interlocked.Increment(ref _nextSequence);
        var stored = record.WithSequence(sequence);
        _records.Enqueue(stored);
        return ValueTask.FromResult(stored);
    }

    public ValueTask<IReadOnlyList<ConstitutionAuditRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ConstitutionAuditRecord> snapshot = _records
            .OrderBy(record => record.Sequence)
            .ToArray();
        return ValueTask.FromResult(snapshot);
    }
}

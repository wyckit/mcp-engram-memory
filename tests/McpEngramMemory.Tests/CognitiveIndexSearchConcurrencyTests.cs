using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression coverage for the BM25 search race in <see cref="CognitiveIndex.Search(SearchRequest)"/>.
///
/// Before the fix, Search released the per-namespace read lock immediately after taking the vector
/// snapshot, then called hybrid/BM25 search unlocked. BM25Index.Search enumerates the namespace's
/// inverted-index postings (a plain HashSet) and reads its DocTermFreqs/DocLengths dictionaries; a
/// concurrent writer (a foreground remember, or a background decay/consolidation/accretion pass)
/// mutates those same collections under the write lock. Reading and mutating a Dictionary/HashSet at
/// once throws "Collection was modified; enumeration operation may not execute." (or a
/// KeyNotFoundException), surfacing as a failed recall.
///
/// The existing ParallelAgentTests only write to per-agent-isolated namespaces, so none of them
/// exercised write-during-search on the SAME namespace. This test does, and is built so it cannot
/// pass without genuine overlap: readers and the writer run on dedicated OS threads (never the
/// ThreadPool, so no reader can be starved out of the window) released together by a Barrier, and the
/// test asserts exact counts of writes and searches in synchronized batches. If readers never
/// searched, the search-count assertion fails rather than the test silently passing. With the read
/// lock now held across the whole BM25/hybrid path the writer simply waits, so no search throws; if
/// the lock hold is ever narrowed again, this fails intermittently (and did fail deterministically
/// against the unfixed implementation when verified).
/// </summary>
public class CognitiveIndexSearchConcurrencyTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;
    private readonly ConcurrentTestWorkers _workers = new();

    public CognitiveIndexSearchConcurrencyTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"search_concurrency_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _embedding = new HashEmbeddingService();
    }

    private CognitiveEntry MakeEntry(int i, string ns)
    {
        // Every entry shares the query tokens ("concurrency", "search") so the queried posting lists
        // are large and are exactly what the writer mutates — maximizing the pre-fix race window.
        var text = $"concurrency search race regression entry {i} about lock contention and retrieval";
        return new CognitiveEntry($"e{i:D4}", _embedding.Embed(text), ns, text, "notes", lifecycleState: "ltm");
    }

    private SearchRequest HybridQuery(string ns)
        => new()
        {
            Query = _embedding.Embed("concurrency search race retrieval contention"),
            QueryText = "concurrency search race retrieval contention",
            Namespace = ns,
            TenantId = "",
            K = 10,
            Hybrid = true,
            Rerank = true,
        };

    /// <summary>
    /// Hammer hybrid search on a namespace while a writer continuously re-indexes and deletes/re-adds
    /// entries in it. No search may throw; both sides must demonstrably run concurrently; and the index
    /// must remain consistent afterward.
    /// </summary>
    [Fact]
    public void HybridSearch_ConcurrentWithWritesToSameNamespace_NeverThrows()
    {
        const string ns = "race-ns";
        const int seedCount = 120;
        const int readerCount = 3;
        const int batches = 40;
        const int writesPerBatch = 10;
        const int searchesPerBatch = 2;

        for (int i = 0; i < seedCount; i++)
            _index.Upsert(MakeEntry(i, ns));

        long searchCount = 0, writeOps = 0;
        var phase = new Barrier(readerCount + 1);
        try
        {
            // Fixed batches keep both sides in the race. The old writer kept mutating until
            // readers reached their floor, while readers searched until the writer finished.
            // Under contention that loop could inflate work toward its 20,000-round safety cap.
            // Here every batch starts together and the next waits for both sides to progress.
            var actions = new List<Action<CancellationToken>>
            {
                token =>
                {
                    for (int batch = 0; batch < batches; batch++)
                    {
                        phase.SignalAndWait(token);
                        for (int round = 0; round < writesPerBatch; round++)
                        {
                            token.ThrowIfCancellationRequested();
                            int i = (batch * writesPerBatch + round) % seedCount;
                            _index.Upsert(MakeEntry(i, ns));
                            int j = (i + seedCount / 2) % seedCount;
                            _index.Delete($"e{j:D4}", ns, tenantId: "");
                            _index.Upsert(MakeEntry(j, ns));
                            Interlocked.Add(ref writeOps, 3);
                        }
                    }
                }
            };
            for (int reader = 0; reader < readerCount; reader++)
                actions.Add(token =>
                {
                    var query = HybridQuery(ns);
                    for (int batch = 0; batch < batches; batch++)
                    {
                        phase.SignalAndWait(token);
                        for (int search = 0; search < searchesPerBatch; search++)
                        {
                            token.ThrowIfCancellationRequested();
                            Assert.NotNull(_index.Search(query));
                            Interlocked.Increment(ref searchCount);
                        }
                    }
                });
            _workers.Run(TimeSpan.FromSeconds(30), actions.ToArray());
        }
        finally
        {
            if (_workers.IsQuiescent) phase.Dispose();
        }

        // Preserve the original mutation floor (1,200) and exceed its search floor (200),
        // with exact counts so cancellation or a skipped worker cannot produce a false pass.
        Assert.Equal(batches * writesPerBatch * 3, writeOps);
        Assert.Equal(batches * searchesPerBatch * readerCount, searchCount);

        // Index is still consistent and searchable after the churn.
        Assert.Equal(seedCount, _index.CountInNamespace(ns));
        Assert.NotEmpty(_index.Search(HybridQuery(ns)));
    }

    public void Dispose()
    {
        if (!_workers.IsQuiescent) return;
        _index.Dispose();
        _persistence.Dispose();
        try { if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true); }
        catch { /* best-effort tempdir cleanup */ }
    }
}

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
/// test asserts both a floor of completed writes and a floor of completed searches. If readers never
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
        const int minWriteRounds = 400;    // writer completes at least this many churn rounds (×3 ops)
        const int minSearches = 200;       // readers must log at least this many overlapping searches
        const int maxWriteRounds = 20_000; // safety cap: a starved reader fails an assertion, not forever

        for (int i = 0; i < seedCount; i++)
            _index.Upsert(MakeEntry(i, ns));

        Exception? failure = null;
        long searchCount = 0, writeOps = 0;
        void Record(Exception ex) { lock (ns) { failure ??= ex; } }

        // Dedicated OS threads (not the ThreadPool) so no reader can be starved out of the window, and
        // a Barrier so every thread enters its loop at the same instant — the searches and the writes
        // therefore genuinely overlap in wall-clock time rather than running end to end.
        using var start = new Barrier(readerCount + 1);
        using var writerDone = new ManualResetEventSlim(false);

        var writer = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                int round = 0;
                // Keep churning the searched namespace's BM25 postings until a solid batch is done AND
                // the readers have proven they searched concurrently. The cap turns a starved reader
                // into a loud assertion failure rather than an infinite loop.
                while (round < minWriteRounds || Volatile.Read(ref searchCount) < minSearches)
                {
                    if (round >= maxWriteRounds) break;
                    int i = round % seedCount;
                    _index.Upsert(MakeEntry(i, ns));    // re-index existing id → BM25 Remove+Add
                    int j = (round + seedCount / 2) % seedCount;
                    _index.Delete($"e{j:D4}", ns);      // BM25 Remove
                    _index.Upsert(MakeEntry(j, ns));    // BM25 Add back
                    Interlocked.Add(ref writeOps, 3);
                    round++;
                }
            }
            catch (Exception ex) { Record(ex); }
            finally { writerDone.Set(); }
        }) { IsBackground = true, Name = "race-writer" };

        var readers = Enumerable.Range(0, readerCount).Select(r =>
            new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    while (!writerDone.IsSet)
                    {
                        var results = _index.Search(HybridQuery(ns));
                        Assert.NotNull(results);
                        Interlocked.Increment(ref searchCount);
                    }
                }
                catch (Exception ex) { Record(ex); }
            }) { IsBackground = true, Name = $"race-reader-{r}" }
        ).ToArray();

        writer.Start();
        foreach (var t in readers) t.Start();

        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "writer thread did not finish in time");
        foreach (var t in readers)
            Assert.True(t.Join(TimeSpan.FromSeconds(5)), "a reader thread did not finish in time");

        // No search threw while the namespace was mutated underneath it...
        Assert.Null(failure);
        // ...and both sides actually ran, concurrently: the writer completed its churn batch and the
        // readers logged many searches during that same window (they only loop while the writer runs).
        Assert.True(Volatile.Read(ref writeOps) >= minWriteRounds * 3,
            $"writer made too few mutations ({writeOps}) to exercise the race");
        Assert.True(Volatile.Read(ref searchCount) >= minSearches,
            $"readers made too few concurrent searches ({searchCount}) to prove overlap");

        // Index is still consistent and searchable after the churn.
        Assert.Equal(seedCount, _index.CountInNamespace(ns));
        Assert.NotEmpty(_index.Search(HybridQuery(ns)));
    }

    public void Dispose()
    {
        _index.Dispose();
        try { if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true); }
        catch { /* best-effort tempdir cleanup */ }
    }
}

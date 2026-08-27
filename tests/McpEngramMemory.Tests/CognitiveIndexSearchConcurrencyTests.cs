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
/// exercised write-during-search on the SAME namespace. These tests do: many hybrid searches run
/// concurrently with a writer churning the searched namespace. With the read lock now held across the
/// whole BM25/hybrid path, writers wait and no exception is thrown. If the lock hold is ever narrowed
/// again, these tests will start failing intermittently under load.
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
    /// entries in it. No search may throw, and the index must remain consistent afterward.
    /// </summary>
    [Fact]
    public async Task HybridSearch_ConcurrentWithWritesToSameNamespace_NeverThrows()
    {
        const string ns = "race-ns";
        const int seedCount = 120;
        for (int i = 0; i < seedCount; i++)
            _index.Upsert(MakeEntry(i, ns));

        Exception? failure = null;
        void Record(Exception ex) { lock (ns) { failure ??= ex; } }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Writer: churn the BM25 inner state under the write lock — re-index (Remove+Add) and
        // delete-then-re-add rotating entries, all in the searched namespace.
        var writer = Task.Run(() =>
        {
            try
            {
                int round = 0;
                while (!cts.IsCancellationRequested)
                {
                    int i = round % seedCount;
                    _index.Upsert(MakeEntry(i, ns));            // re-index existing id → BM25 Remove+Add
                    int j = (round + seedCount / 2) % seedCount;
                    _index.Delete($"e{j:D4}", ns);              // BM25 Remove
                    _index.Upsert(MakeEntry(j, ns));            // BM25 Add back
                    round++;
                }
            }
            catch (Exception ex) { Record(ex); }
        });

        // Readers: concurrent hybrid searches that reach BM25Index.Search on the churned namespace.
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var results = _index.Search(HybridQuery(ns));
                    Assert.NotNull(results);
                }
            }
            catch (Exception ex) { Record(ex); }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));

        Assert.Null(failure);

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

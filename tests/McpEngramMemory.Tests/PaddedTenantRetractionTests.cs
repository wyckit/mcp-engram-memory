using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// TWO CACHES KEYED BY A TENANT ID THAT NOBODY NORMALIZED, AND ONE THAT NEVER SHRANK.
///
/// THE SPLIT-BRAIN. <c>NamespaceStore.PartitionKey</c> validates its components but deliberately
/// does NOT normalize them, while the objects stored under those keys normalize their own tenant —
/// <c>DecayConfig</c> in its constructor, <c>CognitiveIndex</c> everywhere. So a padded tenant wrote
/// one key and every reader used another. <c>IPrincipalContext</c> is an extension point with no
/// normalization of its own and <c>NamespaceAccess.TenantId</c> is a bare passthrough of it, so a
/// host returning a padded claim value reaches these entry points unnormalized; the shipped stdio
/// host normalizes, which is exactly why nothing caught it.
///
/// The consequence is not a crash. For decay configs an operator's <c>EnableAutoLink: false</c>
/// simply never reached the background sweep that was supposed to obey it — the sweep resolves
/// tenants through <c>GetAllTenants</c>, which returns store tenants and is therefore always the
/// canonical spelling. For the diffusion kernel a forced invalidate cleared a slot nothing was
/// reading while the live basis stayed cached.
///
/// THE LEAK. The kernel's per-partition state has no retraction path at all: nothing tells a DI
/// singleton that a namespace was torn down, and the "doesn't qualify" branch is unreachable for a
/// deleted namespace because nothing ever asks for its basis again. Each retained entry is a whole
/// eigenbasis. These tests state that the dictionary SHRINKS, which is invisible in every result,
/// every graph and every timing until the process is out of memory.
/// </summary>
public sealed class PaddedTenantRetractionTests : IDisposable
{
    private const string Canonical = "acme";
    private const string Padded = "  acme  ";
    private const string Ns = "notes";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    public PaddedTenantRetractionTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"padded_tenant_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 600_000);
        _index = new CognitiveIndex(_persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. DECAY CONFIGS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A config written under a padded tenant is the config the CANONICAL spelling reads.
    ///
    /// This is the whole defect in one assertion: the background auto-link sweep reads through
    /// <c>GetAllTenants</c>, so it always asks with the canonical spelling. A config the tool wrote
    /// under a padded one was invisible to it, and <c>EnableAutoLink: false</c> was therefore
    /// ignored immediately, with no error and no restart required.
    /// </summary>
    [Fact]
    public void ADecayConfigWrittenUnderAPaddedTenant_IsReadByTheCanonicalSpelling()
    {
        var lifecycle = new LifecycleEngine(_index, _persistence);

        lifecycle.SetDecayConfig(Ns, decayRate: 0.25f, reinforcementWeight: null,
            stmThreshold: null, archiveThreshold: null,
            useSpectralDecay: null, subdiffusiveExponent: null, tenantId: Padded);

        var canonical = lifecycle.GetDecayConfig(Ns, Canonical);
        Assert.NotNull(canonical);
        Assert.Equal(0.25f, canonical!.DecayRate);
        Assert.Equal(Canonical, canonical.TenantId);

        // And the padded spelling reads the same row rather than a second one.
        var padded = lifecycle.GetDecayConfig(Ns, Padded);
        Assert.Same(canonical, padded);
    }

    /// <summary>
    /// Writing under both spellings produces ONE row, not two.
    ///
    /// Two rows is the state that survives a restart badly: they compose to the same partition key
    /// on the next boot, <c>NamespaceStore.DecayConfigsByPartition</c> logs a duplicate-key warning,
    /// and one of them is discarded arbitrarily.
    /// </summary>
    [Fact]
    public void SettingADecayConfigUnderBothSpellings_KeepsOneRow()
    {
        var lifecycle = new LifecycleEngine(_index, _persistence);

        lifecycle.SetDecayConfig(Ns, decayRate: 0.25f, reinforcementWeight: null,
            stmThreshold: null, archiveThreshold: null,
            useSpectralDecay: null, subdiffusiveExponent: null, tenantId: Padded);
        lifecycle.SetDecayConfig(Ns, decayRate: 0.5f, reinforcementWeight: null,
            stmThreshold: null, archiveThreshold: null,
            useSpectralDecay: null, subdiffusiveExponent: null, tenantId: Canonical);

        var all = lifecycle.GetAllDecayConfigs();
        Assert.Single(all);
        Assert.Equal(0.5f, all[0].DecayRate);
        Assert.Equal(Canonical, all[0].TenantId);
    }

    /// <summary>
    /// A config written under a padded tenant survives a save/reload round trip and is still found —
    /// through EITHER spelling.
    ///
    /// The reload is where the old behaviour changed its own answer: <c>EnsureConfigsLoaded</c>
    /// re-keys the map from the normalized <c>DecayConfig.TenantId</c>, so after a restart even the
    /// padded spelling that wrote the row could no longer find it, and the next write created a
    /// second one.
    /// </summary>
    [Fact]
    public void ADecayConfigWrittenUnderAPaddedTenant_SurvivesAReload()
    {
        var store = new DecayConfigCapturingStore(_persistence);
        var lifecycle = new LifecycleEngine(_index, store);

        lifecycle.SetDecayConfig(Ns, decayRate: null, reinforcementWeight: null,
            stmThreshold: null, archiveThreshold: null,
            useSpectralDecay: null, subdiffusiveExponent: null, tenantId: Padded);
        store.Commit();

        var reloaded = new LifecycleEngine(_index, store);
        Assert.NotNull(reloaded.GetDecayConfig(Ns, Canonical));
        Assert.NotNull(reloaded.GetDecayConfig(Ns, Padded));
        Assert.Single(reloaded.GetAllDecayConfigs());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. THE DIFFUSION KERNEL
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two spellings of one tenant address ONE cache slot, so a forced invalidate clears the copy
    /// the warmup service and the search path are actually reading.
    /// </summary>
    [Fact]
    public void TheKernel_TreatsAPaddedAndACanonicalTenantAsOnePartition()
    {
        var graph = new KnowledgeGraph(_persistence, _index);
        var kernel = new MemoryDiffusionKernel(_index, graph);
        SeedQualifyingNamespace(graph, Ns);

        Assert.NotNull(kernel.GetBasis(Ns, Padded));
        Assert.Equal(1, kernel.CachedPartitionCount);

        // The canonical spelling finds the SAME entry rather than eigensolving a second copy.
        Assert.NotNull(kernel.GetBasis(Ns, Canonical));
        Assert.Equal(1, kernel.CachedPartitionCount);

        // And an invalidate through the other spelling really clears it.
        kernel.Invalidate(Ns, Padded);
        Assert.Equal(0, kernel.CachedPartitionCount);
    }

    /// <summary>
    /// A cached basis for a namespace that no longer qualifies is RETRACTED, one partition per
    /// <c>GetBasis</c> call.
    ///
    /// The rotation is the same shape as <c>AutoLinkScanner.ReconcileOneResumeCursor</c> and for the
    /// same two reasons: nothing tells this singleton that a namespace was torn down, and anything
    /// done here that is linear in the cache size is quadratic across a warmup cycle that calls
    /// <c>GetBasis</c> once per namespace. So the retraction is bounded work, and the property is
    /// that the dictionary shrinks rather than that it shrinks immediately.
    /// </summary>
    [Fact]
    public void TheKernel_RetractsTheCachedBasisOfANamespaceThatNoLongerQualifies()
    {
        var graph = new KnowledgeGraph(_persistence, _index);
        var kernel = new MemoryDiffusionKernel(_index, graph);
        SeedQualifyingNamespace(graph, "doomed");

        Assert.NotNull(kernel.GetBasis("doomed", Canonical));
        Assert.Equal(1, kernel.CachedPartitionCount);

        // The namespace goes away exactly as purge_debates makes it go away: the entries are deleted
        // and nothing notifies the kernel.
        Assert.True(_index.DeleteAllInNamespace("doomed", Canonical) > 0);
        Assert.Equal(1, kernel.CachedPartitionCount);

        // A warmup cycle asking about some OTHER namespace steps the rotation. That namespace is too
        // small to qualify, so it caches nothing of its own and the count can only fall.
        _index.Upsert(new CognitiveEntry("solo", [1f, 0f], "tiny", "solo", tenantId: Canonical));
        for (int i = 0; i < 4 && kernel.CachedPartitionCount > 0; i++)
            Assert.Null(kernel.GetBasis("tiny", Canonical));

        Assert.Equal(0, kernel.CachedPartitionCount);
    }

    /// <summary>
    /// The control: a namespace that still qualifies keeps its basis across the rotation. A
    /// retraction that dropped everything would satisfy the test above and re-eigensolve the store
    /// on every call.
    /// </summary>
    [Fact]
    public void TheKernel_KeepsTheCachedBasisOfANamespaceThatStillQualifies()
    {
        var graph = new KnowledgeGraph(_persistence, _index);
        var kernel = new MemoryDiffusionKernel(_index, graph);
        SeedQualifyingNamespace(graph, Ns);

        var first = kernel.GetBasis(Ns, Canonical);
        Assert.NotNull(first);

        for (int i = 0; i < 5; i++)
            Assert.Same(first, kernel.GetBasis(Ns, Canonical));

        Assert.Equal(1, kernel.CachedPartitionCount);
    }

    /// <summary>
    /// Enough entries and positive-relation edges for the kernel to build a basis at all — below
    /// <see cref="MemoryDiffusionKernel.MinimumNodesForSpectral"/> or
    /// <see cref="MemoryDiffusionKernel.MinimumEdgesForSpectral"/> it bypasses and caches nothing,
    /// which would make every assertion here vacuous.
    /// </summary>
    private void SeedQualifyingNamespace(KnowledgeGraph graph, string ns)
    {
        const int nodes = MemoryDiffusionKernel.MinimumNodesForSpectral + 4;
        for (int i = 0; i < nodes; i++)
        {
            // Distinct directions so the entries are not degenerate duplicates.
            float angle = i * 0.31f;
            _index.Upsert(new CognitiveEntry(
                $"{ns}-{i}", [MathF.Cos(angle), MathF.Sin(angle)], ns, $"node {i}", tenantId: Canonical));
        }

        var edges = new List<GraphEdge>();
        for (int i = 0; i + 1 < nodes; i++)
            edges.Add(new GraphEdge($"{ns}-{i}", $"{ns}-{i + 1}", "similar_to", 0.9f, null, Canonical));

        Assert.Equal(edges.Count, graph.AddEdges(edges));
    }
}

/// <summary>
/// A provider that owns the decay-config blob so a test can reload a lifecycle engine over what a
/// save would actually have written. The debounce is the test's own <see cref="Commit"/>.
/// </summary>
file sealed class DecayConfigCapturingStore : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private Dictionary<string, DecayConfig> _persisted = new();
    private Func<Dictionary<string, DecayConfig>>? _pending;

    public DecayConfigCapturingStore(IStorageProvider inner) => _inner = inner;

    public void Commit()
    {
        var pending = _pending;
        _pending = null;
        if (pending is not null) _persisted = pending();
    }

    // Re-keyed on load exactly as the shipped providers do — by the config's own normalized tenant,
    // which is what made the padded write unreadable after a restart.
    public Dictionary<string, DecayConfig> LoadDecayConfigs()
        => NamespaceStore.DecayConfigsByPartition(_persisted.Values.ToList(), logger: null);

    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _pending = dataProvider;

    public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
    public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public bool UpsertCollapseRecordSync(CollapseRecord record) => _inner.UpsertCollapseRecordSync(record);
    public bool DeleteCollapseRecordSync(string collapseId) => _inner.DeleteCollapseRecordSync(collapseId);
    public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration) => _inner.UpsertCollapseRecordSync(record, onlyIfGeneration);
    public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration) => _inner.DeleteCollapseRecordSync(collapseId, onlyIfGeneration);
    public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record) => _inner.TryReadCollapseRecord(collapseId, out record);
    public bool TryReadCollapseHistory(out List<CollapseRecord> records) => _inner.TryReadCollapseHistory(out records);
    public bool TryFlush() => _inner.TryFlush();
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();
    public void Dispose() { }
}

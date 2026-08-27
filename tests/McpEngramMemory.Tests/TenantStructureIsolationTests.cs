using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Cross-tenant isolation for the graph, cluster, lifecycle, intelligence, diffusion, maintenance,
/// synthesis, and visualization surfaces. The fixture seeds COLLIDING partitions — the same (ns, id)
/// pairs exist in both the legacy tenant and tenant-a, with different content, edges, and clusters —
/// and every test asserts that a tenant principal operates on, and sees, only its own partition,
/// while the legacy principal is entirely unaffected. This is the behavior that replaced the old
/// fail-closed containment.
/// </summary>
public sealed class TenantStructureIsolationTests : IDisposable
{
    private const string Ns = "shared-structure";
    private const string TenantId = "tenant-a";
    private const string EntryA = "same-a";
    private const string EntryB = "same-b";

    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;
    private readonly StubEmbedding _embedding = new();
    private readonly NamespaceRegistry _registry;

    public TenantStructureIsolationTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"tenant_structures_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
        _registry = new NamespaceRegistry(_index, _embedding);
        SeedCollidingPartitions();
    }

    private NamespaceAccess Tenant() => Access(new PrincipalContext(TenantId, "alice"));
    private NamespaceAccess Legacy() => Access(PrincipalContext.LegacyUnisolated);

    [Fact]
    public void Graph_NeighborsAndLinks_AreIsolatedPerTenant()
    {
        var autoLink = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
        var tenantGraph = new GraphTools(_graph, autoLink, _index, Tenant());
        var legacyGraph = new GraphTools(_graph, autoLink, _index, Legacy());

        // Each tenant resolves EntryA's neighbor through its own edge, to its own entry text.
        var tn = Assert.Single(tenantGraph.GetNeighbors(EntryA).Neighbors);
        Assert.Equal("tenant beta", tn.Entry.Text);
        Assert.Equal("depends_on", tn.Edge.Relation);

        var ln = Assert.Single(legacyGraph.GetNeighbors(EntryA).Neighbors);
        Assert.Equal("legacy beta", ln.Entry.Text);
        Assert.Equal("similar_to", ln.Edge.Relation);

        // A tenant link adds an edge to the tenant partition only; the legacy graph is untouched.
        int legacyEdgesBefore = _graph.GetAllEdges("").Count;
        Assert.StartsWith("Linked", tenantGraph.LinkMemories(EntryB, EntryA, "elaborates"));
        Assert.Equal(legacyEdgesBefore, _graph.GetAllEdges("").Count);
        Assert.Contains(_graph.GetAllEdges(TenantId), e => e.Relation == "elaborates");
    }

    [Fact]
    public void Clusters_AreIsolatedPerTenant()
    {
        var tenantClusters = new ClusterTools(_clusters, _embedding, Tenant());
        var legacyClusters = new ClusterTools(_clusters, _embedding, Legacy());

        // Each tenant sees only its own cluster in the shared namespace.
        var tenantList = Assert.Single(tenantClusters.ListClusters(Ns));
        Assert.Equal("tenant-cluster", tenantList.ClusterId);
        var legacyList = Assert.Single(legacyClusters.ListClusters(Ns));
        Assert.Equal("global-cluster", legacyList.ClusterId);

        // The other tenant's cluster id is not found — same shape as a genuine miss.
        Assert.Equal("Cluster 'global-cluster' not found.", tenantClusters.GetCluster("global-cluster"));

        var tenantCluster = Assert.IsType<GetClusterResult>(tenantClusters.GetCluster("tenant-cluster"));
        Assert.All(tenantCluster.Members, m => Assert.StartsWith("tenant", m.Text));
        var legacyCluster = Assert.IsType<GetClusterResult>(legacyClusters.GetCluster("global-cluster"));
        Assert.All(legacyCluster.Members, m => Assert.StartsWith("legacy", m.Text));
    }

    [Fact]
    public void Lifecycle_PromoteMemory_IsolatesPerTenant()
    {
        var tenantLifecycle = new LifecycleTools(_lifecycle, _embedding, _index, Tenant());

        // Promoting the tenant's EntryA moves only the tenant copy; the legacy copy stays STM.
        Assert.Contains("stm -> ltm", tenantLifecycle.PromoteMemory(EntryA, "ltm"));
        Assert.Equal("ltm", _index.Get(EntryA, Ns, TenantId)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryA, Ns)?.LifecycleState);
    }

    [Fact]
    public void Intelligence_DetectAndMerge_IsolatePerTenant()
    {
        var tenantIntel = Intelligence(Tenant());

        // Duplicate detection scans only the tenant's two entries.
        var dupes = Assert.IsType<DuplicateDetectionResult>(tenantIntel.DetectDuplicates(Ns, threshold: 0.9f));
        Assert.Equal(2, dupes.ScannedCount);

        // Merge archives the tenant's EntryB and transfers its tenant edge; legacy EntryB is untouched.
        Assert.StartsWith("Merged", tenantIntel.MergeMemories(EntryA, EntryB, Ns));
        Assert.Equal("archived", _index.Get(EntryB, Ns, TenantId)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryB, Ns)?.LifecycleState);
        // The legacy A->B edge still exists after a tenant-side merge.
        Assert.Contains(_graph.GetAllEdges(""), e => e.SourceId == EntryA && e.TargetId == EntryB);
    }

    [Fact]
    public void Diffusion_GuardLifted_TenantInvalidateSucceeds()
    {
        var kernel = new MemoryDiffusionKernel(_index, _graph);
        var tenantDiffusion = new MemoryDiffusionTools(kernel, Tenant());
        // The operation no longer fails closed for a tenant; it runs and reports success.
        Assert.Contains("Invalidated", tenantDiffusion.InvalidateDiffusion(Ns));
    }

    [Fact]
    public void Maintenance_RebuildEmbeddings_IsolatesPerTenant()
    {
        var legacyVectorBefore = _index.Get(EntryA, Ns)!.Vector.ToArray();
        var tenantMaintenance = new MaintenanceTools(
            _index, new ReembeddingService(), new MetricsCollector(), Tenant());

        var rebuilt = Assert.IsType<RebuildEmbeddingsResult>(tenantMaintenance.RebuildEmbeddings(Ns));
        Assert.Equal(2, rebuilt.TotalUpdated);

        // Tenant vectors were re-embedded (dim 3); legacy vectors are byte-for-byte unchanged.
        Assert.Equal(3, _index.Get(EntryA, Ns, TenantId)!.Vector.Length);
        Assert.Equal(legacyVectorBefore, _index.Get(EntryA, Ns)!.Vector);
    }

    [Fact]
    public async Task Synthesis_RunsOverTenantPartition()
    {
        var generator = new RecordingTextGenerator();
        var synthesis = new SynthesisEngine(_index, _clusters, generator);
        var tenantSynthesis = new SynthesisTools(synthesis, Tenant());

        var result = Assert.IsType<SynthesisResult>(await tenantSynthesis.SynthesizeMemories(Ns));
        Assert.Equal("synthesized", result.Status);
        Assert.True(generator.AvailabilityCalls > 0);
    }

    [Fact]
    public void Visualization_Snapshot_IsolatesPerTenant()
    {
        var tenantViz = new VisualizationTools(_index, _graph, _clusters, Tenant());
        var legacyViz = new VisualizationTools(_index, _graph, _clusters, Legacy());

        var tenantSnapshot = tenantViz.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Equal(2, tenantSnapshot.Nodes.Count);
        Assert.All(tenantSnapshot.Nodes, n => Assert.StartsWith("tenant", n.Text));
        Assert.Single(tenantSnapshot.Edges);
        Assert.Equal("depends_on", tenantSnapshot.Edges[0].Relation);
        Assert.Single(tenantSnapshot.Clusters);
        Assert.Equal("tenant-cluster", tenantSnapshot.Clusters[0].ClusterId);

        var legacySnapshot = legacyViz.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Equal(2, legacySnapshot.Nodes.Count);
        Assert.All(legacySnapshot.Nodes, n => Assert.StartsWith("legacy", n.Text));
        Assert.Single(legacySnapshot.Edges);
        Assert.Equal("similar_to", legacySnapshot.Edges[0].Relation);
        Assert.Single(legacySnapshot.Clusters);
        Assert.Equal("global-cluster", legacySnapshot.Clusters[0].ClusterId);
    }

    [Fact]
    public async Task TenantAdminPurge_DeletesTenantEntriesWithoutTouchingLegacyGraphOrClusters()
    {
        const string debateNs = "active-debate-stale";
        const string debateA = "debate-a";
        const string debateB = "debate-b";
        var stale = DateTimeOffset.UtcNow.AddDays(-3);

        var legacyA = Entry(debateA, debateNs, "legacy debate a");
        var legacyB = Entry(debateB, debateNs, "legacy debate b");
        var tenantAe = Entry(debateA, debateNs, "tenant debate a", TenantId);
        var tenantBe = Entry(debateB, debateNs, "tenant debate b", TenantId);
        legacyA.CreatedAt = stale;
        legacyB.CreatedAt = stale;
        tenantAe.CreatedAt = stale;
        tenantBe.CreatedAt = stale;
        _index.Upsert(legacyA);
        _index.Upsert(legacyB);
        _index.Upsert(tenantAe);
        _index.Upsert(tenantBe);
        _registry.EnsureOwnership(debateNs, "alice", TenantId);

        // Legacy graph + cluster.
        _graph.AddEdge(new GraphEdge(debateA, debateB, "supports"));
        _clusters.CreateCluster("debate-cluster", debateNs, [debateA, debateB]);
        // Tenant graph + cluster over the SAME ids, so the purge cascade must clean up the tenant's
        // own edges/memberships (not the legacy ones).
        _graph.AddEdge(new GraphEdge(debateA, debateB, "opposes", 1f, null, TenantId));
        _clusters.CreateCluster("tenant-debate-cluster", debateNs, [debateA, debateB], null, TenantId);

        var tenantAdmin = new AdminTools(
            _index, _graph, _clusters, _persistence, _registry,
            new PrincipalContext(TenantId, "alice"));
        var tenantResult = Assert.IsType<PurgeDebatesResult>(
            await tenantAdmin.PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(1, tenantResult.NamespacesAffected);
        Assert.Equal(2, tenantResult.TotalEntriesRemoved);
        Assert.True(tenantResult.TotalEdgesRemoved >= 1);
        Assert.Null(_index.Get(debateA, debateNs, TenantId));
        Assert.NotNull(_index.Get(debateA, debateNs));
        // The tenant's own edge and cluster membership were cascaded away by the tenant purge...
        Assert.DoesNotContain(_graph.GetAllEdges(TenantId), e => e.Relation == "opposes");
        Assert.Empty(_clusters.GetClustersForEntry(debateA, TenantId));
        // ...while the legacy graph/cluster are untouched.
        Assert.Contains(_graph.GetAllEdges(""), e => e.SourceId == debateA && e.TargetId == debateB && e.Relation == "supports");
        Assert.Contains("debate-cluster", _clusters.GetClustersForEntry(debateA));
    }

    // ── Partition-key forgery ──

    /// <summary>
    /// A partition key is composed as <c>tenant + PartitionSeparator + ns</c>, and for the legacy
    /// tenant it is the bare namespace. So a LEGACY caller that names the namespace
    /// <c>"tenant-a" + U+001F + "shared-structure"</c> composes byte-for-byte the same key as
    /// (tenant-a, shared-structure) — no ACL involved, because the legacy default agent has
    /// unrestricted access. That aliases the tenant's BM25/HNSW sub-indexes, its per-partition
    /// lock, and its persisted snapshot; <c>DeleteAllInNamespace</c> reaches
    /// <c>NamespaceStore.RemoveNamespace</c> and clears all three.
    ///
    /// Composition is now validated, so every one of those entry points refuses the forged
    /// component instead of silently addressing another partition.
    /// </summary>
    [Fact]
    public void PartitionKey_SeparatorInNamespace_CannotForgeAnotherTenantsPartition()
    {
        string forged = ForgedNamespace;

        // The alphabet guard is public and rejects the whole control-character class, not just the
        // separator — narrowing it to one character would reopen the hole on the next separator change.
        var direct = Assert.Throws<ArgumentException>(
            () => Tenancy.ValidatePartitionComponent(forged, "ns"));
        Assert.Contains("control characters", direct.Message);
        // The offending value is attacker-controlled, so it must not be echoed back into a log line.
        // Ordinal is required, not stylistic: the default overload compares with the current culture,
        // and under ICU collation a control character is zero-weight, so a culture-sensitive search
        // for U+001F matches at position 0 of *every* string — the assertion would fail against a
        // message that never contained the separator at all.
        Assert.DoesNotContain(Tenancy.PartitionSeparator.ToString(), direct.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ns, direct.Message, StringComparison.Ordinal);

        // Every public partition-keyed path refuses it rather than composing tenant-a's key.
        // DeleteAllInNamespace is the damaging one and is checked before any state is touched.
        Assert.Throws<ArgumentException>(() => _index.DeleteAllInNamespace(forged));
        Assert.Throws<ArgumentException>(() => _index.Get(EntryA, forged));
        Assert.Throws<ArgumentException>(() => _index.Delete(EntryA, forged));
        Assert.Throws<ArgumentException>(() => _index.CountInNamespace(forged));

        // The other half of the key is closed too: a tenant id carrying the separator would let a
        // tenant-scoped caller compose a *namespace* boundary instead.
        Assert.Throws<ArgumentException>(
            () => Tenancy.Normalize(TenantId + Tenancy.PartitionSeparator + Ns));

        // OVER-CORRECTION CONTROL — the guard rejects forged components, not tenancy itself.
        // A legitimate tenant key still composes, still resolves, and tenant-a's partition came
        // through the rejected forgery completely intact.
        Assert.Equal(TenantId, Tenancy.Normalize(TenantId));
        Assert.Equal("tenant alpha", _index.Get(EntryA, Ns, TenantId)?.Text);
        Assert.Equal(2, _index.CountInNamespace(Ns, TenantId));
        // LEGACY MIRROR — a single-tenant deployment naming a clean namespace is untouched.
        Assert.Equal("legacy alpha", _index.Get(EntryA, Ns)?.Text);
        Assert.Equal(2, _index.CountInNamespace(Ns));
    }

    /// <summary>
    /// The stay-bootable guarantee. A store written before partition components were validated can
    /// already hold two decay-config rows that compose to one key — here a tenant-scoped row for
    /// (tenant-a, shared-structure) and a legacy row whose namespace IS the composed key. The
    /// obvious <c>ToDictionary</c> throws on that, turning a historical bad write into a host that
    /// cannot start with manual database repair as the only way out. Loading must instead keep one
    /// row deterministically and log.
    ///
    /// Driven through <see cref="PersistenceManager.LoadDecayConfigs"/> because the shared builder
    /// itself is internal to McpEngramMemory.Core, which does not expose internals to this assembly.
    /// </summary>
    [Fact]
    public void LoadDecayConfigs_CollidingPartitionKeys_DoesNotThrow()
    {
        // Its own directory under the fixture's per-run temp path, so the fixture's Dispose still
        // owns the cleanup and no other test in this class sees the poisoned file.
        string poisonedPath = Path.Combine(_dataPath, "poisoned_decay_store");
        Directory.CreateDirectory(poisonedPath);

        const string cleanNs = "decay-clean";
        var poisoned = new List<DecayConfig>
        {
            // Legacy row FIRST in stored order, so the surviving row is decided by the
            // tenant-scoped-first rule and not merely by file position.
            new(ns: ForgedNamespace, decayRate: 0.99f),
            new(ns: Ns, decayRate: 0.42f, tenantId: TenantId),
            // A well-formed legacy row, to pin that a poisoned pair does not disturb the rest.
            new(ns: cleanNs, decayRate: 0.11f),
        };
        File.WriteAllText(
            Path.Combine(poisonedPath, "_decay_configs.json"),
            JsonSerializer.Serialize(poisoned));

        using var persistence = new PersistenceManager(poisonedPath, debounceMs: 50);

        // The load completes instead of throwing — this is the whole point.
        var loaded = persistence.LoadDecayConfigs();

        // Exactly one row survives the collision, and it is the tenant-scoped one.
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.ContainsKey(ForgedNamespace));
        var survivor = loaded[ForgedNamespace];
        Assert.Equal(TenantId, survivor.TenantId);
        Assert.Equal(Ns, survivor.Ns);
        Assert.Equal(0.42f, survivor.DecayRate);

        // LEGACY MIRROR — a well-formed legacy row still keys on the bare namespace, unchanged.
        Assert.True(loaded.ContainsKey(cleanNs));
        var clean = loaded[cleanNs];
        Assert.Equal(string.Empty, clean.TenantId);
        Assert.Equal(0.11f, clean.DecayRate);
    }

    /// <summary>
    /// A tenant id arrives from a host environment variable or an auth-token claim, so
    /// <c>" tenant-a "</c> and <c>"tenant-a"</c> would otherwise address two different partitions
    /// for every consumer at once. Normalization therefore has to live in the init accessor: a
    /// <c>with</c> expression bypasses the constructor entirely and would reintroduce the raw value.
    /// </summary>
    [Fact]
    public void PrincipalContext_PaddedTenantId_NormalizesAtConstruction()
    {
        var padded = new PrincipalContext($"  {TenantId}\t", "alice");
        Assert.Equal(TenantId, padded.TenantId);

        // The `with` path is the one the constructor cannot cover.
        var copied = padded with { TenantId = $"\n{TenantId}  " };
        Assert.Equal(TenantId, copied.TenantId);

        // Observable consequence: the padded principal lands in tenant-a's partition, not the
        // legacy one, so it sees tenant-a's cluster and only that.
        var paddedClusters = new ClusterTools(_clusters, _embedding, Access(padded));
        Assert.Equal("tenant-cluster", Assert.Single(paddedClusters.ListClusters(Ns)).ClusterId);
        Assert.Equal(TenantId, Access(copied).TenantId);

        // Case is PRESERVED by decision — folding here would silently merge two distinct tenants.
        Assert.Equal("Tenant-A", new PrincipalContext(" Tenant-A ", "alice").TenantId);
        Assert.NotEqual(padded.TenantId, new PrincipalContext(" TENANT-A ", "alice").TenantId);

        // Refused rather than silently truncated or allowed to forge a partition key.
        Assert.Throws<ArgumentException>(
            () => new PrincipalContext(new string('t', Tenancy.MaxTenantIdLength + 1), "alice"));
        Assert.Throws<ArgumentException>(
            () => padded with { TenantId = TenantId + Tenancy.PartitionSeparator });

        // OVER-CORRECTION CONTROL / LEGACY MIRROR — a max-length id is accepted, and null,
        // empty and whitespace all still collapse to the legacy partition.
        Assert.Equal(
            new string('t', Tenancy.MaxTenantIdLength),
            new PrincipalContext($" {new string('t', Tenancy.MaxTenantIdLength)} ", "alice").TenantId);
        Assert.Equal(string.Empty, new PrincipalContext(null!, "alice").TenantId);
        Assert.Equal(string.Empty, new PrincipalContext("   ", "alice").TenantId);
        Assert.True(PrincipalContext.LegacyUnisolated.IsLegacyUnisolated);
        Assert.Equal(string.Empty, PrincipalContext.LegacyUnisolated.TenantId);
    }

    /// <summary>
    /// THE EXPLOIT. Synthesis chunks entries along cluster boundaries and puts the cluster's LABEL
    /// into the map and reduce prompts. The entries were already gathered from the tenant partition,
    /// but the cluster lookup used to fall back to the legacy ("") partition — a real, populated
    /// dataset, not a sentinel — so another partition's cluster labels were written straight into
    /// this tenant's prompts and on to the model.
    ///
    /// The assertion is on PROMPT TEXT, never on status: the status is "synthesized" either way,
    /// which is precisely why <see cref="Synthesis_RunsOverTenantPartition"/> could not see this.
    /// </summary>
    [Fact]
    public async Task Synthesis_ClusterLabels_AreTenantScoped()
    {
        var generator = new RecordingTextGenerator();
        var synthesis = new SynthesisEngine(_index, _clusters, generator);
        var tenantSynthesis = new SynthesisTools(synthesis, Tenant());

        var result = Assert.IsType<SynthesisResult>(await tenantSynthesis.SynthesizeMemories(Ns));
        Assert.Equal("synthesized", result.Status);

        var prompts = generator.Prompts;
        Assert.NotEmpty(prompts);
        // The tenant's own cluster label reached the prompts...
        Assert.Contains(prompts, p => p.Contains("tenant cluster", StringComparison.Ordinal));
        // ...and the colliding legacy cluster's label reached none of them.
        Assert.All(prompts, p => Assert.DoesNotContain("legacy cluster", p));
        Assert.All(prompts, p => Assert.DoesNotContain("global-cluster", p));
        // Entry text was already tenant-scoped; pin it so a regression there cannot hide here.
        Assert.All(prompts, p => Assert.DoesNotContain("legacy alpha", p));
        Assert.All(prompts, p => Assert.DoesNotContain("legacy beta", p));
    }

    /// <summary>
    /// LEGACY MIRROR — the fix scopes the cluster lookup to the caller's partition; for a
    /// single-tenant deployment that partition is still the legacy one, so its prompts are
    /// byte-for-byte what they always were.
    /// </summary>
    [Fact]
    public async Task Synthesis_LegacyPrincipal_StillUsesLegacyClusterLabel()
    {
        var generator = new RecordingTextGenerator();
        var synthesis = new SynthesisEngine(_index, _clusters, generator);
        var legacySynthesis = new SynthesisTools(synthesis, Legacy());

        var result = Assert.IsType<SynthesisResult>(await legacySynthesis.SynthesizeMemories(Ns));
        Assert.Equal("synthesized", result.Status);

        var prompts = generator.Prompts;
        Assert.NotEmpty(prompts);
        Assert.Contains(prompts, p => p.Contains("legacy cluster", StringComparison.Ordinal));
        Assert.All(prompts, p => Assert.DoesNotContain("tenant cluster", p));
        Assert.All(prompts, p => Assert.DoesNotContain("tenant alpha", p));
        Assert.All(prompts, p => Assert.DoesNotContain("tenant beta", p));
    }

    /// <summary>
    /// The namespace that composes to tenant-a's partition key when it is named by a LEGACY caller,
    /// for whom the composed key is the bare namespace.
    /// </summary>
    private static string ForgedNamespace
        => string.Concat(TenantId, Tenancy.PartitionSeparator.ToString(), Ns);

    private NamespaceAccess Access(IPrincipalContext principal)
        => new(_registry, principal);

    private IntelligenceTools Intelligence(NamespaceAccess access)
        => new(_index, _graph, _embedding, _scanner, _clusters, _lifecycle, access);

    private void SeedCollidingPartitions()
    {
        // Same (ns, id) pairs in BOTH partitions, with distinct content.
        _index.Upsert(Entry(EntryA, Ns, "legacy alpha"));
        _index.Upsert(Entry(EntryB, Ns, "legacy beta"));
        _index.Upsert(Entry(EntryA, Ns, "tenant alpha", TenantId));
        _index.Upsert(Entry(EntryB, Ns, "tenant beta", TenantId));

        // Legacy graph + cluster.
        _graph.AddEdge(new GraphEdge(EntryA, EntryB, "similar_to"));
        _clusters.CreateCluster("global-cluster", Ns, [EntryA, EntryB], "legacy cluster");

        // Tenant-a graph + cluster over the same bare ids — must not collide with legacy.
        _graph.AddEdge(new GraphEdge(EntryA, EntryB, "depends_on", 1f, null, TenantId));
        _clusters.CreateCluster("tenant-cluster", Ns, [EntryA, EntryB], "tenant cluster", TenantId);

        // The identified tenant principal must own the namespace to reach it — an unregistered
        // namespace is closed to identified agents. The legacy default agent needs no ownership.
        _registry.EnsureOwnership(Ns, "alice", TenantId);
    }

    private static CognitiveEntry Entry(string id, string ns, string text, string tenantId = "")
        => new(id, [1f, 0f], ns, text, tenantId: tenantId);

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [1f, 0f];
    }

    private sealed class ReembeddingService : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [0f, 1f, 0f];
    }

    /// <summary>
    /// Captures every prompt handed to the generator. The prompt is the only place a cluster
    /// label ever becomes observable — <see cref="SynthesisResult.Status"/> reads "synthesized"
    /// whether the labels came from this tenant's partition or someone else's, which is exactly
    /// why <see cref="Synthesis_RunsOverTenantPartition"/> passed while the leak was live.
    ///
    /// The list is lock-guarded and reads snapshot under the SAME lock:
    /// <see cref="SynthesisEngine"/> runs two map workers, so <see cref="GenerateAsync"/> is
    /// called concurrently and an unsynchronized <c>List.Add</c> would tear or lose entries.
    /// <see cref="AvailabilityCalls"/> needs no lock only because it is incremented once,
    /// before the pipeline starts.
    /// </summary>
    private sealed class RecordingTextGenerator : ITextGenerator
    {
        // Plain object, not System.Threading.Lock: this fixture also builds for net8.0.
        private readonly object _gate = new();
        private readonly List<string> _prompts = [];

        public int AvailabilityCalls { get; private set; }

        /// <summary>Snapshot of the prompts captured so far, taken under the capture lock.</summary>
        public IReadOnlyList<string> Prompts
        {
            get { lock (_gate) return _prompts.ToArray(); }
        }

        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
        {
            AvailabilityCalls++;
            return Task.FromResult(true);
        }

        public Task<string?> GenerateAsync(
            string model,
            string prompt,
            int maxTokens = 512,
            float temperature = 0.1f,
            CancellationToken ct = default)
        {
            lock (_gate) _prompts.Add(prompt);
            return Task.FromResult<string?>("generated synthesis");
        }

        public void Dispose() { }
    }
}

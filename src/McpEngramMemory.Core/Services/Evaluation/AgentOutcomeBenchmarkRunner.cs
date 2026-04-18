using System.Diagnostics;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Runs task-style benchmark datasets across multiple memory conditions to estimate
/// whether memory improves agent outcomes beyond pure retrieval metrics.
/// This is a proxy harness: it scores evidence coverage, conflict suppression,
/// and task pass rate under different memory policies.
/// </summary>
public sealed class AgentOutcomeBenchmarkRunner
{
    public const string NoMemoryCondition = "no_memory";
    public const string TranscriptReplayCondition = "transcript_replay";
    public const string VectorMemoryCondition = "vector_memory";
    public const string FullEngramCondition = "full_engram";

    // T2 ablation conditions. Each disables one Engram module so benchmark deltas map to a
    // specific cognitive mechanism (graph traversal, lifecycle/deep-recall, hybrid BM25 lexical).
    public const string FullEngramNoGraphCondition = "full_engram_no_graph";
    public const string FullEngramNoLifecycleCondition = "full_engram_no_lifecycle";
    public const string FullEngramNoHybridCondition = "full_engram_no_hybrid";

    private static readonly string[] ComparisonConditions =
        [TranscriptReplayCondition, VectorMemoryCondition, FullEngramCondition];

    private static readonly string[] AblationConditions =
    [
        FullEngramNoGraphCondition,
        FullEngramNoLifecycleCondition,
        FullEngramNoHybridCondition
    ];

    private readonly record struct FullEngramPolicy(bool UseGraph, bool UseLifecycle, bool UseHybrid)
    {
        public static FullEngramPolicy Full => new(true, true, true);
        public static FullEngramPolicy NoGraph => new(false, true, true);
        public static FullEngramPolicy NoLifecycle => new(true, false, true);
        public static FullEngramPolicy NoHybrid => new(true, true, false);
    }

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;

    public AgentOutcomeBenchmarkRunner(
        CognitiveIndex index,
        IEmbeddingService embedding,
        KnowledgeGraph graph,
        LifecycleEngine lifecycle)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        _lifecycle = lifecycle;
    }

    /// <summary>Run an agent-outcome benchmark dataset across all supported memory conditions.
    /// Set <paramref name="runAblations"/> to true to additionally run the three T2 ablation
    /// conditions (<c>full_engram_no_graph</c>, <c>full_engram_no_lifecycle</c>,
    /// <c>full_engram_no_hybrid</c>) for per-module intelligence attribution.</summary>
    public AgentOutcomeBenchmarkResult Run(
        AgentOutcomeDataset dataset,
        bool useContextualPrefix = false,
        bool runAblations = false)
    {
        var baseline = RunCondition(dataset, NoMemoryCondition, useContextualPrefix);
        var conditions = runAblations
            ? ComparisonConditions.Concat(AblationConditions).ToArray()
            : ComparisonConditions;

        var comparisons = new List<AgentOutcomeConditionComparison>(conditions.Length);

        foreach (var condition in conditions)
        {
            var result = RunCondition(dataset, condition, useContextualPrefix);
            comparisons.Add(new AgentOutcomeConditionComparison(
                condition,
                result,
                result.MeanSuccessScore - baseline.MeanSuccessScore,
                result.PassRate - baseline.PassRate,
                result.MeanRequiredCoverage - baseline.MeanRequiredCoverage,
                result.MeanConflictRate - baseline.MeanConflictRate,
                result.MeanLatencyMs - baseline.MeanLatencyMs));
        }

        return new AgentOutcomeBenchmarkResult(
            dataset.DatasetId,
            DateTimeOffset.UtcNow,
            NoMemoryCondition,
            baseline,
            comparisons,
            DescribeEmbeddingModel(),
            _embedding.Dimensions,
            "Task-style proxy benchmark comparing memory conditions on evidence coverage, conflict rate, and latency.");
    }

    /// <summary>Get all available agent-outcome dataset IDs.</summary>
    public static IReadOnlyList<string> GetAvailableDatasets() =>
    [
        "agent-outcome-v1",
        "agent-outcome-repo-v1",
        "agent-outcome-hard-v1",
        "agent-outcome-reasoning-v1",
        "reasoning-ladder-v1",
        "contradiction-arena-v1",
        "adversarial-retrieval-v1",
        "counterfactual-v1"
    ];

    /// <summary>Create an agent-outcome dataset by ID.</summary>
    public static AgentOutcomeDataset? CreateDataset(string datasetId)
        => datasetId switch
        {
            "agent-outcome-v1" => CreateAgentOutcomeDataset(),
            "agent-outcome-repo-v1" => CreateRepoRecoveryDataset(),
            "agent-outcome-hard-v1" => CreateHardOutcomeDataset(),
            "agent-outcome-reasoning-v1" => CreateReasoningOutcomeDataset(),
            "reasoning-ladder-v1" => IntelligenceDatasets.CreateReasoningLadderDataset(),
            "contradiction-arena-v1" => IntelligenceDatasets.CreateContradictionArenaDataset(),
            "adversarial-retrieval-v1" => IntelligenceDatasets.CreateAdversarialRetrievalDataset(),
            "counterfactual-v1" => IntelligenceDatasets.CreateCounterfactualDataset(),
            _ => null
        };

    /// <summary>
    /// First task-level dataset focused on practical coding-assistant memory behaviors:
    /// archived decision recovery, graph-assisted recall, preference retention, and
    /// hybrid rescue of colloquial queries.
    /// </summary>
    public static AgentOutcomeDataset CreateAgentOutcomeDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("ao-sqlitewal", "SQLite WAL mode is enabled for crash safety and concurrent reads.", "architecture", LifecycleState: "archived"),
            new("ao-dlllock", "DLL lock issue caused by MCP server processes holding file handles after build. Kill the server process before rebuilding.", "bug-fix"),
            new("ao-buildcore", "Build the Core project alone with BuildProjectReferences=false to avoid project-reference locking during rebuilds.", "bug-fix"),
            new("ao-lockorder", "Lock ordering pattern: snapshot shared state under the owning lock, then resolve dependent data outside that lock to avoid deadlocks.", "pattern"),
            new("ao-graphresolve", "Graph traversal snapshots adjacency under the graph lock and resolves entries outside the graph lock to prevent deadlocks.", "pattern"),
            new("ao-darkmode", "User prefers dark mode in interfaces and terminal workflows.", "preference"),
            new("ao-concise", "User prefers concise, direct responses without unnecessary preamble.", "preference"),
            new("ao-noemoji", "Do not use emojis unless the user explicitly asks for them.", "preference"),
            new("ao-accretion", "AccretionScanner consolidates aging STM entries via decay and collapse into denser summaries.", "architecture"),
            new("ao-nocommit", "Never auto-commit code changes unless the user explicitly asks.", "preference")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("ao-dlllock", "ao-buildcore", "depends_on", 0.9f),
            new("ao-graphresolve", "ao-lockorder", "depends_on", 0.8f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "task-build-lock",
                "Before rebuilding after a DLL lock, what workaround avoids project-reference locking?",
                ["ao-dlllock", "ao-buildcore"],
                K: 1,
                MinScore: 0.35f,
                Notes: "Requires graph-assisted recovery of the concrete workaround."),
            new(
                "task-archived-decision",
                "What older storage decision gives us crash safety and concurrent reads?",
                ["ao-sqlitewal"],
                K: 1,
                MinScore: 0.35f,
                Notes: "Checks deep recall of archived decisions."),
            new(
                "task-style-preferences",
                "How should responses be formatted for this user?",
                ["ao-darkmode", "ao-concise", "ao-noemoji"],
                K: 3,
                MinScore: 0.25f,
                Notes: "Tests cross-session preference retention."),
            new(
                "task-maintenance-terminology",
                "How does the system handle automatic memory maintenance?",
                ["ao-accretion"],
                K: 1,
                MinScore: 0.35f,
                Notes: "Colloquial query should be rescued by hybrid search + synonym expansion."),
            new(
                "task-commit-policy",
                "Should I auto-commit these changes?",
                ["ao-nocommit"],
                K: 1,
                MinScore: 0.35f,
                Notes: "Simple memory-backed policy recall.")
        };

        return new AgentOutcomeDataset(
            "agent-outcome-v1",
            "Agent Outcome Benchmark v1",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 2);
    }

    /// <summary>
    /// Second task-level dataset focused on interrupted repo work, confirmed-cause recall,
    /// and multi-step recovery from partially completed debugging sessions.
    /// </summary>
    public static AgentOutcomeDataset CreateRepoRecoveryDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("rr-deadlock-root", "Confirmed deadlock root cause: resolving entries while still holding the graph lock can invert lock order.", "bug-fix"),
            new("rr-deadlock-fix", "Deadlock fix plan: snapshot adjacency under the graph lock, then resolve dependent entries after releasing that lock.", "pattern"),
            new("rr-build-lock", "Before rerunning a rebuild, kill lingering MCP server dotnet processes that are locking the output DLL.", "bug-fix"),
            new("rr-core-build", "If rebuild pressure remains, build the Core project alone with BuildProjectReferences=false.", "bug-fix"),
            new("rr-old-hypothesis", "Earlier hypothesis: rebuild failures were caused by checksum corruption in persisted namespace files.", "lesson"),
            new("rr-confirmed-cause", "Confirmed cause of the rebuild failure is DLL locking by lingering dotnet processes, not checksum corruption.", "bug-fix"),
            new("rr-commit-policy", "Never auto-commit code changes unless the user explicitly asks.", "preference"),
            new("rr-response-style", "User prefers concise status updates during longer debugging sessions.", "preference")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("rr-deadlock-root", "rr-deadlock-fix", "depends_on", 0.9f),
            new("rr-build-lock", "rr-core-build", "depends_on", 0.8f),
            new("rr-confirmed-cause", "rr-build-lock", "elaborates", 0.7f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "repo-resume-deadlock",
                "Resume the deadlock fix from the previous debugging session.",
                ["rr-deadlock-root", "rr-deadlock-fix"],
                HelpfulMemoryIds: ["rr-response-style"],
                K: 2,
                MinScore: 0.30f,
                Notes: "Interrupted repo work should recover both root cause and fix plan."),
            new(
                "repo-confirmed-build-cause",
                "What is the confirmed cause of the rebuild failure?",
                ["rr-confirmed-cause"],
                ForbiddenMemoryIds: ["rr-old-hypothesis"],
                K: 2,
                MinScore: 0.30f,
                Notes: "Conflict rate exposes whether stale hypotheses are still surfacing."),
            new(
                "repo-pre-rerun-check",
                "Before rerunning the build, what should I do first?",
                ["rr-build-lock"],
                HelpfulMemoryIds: ["rr-core-build"],
                K: 2,
                MinScore: 0.30f,
                Notes: "Graph links should help recover the fallback workaround."),
            new(
                "repo-commit-policy",
                "Should I commit the changes automatically now?",
                ["rr-commit-policy"],
                K: 1,
                MinScore: 0.30f,
                Notes: "Simple policy recall with no ambiguity.")
        };

        return new AgentOutcomeDataset(
            "agent-outcome-repo-v1",
            "Agent Outcome Repo Recovery Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 2);
    }

    /// <summary>
    /// Harder task-level dataset designed to separate simple transcript replay from
    /// retrieval policies that actually use synonym expansion, deep recall, and graph expansion.
    /// Transcript replay is intentionally constrained with one-memory chunks so linked multi-memory
    /// answers require retrieval policy, not just adjacent transcript replay.
    /// </summary>
    public static AgentOutcomeDataset CreateHardOutcomeDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("hard-archive-return", "Deep recall resurrects archived entries when similarity exceeds the resurrection threshold.", "architecture"),
            new("hard-archive-policy", "Decay archives stale entries instead of deleting them, preserving them for later recall.", "architecture"),
            new("hard-accretion-cleanup", "AccretionScanner consolidates aging STM entries via decay and collapse into denser summaries.", "architecture"),
            new("hard-graph-snapshot", "Graph traversal snapshots adjacency under the graph lock and resolves entries outside the graph lock to prevent deadlocks.", "pattern"),
            new("hard-lock-order", "Lock ordering pattern: snapshot shared state under the owning lock, then resolve dependent data outside that lock to avoid deadlocks.", "pattern"),
            new("hard-security-policy", "Never expose raw memory vectors or internal entry IDs in public API responses.", "preference"),
            new("hard-security-audit", "Security audits must verify that vector redaction is active for all cross-tenant requests.", "pattern"),
            new("hard-tenant-isolation", "Tenant isolation is enforced via mandatory namespace prefixing on all index operations.", "architecture")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("hard-archive-policy", "hard-archive-return", "depends_on", 0.8f),
            new("hard-graph-snapshot", "hard-lock-order", "depends_on", 0.9f),
            new("hard-tenant-isolation", "hard-security-policy", "depends_on", 0.7f),
            new("hard-security-policy", "hard-security-audit", "depends_on", 0.9f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "hard-cleanup-job",
                "Which automatic cleanup job tidies stale notes into compact digests?",
                ["hard-accretion-cleanup"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Transcript replay should miss the synonym gap while hybrid search rescues it via cleanup and automatic expansions."),
            new(
                "hard-expired-return",
                "When old notes expire, are they deleted or preserved for later recall?",
                ["hard-archive-policy", "hard-archive-return"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Answer must cite both the retention policy and the recall mechanism. Transcript replay only sees one chunk, but graph-aware retrieval should recover both."),
            new(
                "hard-graph-inversion",
                "When the graph mutex bites us, what pattern avoids lock inversion?",
                ["hard-graph-snapshot", "hard-lock-order"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Answer must cite both the graph-specific procedure and the general lock-order rule. Transcript replay sees at most one memory chunk; graph expansion should recover both linked memories."),
            new(
                "hard-security-chain",
                "What is the multi-layered strategy for protecting sensitive data in cross-tenant requests?",
                ["hard-tenant-isolation", "hard-security-policy", "hard-security-audit"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Requires following a 3-hop graph chain from architecture to preference to audit pattern. Separates graph-depth learners from surface matches.")
        };

        return new AgentOutcomeDataset(
            "agent-outcome-hard-v1",
            "Agent Outcome Hard Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }

    public static AgentOutcomeDataset CreateReasoningOutcomeDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // Conflict pair: Port configuration
            new("reason-port-legacy", "Legacy architecture uses port 8080 for internal signaling.", "architecture"),
            new("reason-port-secure", "Security policy update: all signaling must migrate to port 443 with TLS 1.3.", "decision"),
            
            // Logic chain: Quantization depth
            new("reason-quant-core", "Search performance is bound by dot-product throughput on FP32 vectors.", "architecture"),
            new("reason-quant-pref", "The user prefers low-latency over perfect recall for draft-mode operations.", "preference"),
            new("reason-quant-impl", "Draft-mode operations should use 8-bit integer quantization to maximize throughput.", "pattern"),
            
            // Temporal context: Naming
            new("reason-name-old", "The system codename is 'Project Icarus'.", "reference"),
            new("reason-name-new", "Project Icarus has been renamed to 'Nexus-SHI' to avoid copyright issues.", "decision"),
            
            // Indirect logic: Shutdown
            new("reason-shutdown-db", "Storage transactions must be flushed before the PersistenceManager stops.", "pattern"),
            new("reason-shutdown-mcp", "The MCP server shuts down when it receives a SIGTERM signal.", "architecture")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("reason-port-secure", "reason-port-legacy", "contradicts", 1.0f),
            new("reason-name-new", "reason-name-old", "contradicts", 1.0f),
            new("reason-quant-core", "reason-quant-pref", "similar_to", 0.5f),
            new("reason-quant-pref", "reason-quant-impl", "elaborates", 0.8f),
            new("reason-shutdown-mcp", "reason-shutdown-db", "depends_on", 0.9f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "reason-resolve-port",
                "What port should I use for internal signaling, and are there any conflicting rules I should know about?",
                ["reason-port-secure", "reason-port-legacy"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Tests contradiction awareness. Intelligence is citing the migration to 443 while acknowledging the legacy 8080 rule is now obsolete."),
            new(
                "reason-low-latency-search",
                "How should we implement search for draft mode to satisfy performance requirements?",
                ["reason-quant-core", "reason-quant-pref", "reason-quant-impl"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Requires 3-hop logic: Core bottleneck (FP32) + User preference (Low latency) -> Implementation choice (Int8)."),
            new(
                "reason-rename-resolution",
                "What is the current name of the project, and why was it changed?",
                ["reason-name-new", "reason-name-old"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Tests temporal reasoning. Must identify Nexus-SHI as current and Icarus as obsolete due to copyright."),
            new(
                "reason-safe-exit",
                "What needs to happen when the server receives a SIGTERM to ensure data integrity?",
                ["reason-shutdown-mcp", "reason-shutdown-db"],
                K: 1,
                MinScore: 0.25f,
                Notes: "Tests dependency reasoning: SIGTERM (MCP) triggers shutdown, which must flush transactions (DB) per the dependency link.")
        };

        return new AgentOutcomeDataset(
            "agent-outcome-reasoning-v1",
            "Agent Outcome Reasoning & Intelligence Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }

    private AgentOutcomeConditionResult RunCondition(
        AgentOutcomeDataset dataset,
        string condition,
        bool useContextualPrefix)
    {
        return condition switch
        {
            NoMemoryCondition => Aggregate(condition, dataset.Tasks.Select(t => ScoreTask(t, Array.Empty<string>(), 0, dataset.Edges)).ToList()),
            TranscriptReplayCondition => RunTranscriptReplay(dataset),
            VectorMemoryCondition => RunIndexedCondition(dataset, condition, useContextualPrefix, policy: null),
            FullEngramCondition => RunIndexedCondition(dataset, condition, useContextualPrefix, FullEngramPolicy.Full),
            FullEngramNoGraphCondition => RunIndexedCondition(dataset, condition, useContextualPrefix, FullEngramPolicy.NoGraph),
            FullEngramNoLifecycleCondition => RunIndexedCondition(dataset, condition, useContextualPrefix, FullEngramPolicy.NoLifecycle),
            FullEngramNoHybridCondition => RunIndexedCondition(dataset, condition, useContextualPrefix, FullEngramPolicy.NoHybrid),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), $"Unknown memory condition '{condition}'.")
        };
    }

    private AgentOutcomeConditionResult RunTranscriptReplay(AgentOutcomeDataset dataset)
    {
        var chunks = BuildTranscriptChunks(dataset);
        var scores = new List<AgentOutcomeTaskScore>(dataset.Tasks.Count);

        foreach (var task in dataset.Tasks)
        {
            var sw = Stopwatch.StartNew();
            var retrievedIds = SearchTranscript(task, chunks);
            sw.Stop();
            scores.Add(ScoreTask(task, retrievedIds, sw.Elapsed.TotalMilliseconds, dataset.Edges));
        }

        return Aggregate(TranscriptReplayCondition, scores);
    }

    private AgentOutcomeConditionResult RunIndexedCondition(
        AgentOutcomeDataset dataset,
        string condition,
        bool useContextualPrefix,
        FullEngramPolicy? policy)
    {
        var seeded = SeedConditionNamespace(dataset, condition, useContextualPrefix);
        try
        {
            var scores = new List<AgentOutcomeTaskScore>(dataset.Tasks.Count);
            foreach (var task in dataset.Tasks)
            {
                var sw = Stopwatch.StartNew();
                var queryVector = _embedding.Embed(task.QueryText);
                IReadOnlyList<string> canonicalIds = policy is FullEngramPolicy p
                    ? ExecuteFullEngramTask(seeded, task, queryVector, p, dataset)
                    : ExecuteVectorTask(seeded, task, queryVector);
                sw.Stop();

                scores.Add(ScoreTask(task, canonicalIds, sw.Elapsed.TotalMilliseconds, dataset.Edges));
            }

            return Aggregate(condition, scores);
        }
        finally
        {
            CleanupSeededNamespace(seeded);
        }
    }

    private IReadOnlyList<string> ExecuteVectorTask(
        SeededNamespace seeded,
        AgentOutcomeTask task,
        float[] queryVector)
    {
        var results = _index.Search(queryVector, seeded.Namespace, task.K, minScore: task.MinScore);
        return ToCanonicalIds(results.Select(r => r.Id), seeded.LocalToCanonical);
    }

    private IReadOnlyList<string> ExecuteFullEngramTask(
        SeededNamespace seeded,
        AgentOutcomeTask task,
        float[] queryVector,
        FullEngramPolicy policy,
        AgentOutcomeDataset dataset)
    {
        // Expert panel t2-benchmark-design-2026-04-17 Q2: BM25 IDF collapses on tiny synthetic
        // corpora (<~30 entries), polluting RRF rank fusion. Gate hybrid at 50 entries so the
        // ablation is not confused with calibration noise.
        bool useHybrid = policy.UseHybrid && BenchmarkPolicyPatches.ShouldUseHybrid(dataset);

        IReadOnlyList<CognitiveSearchResult> results = _index.Search(new SearchRequest
        {
            Query = queryVector,
            QueryText = task.QueryText,
            Namespace = seeded.Namespace,
            K = task.K,
            MinScore = task.MinScore,
            Hybrid = useHybrid,
            Rerank = true
        });

        if (policy.UseLifecycle && (results.Count == 0 || results[0].Score < 0.50f))
        {
            results = _lifecycle.DeepRecall(
                queryVector,
                seeded.Namespace,
                k: task.K,
                minScore: task.MinScore,
                resurrectionThreshold: 0.7f,
                queryText: task.QueryText,
                hybrid: useHybrid,
                rerank: true);
        }

        var canonicalIds = policy.UseGraph
            ? ExpandWithGraph(results, seeded.LocalToCanonical)
            : ToCanonicalIds(results.Select(r => r.Id), seeded.LocalToCanonical);

        // Expert panel Q1: lifecycle-aware policies must treat archived entries as superseded
        // when they are joined by a `contradicts` edge to a live LTM entry. Drop the archived
        // side so deep_recall resurrection does not cause CHS regression on contradiction pairs.
        if (policy.UseLifecycle)
            canonicalIds = BenchmarkPolicyPatches.ResolveLifecycleContradictions(canonicalIds, dataset);

        return canonicalIds;
    }

    private SeededNamespace SeedConditionNamespace(
        AgentOutcomeDataset dataset,
        string condition,
        bool useContextualPrefix)
    {
        string ns = $"__agent_outcome_{condition}_{Guid.NewGuid():N}";
        var canonicalToLocal = new Dictionary<string, string>(dataset.SeedEntries.Count);
        var localToCanonical = new Dictionary<string, string>(dataset.SeedEntries.Count);
        var localIds = new List<string>(dataset.SeedEntries.Count);

        foreach (var seed in dataset.SeedEntries)
        {
            string localId = $"{condition}:{seed.Id}";
            canonicalToLocal[seed.Id] = localId;
            localToCanonical[localId] = seed.Id;
            localIds.Add(localId);

            var textToEmbed = useContextualPrefix
                ? BenchmarkRunner.BuildContextualPrefix(category: seed.Category) + seed.Text
                : seed.Text;
            var vector = _embedding.Embed(textToEmbed);

            var entry = new CognitiveEntry(
                localId,
                vector,
                ns,
                seed.Text,
                seed.Category,
                lifecycleState: seed.LifecycleState ?? "ltm");

            if (seed.AccessCount is int accessCount)
                entry.AccessCount = accessCount;
            if (seed.IsSummaryNode == true)
                entry.IsSummaryNode = true;
            if (seed.SourceClusterId is not null)
                entry.SourceClusterId = seed.SourceClusterId;

            _index.Upsert(entry);
        }

        foreach (var edge in dataset.Edges)
        {
            if (!canonicalToLocal.TryGetValue(edge.SourceId, out var localSource)) continue;
            if (!canonicalToLocal.TryGetValue(edge.TargetId, out var localTarget)) continue;

            _graph.AddEdge(new GraphEdge(localSource, localTarget, edge.Relation, edge.Weight));
        }

        return new SeededNamespace(ns, canonicalToLocal, localToCanonical, localIds);
    }

    private void CleanupSeededNamespace(SeededNamespace seeded)
    {
        foreach (var localId in seeded.LocalIds)
            _graph.RemoveAllEdgesForEntry(localId);

        _index.DeleteAllInNamespace(seeded.Namespace);
    }

    private static AgentOutcomeTaskScore ScoreTask(
        AgentOutcomeTask task,
        IReadOnlyCollection<string> retrievedIds,
        double latencyMs,
        IReadOnlyList<OutcomeGraphEdgeSeed> edges)
    {
        var required = task.RequiredMemoryIds;
        var helpful = task.HelpfulMemoryIds ?? Array.Empty<string>();
        var forbidden = task.ForbiddenMemoryIds ?? Array.Empty<string>();

        int requiredHits = required.Count(retrievedIds.Contains);
        int helpfulHits = helpful.Count(retrievedIds.Contains);
        int forbiddenHits = forbidden.Count(retrievedIds.Contains);

        float requiredCoverage = required.Count == 0 ? 1f : (float)requiredHits / required.Count;
        float helpfulCoverage = helpful.Count == 0 ? 1f : (float)helpfulHits / helpful.Count;
        float conflictRate = forbidden.Count == 0 ? 0f : (float)forbiddenHits / forbidden.Count;

        float helpfulWeight = helpful.Count > 0 ? 0.20f : 0f;
        float requiredWeight = 1.0f - helpfulWeight;
        float baseSuccess = Math.Clamp(
            (requiredCoverage * requiredWeight) +
            (helpfulCoverage * helpfulWeight) -
            (conflictRate * 0.50f),
            0f,
            1f);

        // In the offline runner there is no separate model-citation step, so context == cited.
        var intelligence = IntelligenceScoring.Compute(task, retrievedIds, retrievedIds, edges);
        float successScore = IntelligenceScoring.AdjustSuccessScore(baseSuccess, intelligence, task);

        bool passed = requiredCoverage >= 0.999f && conflictRate == 0f && intelligence.StaleMemoryPenalty == 0f;

        return new AgentOutcomeTaskScore(
            task.TaskId,
            requiredCoverage,
            helpfulCoverage,
            conflictRate,
            successScore,
            passed,
            latencyMs,
            retrievedIds.ToList(),
            intelligence.ReasoningPathValidity,
            intelligence.DependencyCompletionScore,
            intelligence.StaleMemoryPenalty,
            intelligence.MinimalEvidenceScore,
            intelligence.NoiseResistanceScore,
            intelligence.NoiseResistanceScoreRanked,
            intelligence.ContradictionHandlingScore);
    }

    private static AgentOutcomeConditionResult Aggregate(
        string condition,
        IReadOnlyList<AgentOutcomeTaskScore> scores)
    {
        if (scores.Count == 0)
        {
            return new AgentOutcomeConditionResult(condition, scores, 0f, 0f, 0f, 0f, 0);
        }

        return new AgentOutcomeConditionResult(
            condition,
            scores,
            scores.Average(s => s.SuccessScore),
            (float)scores.Count(s => s.Passed) / scores.Count,
            scores.Average(s => s.RequiredCoverage),
            scores.Average(s => s.ConflictRate),
            scores.Average(s => s.LatencyMs),
            scores.Average(s => s.ReasoningPathValidity),
            scores.Average(s => s.DependencyCompletionScore),
            scores.Average(s => s.StaleMemoryPenalty),
            scores.Average(s => s.MinimalEvidenceScore),
            scores.Average(s => s.NoiseResistanceScore),
            scores.Average(s => s.NoiseResistanceScoreRanked),
            scores.Average(s => s.ContradictionHandlingScore));
    }

    private IReadOnlyList<string> ExpandWithGraph(
        IReadOnlyList<CognitiveSearchResult> results,
        IReadOnlyDictionary<string, string> localToCanonical)
    {
        var canonicalIds = new List<string>();
        var seenCanonical = new HashSet<string>();

        foreach (var result in results)
        {
            if (localToCanonical.TryGetValue(result.Id, out var canonical) && seenCanonical.Add(canonical))
                canonicalIds.Add(canonical);

            var neighbors = _graph.GetNeighbors(result.Id);
            foreach (var neighbor in neighbors.Neighbors)
            {
                if (neighbor.Entry.LifecycleState == "archived") continue;
                if (localToCanonical.TryGetValue(neighbor.Entry.Id, out var neighborCanonical) &&
                    seenCanonical.Add(neighborCanonical))
                {
                    canonicalIds.Add(neighborCanonical);
                }
            }
        }

        return canonicalIds;
    }

    private static IReadOnlyList<string> ToCanonicalIds(
        IEnumerable<string> localIds,
        IReadOnlyDictionary<string, string> localToCanonical)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>();

        foreach (var localId in localIds)
        {
            if (localToCanonical.TryGetValue(localId, out var canonical) && seen.Add(canonical))
                ids.Add(canonical);
        }

        return ids;
    }

    private static IReadOnlyList<TranscriptChunk> BuildTranscriptChunks(AgentOutcomeDataset dataset)
    {
        int chunkSize = Math.Max(dataset.TranscriptChunkSize, 1);
        var chunks = new List<TranscriptChunk>();

        for (int i = 0; i < dataset.SeedEntries.Count; i += chunkSize)
        {
            var slice = dataset.SeedEntries.Skip(i).Take(chunkSize).ToList();
            var builder = new StringBuilder();
            foreach (var seed in slice)
                builder.AppendLine(seed.Text);

            chunks.Add(new TranscriptChunk(
                $"chunk-{(i / chunkSize) + 1}",
                builder.ToString(),
                slice.Select(s => s.Id).ToList()));
        }

        return chunks;
    }

    private static IReadOnlyList<string> SearchTranscript(
        AgentOutcomeTask task,
        IReadOnlyList<TranscriptChunk> chunks)
    {
        var queryTokens = BM25Index.Tokenize(task.QueryText).Distinct().ToHashSet();
        if (queryTokens.Count == 0)
            return Array.Empty<string>();

        var ranked = chunks
            .Select(chunk => (Chunk: chunk, Score: ScoreTranscriptChunk(queryTokens, chunk.Text)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.ChunkId, StringComparer.Ordinal)
            .Take(Math.Max(task.K, 1))
            .ToList();

        var retrieved = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (chunk, _) in ranked)
        {
            foreach (var id in chunk.MemoryIds)
            {
                if (seen.Add(id))
                    retrieved.Add(id);
            }
        }

        return retrieved;
    }

    private static int ScoreTranscriptChunk(HashSet<string> queryTokens, string text)
    {
        var chunkTokens = BM25Index.Tokenize(text).Distinct();
        int overlap = 0;
        foreach (var token in chunkTokens)
        {
            if (queryTokens.Contains(token))
                overlap++;
        }
        return overlap;
    }

    private string DescribeEmbeddingModel()
    {
        return _embedding switch
        {
            OnnxEmbeddingService => "ONNX bge-micro-v2",
            HashEmbeddingService => "HashEmbeddingService",
            _ => _embedding.GetType().Name
        };
    }

    private sealed record TranscriptChunk(string ChunkId, string Text, IReadOnlyList<string> MemoryIds);

    private sealed record SeededNamespace(
        string Namespace,
        IReadOnlyDictionary<string, string> CanonicalToLocal,
        IReadOnlyDictionary<string, string> LocalToCanonical,
        IReadOnlyList<string> LocalIds);
}

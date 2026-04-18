using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// T2 intelligence-claim datasets for agent-outcome benchmarking. Each dataset exercises a
/// specific mechanism the Engram system claims to enable:
/// <list type="bullet">
/// <item><b>reasoning-ladder-v1</b> — same causal chain queried at hop depths 1, 2, 3, 4. Shows
/// where non-graph memory policies break. Exercises ReasoningPathValidity + DependencyCompletionScore.</item>
/// <item><b>contradiction-arena-v1</b> — old/new fact pairs with provenance. Exercises
/// ContradictionHandlingScore and StaleMemoryPenalty. Lifecycle-aware policies should prefer the
/// current fact; naive RAG should blend both.</item>
/// <item><b>adversarial-retrieval-v1</b> — near-duplicate, synonym, stale, and contradictory decoys
/// injected beside real facts. Exercises NoiseResistanceScore. Hybrid lexical search should lift
/// precision over pure vector.</item>
/// <item><b>counterfactual-v1</b> — "what breaks if memory X is removed?" tasks with
/// dependency-chain graphs. Exercises ReasoningPathValidity on counterfactual reasoning: models
/// must enumerate downstream dependents, not the removed fact itself.</item>
/// </list>
/// Transcript chunk size = 1 across all four so transcript-replay condition has to rely on the
/// single most-topical chunk (cannot accidentally see multiple linked memories).
/// </summary>
internal static class IntelligenceDatasets
{
    public static AgentOutcomeDataset CreateReasoningLadderDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("ladder-fp32-bottleneck", "Vector-search bottleneck is FP32 dot-product throughput on large embedding sets.", "architecture"),
            new("ladder-int8-quant", "Int8 quantization raises dot-product throughput roughly 3x over FP32 on modern CPUs.", "pattern"),
            new("ladder-hnsw-latency", "Higher dot-product throughput is what makes HNSW search stay under 50ms at K=10.", "architecture"),
            new("ladder-draft-slo", "Sub-50ms HNSW latency is the SLO that the real-time draft-mode endpoint is held to.", "decision"),
            new("ladder-draft-ui", "The real-time draft-mode endpoint is what powers the autocomplete UI surface.", "architecture"),

            // Ladder-adjacent distractor seeds to make vector-only retrieval noisier without
            // breaking the chain.
            new("ladder-unrelated-gc", "The GC pressure on the embedding pool dropped after moving to ArrayPool buffers.", "pattern"),
            new("ladder-unrelated-warm", "Embedding warmup runs on startup to prevent first-query latency spikes.", "architecture")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("ladder-fp32-bottleneck", "ladder-int8-quant", "depends_on", 0.9f),
            new("ladder-int8-quant", "ladder-hnsw-latency", "depends_on", 0.9f),
            new("ladder-hnsw-latency", "ladder-draft-slo", "elaborates", 0.8f),
            new("ladder-draft-slo", "ladder-draft-ui", "depends_on", 0.8f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "ladder-1hop",
                "What raises vector dot-product throughput on CPUs?",
                ["ladder-int8-quant"],
                K: 1, MinScore: 0.25f,
                Notes: "Hop depth 1. Baselines should all pass.",
                OrderedSteps: ["ladder-int8-quant"],
                ReasoningHops: 1,
                TaskFamily: "reasoning-ladder"),
            new(
                "ladder-2hop",
                "What two facts together explain sub-50ms HNSW latency?",
                ["ladder-int8-quant", "ladder-hnsw-latency"],
                K: 2, MinScore: 0.25f,
                Notes: "Hop depth 2. Vector baseline should still find both; transcript replay with chunk size 1 should fail.",
                OrderedSteps: ["ladder-int8-quant", "ladder-hnsw-latency"],
                ReasoningHops: 2,
                TaskFamily: "reasoning-ladder"),
            new(
                "ladder-3hop",
                "Why does Int8 quantization matter for the draft-mode SLO?",
                ["ladder-int8-quant", "ladder-hnsw-latency", "ladder-draft-slo"],
                K: 2, MinScore: 0.25f,
                Notes: "Hop depth 3. Requires graph traversal to surface the SLO node, which is lexically distant from quantization.",
                OrderedSteps: ["ladder-int8-quant", "ladder-hnsw-latency", "ladder-draft-slo"],
                ReasoningHops: 3,
                TaskFamily: "reasoning-ladder"),
            new(
                "ladder-4hop",
                "Trace the full chain from the FP32 throughput bottleneck to the autocomplete UI.",
                ["ladder-fp32-bottleneck", "ladder-int8-quant", "ladder-hnsw-latency", "ladder-draft-slo", "ladder-draft-ui"],
                K: 3, MinScore: 0.22f,
                Notes: "Hop depth 4. Only graph traversal should reliably assemble the full chain.",
                OrderedSteps:
                [
                    "ladder-fp32-bottleneck",
                    "ladder-int8-quant",
                    "ladder-hnsw-latency",
                    "ladder-draft-slo",
                    "ladder-draft-ui"
                ],
                ReasoningHops: 4,
                TaskFamily: "reasoning-ladder",
                MinEvidence: 5)
        };

        return new AgentOutcomeDataset(
            "reasoning-ladder-v1",
            "Reasoning Ladder Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }

    public static AgentOutcomeDataset CreateContradictionArenaDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("contra-port-old", "Internal signaling uses TCP port 8080 per the legacy networking spec.", "architecture", LifecycleState: "archived"),
            new("contra-port-new", "Internal signaling must use port 443 with TLS 1.3 as of the 2026 security policy.", "decision"),

            new("contra-quant-old", "Quantization was FP32-only when the service launched.", "architecture", LifecycleState: "archived"),
            new("contra-quant-new", "Draft-mode quantization is Int8 as of v0.6 to meet latency SLOs.", "decision"),

            new("contra-name-old", "The system codename is Project Icarus.", "reference", LifecycleState: "archived"),
            new("contra-name-new", "Project Icarus was renamed to Nexus-SHI in 2026 to avoid copyright conflicts.", "decision"),

            new("contra-auth-old", "Auth tokens persist for 24 hours before expiring.", "architecture", LifecycleState: "archived"),
            new("contra-auth-new", "Auth tokens rotate every 15 minutes under the new zero-trust policy.", "decision"),

            // Neutral fillers so the embedding space isn't dominated by paired contradictions.
            new("contra-filler-logging", "Structured logging uses JSON Lines and is written to stdout.", "architecture"),
            new("contra-filler-metrics", "Metrics are exported via Prometheus scrape on port 9090.", "architecture")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("contra-port-new", "contra-port-old", "contradicts", 1.0f),
            new("contra-quant-new", "contra-quant-old", "contradicts", 1.0f),
            new("contra-name-new", "contra-name-old", "contradicts", 1.0f),
            new("contra-auth-new", "contra-auth-old", "contradicts", 1.0f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "contra-port",
                "Which port should internal signaling use, and is there a conflicting rule I should know about?",
                ["contra-port-new"],
                K: 2, MinScore: 0.25f,
                Notes: "Citing both is acceptable only if the model picks the new port as authoritative. CHS: 1.0 if only new cited; 0.5 if both cited; 0.0 if only old cited.",
                StaleMemoryIds: ["contra-port-old"],
                TaskFamily: "contradiction-arena"),
            new(
                "contra-quant",
                "What quantization does draft mode use today?",
                ["contra-quant-new"],
                K: 2, MinScore: 0.25f,
                Notes: "Lifecycle-aware policies should prefer the LTM fact over the archived one.",
                StaleMemoryIds: ["contra-quant-old"],
                TaskFamily: "contradiction-arena"),
            new(
                "contra-name",
                "What is the current project name?",
                ["contra-name-new"],
                K: 2, MinScore: 0.25f,
                Notes: "Naive vector retrieval will often return the archived codename with high similarity.",
                StaleMemoryIds: ["contra-name-old"],
                TaskFamily: "contradiction-arena"),
            new(
                "contra-auth",
                "How long do auth tokens remain valid under current policy?",
                ["contra-auth-new"],
                K: 2, MinScore: 0.25f,
                Notes: "Blending the two token lifetimes is the failure mode we're scoring against.",
                StaleMemoryIds: ["contra-auth-old"],
                TaskFamily: "contradiction-arena")
        };

        return new AgentOutcomeDataset(
            "contradiction-arena-v1",
            "Contradiction Arena Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }

    public static AgentOutcomeDataset CreateAdversarialRetrievalDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // Real facts.
            new("adv-stem-real", "BM25 tokenization in the retrieval pipeline uses Porter stemming with an expanded synonym list of 98 mappings.", "architecture"),
            new("adv-archive-real", "Lifecycle state 'archived' is excluded from default search but is still reachable via deep_recall when resurrection similarity exceeds 0.7.", "architecture"),
            new("adv-spread-real", "The spreading-activation traversal decays edge weight by 0.5 per hop, terminating when cumulative weight falls below 0.1.", "architecture"),
            new("adv-rerank-real", "Token-overlap reranking is applied only to the top-K candidates returned by the primary retriever.", "architecture"),

            // Near-duplicate paraphrases (retrieval-layer traps — lexically similar, semantically weaker).
            new("adv-stem-near", "Porter stemming is a common step in general search indexing for document retrieval.", "reference"),
            new("adv-archive-near", "Archived memories are permanently removed and cannot be retrieved.", "reference"),
            new("adv-spread-near", "Spreading activation halves its weight on every traversal step.", "reference"),

            // Synonym trap (jargon swap — can look right to embedding but is not the project's terminology).
            new("adv-stem-synonym", "Word-stem pruning helps keyword indexing in natural-language search systems.", "reference"),

            // Stale fact (archived — would be wrong to cite).
            new("adv-stem-stale", "Legacy retrieval used suffix-array stemming prior to v0.3.", "reference", LifecycleState: "archived"),

            // Contradictory distractor (directly wrong).
            new("adv-rerank-wrong", "Token-overlap reranking is applied to every candidate in the full namespace before returning results.", "reference")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("adv-archive-real", "adv-archive-near", "contradicts", 1.0f),
            new("adv-rerank-real", "adv-rerank-wrong", "contradicts", 1.0f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "adv-stem",
                "What stemmer does the retrieval pipeline's BM25 tokenization use?",
                ["adv-stem-real"],
                K: 2, MinScore: 0.25f,
                Notes: "Distractors are paraphrase, jargon swap, and legacy fact. Noise Resistance measures whether hybrid + lifecycle lift precision.",
                StaleMemoryIds: ["adv-stem-stale"],
                DistractorMemoryIds: ["adv-stem-near", "adv-stem-synonym"],
                TaskFamily: "adversarial-retrieval"),
            new(
                "adv-archive",
                "Can archived memories be retrieved, and if so, how?",
                ["adv-archive-real"],
                K: 2, MinScore: 0.25f,
                Notes: "The distractor is lexically close but directly wrong. Vector-only retrieval tends to surface the distractor.",
                DistractorMemoryIds: ["adv-archive-near"],
                TaskFamily: "adversarial-retrieval"),
            new(
                "adv-spread",
                "How does spreading activation decay per hop, and when does it stop?",
                ["adv-spread-real"],
                K: 2, MinScore: 0.25f,
                Notes: "Near-duplicate captures only half the fact (the decay) and omits the termination threshold.",
                DistractorMemoryIds: ["adv-spread-near"],
                TaskFamily: "adversarial-retrieval",
                MinEvidence: 1),
            new(
                "adv-rerank",
                "Is token-overlap reranking applied to every candidate, or only to the top-K?",
                ["adv-rerank-real"],
                K: 2, MinScore: 0.25f,
                Notes: "Directly contradictory distractor. Graph 'contradicts' edge should route toward the authoritative seed.",
                DistractorMemoryIds: ["adv-rerank-wrong"],
                TaskFamily: "adversarial-retrieval")
        };

        return new AgentOutcomeDataset(
            "adversarial-retrieval-v1",
            "Adversarial Retrieval Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }

    public static AgentOutcomeDataset CreateCounterfactualDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // Core dependency chain A: WAL → atomic namespace delete → Accretion cleanup.
            new("cf-wal", "SqliteStorageProvider uses WAL (write-ahead log) mode for atomic multi-statement writes.", "architecture"),
            new("cf-atomic-delete", "Namespace deletion requires WAL atomicity so concurrent writers don't see a half-deleted index.", "architecture"),
            new("cf-accretion", "The Accretion cleanup job calls namespace deletion whenever it compacts stale STM into summaries.", "pattern"),
            new("cf-corruption", "Without WAL atomicity, concurrent namespace deletion has corrupted the HNSW graph index in past incidents.", "lesson"),

            // Core dependency chain B: HNSW snapshot → cold-start latency → embedding warmup.
            new("cf-hnsw-snapshot", "HNSW graph snapshots are persisted to disk on shutdown so cold start doesn't rebuild the O(N log N) graph.", "architecture"),
            new("cf-cold-start", "Cold-start latency was dominated by HNSW rebuild before snapshots were introduced in v0.5.5.", "lesson"),
            new("cf-warmup", "Embedding warmup runs at startup but does not rebuild the HNSW graph; that's snapshot-restore only.", "architecture"),

            // Distractors: related-sounding but independent systems.
            new("cf-distract-sharding", "Namespace sharding uses consistent hashing across storage backends.", "reference"),
            new("cf-distract-rate", "API rate limiting is enforced per-tenant with a 100 QPS default.", "reference")
        };

        var edges = new List<OutcomeGraphEdgeSeed>
        {
            new("cf-accretion", "cf-atomic-delete", "depends_on", 0.9f),
            new("cf-atomic-delete", "cf-wal", "depends_on", 0.95f),
            new("cf-wal", "cf-corruption", "elaborates", 0.7f),
            new("cf-atomic-delete", "cf-corruption", "elaborates", 0.8f),

            new("cf-cold-start", "cf-hnsw-snapshot", "elaborates", 0.85f),
            new("cf-warmup", "cf-hnsw-snapshot", "similar_to", 0.5f)
        };

        var tasks = new List<AgentOutcomeTask>
        {
            new(
                "cf-disable-wal",
                "If WAL mode were disabled on the storage provider, what would break first and what is the recorded failure mode?",
                ["cf-atomic-delete", "cf-corruption"],
                HelpfulMemoryIds: ["cf-accretion"],
                K: 3, MinScore: 0.25f,
                Notes: "Counterfactual removal traversal: consequences propagate from cf-wal outward along dependency edges. Must cite the dependent and the historical failure, not WAL itself.",
                DistractorMemoryIds: ["cf-distract-sharding", "cf-distract-rate"],
                OrderedSteps: ["cf-atomic-delete", "cf-corruption"],
                ReasoningHops: 2,
                TaskFamily: "counterfactual"),
            new(
                "cf-accretion-dependency",
                "Which storage property does the Accretion cleanup ultimately depend on, and why?",
                ["cf-atomic-delete", "cf-wal"],
                HelpfulMemoryIds: ["cf-corruption"],
                K: 3, MinScore: 0.25f,
                Notes: "Two-hop forward chain from cf-accretion → cf-atomic-delete → cf-wal.",
                OrderedSteps: ["cf-accretion", "cf-atomic-delete", "cf-wal"],
                ReasoningHops: 2,
                TaskFamily: "counterfactual"),
            new(
                "cf-remove-snapshot",
                "If HNSW snapshots were removed, which metric would regress and what is the historical precedent?",
                ["cf-cold-start"],
                HelpfulMemoryIds: ["cf-warmup"],
                K: 2, MinScore: 0.25f,
                Notes: "Should cite the lesson, not just the architecture fact. cf-warmup is helpful context that clarifies warmup does NOT substitute for the snapshot.",
                DistractorMemoryIds: ["cf-distract-sharding"],
                OrderedSteps: ["cf-hnsw-snapshot", "cf-cold-start"],
                ReasoningHops: 1,
                TaskFamily: "counterfactual")
        };

        return new AgentOutcomeDataset(
            "counterfactual-v1",
            "Counterfactual Dependency Benchmark",
            seeds,
            edges,
            tasks,
            TranscriptChunkSize: 1);
    }
}

[< Back to README](../README.md)

# Architecture

```mermaid
graph TD
    subgraph MCP["MCP Server (stdio)"]
        Tools["17 Tool Classes<br/>65 MCP Tools"]
    end

    Tools --> CI["CognitiveIndex<br/><i>Thin facade: CRUD, locking, limits</i>"]

    CI --> RE["Retrieval"]
    CI --> GR["Graph"]
    CI --> LC["Lifecycle"]
    CI --> IN["Intelligence"]
    CI --> EX["Experts"]
    CI --> EV["Evaluation"]

    subgraph RE["Retrieval Engine"]
        VS["VectorSearchEngine<br/><i>Two-stage Int8→FP32</i>"]
        HS["HybridSearchEngine<br/><i>Adaptive RRF + Cascade</i>"]
        HW["HnswIndex<br/><i>O(log N) ANN</i>"]
        BM["BM25Index<br/><i>Porter stemming</i>"]
        SY["SynonymExpander<br/><i>98 mappings</i>"]
        DE["DocumentEnricher<br/><i>47 reverse maps</i>"]
        PS["PorterStemmer<br/><i>Steps 1-3</i>"]
        QE["QueryExpander<br/><i>PRF</i>"]
        TR["TokenReranker"]
        DR["DiversityReranker<br/><i>Cluster-aware MMR</i>"]
        VQ["VectorQuantizer<br/><i>SIMD Int8</i>"]
        SR["SpectralRetrievalReranker<br/><i>Broad / Specific / Auto</i>"]
        ES["EmbeddingSubspace<br/><i>Randomized SVD</i>"]
    end

    subgraph GR["Knowledge Graph"]
        KG["KnowledgeGraph<br/><i>Directed edges, BFS, Revision counter</i>"]
        MDK["MemoryDiffusionKernel<br/><i>Top-K Laplacian eigenbasis</i>"]
        ALS["AutoLinkScanner<br/><i>similar_to densification</i>"]
        RES["RandomizedEigensolver<br/><i>Halko-Martinsson-Tropp</i>"]
    end

    subgraph LC["Lifecycle Engine"]
        LE["LifecycleEngine<br/><i>Decay, consolidation, state transitions</i>"]
        PE["PhysicsEngine<br/><i>Gravitational re-ranking</i>"]
    end

    subgraph IN["Intelligence"]
        CM["ClusterManager"]
        AS["AccretionScanner<br/><i>DBSCAN</i>"]
        DD["DuplicateDetector<br/><i>Spectral pre-filter ≥256</i>"]
    end

    subgraph EX["Expert Routing"]
        ED["ExpertDispatcher<br/><i>HMoE tree walk</i>"]
        DM["DebateSessionManager"]
        SA["SpreadingActivation<br/><i>Collins &amp; Loftus</i>"]
    end

    subgraph SN["Synthesis"]
        SE["SynthesisEngine<br/><i>Ollama map-reduce</i>"]
    end

    subgraph SH["Multi-Agent Sharing"]
        NR["NamespaceRegistry<br/><i>Ownership + permissions</i>"]
    end

    subgraph EV["Evaluation"]
        BR["BenchmarkRunner<br/><i>MRR, nDCG, Recall@K</i>"]
        LR["LiveAgentOutcomeBenchmarkRunner<br/><i>Real model A/B harness</i>"]
        MC["MetricsCollector<br/><i>P50/P95/P99</i>"]
    end

    CI --> SH
    CI --> SN
    CI --> NS["NamespaceStore<br/><i>ConcurrentDictionary, partitioned locks</i>"]
    NS --> SP["Storage Provider"]

    subgraph SP["Storage"]
        PM["PersistenceManager<br/><i>JSON + SHA-256 checksums</i>"]
        SQ["SqliteStorageProvider<br/><i>WAL mode, busy_timeout</i>"]
    end

    NS --> EMB["OnnxEmbeddingService<br/><i>bge-micro-v2, 384-dim, concurrent</i>"]

    subgraph BG["Background Services"]
        BG1["EmbeddingWarmup<br/><i>startup</i>"]
        BG2["DecayService<br/><i>every 15 min</i>"]
        BG3["AccretionService<br/><i>every 30 min</i>"]
        BG4["DiffusionKernelWarmup<br/><i>every 30 min</i>"]
        BG5["AutoLinkScanner<br/><i>every 6 hours</i>"]
        BG6["ConsolidationPass<br/><i>every 24 hours</i>"]
    end

    LE -.->|reads| MDK
    SR -.->|reads| MDK
    ALS -.->|invalidates| KG
    KG -.->|revision counter| MDK
    MDK -.->|uses| RES
    DD -.->|uses| ES
```

The **memory-diffusion subsystem** (added in v0.9.0) is the spine of all
graph-aware behavior in the engine. `MemoryDiffusionKernel` computes the
top-K eigenbasis of each namespace's normalized Laplacian once, then
serves three downstream consumers: `LifecycleEngine` reads it to diffuse
decay debt and run consolidation passes; `SpectralRetrievalReranker`
reads it to apply low-pass / high-pass filters to relevance scores;
duplicate detection uses the parallel `EmbeddingSubspace` projector for
its own low-rank pre-filter. The `KnowledgeGraph.Revision` counter
makes cache invalidation lock-free — any edge mutation increments the
revision and the next kernel read rebuilds.


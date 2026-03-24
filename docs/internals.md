[< Back to README](../README.md)

# Internals

### The Core Memory Loop

```
INGEST → ENRICH → INDEX → RETRIEVE → REINFORCE → DECAY → SUMMARIZE/COLLAPSE
   │        │                │           │           │              │
   └── store_memory          │    memory_feedback    │    collapse_cluster
       (embed + upsert)      │    (agent feedback)   │    (DBSCAN → summary)
                │             └── search_memory       └── decay_cycle
          DocumentEnricher        (hybrid pipeline)   (activation energy)
          (auto-keywords)
```

Memories move through lifecycle states based on usage:

```
STM (short-term) ──promote──→ LTM (long-term) ──decay──→ Archived
                   ←─────────────────────────────────── deep_recall
                              (auto-resurrect if score ≥ 0.7)
```

### Retrieval Pipeline (v0.5.4)

The hybrid search pipeline applies six stages to maximize recall without sacrificing precision:

```
Query → Synonym Expansion → Vector Search ──┐
              │                              ├─→ Adaptive RRF Fusion ──→ Auto-PRF ──→ Category Boost ──→ Results
              └──→ BM25 Search ──────────────┘         │
                   (Porter stemming)              Cascade mode
                                               (≥50 entries: BM25
                                                boosts vector only)
```

1. **Synonym Expansion**: Query terms are expanded using 98 domain synonym mappings (e.g., "maintenance" → accretion/decay/collapse, "encrypt" → TLS/cipher/cryptography)
2. **Dual-Path Search**: Vector cosine similarity (with HNSW for large namespaces) runs in parallel with BM25 keyword search (with Porter stemming and compound tokenization)
3. **Adaptive RRF Fusion**: Confidence-gated Reciprocal Rank Fusion — high vector confidence (>0.70) suppresses BM25 noise, low confidence (<0.50) amplifies BM25 rescue. For namespaces ≥50 entries, cascade mode uses BM25 as a precision booster (up to 15%) instead of introducing new candidates
4. **Auto-PRF**: When top result score is low (<0.015 RRF), Pseudo-Relevance Feedback extracts key terms from initial results and re-searches. Only used if PRF improves the top score
5. **Category Boost**: 8% score boost when query tokens overlap with entry categories, improving disambiguation at scale
6. **Document Enrichment** (at store time): `DocumentEnricher` auto-generates keyword aliases from entry text using 47 reverse synonym mappings, so BM25 indexes both technical text and colloquial equivalents

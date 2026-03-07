# Benchmark Ideas

10 proposals for expanding the IR quality benchmark suite. Three have been implemented (paraphrase-v1, multihop-v1, scale-v1). Seven remain as future candidates.

## Current Baselines

| Dataset | Seeds | Queries | Recall@5 | Precision@5 | MRR | nDCG@5 |
|---------|-------|---------|----------|-------------|-----|--------|
| default-v1 | 25 | 20 | 0.867 | 0.430 | 1.000 | 0.938 |
| paraphrase-v1 | 25 | 15 | 0.944 | 0.360 | 1.000 | 0.977 |
| multihop-v1 | 25 | 15 | 0.939 | 0.587 | 1.000 | 0.929 |
| scale-v1 | 80 | 30 | 0.717 | 0.447 | 1.000 | 0.860 |

## Ideas

### 1. Cross-Domain Ambiguity

Queries where the same term has different meanings across domains. Tests whether the system retrieves the correct semantic context rather than surface-level keyword matches.

**Example seeds:** "Tree (data structure)", "Tree (file system hierarchy)", "Tree (DOM in web browsers)"
**Example query:** "How do I traverse a tree?" — should retrieve all three with appropriate ranking.

**Measures:** Precision under ambiguity, whether the system diversifies results.

---

### 2. Paraphrase Robustness — IMPLEMENTED as `paraphrase-v1`

Queries that are heavy paraphrases or indirect descriptions of seed content. Tests embedding model's semantic understanding beyond lexical overlap.

**Example seed:** "C# is a modern, object-oriented programming language developed by Microsoft."
**Example query:** "The language Microsoft built for .NET" — should still retrieve bench-csharp as #1.

**Measures:** MRR stability when query wording diverges from seed text.

---

### 3. Negative / Distractor Resilience

Queries that are superficially similar to seeds but semantically different. Tests whether the system avoids false positives.

**Example seed:** "Python is a high-level programming language..."
**Example query:** "Python snake species native to Southeast Asia" — should NOT retrieve bench-python highly.

**Measures:** Precision (low false-positive rate), ability to distinguish homonyms.

---

### 4. Multi-Hop Reasoning — IMPLEMENTED as `multihop-v1`

Queries that span two or more seed topics, requiring the system to surface multiple relevant entries that together answer the query.

**Example query:** "Building a REST API in Rust" — should retrieve both bench-rust and bench-restapi.
**Example query:** "Using gradient descent to train a transformer" — should retrieve bench-gradientdescent and bench-transformer.

**Measures:** Recall across multi-topic queries, result diversity.

---

### 5. Specificity Gradient

Pairs of broad and narrow queries on the same topic. Tests how well the system handles queries at different levels of abstraction.

**Broad:** "programming" — should retrieve multiple language seeds.
**Medium:** "web development" — should retrieve JavaScript, REST, HTTP.
**Narrow:** "async/await in JavaScript event loop" — should strongly prefer bench-javascript.

**Measures:** Precision at different specificity levels, nDCG sensitivity to query scope.

---

### 6. Lifecycle-Aware Retrieval

Benchmark that stores seeds across STM, LTM, and archived states, then queries with different lifecycle filters to verify filtering works correctly.

**Setup:** 10 seeds in STM, 10 in LTM, 5 archived.
**Queries:** Same query text with different `includeStates` filters.
**Expected:** Results change based on filter; archived entries excluded by default.

**Measures:** Filter correctness, deep_recall resurrection accuracy.

---

### 7. Cluster Summary Quality

Store cluster summaries alongside member entries, then query to verify summaries rank appropriately with and without `summaryFirst` mode.

**Setup:** 15 member entries grouped into 3 clusters, each with a stored summary.
**Queries:** Topic queries where the summary should be the best single answer.
**Expected:** With summaryFirst=true, summaries rank above individual members.

**Measures:** Summary ranking lift, nDCG improvement with summaryFirst.

---

### 8. Scale Stress Test — IMPLEMENTED as `scale-v1`

80 seed entries across 8 categories to test how IR metrics and latency degrade as corpus size grows.

**Setup:** 80 seed entries across 8 categories (languages, data structures, ML, databases, networking, systems, security, devops).
**Queries:** 30 queries with graded relevance.
**Track:** Recall@5, latency percentiles.

**Measures:** Metric degradation curve, latency scaling behavior.

---

### 9. Temporal / Recency Bias (Physics Re-ranking)

Seeds with varying access counts and creation times. Tests whether physics-based re-ranking (gravitational force) properly boosts high-activation entries.

**Setup:** Duplicate-topic seeds where one has high accessCount (frequently accessed) and one has low.
**Queries:** Run with `usePhysics=true` and `usePhysics=false`.
**Expected:** Physics mode boosts the high-activation entry; non-physics mode ranks purely by cosine.

**Measures:** Rank shift between physics/non-physics, activation energy correlation with rank.

---

### 10. Near-Duplicate Contamination

Intentionally inject near-duplicate seeds (slightly reworded versions of the same content) and verify the system still returns diverse results.

**Setup:** 15 unique seeds + 10 near-duplicates (paraphrases of existing seeds).
**Queries:** Standard topic queries.
**Expected:** Results should not be dominated by duplicates of the same entry. Duplicate detection warning should fire on ingest.

**Measures:** Result diversity (unique topics in top-5), duplicate detection recall, precision impact of contamination.

---

## Implementation Priority

| Priority | Idea | Status |
|----------|------|--------|
| High | 2. Paraphrase Robustness | **Implemented** — `paraphrase-v1` |
| High | 4. Multi-Hop Reasoning | **Implemented** — `multihop-v1` |
| High | 8. Scale Stress Test | **Implemented** — `scale-v1` |
| Medium | 1. Cross-Domain Ambiguity | Not started |
| Medium | 3. Negative/Distractor | Not started |
| Medium | 5. Specificity Gradient | Not started |
| Medium | 9. Physics Re-ranking | Not started |
| Low | 6. Lifecycle-Aware | Not started |
| Low | 7. Cluster Summary | Not started |
| Low | 10. Duplicate Contamination | Not started |

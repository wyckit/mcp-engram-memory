# Benchmark Ideas

10 proposals for expanding the IR quality benchmark suite. Nine have been implemented (paraphrase-v1, multihop-v1, scale-v1, ambiguity-v1, distractor-v1, specificity-v1, physics-v1, lifecycle-v1, contamination-v1). One remains as a future candidate.

## Current Baselines

| Dataset | Seeds | Queries | Recall@5 | Precision@5 | MRR | nDCG@5 |
|---------|-------|---------|----------|-------------|-----|--------|
| default-v1 | 25 | 20 | 0.867 | 0.430 | 1.000 | 0.938 |
| paraphrase-v1 | 25 | 15 | 0.944 | 0.360 | 1.000 | 0.977 |
| multihop-v1 | 25 | 15 | 0.939 | 0.587 | 1.000 | 0.929 |
| scale-v1 | 80 | 30 | 0.717 | 0.447 | 1.000 | 0.860 |
| ambiguity-v1 | 24 | 15 | 0.922 | 0.467 | 1.000 | 0.941 |
| distractor-v1 | 22 | 15 | 0.737 | 0.387 | 1.000 | 0.988 |
| specificity-v1 | 30 | 18 | 0.919 | 0.622 | 0.972 | 0.905 |

## Ideas

### 1. Cross-Domain Ambiguity — IMPLEMENTED as `ambiguity-v1`

Queries where the same term has different meanings across domains. Tests whether the system retrieves the correct semantic context rather than surface-level keyword matches.

**Dataset:** 24 seeds using 10 ambiguous term groups (network, tree, memory, model, kernel, port, pipeline, table, branch, node) across different CS domains. 15 queries: 6 unambiguous (clear domain context), 6 ambiguous (no context), 3 cross-domain.

**Measures:** Precision under ambiguity, whether the system diversifies results.

---

### 2. Paraphrase Robustness — IMPLEMENTED as `paraphrase-v1`

Queries that are heavy paraphrases or indirect descriptions of seed content. Tests embedding model's semantic understanding beyond lexical overlap.

**Example seed:** "C# is a modern, object-oriented programming language developed by Microsoft."
**Example query:** "The language Microsoft built for .NET" — should still retrieve bench-csharp as #1.

**Measures:** MRR stability when query wording diverges from seed text.

---

### 3. Negative / Distractor Resilience — IMPLEMENTED as `distractor-v1`

Queries that are superficially similar to seeds but semantically different. Tests whether the system avoids false positives.

**Dataset:** 22 seeds with 10 homonym groups (Python lang/snake, Java lang/island/coffee, Mars planet/candy, Spring framework/season/mechanical, Rust lang/corrosion, Docker tech/worker, Shell CLI/sea, Apache server/helicopter, LaTeX typeset/material, Mercury planet/element). 15 queries with explicit grade-0 distractors.

**Measures:** Precision (low false-positive rate), ability to distinguish homonyms.

---

### 4. Multi-Hop Reasoning — IMPLEMENTED as `multihop-v1`

Queries that span two or more seed topics, requiring the system to surface multiple relevant entries that together answer the query.

**Example query:** "Building a REST API in Rust" — should retrieve both bench-rust and bench-restapi.
**Example query:** "Using gradient descent to train a transformer" — should retrieve bench-gradientdescent and bench-transformer.

**Measures:** Recall across multi-topic queries, result diversity.

---

### 5. Specificity Gradient — IMPLEMENTED as `specificity-v1`

Queries at 3 specificity tiers (broad, medium, narrow) on 6 topic clusters. Tests how precision and ranking change as queries move from general to highly specific.

**Dataset:** 30 seeds across 6 clusters (languages, web, databases, ML, systems, security) with 5 seeds each. 18 queries: 6 broad (4-5 relevant seeds each), 6 medium (2-5 relevant seeds), 6 narrow (1-3 relevant seeds).

**Measures:** Per-tier recall, precision at different specificity levels, nDCG sensitivity to query scope.

---

### 6. Lifecycle-Aware Retrieval — IMPLEMENTED as `lifecycle-v1`

Benchmark that stores seeds across STM, LTM, and archived states, then queries with different lifecycle filters to verify filtering works correctly.

**Dataset:** 25 seeds (10 STM, 10 LTM, 5 archived) covering patterns, databases, networking, security, devops, architecture, and tooling. 15 queries: 3 targeting STM, 4 targeting LTM, 3 cross-state, 5 targeting archived entries (testing exclusion from default search).

**Measures:** Filter correctness, archived entry exclusion, deep_recall resurrection accuracy.

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

### 9. Temporal / Recency Bias (Physics Re-ranking) — IMPLEMENTED as `physics-v1`

Seeds with varying access counts and creation times. Tests whether physics-based re-ranking (gravitational force) properly boosts high-activation entries.

**Dataset:** 20 seeds (10 topic pairs with cold/hot activation profiles). Hot seeds have AccessCount 50-80, cold seeds have AccessCount 0-2. 10 queries targeting both seeds in each pair, with hot seed graded higher.

**Measures:** Rank shift between physics/non-physics, activation energy correlation with rank, nDCG improvement when high-activation seeds are expected first.

---

### 10. Near-Duplicate Contamination — IMPLEMENTED as `contamination-v1`

Intentionally inject near-duplicate seeds (slightly reworded versions of the same content) and verify the system still returns diverse results.

**Dataset:** 25 seeds (15 unique + 10 near-duplicate paraphrases of seeds 1-10) across data-structures, devops, ml, security, networking, databases, patterns, and systems. 12 queries with unique seeds graded 3 and duplicates graded 2.

**Measures:** Result diversity (unique topics in top-5), duplicate detection recall, precision impact of contamination.

---

## Implementation Priority

| Priority | Idea | Status |
|----------|------|--------|
| High | 2. Paraphrase Robustness | **Implemented** — `paraphrase-v1` |
| High | 4. Multi-Hop Reasoning | **Implemented** — `multihop-v1` |
| High | 8. Scale Stress Test | **Implemented** — `scale-v1` |
| Medium | 1. Cross-Domain Ambiguity | **Implemented** — `ambiguity-v1` |
| Medium | 3. Negative/Distractor | **Implemented** — `distractor-v1` |
| Medium | 5. Specificity Gradient | **Implemented** — `specificity-v1` |
| Medium | 9. Physics Re-ranking | **Implemented** — `physics-v1` |
| Low | 6. Lifecycle-Aware | **Implemented** — `lifecycle-v1` |
| Low | 7. Cluster Summary | Not started |
| Low | 10. Duplicate Contamination | **Implemented** — `contamination-v1` |

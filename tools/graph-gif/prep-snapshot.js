// Curate a legible hero subset from the real visualization/snapshot.json.
//
// The live snapshot is ~1200 nodes, almost all LTM (blue) and mostly isolated —
// an illegible hairball with no amber. This script extracts a connected,
// well-clustered slice of the REAL data (real text, real relation types) and
// stages a handful of nodes as STM (amber) so the capture can animate a
// believable STM -> LTM consolidation beat. Output: hero-snapshot.json.

const fs = require('fs');
const path = require('path');

const SRC = path.resolve(__dirname, '../../visualization/snapshot.json');
const OUT = path.resolve(__dirname, 'hero-snapshot.json');

const TARGET_NODES = 46;   // legible without being sparse
const STM_COUNT = 11;      // amber nodes that will consolidate to blue

const snap = JSON.parse(fs.readFileSync(SRC, 'utf8').replace(/^﻿/, ''));

// ── Build adjacency over the real edge set ──────────────────────────────────
const byId = new Map(snap.nodes.map(n => [n.id, n]));
const adj = new Map();
const add = (a, b) => { if (!adj.has(a)) adj.set(a, new Set()); adj.get(a).add(b); };
for (const e of snap.edges) {
  if (byId.has(e.source) && byId.has(e.target)) { add(e.source, e.target); add(e.target, e.source); }
}

// ── Connected components, largest first ─────────────────────────────────────
const seen = new Set();
const components = [];
for (const id of adj.keys()) {
  if (seen.has(id)) continue;
  const comp = [];
  const stack = [id];
  while (stack.length) {
    const cur = stack.pop();
    if (seen.has(cur)) continue;
    seen.add(cur); comp.push(cur);
    for (const nb of adj.get(cur) ?? []) if (!seen.has(nb)) stack.push(nb);
  }
  components.push(comp);
}
components.sort((a, b) => b.length - a.length);

// ── Accumulate whole components until we reach TARGET_NODES ─────────────────
// Keeping components whole preserves real cluster structure (no orphan edges).
const keep = new Set();
for (const comp of components) {
  if (keep.size >= TARGET_NODES) break;
  if (keep.size + comp.length > TARGET_NODES + 6) continue; // skip a too-big comp, try smaller
  comp.forEach(id => keep.add(id));
}
// Fallback: if nothing fit, take the largest component truncated.
if (keep.size === 0) components[0].slice(0, TARGET_NODES).forEach(id => keep.add(id));

// ── Stage STM nodes: prefer low-degree (leaf-ish) nodes — newly-arrived
// memories tend to be sparsely connected before consolidation links them in. ──
const degree = id => (adj.get(id)?.size ?? 0);
const stmCandidates = [...keep].sort((a, b) => degree(a) - degree(b)).slice(0, STM_COUNT);
const stmSet = new Set(stmCandidates);

// ── Emit nodes (real text, staged lifecycle) ────────────────────────────────
const nodes = [...keep].map(id => {
  const n = byId.get(id);
  return {
    id: n.id,
    text: n.text,
    lifecycleState: stmSet.has(id) ? 'stm' : 'ltm',
    activationEnergy: n.activationEnergy ?? 1,
    accessCount: n.accessCount ?? 0,
    isSummaryNode: !!n.isSummaryNode,
  };
});

const edges = snap.edges
  .filter(e => keep.has(e.source) && keep.has(e.target))
  .map(e => ({ source: e.source, target: e.target, relation: e.relation }));

// Cluster hulls are intentionally dropped: the force layout does not group by
// cluster, so convex hulls over scattered members draw ugly cross-frame bands.
// The visual "clustering" story is told by force grouping + the consolidation
// recolor. We still report a meaningful cluster count = connected components.
const componentCount = components.filter(c => c.some(id => keep.has(id))).length;
const clusters = [];

const hero = {
  namespace: '*',
  capturedAt: snap.capturedAt,
  stats: { nodeCount: nodes.length, edgeCount: edges.length, clusterCount: componentCount },
  nodes, edges, clusters,
};

fs.writeFileSync(OUT, JSON.stringify(hero));
console.log(`hero-snapshot.json: ${nodes.length} nodes (${STM_COUNT} STM), ${edges.length} edges, ${clusters.length} clusters`);
console.log('STM node ids:', [...stmSet].join(', '));

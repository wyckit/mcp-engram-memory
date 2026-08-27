# Tenant Isolation Design (decision 3b)

Status: **Phase 1 implemented** (storage/model) · **Phase 2 implemented** (index/search/store) · **Phase 3 implemented** (graph/intelligence/lifecycle/diffusion) — see §5

This document describes the introduction of a first-class `tenant_id` into the Engram
storage layer. The overriding constraint is **backward compatibility**: `mcp-engram-memory`
is a shared library used by Conductor *and* by non-Conductor consumers, so every existing
single-tenant caller must keep working with zero code changes.

---

## 1. Model

### 1.1 The empty-string tenant = legacy partition

`CognitiveEntry` gains one optional field:

```csharp
[JsonPropertyName("tenantId")]
public string TenantId { get; }   // default "" (legacy single-tenant partition)
```

* Default value is `""` (empty string), **not** `null`. `""` is the *legacy tenant* —
  the partition every pre-tenant entry implicitly lived in.
* `TenantId` is normalized in the constructor: `null`/whitespace → `""`, otherwise trimmed.
* Max length is 64 characters (`CognitiveEntry.MaxTenantIdLength`), matching the storage
  column; over-length throws `ArgumentException` so a tenant key can never silently truncate.
* The field is immutable (get-only, set via constructor), exactly like `Id` and `Ns`.

Both constructors (the public convenience ctor and the `[JsonConstructor]`) take `tenantId`
as a **trailing optional parameter**, so:

* Every existing positional call site (`new CognitiveEntry(id, vector, ns, text, ...)`)
  compiles and behaves identically — it gets `TenantId == ""`.
* Deserializing pre-tenant JSON (no `tenantId` property) yields `TenantId == ""` because
  `System.Text.Json` supplies the parameter's default when the property is absent.

### 1.2 Serialization

`tenantId` serializes into `json_data` for **all** providers (SQLite and SQL Server), so an
entry round-trips its tenant regardless of backend. On SQL Server the value is *also* promoted
to a real column (below) for indexing and primary-key partitioning; the JSON copy remains the
source of truth for the deserialized object.

---

## 2. Storage schema

### 2.1 SQL Server (this task)

Schema version bumped **v2 → v3**. Forward migration (`MigrateToV3`, mirrored in
`scripts/migrations/sqlserver_v3_tenant_id.up.sql`):

| Change | Detail |
|--------|--------|
| Column | `tenant_id NVARCHAR(64) NOT NULL CONSTRAINT DF_engram_entries_tenant DEFAULT('')` |
| Primary key | `(ns, id)` → **`(tenant_id, ns, id)`** (constraint name `PK_engram_entries` reused) |
| Index | new `idx_entries_tenant_ns_state (tenant_id, ns, lifecycle_state)` |

Properties:

* **Version-gated** — `RunMigrations` only runs when the stored `schema_version` is behind,
  so re-opening an already-migrated DB is a no-op → **idempotent**.
* **Atomic** — the whole migration + version bump runs in one transaction; any failure rolls
  back to v2.
* **Guarded steps** — each DDL statement has an `IF [NOT] EXISTS` guard, so even a partial
  re-run (were the transaction ever bypassed) is safe.
* **Reversible** — `scripts/migrations/sqlserver_v3_tenant_id.down.sql` drops the index, PK,
  default constraint and column and restores `PRIMARY KEY (ns, id)`. The reverse is lossless
  **only** in a single-tenant DB (all `tenant_id = ''`); it aborts with `RAISERROR` if real
  tenants exist, because collapsing `(tenant_id, ns, id)` back to `(ns, id)` would violate
  uniqueness.

Fresh databases: `InitializeSchema` still creates the v2-shaped table, then immediately
migrates it to v3. This means the migration path is exercised on every fresh install, not just
on upgrades — the idempotency/atomicity guarantees are always in force.

Writes now include `tenant_id`:

* `WriteNamespaceData` (full-namespace snapshot) inserts `tenant_id = entry.TenantId`.
* `BuildEntryUpsertSql` (incremental `MERGE`) matches on the full key
  `(tenant_id, ns, id)` and inserts `tenant_id = entry.TenantId`. Matching on the full key
  is required under the new PK so a `MERGE` for one tenant can never update another tenant's
  row that happens to share `(ns, id)`.

Reads (`LoadNamespace`) are **unchanged** in this task — they still filter by `ns` only and
reconstruct `TenantId` from `json_data`. Tenant-scoped reads are Phase 2 (see §4). This keeps
the legacy load path byte-for-byte identical.

> **Delete note.** `ScheduleDeleteEntry(ns, id)` still deletes by `(ns, id)`. With no
> multi-tenant data flowing yet (the write plumbing is Phase 2), the only rows present are in
> the `''` tenant, so this is correct today. Phase 2 introduces a tenant-aware delete
> (see §4) before any second tenant can be written through the higher layers.

### 2.2 SQLite (unchanged, intentionally)

SQLite is the default/dev/single-tenant backend. Its `tenant_id` lives **in `json_data` only**
— no column, no PK change. This is a deliberate scope boundary: it keeps every existing SQLite
test green and unmodified, and single-tenant SQLite deployments need nothing more. If/when a
tenant-partitioned SQLite backend is required, a parallel v2→v3 SQLite migration mirrors §2.1.

---

## 3. Current (Phase 1) public API surface

No public signatures changed. The only additive surface is `CognitiveEntry.TenantId`
(get-only) and the trailing optional `tenantId` constructor parameter. All of `IStorageProvider`
is unchanged. Every no-tenant caller is unaffected — this is the FROZEN contract.

---

## 4. Phase 2 spec (T2-05) — tenant-aware index/search/store plumbing

Phase 2 threads `tenant_id` through `NamespaceStore` / `CognitiveIndex` and the retrieval
engines. The storage layer from Phase 1 already persists and partitions by tenant; Phase 2
makes the in-memory index and query APIs tenant-aware. **RRF/rerank internals and embedding
pins stay FROZEN** — tenancy is a *filter* applied around them, never a change to scoring.

### 4.1 Isolation invariant

> A caller that supplies tenant `T` sees exactly the entries with `TenantId == T`.
> A caller that supplies no tenant sees exactly the legacy `""` tenant.
> There is **no** API path that returns another tenant's entry, and none that even reveals
> whether an id exists in another tenant.

### 4.2 API additions / changes

| Method | Phase 2 behavior |
|--------|------------------|
| `Upsert(CognitiveEntry entry)` | Honors `entry.TenantId`. In-memory index is keyed by `(tenantId, ns, id)`; the entry is only visible within its tenant. |
| `Get(string id, string ns)` | Add an optional `string tenantId = ""` overload/param. Scoped to that tenant; default `""` = legacy tenant only. |
| `Get(string id)` (global) | Under tenancy, resolves **only within the legacy `""` tenant** unless a tenant is supplied. A global id-probe must never fall through to another tenant's entry — this is what makes cross-tenant id-probing impossible. |
| `Delete(string id)` | Keep existing legacy-tenant semantics; **add `Delete(string id, string ns, string tenantId = "")`** that deletes the tenant-scoped row. The storage `ScheduleDeleteEntry` gains a tenant parameter (or a tenant-aware overload) so deletes target `(tenant_id, ns, id)`. |
| `HybridSearch(...)` | Add `string tenantId = ""`. Candidate set is pre-filtered to the tenant *before* RRF/rerank; scoring math is untouched. |
| `SearchMultiple(...)` | Add `string tenantId = ""`, applied to every namespace fanned out. Cross-namespace, still single-tenant per call. |

### 4.3 Storage-provider additions for Phase 2

* `LoadNamespace(ns)` → add `LoadNamespace(ns, tenantId)` (or optional param) filtering
  `WHERE ns = @ns AND tenant_id = @tenant`. The tenant-aware index
  `idx_entries_tenant_ns_state` covers this.
* `ScheduleDeleteEntry(ns, id)` → tenant-aware overload deleting by `(tenant_id, ns, id)`.
* `GetPersistedNamespaces()` → optional tenant filter, or a `GetPersistedTenants()` companion.
* `DeleteNamespaceAsync(ns)` → tenant-scoped overload.

### 4.4 Index-layer notes

* `CognitiveIndex`/`NamespaceStore` in-memory dictionaries move from `ns → (id → entry)` to
  `(tenantId, ns) → (id → entry)` (or a nested tenant map). HNSW/BM25 sub-indexes are built
  per `(tenant, ns)` partition so a search never mixes tenants at the candidate stage.
* Because filtering happens on the candidate set, RRF fusion and all rerankers
  (`DiversityReranker`, `SpectralRetrievalReranker`, `TokenReranker`) operate exactly as today
  on the already-tenant-scoped candidates — **no reranker code changes**, preserving the frozen
  retrieval behavior.

### 4.5 Migration/rollout ordering

1. Phase 1 (this task): schema + model + storage persistence. Ships behind the empty-tenant
   default; no behavior change for anyone.
2. Phase 2 (T2-05): index/search/store threading + tenant-aware provider reads/deletes.
3. Conductor opts in by stamping `TenantId` on the entries it writes and passing `tenantId`
   on its reads. Everyone else keeps using `""` and is unaffected.

---

## 5. Phase 3 — tenant-qualified graph / intelligence / lifecycle / diffusion

Phase 2 left the *cognitive support structures* — the knowledge graph, semantic clusters, collapse
history, decay configs, and diffusion/spectral bases — keyed by **global bare entry ids**. Until
Phase 3, a non-empty-tenant principal failed closed for every graph/cluster/lifecycle/intelligence/
diffusion/spectral/maintenance/synthesis/visualization tool. Phase 3 removes that containment and
makes those structures first-class tenant partitions.

### 5.1 Chosen approach — extend the partition key, don't make ids global

Bare ids are **not** globally unique (the entries PK is `(tenant, ns, id)`), so the same `(ns, id)`
can exist under two tenants. Rather than a breaking id-format migration, Phase 3 keys every
downstream structure by the same tenant the entry store already uses:

* `GraphEdge`, `SemanticCluster`, `PendingCollapse`, `CollapseRecord`, and `DecayConfig` each gain a
  trailing-optional `TenantId` (default `""`, `Tenancy.Normalize`, serialized as `tenantId`). Because
  these persist as single JSON **blobs** (a serialized `List<T>`), the tenant round-trips inside each
  element and **no `global_data` schema/PK migration is needed** — legacy blobs deserialize as
  `tenant == ""`.
* `KnowledgeGraph._outgoing`/`_incoming` are keyed by `(tenant, entryId)`. The graph is deliberately
  **per-tenant, not per-`(tenant, ns)`** — cross-namespace association within a tenant is preserved
  (the existing behavior), while edges never cross tenants (each edge carries a single `TenantId` and
  both endpoints are interpreted within it).
* `ClusterManager._clusters` is keyed by `(tenant, clusterId)`; `AccretionScanner` tenant-filters
  every pending/committed collapse and keys dismissed ids by `(tenant, id)`.
* `MemoryDiffusionKernel`'s cache/lock/failure state and `LifecycleEngine._decayConfigs` are keyed by
  `PartitionKey(tenant, ns)` (identical to `ns` for the legacy tenant, so legacy keys are unchanged).

### 5.2 Resolution and the security boundary

Structures that hold a bare id resolve it **within the caller's tenant**: `GetForTenant(id, tenant)`
for a real tenant, the fast legacy id-locator `Get(id)` for `""`. The global by-id
`get_memory`/`delete` path stays **legacy-only** (the reverse locator is not widened), so a bare id
can never be probed or reached across tenants. Denials keep the empty/not-found shape so no read
becomes an existence oracle.

### 5.3 Tool surface and background maintenance

Every standard/full tool threads `NamespaceAccess.TenantId` into the now-tenant-aware services; the
`RequiresTenantQualifiedStructures` fail-closed guard is removed. The four background services
(decay, consolidation, auto-link, accretion) iterate `CognitiveIndex.GetAllTenants()` so every
tenant's memories are maintained, not just legacy data.

### 5.4 Migration

Additive and backward-compatible. The existing store is 100% legacy tenant (`""`); every new
`TenantId` defaults to `""`, so persisted graph/cluster/collapse/decay blobs keep resolving under the
legacy partition with no migration step. Single-tenant deployments are byte-for-byte unchanged.

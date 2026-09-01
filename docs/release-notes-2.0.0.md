_Major release. Tenant scope stops being a defaulted argument and becomes a required one: 55 Core
retrieval and scoping APIs now take `tenantId` with no default, and the parameter was placed so that
pre-2.0 positional calls fail to compile rather than silently rebinding. Also lands revision-consistent
topology reads, exact auto-link accounting, and fully retractable diffusion-kernel state._

_Major rather than minor because `McpEngramMemory.Core` is a published assembly and this is a
deliberate source- and binary-breaking change to it. **The MCP tool surface is unchanged** — tool
names, arguments, and results are identical, on-disk data needs no migration, and legacy
empty-tenant deployments behave byte-for-byte as before. If you consume Engram over stdio as an MCP
server, this upgrade is a version bump and nothing else._

_2.0.0 supersedes 1.6.0. If you are coming from 1.5.0 or earlier, read the
[1.6.0 entry in the CHANGELOG](../CHANGELOG.md) as well — full multi-tenant graph/intelligence
support and the governed cognitive constitution landed there, and this release makes that boundary
mandatory rather than optional._

### Breaking — `tenantId` is required

- **`tenantId` is now a required argument on 55 Core methods**, across `CognitiveIndex`,
  `KnowledgeGraph`, `MemoryDiffusionKernel`, `AutoLinkScanner`, `AccretionScanner`, `ClusterManager`,
  `LifecycleEngine`, `SpectralRetrievalReranker`, `NamespaceRegistry`, `SpreadingActivationService`,
  and `SynthesisEngine`.

  The old `tenantId = ""` default was never a sentinel. `""` is the legacy partition — a real,
  readable, writable dataset — so a forgotten tenant argument compiled clean and silently degraded
  to cross-tenant legacy scope. That is not hypothetical: it happened twice, in `SynthesisEngine`
  and `DiffusionKernelWarmupService`, and both were caught in the PR #18 security review rather than
  by the compiler. Removing the default converts an entire class of silent scope bug into a build
  error.

- **Placement is anti-rebinding by construction.** Where optional parameters preceded `tenantId`, it
  moved to just after the required ones — but only into slots previously occupied by an `int`,
  `float`, or `bool`, so every pre-2.0 positional call fails with a type or missing-argument error
  instead of quietly binding something else into the tenant slot.

  On eleven methods a `string` or nullable parameter sat in the way — `GetNeighbors`, `RemoveEdges`,
  `SearchMultiple`, `CreateCluster`, `UpdateCluster`, `Scan`, `SetDecayConfig`, `PromoteMemory`,
  `ApplyFeedback`, `SynthesizeNamespaceAsync`, `HasAccess`. Those parameters became **required**
  instead, because moving `tenantId` past them would have let an old positional call bind a relation,
  label, query, or access level into the tenant slot: precisely the bug class this release exists to
  remove.

- **Exceptions, deliberately kept trailing and optional:** `DeepRecall`'s `resurrect` (still defaults
  `true`; benchmark IR baselines unchanged) and `SynthesizeNamespaceAsync`'s `ct`.
  `PromoteMemory` and `ApplyFeedback` also lost their `ns` defaults — `ns: ""` / `ns: null` now
  selects the legacy bare-id locator explicitly rather than by omission.

### Breaking — other public surface

- **`AutoLinkResult` gained four trailing positional members** — `PairScanIncomplete`,
  `ScanAlreadyInProgress`, `PairsAboveThreshold`, and `PairSlotsPlanned` — and its existing
  `PairsExamined` changed from `int` to `long`. Its meaning also changed: it now reports comparison
  slots actually *completed*, including exact partial-window progress under cancellation, rather
  than pairs offered or above-threshold hits. Use `PairSlotsPlanned` for the window budget and
  `PairsAboveThreshold` for the find count. The generated positional constructor and deconstructor
  signatures changed, so this is source- and binary-breaking.

- **New public surface on `McpEngramMemory.Core`** — additive, but on a packaged assembly, so it is
  a compatibility commitment: `EdgeAddMode`, an optional `mode` parameter on `KnowledgeGraph.AddEdges`,
  `TopologyGuard.Sweep.TenantId`, `AutoLinkScanner`'s `maxPairComparisons` and `CancellationToken`
  parameters plus `DefaultMaxPairComparisons`, and `CognitiveIndex.DisposalContendedFenceCount`.

### Changed

- **`DiffusionKernelWarmupService` warms every tenant.** It now sweeps `GetAllTenants()` →
  `GetNamespaces(tenant)` → `GetBasis(ns, tenantId: tenant)`. Previously it enumerated namespaces
  with the no-tenant overload and warmed every one of them as the legacy partition — the second
  silent-scope incident above — which warmed the wrong `(tenant, ns)` partition for every identified
  tenant and left them paying a foreground eigendecomposition on first use.

  Fault isolation is now per partition: neither a failing basis nor an unreadable tenant's namespace
  enumeration aborts the sweep for the partitions after it. Tenant discovery costs a full
  `NamespaceStore.LoadAll` that the old enumeration did not pay; it is idempotent per namespace and
  runs on the background thread after the existing 5s startup delay, so the startup path is
  unaffected.

- **Bare-id resolution converged on `EntryAccessResolver`** in `link_memories`/`unlink_memories`,
  `promote_memory`, `memory_feedback`, and `get_memory`'s edge filter. Legacy single-tenant
  resolution changes from "whatever the global id→ns map happens to hold" to "unique match among
  visible namespaces": an id present in more than one namespace now refuses to resolve — same reply
  as not-found — rather than acting on an arbitrary twin, and for identified agents an invisible
  same-id entry can no longer blank or hijack resolution of one they are allowed to see.
  Deployments with unique ids, which is the normal case, see no change.

### Fixed

- **Attributable topology reads are revision-consistent.** Graph and cluster projections use a
  bounded optimistic retry and publish only if the tenant's attribution revision stayed fixed through
  the whole projection; continuous churn fails closed rather than publishing a torn view. Cascade
  deletion takes a fresh sweep for each fenced graph/cluster primitive, and centroid publication is
  conditional on the exact immutable member-list generation it was computed from.

- **Auto-link progress and lifecycle accounting are exact and bounded.** Production direct and
  spectral pair walks report completed logical comparison slots once per anchor, so cancellation no
  longer over- or under-states `PairsExamined`. Namespace deletion retracts the exact normalized
  resume-cursor key synchronously — including deletion down to zero namespaces — and cannot race an
  in-flight scan into resurrecting it.

- **Diffusion-kernel retained state is fully retractable.** The bounded per-call rotation now covers
  positive bases, negative-cached failures, and lock-only bypasses. In-flight computation and
  retraction use publication ordering that cannot leave a basis outside the cleanup registry.

- **The package set is checked before merge.** CI packs and verifies Core, the optional ONNX
  synthesis backend, and the global tool on pull requests; main-branch artifact upload stays gated
  behind successful builds and tests.

### Packaging

- **All three packages ship as a 2.0.0 set** — `McpEngramMemory.Core`,
  `McpEngramMemory.Synthesis.Onnx`, and the `McpEngramMemory` global tool. `publish-nuget.ps1`
  refuses to publish if their `<Version>` values disagree, and packs and size-checks everything
  before the first push, because nuget.org has no delete.

- **ONNX Runtime stays at 1.29.0 and ONNX Runtime GenAI at 0.15.2** — both the current stable
  releases. Note the version relationship, which is now stated in both project files rather than
  left to be inferred: GenAI 0.15.2 declares a dependency on ONNX Runtime **1.28.0**, so in a project
  that installs both, NuGet unifies the native runtime *up* to Core's 1.29.0 pin. Core is installed
  on its own far more often than it is paired with the optional GenAI backend, and holding it back
  to 1.28.0 would deny those consumers ONNX Runtime's servicing fixes in order to match an optional
  package's declared minimum exactly. `McpEngramMemory.Synthesis.Onnx` now restates the 1.29.0 pin
  explicitly so the version it actually loads is visible in the file that owns the backend.

- **The global tool now embeds debug symbols and sources**, matching what both library packages
  already did, so a stack trace out of the shipped tool resolves to source the same way one out of
  Core does.

- **The global tool actually drops mobile native runtimes now — it has been shipping them since the
  trim was introduced.** `TrimNonDesktopNativeRuntimes` exists because ONNX Runtime ships natives for
  every RID it supports, and a `dotnet` global tool is RID-agnostic, so `PackAsTool` bundles all of
  them into a console stdio process that cannot run on any mobile platform. Its match pattern
  anchored `ios` and `android` directly against the path separator, but those never appear as bare
  directories — they are `ios-arm64`, `iossimulator-x64`, `android-arm`, and so on. Only the
  `maccatalyst-` and `browser-` alternatives, which were written with their suffix pattern, ever
  matched. The target reported "excluded 6 native file(s)" and looked like it was working.

  With the pattern corrected it excludes 14, and the tool package drops from **89.3 MB to 79.7 MB**.
  Every desktop and server RID is untouched, `linux-musl-*` and the minor architectures included.

### Compatibility

- **Breaking for hosts embedding `McpEngramMemory.Core`.** Recompile is mandatory; see **Upgrading**.
- **Not breaking for MCP clients.** Tool names, arguments, results, and the `MEMORY_TOOL_PROFILE`
  counts (17 / 39 / 63) are unchanged.
- **No storage migration.** Tenant travels inside the existing JSON blobs on `GraphEdge`,
  `SemanticCluster`, `PendingCollapse`, `CollapseRecord`, and `DecayConfig`. Existing stores open
  as-is.
- **Legacy empty-tenant deployments are byte-for-byte unchanged.** A server that does not set
  `MEMORY_TENANT_ID` runs on the legacy partition exactly as it did in 1.6.0.
- **One behavioral delta worth checking:** bare-id resolution now refuses ambiguous ids instead of
  picking one. If you deliberately store the same id in multiple namespaces and rely on bare-id
  operations resolving to *some* twin, those calls will now report not-found.

## Upgrading

```bash
dotnet tool update --global McpEngramMemory --version 2.0.0
dotnet add package McpEngramMemory.Core --version 2.0.0
```

For an embedding host, recompile and work through the errors. At each one, pass the tenant the call
site already holds:

```csharp
// Before (2.0 will not compile this)
var results = index.Search(vector, ns, k: 5);

// After — name the argument; the placement rules mean positional calls fail loudly, not silently
var scoped = index.Search(vector, ns, tenantId: principal.TenantId, k: 5);

// Legacy single-tenant deployments state the legacy partition explicitly
var legacy = index.Search(vector, ns, tenantId: "", k: 5);
```

**Treat every `tenantId: ""` you add as a claim, not a fix.** It is the correct answer for a
single-tenant deployment and the wrong answer anywhere a real tenant was in scope — and the whole
point of this release is that the compiler can no longer tell the two apart for you. Work through
the errors deliberately rather than mechanically.

If you use the ONNX synthesis backend, no action is needed: the runtime versions are unchanged from
1.6.0 and the pin is now explicit in `McpEngramMemory.Synthesis.Onnx` rather than transitive.

See [Tenant Isolation Design](tenant-isolation-design.md) for the partitioning model and guarantees,
and the [CHANGELOG](../CHANGELOG.md) for the complete entry.

## Verified

1696 tests passing on each of net8.0, net9.0 and net10.0 — Debug configuration, excluding the
`MSA`, `LiveBenchmark`, and `T2Benchmark` categories, which is the same filter `publish-nuget.ps1`
applies before a release.

All three packages pack clean at 2.0.0 from a `--no-incremental` Release build with 0 warnings and
0 errors:

| Package | Size | Checked |
|---------|------|---------|
| `McpEngramMemory.Core` | 4.4 MB | `lib/` for net8.0, net9.0 and net10.0; XML docs; README and icon; `build/` + `buildMultiTargeting/` targets; ONNX Runtime resolving to 1.29.0 |
| `McpEngramMemory.Synthesis.Onnx` | 91 KB | `lib/` for all three frameworks; declares Core 2.0.0, GenAI 0.15.2 and ONNX Runtime 1.29.0 |
| `McpEngramMemory` (global tool) | 79.7 MB | `engram-memory` command entry point; every desktop and server RID present with natives intact; no ios/android/maccatalyst/browser runtimes; no loose `.pdb` (symbols embedded) |

The tool package is well under nuget.org's 250 MB ceiling, which `publish-nuget.ps1` enforces before
the first push.

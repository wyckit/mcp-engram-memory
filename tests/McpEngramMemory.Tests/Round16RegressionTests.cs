using System.Text.Json;
using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-16 review findings: a refused summary
/// store publishes nothing (so it can never erase a later success's pointer); the summary
/// entry CAS never overwrites a non-summary resident, null stamps included; summary reads
/// validate ownership before serving; a poisoned stored decay config no longer bricks
/// lifecycle loading; the collapse-record namespace read is store-first in both directions;
/// and a throwing decay-config provider is a reported flush failure, not an escaped
/// exception.
/// </summary>
public class Round16RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round16RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round16_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private string SeedAndDetect(string tenantId = "")
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        return _scanner.ScanNamespace("test", tenantId: tenantId).NewCollapses[0].CollapseId;
    }

    /// <summary>
    /// Finding 2: the summary CAS never hands a user's entry to the summary machinery — the
    /// legacy stampless world included, whose null stamp EQUALS an ordinary entry's null
    /// stamp. A resident that is not a summary of the writer's own cluster refuses whatever
    /// the stamps say.
    /// </summary>
    [Fact]
    public void SummaryCas_LegacyNullStamps_NeverOverwriteAUserEntry()
    {
        _index.Upsert(new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "the user's own memory", lifecycleState: "ltm"));

        // A legacy (stampless) summary writer: null stamp, same as the user entry's.
        var legacyWriter = new CognitiveEntry("summary:k", new[] { 0f, 1f, 0f }, "test",
            text: "legacy summary", category: "cluster-summary", lifecycleState: "ltm")
        {
            IsSummaryNode = true,
            SourceClusterId = "k",
            SourceClusterStamp = null
        };

        Assert.False(_index.UpsertSummaryIfIncarnation(legacyWriter));
        Assert.False(_index.UpsertSummaryIfIncarnation(legacyWriter, replaceStale: true,
            staleRevision: _index.Get("summary:k", "test", tenantId: "")!.Revision));
        Assert.Equal("the user's own memory", _index.Get("summary:k", "test", tenantId: "")!.Text);
    }

    /// <summary>
    /// Findings 1 + 2 at the ClusterManager: a store refused by a non-summary squatter says
    /// so truthfully, publishes NO pointer (the publish now happens only after a successful
    /// entry write, so no refusal ever has a pointer edit to roll back — the rollback shape
    /// could erase a pointer a concurrent successful store had legitimately published), and
    /// nothing on disk names the phantom summary.
    /// </summary>
    [Fact]
    public void StoreSummary_SquatterResident_RefusesTruthfullyAndPublishesNothing()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));
        _index.Upsert(new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "squatter", lifecycleState: "ltm"));

        var refused = _clusters.StoreSummary("k", "real summary", new[] { 0f, 1f, 0f }, tenantId: "");

        Assert.StartsWith("Error:", refused);
        Assert.Contains("non-summary entry", refused);
        Assert.Equal("squatter", _index.Get("summary:k", "test", tenantId: "")!.Text);
        Assert.True(_persistence.TryFlush());
        var clustersJson = File.ReadAllText(Path.Combine(_testDataPath, "_clusters.json"));
        Assert.DoesNotContain("summary:k", clustersJson);
    }

    /// <summary>
    /// Finding 3: summary reads validate OWNERSHIP before serving. A pointer left at a slot
    /// now occupied by a non-summary entry serves no summary — not the squatter's text — in
    /// both the cluster projection and the listing's summary bit.
    /// </summary>
    [Fact]
    public void GetCluster_PointerAtNonSummaryEntry_ServesNoSummary()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "real summary", new[] { 0f, 1f, 0f }, tenantId: ""));

        // The stored summary is deleted out from under the published pointer and a
        // non-summary entry takes the id — the pointer now dangles at a squatter.
        Assert.True(_index.Delete("summary:k", "test", tenantId: ""));
        _index.Upsert(new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "squatter", lifecycleState: "ltm"));

        var projection = _clusters.GetCluster("k", tenantId: "");
        Assert.NotNull(projection);
        Assert.Null(projection!.SummaryEntry);
        var listing = Assert.Single(_clusters.ListClusters("test", tenantId: ""));
        Assert.False(listing.HasSummary);
    }

    /// <summary>
    /// Finding 4: decay configs poisoned before partition-component validation existed must
    /// not brick lifecycle loading. Re-composing loaded rows through the validating
    /// PartitionKey threw on every lifecycle-touching call forever. Two poisoned shapes with
    /// different fates: an OVER-LONG tenant loads under its true (unaliasable) key, while a
    /// row carrying a partition CONTROL CHARACTER is dropped with a warning - its unchecked
    /// key would alias a key a valid caller can legitimately compose, letting the poisoned
    /// row silently govern (and absorb writes for) another tenant's namespace.
    /// </summary>
    [Fact]
    public void LifecycleEngine_PoisonedStoredConfigs_LoadOrDropWithoutBricking()
    {
        var longTenant = new string('t', 80); // over Tenancy's cap - Normalize throws on it
        var rowJson = JsonSerializer.Serialize(new DecayConfig("test", tenantId: "t"));
        Assert.Contains("\"tenantId\":\"t\"", rowJson);
        var goodRow = rowJson;
        var overLongRow = rowJson.Replace("\"tenantId\":\"t\"", $"\"tenantId\":\"{longTenant}\"");
        // ComposeKeyUnchecked("", "acme<US>notes") == PartitionKey("acme", "notes") - the alias.
        var aliasRow = rowJson
            .Replace("\"tenantId\":\"t\"", "\"tenantId\":\"\"")
            .Replace("\"ns\":\"test\"", "\"ns\":\"acme\\u001Fnotes\"");
        Assert.NotEqual(rowJson, overLongRow);
        Assert.NotEqual(rowJson, aliasRow);
        File.WriteAllText(Path.Combine(_testDataPath, "_decay_configs.json"),
            $"[{goodRow},{overLongRow},{aliasRow}]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        var engine = new LifecycleEngine(_index, persistence2);

        var configs = engine.GetAllDecayConfigs();
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.TenantId == "t");
        Assert.Contains(configs, c => c.TenantId == longTenant);
        // The alias row was dropped: tenant acme's namespace "notes" is NOT governed by it.
        Assert.Null(engine.GetDecayConfig("notes", tenantId: "acme"));
    }

    /// <summary>
    /// Finding 5: the record-namespace read is STORE-FIRST in both directions. A record
    /// retired by another stack must stop being reported even by the scanner whose cache
    /// still holds it warm — the strict miss path alone left every cache HIT stale forever.
    /// </summary>
    [Fact]
    public void GetCollapseRecordNs_StoreRetirement_OverridesAWarmCache()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        // The executing scanner's own cache is warm.
        Assert.Equal("test", _scanner.GetCollapseRecordNs(collapseId, tenantId: ""));

        // The record is retired out from under it — another stack's undo committing.
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var durable));
        Assert.Equal(CollapseRecordCas.Applied,
            _persistence.DeleteCollapseRecordSync(collapseId, durable!.Generation));

        Assert.Null(_scanner.GetCollapseRecordNs(collapseId, tenantId: ""));
    }

    /// <summary>
    /// Finding 6 (SQLite): a decay-config provider that throws is a REPORTED flush failure
    /// with the save retained — hoisted outside the writer's try, it escaped the flush as an
    /// exception and the pending save was gone. A replacement provider then flushes clean.
    /// </summary>
    [Fact]
    public void SqliteFlush_ThrowingDecayProvider_ReportsFalseInsteadOfThrowing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round16_sqlite_{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            provider.ScheduleSaveDecayConfigs(() => throw new InvalidOperationException("provider fault"));

            Assert.False(provider.TryFlush());

            provider.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig>());
            Assert.True(provider.TryFlush());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 6 (SQL Server): same class, same shape — the provider throw is contained by
    /// the writer and reported as a failed flush with the save retained, then a replacement
    /// provider flushes clean. Gated on ENGRAM_TEST_SQLSERVER_CONNECTION like every SQL
    /// Server integration test (the provider's constructor initializes schema eagerly, so no
    /// serverless variant exists).
    /// </summary>
    [Fact]
    public void SqlServerFlush_ThrowingDecayProvider_ReportsFalseInsteadOfThrowing()
    {
        var connectionString = Environment.GetEnvironmentVariable("ENGRAM_TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var schema = $"engram_r16_{Guid.NewGuid():N}"[..24];
        using var provider = new SqlServerStorageProvider(connectionString, schema: schema, debounceMs: 60_000);
        provider.ScheduleSaveDecayConfigs(() => throw new InvalidOperationException("provider fault"));

        Assert.False(provider.TryFlush());

        provider.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig>());
        Assert.True(provider.TryFlush());
    }
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => new[] { 0.9f, 0.1f, 0f };
    }

    /// <summary>
    /// Refuter finding (P2): the not-published reap deletes by the REVISION its own CAS
    /// installed, never by the lineage stamp. The stamp is not unique to a cluster object -
    /// a collapse retry re-creates its cluster with the record's reused stamp - so a stamped
    /// reap could take down the LIVE summary a successful retry published between this
    /// call's publish check and its reap.
    /// </summary>
    [Fact]
    public void SummaryReap_LineageStampReuse_SparesTheRetrysLiveSummary()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "first", tenantId: ""));
        Assert.True(_clusters.TryGetClusterStamp("k", "", out var stamp));

        _clusters.OnBeforeSummaryPublish = () =>
        {
            // An undo tears the lineage down in the CAS-to-publish window...
            _clusters.OnBeforeSummaryPublish = null;
            Assert.DoesNotContain("Error:", _clusters.UpdateCluster("k", addIds: null,
                removeIds: new List<string> { "x" }, label: null, tenantId: ""));
            _index.DeleteIfSummaryOf("summary:k", "test", "", "k", onlyIfStamp: stamp);
            Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty("k", tenantId: ""));
        };
        _clusters.OnBeforeSummaryReap = () =>
        {
            // ...and a retry-execute recreates the SAME lineage stamp and publishes ITS summary.
            _clusters.OnBeforeSummaryReap = null;
            _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
            Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "y" }, "retry",
                tenantId: "", creationStamp: stamp));
            Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "retry summary", new[] { 1f, 0f, 0f }, tenantId: ""));
        };

        var refused = _clusters.StoreSummary("k", "preempted summary", new[] { 0f, 1f, 0f }, tenantId: "");

        Assert.StartsWith("Error:", refused);
        Assert.Equal("retry summary", _index.Get("summary:k", "test", tenantId: "")!.Text);
    }

    /// <summary>
    /// Refuter finding (P2): the auto-summarize rescan HEALS a cluster whose summary is
    /// missing. Membership equality used to skip the whole iteration, so a summary lost to a
    /// crash window or a quota refusal was never re-stored by the only automatic path that
    /// ever stores it.
    /// </summary>
    [Fact]
    public void AutoSummarize_RescanHealsAMissingSummary()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var embedding = new StubEmbedding();

        var scan1 = _scanner.ScanNamespace("test", tenantId: "", autoSummarize: true,
            clusters: _clusters, embedding: embedding);
        var auto = Assert.Single(scan1.AutoSummaries!);
        Assert.NotNull(_index.Get(auto.SummaryId, "test", tenantId: ""));

        // The summary is lost (crash window / quota refusal); the cluster survives.
        Assert.True(_index.Delete(auto.SummaryId, "test", tenantId: ""));

        var scan2 = _scanner.ScanNamespace("test", tenantId: "", autoSummarize: true,
            clusters: _clusters, embedding: embedding);

        var healed = Assert.Single(scan2.AutoSummaries!);
        Assert.Equal(auto.ClusterId, healed.ClusterId);
        Assert.NotNull(_index.Get(healed.SummaryId, "test", tenantId: ""));
    }

    /// <summary>
    /// Refuter finding: a stored pre-validation tenant surfaced by GetAllTenants must not
    /// unwind the per-tenant maintenance loops - the enumeration guard contains the
    /// validating normalize's ArgumentException exactly like a failed enumeration.
    /// </summary>
    [Fact]
    public void RunDecayCycle_UnnormalizableStoredTenant_IsContainedNotThrown()
    {
        var poisoned = new string('t', 80);
        var result = _lifecycle.RunDecayCycle("*", tenantId: poisoned);
        Assert.Contains("*", result.FailedNamespaces!);
        // (RunConsolidationPass carries the identical guard; without a diffusion kernel it
        // early-returns before enumeration, so only the decay path is exercisable here.)
    }

    /// <summary>
    /// Refuter finding: a TryFlush that reaches the gate after Dispose refuses with false -
    /// racing the disposed drain event used to throw ObjectDisposedException into callers
    /// expecting a bool.
    /// </summary>
    [Fact]
    public void TryFlush_AfterDispose_RefusesInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"round16_disposed_{Guid.NewGuid():N}");
        try
        {
            var pm = new PersistenceManager(path, debounceMs: 60_000);
            pm.Dispose();
            Assert.False(pm.TryFlush());
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}

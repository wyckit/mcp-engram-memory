using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

/// <summary>
/// Tests for <see cref="TenantPrefixMigrationTool"/> — the one-shot migration that retires the
/// prefix-era tenant encoding ("{tenant}::{path}" namespaces, Conductor's T2-01 interim format) in
/// favor of the first-class <see cref="CognitiveEntry.TenantId"/> column/field introduced in
/// T1-06/T2-05. Uses <see cref="SqliteStorageProvider"/> as the seeded store: the tool operates
/// purely through the <see cref="IStorageProvider"/> abstraction and is backend-agnostic, but the
/// prefix convention relies on "::" surviving verbatim in the `ns` value — true for SQLite/SQL
/// Server (a real column) but not for the JSON file backend (`PersistenceManager`), which sanitizes
/// ":" out of on-disk filenames. Real prefix-era (Conductor T2-01) data lives in SQLite/SQL Server.
/// </summary>
public class TenantPrefixMigrationToolTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteStorageProvider _storage;
    private readonly TenantPrefixMigrationTool _tool;

    public TenantPrefixMigrationToolTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"tenant_migration_test_{Guid.NewGuid():N}", "memory.db");
        _storage = new SqliteStorageProvider(_testDbPath, debounceMs: 10);
        _tool = new TenantPrefixMigrationTool(_storage);
    }

    public void Dispose()
    {
        _storage.Dispose();
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_testDbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    private static CognitiveEntry Entry(string id, string ns, string? text = null)
        => new(id, new[] { 1f, 0f }, ns, text: text);

    private int TotalStoredEntries()
        => _storage.GetPersistedNamespaces().Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

    // ── TryParsePrefixedNamespace ──

    [Theory]
    [InlineData("tenant-a::work", true, "tenant-a", "work")]
    [InlineData("acme::patients::triage", true, "acme", "patients::triage")]
    [InlineData("bare-ns", false, "", "bare-ns")]
    [InlineData("::no-tenant", false, "", "::no-tenant")]
    [InlineData("no-path::", false, "", "no-path::")]
    public void TryParsePrefixedNamespace_ParsesExpected(string ns, bool expectSuccess, string expectedTenant, string expectedPath)
    {
        var result = TenantPrefixMigrationTool.TryParsePrefixedNamespace(ns, out var tenant, out var path);

        Assert.Equal(expectSuccess, result);
        Assert.Equal(expectedTenant, tenant);
        Assert.Equal(expectedPath, path);
    }

    // ── Forward migration: prefix-era rows ──

    [Fact]
    public void Migrate_PrefixedNamespace_SplitsIntoTenantAndPath()
    {
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData
        {
            Entries = [Entry("e1", "tenant-a::work", "hello")]
        });

        var manifest = _tool.Migrate();

        var loaded = _storage.LoadNamespace("work");
        Assert.Single(loaded.Entries);
        Assert.Equal("tenant-a", loaded.Entries[0].TenantId);
        Assert.Equal("work", loaded.Entries[0].Ns);
        Assert.Equal("hello", loaded.Entries[0].Text);

        // Old prefixed namespace is retired.
        Assert.Empty(_storage.LoadNamespace("tenant-a::work").Entries);
        Assert.DoesNotContain("tenant-a::work", _storage.GetPersistedNamespaces());

        Assert.True(manifest.RowCountParityOk);
        Assert.Equal(1, manifest.TotalEntriesBefore);
        Assert.Equal(1, manifest.TotalEntriesAfter);
    }

    [Fact]
    public void Migrate_MultipleTenantsSamePath_AllLandInSameNamespaceDisjointByTenant()
    {
        // Distinct ids: SQLite's (ns, id) primary key is intentionally NOT tenant-partitioned
        // (design doc §2.2 — tenant_id lives only in json_data on this backend), so two tenants
        // sharing both ns AND id at the destination is a SQL Server-only scenario (see the
        // ENGRAM_TEST_SQLSERVER_CONNECTION-gated collision test below).
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("a1", "tenant-a::work", "alpha")] });
        _storage.SaveNamespaceSync("tenant-b::work", new NamespaceData { Entries = [Entry("b1", "tenant-b::work", "bravo")] });

        _tool.Migrate();

        var loaded = _storage.LoadNamespace("work");
        Assert.Equal(2, loaded.Entries.Count);
        var a = loaded.Entries.Single(e => e.TenantId == "tenant-a");
        var b = loaded.Entries.Single(e => e.TenantId == "tenant-b");
        Assert.Equal("alpha", a.Text);
        Assert.Equal("bravo", b.Text);
    }

    [Fact]
    public void Migrate_SameIdAcrossTenantsAtSameDestination_SqliteBackendBoundary()
    {
        // Documents a known, deliberate backend boundary (design doc §2.2): SQLite's primary key
        // is (ns, id) only — not tenant-partitioned — so folding two prefixed namespaces that share
        // both the destination path AND an entry id is only safe on a tenant-partitioned backend
        // (SQL Server's (tenant_id, ns, id) PK). On SQLite this surfaces as a constraint violation
        // rather than silently corrupting data.
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("shared", "tenant-a::work", "alpha")] });
        _storage.SaveNamespaceSync("tenant-b::work", new NamespaceData { Entries = [Entry("shared", "tenant-b::work", "bravo")] });

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _tool.Migrate());
    }

    [Fact]
    public void Migrate_PrefixedNamespace_MergesIntoPreexistingBareNamespaceAtSamePath()
    {
        // A bare "work" namespace already has legacy ("") tenant content...
        _storage.SaveNamespaceSync("work", new NamespaceData { Entries = [Entry("legacy1", "work", "legacy")] });
        // ...and a prefixed source also targets the same physical path.
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("a1", "tenant-a::work", "alpha")] });

        var manifest = _tool.Migrate();

        var loaded = _storage.LoadNamespace("work");
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Contains(loaded.Entries, e => e.Id == "legacy1" && e.TenantId == "");
        Assert.Contains(loaded.Entries, e => e.Id == "a1" && e.TenantId == "tenant-a");
        Assert.True(manifest.RowCountParityOk);
    }

    // ── Forward migration: legacy bare-ns rows ──

    [Fact]
    public void Migrate_BareNamespace_NoDefault_LeavesEntryUntouched()
    {
        var original = Entry("b1", "conductor-memory", "bare");
        _storage.SaveNamespaceSync("conductor-memory", new NamespaceData { Entries = [original] });

        var manifest = _tool.Migrate();

        var loaded = _storage.LoadNamespace("conductor-memory");
        Assert.Single(loaded.Entries);
        Assert.Equal("", loaded.Entries[0].TenantId);
        Assert.Equal("conductor-memory", loaded.Entries[0].Ns);
        Assert.DoesNotContain(manifest.Records, r => r.Id == "b1");
    }

    [Fact]
    public void Migrate_BareNamespace_WithSuppliedDefault_StampsTenant()
    {
        _storage.SaveNamespaceSync("conductor-memory", new NamespaceData { Entries = [Entry("b1", "conductor-memory", "bare")] });

        var manifest = _tool.Migrate(defaultTenantId: "conductor-default");

        var loaded = _storage.LoadNamespace("conductor-memory");
        Assert.Single(loaded.Entries);
        Assert.Equal("conductor-default", loaded.Entries[0].TenantId);
        Assert.Equal("conductor-memory", loaded.Entries[0].Ns);

        var record = Assert.Single(manifest.Records);
        Assert.Equal(TenantMigrationKind.DefaultAssigned, record.Kind);
        Assert.Equal("", record.OriginalTenantId);
        Assert.Equal("conductor-default", record.NewTenantId);
    }

    // ── Mixed store: prefix-era + legacy rows together ──

    [Fact]
    public void Migrate_MixedStore_ParityHoldsAcrossWholeStore()
    {
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData
        {
            Entries = [Entry("a1", "tenant-a::work", "alpha-1"), Entry("a2", "tenant-a::work", "alpha-2")]
        });
        _storage.SaveNamespaceSync("tenant-b::personal", new NamespaceData
        {
            Entries = [Entry("b1", "tenant-b::personal", "bravo-1")]
        });
        _storage.SaveNamespaceSync("legacy-ns", new NamespaceData
        {
            Entries = [Entry("l1", "legacy-ns", "legacy-1"), Entry("l2", "legacy-ns", "legacy-2")]
        });

        var totalBefore = TotalStoredEntries();
        Assert.Equal(5, totalBefore);

        var manifest = _tool.Migrate();

        Assert.True(manifest.RowCountParityOk);
        Assert.Equal(5, manifest.TotalEntriesBefore);
        Assert.Equal(5, TotalStoredEntries());

        Assert.Equal(2, _storage.LoadNamespace("work").Entries.Count);
        Assert.All(_storage.LoadNamespace("work").Entries, e => Assert.Equal("tenant-a", e.TenantId));

        Assert.Single(_storage.LoadNamespace("personal").Entries);
        Assert.Equal("tenant-b", _storage.LoadNamespace("personal").Entries[0].TenantId);

        Assert.Equal(2, _storage.LoadNamespace("legacy-ns").Entries.Count);
        Assert.All(_storage.LoadNamespace("legacy-ns").Entries, e => Assert.Equal("", e.TenantId));
    }

    // ── Reversibility / round-trip ──

    [Fact]
    public void Migrate_ThenReverse_RestoresOriginalLayoutWithParity()
    {
        // Note: ids are distinct across tenants at the shared "work" destination — see
        // Migrate_SameIdAcrossTenantsAtSameDestination_SqliteBackendBoundary for why a
        // same-id collision at the same (ns) is a SQL Server-only scenario on this backend.
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData
        {
            Entries = [Entry("a1", "tenant-a::work", "alpha-1"), Entry("a2", "tenant-a::work", "alpha-shared")]
        });
        _storage.SaveNamespaceSync("tenant-b::work", new NamespaceData
        {
            Entries = [Entry("b-shared", "tenant-b::work", "bravo-shared")]
        });
        _storage.SaveNamespaceSync("work", new NamespaceData
        {
            Entries = [Entry("legacyWork", "work", "legacy-in-work")]
        });
        _storage.SaveNamespaceSync("bare-legacy", new NamespaceData
        {
            Entries = [Entry("l1", "bare-legacy", "bare-1")]
        });

        var totalBefore = TotalStoredEntries();

        var forward = _tool.Migrate();
        Assert.True(forward.RowCountParityOk);

        var reverse = _tool.Reverse(forward);
        Assert.True(reverse.RowCountParityOk);

        var totalAfterReverse = TotalStoredEntries();
        Assert.Equal(totalBefore, totalAfterReverse);

        // Original physical namespaces are back, with original tenant ids restored.
        var a = _storage.LoadNamespace("tenant-a::work");
        Assert.Equal(2, a.Entries.Count);
        Assert.All(a.Entries, e => Assert.Equal("", e.TenantId));
        Assert.Contains(a.Entries, e => e.Id == "a1" && e.Text == "alpha-1");
        Assert.Contains(a.Entries, e => e.Id == "a2" && e.Text == "alpha-shared");

        var b = _storage.LoadNamespace("tenant-b::work");
        Assert.Single(b.Entries);
        Assert.Equal("", b.Entries[0].TenantId);
        Assert.Equal("bravo-shared", b.Entries[0].Text);

        var work = _storage.LoadNamespace("work");
        Assert.Single(work.Entries);
        Assert.Equal("legacy-in-work", work.Entries[0].Text);
        Assert.Equal("", work.Entries[0].TenantId);

        var bare = _storage.LoadNamespace("bare-legacy");
        Assert.Single(bare.Entries);
        Assert.Equal("bare-1", bare.Entries[0].Text);
    }

    [Fact]
    public void Migrate_ThenReverse_WithDefaultTenant_RoundTrips()
    {
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("a1", "tenant-a::work", "alpha")] });
        _storage.SaveNamespaceSync("conductor-memory", new NamespaceData { Entries = [Entry("c1", "conductor-memory", "c-mem")] });

        var totalBefore = TotalStoredEntries();

        var forward = _tool.Migrate(defaultTenantId: "conductor-default");
        var reverse = _tool.Reverse(forward);

        Assert.Equal(totalBefore, TotalStoredEntries());

        var conductorMem = _storage.LoadNamespace("conductor-memory");
        Assert.Single(conductorMem.Entries);
        Assert.Equal("", conductorMem.Entries[0].TenantId); // restored to pre-default state

        var work = _storage.LoadNamespace("tenant-a::work");
        Assert.Single(work.Entries);
        Assert.Equal("", work.Entries[0].TenantId);

        Assert.True(reverse.RowCountParityOk);
    }

    [Fact]
    public void Migrate_ThenReverse_DoesNotDisturbUnrelatedEntriesAtSharedDestination()
    {
        // "work" pre-exists independently of any migration and must survive a reverse untouched.
        _storage.SaveNamespaceSync("work", new NamespaceData { Entries = [Entry("preexisting", "work", "pre")] });
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("a1", "tenant-a::work", "alpha")] });

        var forward = _tool.Migrate();
        var reverse = _tool.Reverse(forward);

        var work = _storage.LoadNamespace("work");
        Assert.Single(work.Entries);
        Assert.Equal("preexisting", work.Entries[0].Id);
        Assert.Equal("pre", work.Entries[0].Text);

        var tenantWork = _storage.LoadNamespace("tenant-a::work");
        Assert.Single(tenantWork.Entries);
        Assert.Equal("a1", tenantWork.Entries[0].Id);

        Assert.True(reverse.RowCountParityOk);
    }

    [Fact]
    public void Migrate_EmptyStore_IsNoOp()
    {
        var manifest = _tool.Migrate();

        Assert.Empty(manifest.Records);
        Assert.Equal(0, manifest.TotalEntriesBefore);
        Assert.Equal(0, manifest.TotalEntriesAfter);
        Assert.True(manifest.RowCountParityOk);
    }

    [Fact]
    public void Migrate_DryRun_MakesNoChanges()
    {
        _storage.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("a1", "tenant-a::work", "alpha")] });

        var manifest = _tool.Migrate(dryRun: true);

        Assert.NotEmpty(manifest.Records);
        Assert.True(manifest.RowCountParityOk);

        // Nothing actually moved.
        Assert.Empty(_storage.LoadNamespace("work").Entries);
        Assert.Single(_storage.LoadNamespace("tenant-a::work").Entries);
    }
}

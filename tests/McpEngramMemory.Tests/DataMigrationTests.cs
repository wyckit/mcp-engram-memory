using System.Security.Cryptography;
using System.Text;
using McpEngramMemory;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "engram-migration-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "old-install", "data");
    private string Destination => Path.Combine(_root, "durable", "memory");

    public DataMigrationTests() => Directory.CreateDirectory(Source);
    private static void ClosePool(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "memory.db")}");
        SqliteConnection.ClearPool(connection);
    }
    public void Dispose()
    {
        ClosePool(Source);
        ClosePool(Destination);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DefaultPath_IsIndependentOfInstallationAndExplicitPathIsPreserved()
    {
        Assert.Equal(Path.Combine(_root, "engram-memory"), ServerDataPaths.Resolve(null, _root));
        Assert.Equal(Path.GetFullPath(Destination), ServerDataPaths.Resolve(Destination, "ignored"));
        Assert.Throws<ArgumentException>(() => ServerDataPaths.Resolve(" ", _root));
        Assert.Throws<InvalidOperationException>(() => ServerDataPaths.Resolve(null, ""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copy_PreservesPersistedTenantTwinsAndOriginalStore(bool sqlite)
    {
        IStorageProvider Open(string root) => sqlite
            ? new SqliteStorageProvider(Path.Combine(root, "memory.db"))
            : new PersistenceManager(root);
        using (var old = Open(Source))
        {
            old.ScheduleSave("project", () => new NamespaceData
            {
                Entries = new()
                {
                    new CognitiveEntry("same-id", new[] { 1f, 0f }, "project", "tenant-a fact", tenantId: "a", lifecycleState: "ltm"),
                    new CognitiveEntry("same-id", new[] { 0f, 1f }, "project", "tenant-b fact", tenantId: "b", lifecycleState: "ltm")
                }
            });
            old.Flush();
        }
        ClosePool(Source);
        Directory.CreateDirectory(Path.Combine(Source, "governance"));
        File.WriteAllText(Path.Combine(Source, "governance", "audit.json"), "{\"decision\":\"preserved\"}");
        var before = DataMigration.Snapshot(Source);
        Assert.Throws<InvalidOperationException>(() => ServerDataPaths.CheckLegacyData(Destination, Source));

        Assert.Equal("migrated", DataMigration.Copy(Source, Destination));
        Assert.Equal(before, DataMigration.Snapshot(Source));
        Assert.Equal("already_migrated", DataMigration.Copy(Source, Destination));
        ServerDataPaths.CheckLegacyData(Destination, Source);
        using (var migrated = Open(Destination))
        {
            var entries = migrated.LoadNamespace("project").Entries;
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.TenantId == "a" && e.Text == "tenant-a fact");
            Assert.Contains(entries, e => e.TenantId == "b" && e.Text == "tenant-b fact");
        }
        Assert.Equal("{\"decision\":\"preserved\"}", File.ReadAllText(Path.Combine(Destination, "governance", "audit.json")));
    }

    [Theory]
    [InlineData("not json", false)]
    [InlineData("{}", true)]
    public void InvalidSource_IsNotPublishedAndOriginalIsUntouched(string text, bool badChecksum)
    {
        var path = Path.Combine(Source, "memory.json");
        File.WriteAllText(path, text);
        if (badChecksum) File.WriteAllText(path + ".sha256", new string('0', 64));
        Assert.ThrowsAny<Exception>(() => DataMigration.Copy(Source, Destination));
        Assert.False(Directory.Exists(Destination));
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Destination)!, ".engram-migration-*"));
    }

    [Fact]
    public void ChecksummedSource_IsValidatedAndRetryNeverOverwritesNewData()
    {
        var path = Path.Combine(Source, "memory.json");
        File.WriteAllText(path, "{}");
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("{}"))));
        DataMigration.Copy(Source, Destination);
        File.WriteAllText(Path.Combine(Destination, "new.json"), "{\"new\":true}");
        Assert.Equal("already_migrated", DataMigration.Copy(Source, Destination));
        Assert.True(File.Exists(Path.Combine(Destination, "new.json")));
        File.WriteAllText(path, "{\"changed\":true}");
        Assert.Throws<IOException>(() => DataMigration.Copy(Source, Destination));
        Assert.Throws<InvalidOperationException>(() => ServerDataPaths.CheckLegacyData(Destination, Source));
    }

    [Fact]
    public void ExistingDestination_IsNeverMergedOrOverwritten()
    {
        File.WriteAllText(Path.Combine(Source, "memory.json"), "{}");
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "existing.json"), "{}");
        Assert.Throws<IOException>(() => DataMigration.Copy(Source, Destination));
        Assert.False(File.Exists(Path.Combine(Destination, "memory.json")));
    }

    [Fact]
    public void OverlappingPaths_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => DataMigration.Copy(Source, Source));
        Assert.Throws<ArgumentException>(() => DataMigration.Copy(Source, Path.Combine(Source, "nested")));
        Assert.Throws<ArgumentException>(() => DataMigration.Copy(Source, _root));
    }

    [Fact]
    public void LockedSource_IsNotCopied()
    {
        var path = Path.Combine(Source, "memory.json");
        File.WriteAllText(path, "{}");
        using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => DataMigration.Copy(Source, Destination));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public void EmptySource_DoesNotCreateAnApparentlyMigratedStore()
    {
        Assert.Throws<InvalidOperationException>(() => DataMigration.Copy(Source, Destination));
        Assert.False(Directory.Exists(Destination));
        ServerDataPaths.CheckLegacyData(Destination, Source);
    }

    [Fact]
    public void MigrationCommand_RequiresExplicitStoppedServerAcknowledgment()
    {
        Assert.Equal(1, DataCommands.Run(new[] { "migrate-data", "--source", Source, "--destination", Destination }));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public void SourceSetup_DetectsLegacyStoreAndRequiresStoppedWriters()
    {
        var legacy = Path.Combine(_root, "src", "McpEngramMemory", "bin", "Debug", "net8.0", "data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "namespace.json"), "{}");
        var args = new[] { "prepare-data", "--repo", _root, "--destination", Destination };
        Assert.Equal(1, DataCommands.Run(args));
        Assert.False(Directory.Exists(Destination));
        Assert.Equal(0, DataCommands.Run(args.Append("--server-stopped").ToArray()));
        Assert.Equal(0, DataCommands.Run(args));
        Assert.True(File.Exists(Path.Combine(legacy, "namespace.json")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Destination));
    }

    [Fact]
    public void SourceSetup_RequiresSelectionForMultipleLegacyStores()
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(_root, "src", "McpEngramMemory", "bin", configuration, "net8.0", "data");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "namespace.json"), "{}");
        }
        Assert.Equal(1, DataCommands.Run(new[] { "prepare-data", "--repo", _root,
            "--destination", Destination, "--server-stopped" }));
        Assert.False(Directory.Exists(Destination));
    }
}

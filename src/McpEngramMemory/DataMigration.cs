using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory;

internal static class DataMigration
{
    internal const string ManifestName = "_engram-migration.json";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record Manifest(string Source, Dictionary<string, string> Files);

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);

    private static bool ContainsPath(string parent, string child) => SamePath(parent, child) ||
        Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
            Path.DirectorySeparatorChar, PathComparison);

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Migration does not follow symbolic links or junctions: '{current.FullName}'.");
    }

    internal static Dictionary<string, string> Snapshot(string directory)
    {
        RejectLinks(directory);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string parent)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Migration does not follow symbolic links or junctions: '{path}'.");
                if ((attributes & FileAttributes.Directory) != 0) { Visit(path); continue; }
                // A source manifest describes an earlier migration, not a memory namespace.
                if (SamePath(path, Path.Combine(directory, ManifestName))) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                result[Path.GetRelativePath(directory, path)] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        Visit(directory);
        return result;
    }

    private static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var hash) && hash == kv.Value);

    internal static bool WasMigrated(string source, string destination, Dictionary<string, string> snapshot)
    {
        var path = Path.Combine(destination, ManifestName);
        if (!File.Exists(path)) return false;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
        return manifest is not null && SamePath(manifest.Source, source) && Equal(manifest.Files, snapshot);
    }

    internal static string Copy(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        RejectLinks(source);
        RejectLinks(destination);
        if (ContainsPath(source, destination) || ContainsPath(destination, source))
            throw new ArgumentException("Source and destination must be separate, non-overlapping directories.");
        var before = Snapshot(source);
        if (before.Count == 0) throw new InvalidOperationException("The source directory contains no data to migrate.");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (WasMigrated(source, destination, before)) return "already_migrated";
            throw new IOException("The destination already exists. Migration never merges or overwrites a store; choose a new directory.");
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".engram-migration-" + Guid.NewGuid().ToString("N"));
        ServerDataPaths.CreatePrivateDirectory(staging);
        try
        {
            foreach (var (relative, expected) in before)
            {
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = new FileStream(Path.Combine(source, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!Equal(before, Snapshot(staging)) || !Equal(before, Snapshot(source)))
                throw new IOException("Source data changed during migration. Stop every writer and retry; the destination was not installed.");

            ValidateCopy(staging, before.Keys);
            File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(new Manifest(source, before)));
            if (!Equal(before, Snapshot(source)))
                throw new IOException("Source data changed during validation. Stop every writer and retry.");
            // Atomic publication; a concurrently created destination causes failure, never replacement.
            Directory.Move(staging, destination);
            return "migrated";
        }
        finally
        {
            // Only this invocation's generated staging directory can be removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void ValidateCopy(string directory, IEnumerable<string> files)
    {
        foreach (var relative in files)
        {
            var path = Path.Combine(directory, relative);
            if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                var checksum = path + ".sha256";
                if (File.Exists(checksum) && !string.Equals(File.ReadAllText(checksum).Trim(),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Checksum mismatch: '{relative}'. Restore a valid backup before migrating.");
            }
            if (!relative.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) continue;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidDataException($"SQLite integrity check failed: '{relative}'.");
        }
    }
}

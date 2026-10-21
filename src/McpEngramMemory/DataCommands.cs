using System.Text.Json;

namespace McpEngramMemory;

internal static class DataCommands
{
    internal static int Run(string[] args)
    {
        try
        {
            var root = ServerDataPaths.Resolve(Environment.GetEnvironmentVariable("MEMORY_DATA_PATH"));
            if (args is ["data-path"] or ["data-path", "--plain"])
            {
                Console.WriteLine(args.Length == 2 ? root : JsonSerializer.Serialize(new { dataPath = root }));
                return 0;
            }
            string? source = null;
            string? repository = null;
            var destination = root;
            var stopped = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--server-stopped") { stopped = true; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException("Missing migration option value.");
                switch (args[i])
                {
                    case "--source": source = args[++i]; break;
                    case "--destination": destination = args[++i]; break;
                    case "--repo": repository = args[++i]; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            if (args[0] == "prepare-data")
            {
                if (source is null && repository is not null)
                {
                    var bin = Path.Combine(repository, "src", "McpEngramMemory", "bin");
                    var candidates = Directory.Exists(bin)
                        ? Directory.GetDirectories(bin).Select(p => Path.Combine(p, "net8.0", "data"))
                            .Where(Directory.Exists).Where(p => DataMigration.Snapshot(p).Count > 0).ToArray()
                        : Array.Empty<string>();
                    if (candidates.Length > 1)
                        throw new InvalidOperationException("Multiple legacy stores found. Select one with --source / -LegacyData / --legacy-data; automatic merging is unsafe.");
                    source = candidates.SingleOrDefault();
                }
                if (source is null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "ready", dataPath = destination }));
                    return 0;
                }
                if (DataMigration.WasMigrated(source, destination, DataMigration.Snapshot(source))) return 0;
            }
            if (source is null || !stopped)
                throw new ArgumentException("Stop all servers using the source, then run migrate-data --source <directory> [--destination <new-directory>] --server-stopped. The source is preserved.");
            var status = DataMigration.Copy(source, destination);
            Console.WriteLine(JsonSerializer.Serialize(new { status, dataPath = Path.GetFullPath(destination) }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

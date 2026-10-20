namespace McpEngramMemory;

internal static class ServerDataPaths
{
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static string Resolve(string? configured, string? localApplicationData = null)
    {
        if (configured is not null)
        {
            if (string.IsNullOrWhiteSpace(configured))
                throw new ArgumentException("MEMORY_DATA_PATH must not be empty.");
            return Path.GetFullPath(configured);
        }

        var parent = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Cannot locate the user data directory. Set MEMORY_DATA_PATH explicitly.");
        return Path.GetFullPath(Path.Combine(parent, "engram-memory"));
    }

    internal static void CheckLegacyData(string destination, string legacyDirectory)
    {
        if (!Directory.Exists(legacyDirectory) || DataMigration.SamePath(destination, legacyDirectory)) return;
        var files = DataMigration.Snapshot(legacyDirectory);
        if (files.Count == 0 || DataMigration.WasMigrated(legacyDirectory, destination, files)) return;
        throw new InvalidOperationException(
            $"Existing memory data was found at '{legacyDirectory}'. Stop the old server and run " +
            $"'engram-memory migrate-data --source <old-data-directory> --destination <durable-directory> --server-stopped' " +
            $"before switching stores. The default durable directory is '{destination}'. " +
            "For a 2.0 tool installation use scripts/upgrade-tool.py BEFORE dotnet tool update; " +
            "the package manager removes the old version directory. No data was changed.");
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace McpEngramMemory;

internal static class ClientConfiguration
{
    internal sealed record Options(string Server, string? Profile, string? Storage, string DataPath, string? AgentId, bool ReplaceDataPath = false);
    internal sealed record Edit(string Path, byte[]? Original, string Updated);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TomlSerializerOptions TomlOptions = new() { DuplicateKeyHandling = TomlDuplicateKeyHandling.Error };

    internal static Edit Prepare(string path, bool toml, Options options)
    {
        if (options.Profile is not (null or "minimal" or "standard" or "full")) throw new ArgumentException("Unknown tool profile.");
        if (options.Storage is not (null or "json" or "sqlite" or "sqlserver")) throw new ArgumentException("Unknown storage backend.");
        if (!File.Exists(options.Server)) throw new FileNotFoundException("Server assembly not found.", options.Server);
        var original = ReadConfiguration(path);
        var text = original is null ? "" : Utf8.GetString(original).TrimStart('\uFEFF');
        return new Edit(System.IO.Path.GetFullPath(path), original,
            toml ? UpdateToml(text, options) : UpdateJson(text, options));
    }

    private static Dictionary<string, string> EnvironmentValues(Options options, Func<string, bool> contains)
    {
        var values = new Dictionary<string, string>();
        if (options.Profile is not null || !contains("MEMORY_TOOL_PROFILE"))
            values["MEMORY_TOOL_PROFILE"] = options.Profile ?? "minimal";
        if (options.Storage is not null || !contains("MEMORY_STORAGE"))
            values["MEMORY_STORAGE"] = options.Storage ?? "json";
        if (options.ReplaceDataPath || !contains("MEMORY_DATA_PATH"))
            values["MEMORY_DATA_PATH"] = System.IO.Path.GetFullPath(options.DataPath);
        if (!string.IsNullOrEmpty(options.AgentId)) values["AGENT_ID"] = options.AgentId;
        return values;
    }

    internal static string UpdateJson(string text, Options options)
    {
        JsonObject root;
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = JsonDocument.Parse(text);
                ValidateJsonKeys(document.RootElement);
            }
            root = string.IsNullOrWhiteSpace(text) ? new() : JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { throw new InvalidDataException("Invalid JSON configuration; the file was not changed."); }
        var servers = Object(root, "mcpServers");
        var entry = Object(servers, "engram-memory");
        var environment = Object(entry, "env");
        entry["type"] = "stdio";
        entry["command"] = "dotnet";
        entry["args"] = new JsonArray(System.IO.Path.GetFullPath(options.Server));
        foreach (var (key, value) in EnvironmentValues(options, environment.ContainsKey)) environment[key] = value;
        return root.ToJsonString(JsonOptions) + "\n";
    }

    private static void ValidateJsonKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate configuration key.");
                ValidateJsonKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) ValidateJsonKeys(value);
    }

    private static JsonObject Object(JsonObject parent, string key)
    {
        if (!parent.ContainsKey(key)) parent[key] = new JsonObject();
        return parent[key] as JsonObject ?? throw new InvalidDataException($"Configuration field '{key}' must be an object; the file was not changed.");
    }

    private static TomlTable Table(TomlTable parent, string key)
    {
        if (!parent.ContainsKey(key)) parent[key] = new TomlTable();
        return parent[key] as TomlTable ?? throw new InvalidDataException($"Configuration field '{key}' must be a table; the file was not changed.");
    }

    private static string Key(BareKeyOrStringValueSyntax key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax quoted => quoted.Value!,
        _ => throw new InvalidDataException("Unsupported TOML key.")
    };

    private static string[] Keys(KeySyntax key) => new[] { Key(key.Key!) }.Concat(key.DotKeys.Select(k => Key(k.Key!))).ToArray();
    private static bool IsEngram(IEnumerable<string> keys) => keys.Take(2).SequenceEqual(new[] { "mcp_servers", "engram-memory" });

    internal static string UpdateToml(string text, Options options)
    {
        TomlTable root;
        try { root = TomlSerializer.Deserialize<TomlTable>(text, TomlOptions) ?? new(); }
        catch (TomlException) { throw new InvalidDataException("Invalid TOML configuration; the file was not changed."); }
        var servers = Table(root, "mcp_servers");
        var entry = Table(servers, "engram-memory");
        var environment = Table(entry, "env");
        entry["command"] = "dotnet";
        entry["args"] = new TomlArray { System.IO.Path.GetFullPath(options.Server) };
        foreach (var (key, value) in EnvironmentValues(options, environment.ContainsKey)) environment[key] = value;

        // Edit actual parsed table/key paths, so quoted keys, multiline strings and
        // similarly named servers cannot be mistaken for the Engram table.
        var syntax = SyntaxParser.Parse(text, validate: true);
        if (syntax.HasErrors) throw new InvalidDataException("Invalid TOML syntax; the file was not changed.");
        foreach (var item in syntax.KeyValues.ToArray())
            if (IsEngram(Keys(item.Key!))) syntax.KeyValues.RemoveChild(item);
        foreach (var table in syntax.Tables.ToArray())
        {
            var prefix = Keys(table.Name!);
            if (IsEngram(prefix)) { syntax.Tables.RemoveChild(table); continue; }
            foreach (var item in table.Items.ToArray())
                if (IsEngram(prefix.Concat(Keys(item.Key!)))) table.Items.RemoveChild(item);
        }
        var replacementSyntax = SyntaxParser.Parse(TomlSerializer.Serialize(new TomlTable
        {
            ["mcp_servers"] = new TomlTable { ["engram-memory"] = entry }
        }));
        foreach (var table in replacementSyntax.Tables.ToArray())
            if (Keys(table.Name!).SequenceEqual(new[] { "mcp_servers" })) replacementSyntax.Tables.RemoveChild(table);
        var replacement = replacementSyntax.ToString().Trim() + "\n";
        var updated = syntax.ToString().TrimEnd() + "\n\n" + replacement;
        // Inline parent tables cannot be extended with a later table declaration.
        // Serialize the complete semantic model in that case; the original formatting
        // remains in the mandatory backup and all unrelated values are retained.
        try
        {
            var parsed = TomlSerializer.Deserialize<TomlTable>(updated, TomlOptions)!;
            if (!SyntaxParser.Parse(updated, validate: true).HasErrors && Equivalent(parsed, root)) return updated;
        }
        catch (TomlException) { }
        var serialized = TomlSerializer.Serialize(root);
        if (SyntaxParser.Parse(serialized, validate: true).HasErrors)
            throw new InvalidDataException("Could not produce valid TOML; the file was not changed.");
        return serialized;
    }

    private static bool Equivalent(object? left, object? right)
    {
        if (left is TomlTable a && right is TomlTable b)
            return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) && Equivalent(kv.Value, value));
        if (left is System.Collections.IEnumerable x && right is System.Collections.IEnumerable y && left is not string && right is not string)
        {
            var aItems = x.Cast<object?>().ToArray();
            var bItems = y.Cast<object?>().ToArray();
            return aItems.Length == bItems.Length && aItems.Zip(bItems).All(pair => Equivalent(pair.First, pair.Second));
        }
        return Equals(left, right);
    }

    internal static string? Commit(Edit edit)
    {
        var current = ReadConfiguration(edit.Path);
        if (!(current is null && edit.Original is null) &&
            (current is null || edit.Original is null || !current.SequenceEqual(edit.Original)))
            throw new IOException("Configuration changed after validation; retry without overwriting the other writer.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(edit.Path)!);
        var temporary = edit.Path + ".engram-" + Guid.NewGuid().ToString("N") + ".tmp";
        string? backup = null;
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, fileOptions))
            {
                stream.Write(Utf8.GetBytes(edit.Updated));
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, edit.Original is null
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(edit.Path));
            if (edit.Original is not null)
            {
                backup = edit.Path + ".engram-backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N");
                File.Replace(temporary, edit.Path, backup);
            }
            else File.Move(temporary, edit.Path);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[]? ReadConfiguration(string path)
    {
        // Replacing a symlink would silently detach a managed dotfile. Ask the owner
        // to edit its real target instead, retaining both the link and its contents.
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Configuration is a symbolic link; update its target explicitly: '{path}'.");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    internal static int Run(string[] args)
    {
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--root" or "--for" or "--server" or "--profile" or "--storage" or "--data-path" or "--agent-id" or "--replace-data-path"))
                    throw new ArgumentException("Invalid configure-client option.");
                options.Add(args[i], args[i + 1]);
            }
            var root = options.GetValueOrDefault("--root") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var data = options.GetValueOrDefault("--data-path") ?? ServerDataPaths.Resolve(Environment.GetEnvironmentVariable("MEMORY_DATA_PATH"));
            var settings = new Options(options["--server"], options.GetValueOrDefault("--profile"),
                options.GetValueOrDefault("--storage"), data, options.GetValueOrDefault("--agent-id"),
                options.GetValueOrDefault("--replace-data-path") == "true");
            var targets = options.GetValueOrDefault("--for", "claude");
            var names = targets == "all" ? new[] { "claude", "gemini", "codex" } : targets.Split(',').Select(s => s.Trim().ToLowerInvariant()).Distinct().ToArray();
            var edits = names.Select(name => name switch
            {
                "claude" => Prepare(System.IO.Path.Combine(root, ".claude.json"), false, settings),
                "gemini" => Prepare(System.IO.Path.Combine(root, ".gemini", "settings.json"), false, settings),
                "codex" => Prepare(System.IO.Path.Combine(root, ".codex", "config.toml"), true, settings),
                _ => throw new ArgumentException($"Unknown client: {name}")
            }).ToArray();
            // Parse every selected config before writing the first one.
            foreach (var edit in edits)
                Console.WriteLine(JsonSerializer.Serialize(new { config = edit.Path, backup = Commit(edit) }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

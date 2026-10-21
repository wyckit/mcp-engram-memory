using System.Text.Json.Nodes;
using McpEngramMemory;
using Tomlyn;
using Tomlyn.Model;

namespace McpEngramMemory.Tests;

public sealed class ClientConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "engram-config-tests-" + Guid.NewGuid().ToString("N"));
    private ClientConfiguration.Options Options => new(typeof(ClientConfiguration).Assembly.Location, "full", "sqlite", Path.Combine(_root, "data"), null);
    public ClientConfigurationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false, "{\"preferences\":true,")]
    [InlineData(false, "[]")]
    [InlineData(false, "{\"duplicate\":1,\"duplicate\":2}")]
    [InlineData(false, "{\"mcpServers\":42}")]
    [InlineData(false, "{\"mcpServers\":{\"engram-memory\":{\"env\":false}}}")]
    [InlineData(true, "[broken")]
    [InlineData(true, "duplicate = 1\nduplicate = 2")]
    [InlineData(true, "[duplicate]\na = 1\n[duplicate]\nb = 2")]
    [InlineData(true, "[mcp_servers]\nengram-memory = 42")]
    [InlineData(true, "[mcp_servers.engram-memory]\nenv = false")]
    public void InvalidConfiguration_IsNotChangedOrBackedUp(bool toml, string original)
    {
        var path = Path.Combine(_root, "config");
        File.WriteAllText(path, original);
        Assert.Throws<InvalidDataException>(() => ClientConfiguration.Prepare(path, toml, Options));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void JsonUpdate_PreservesUnrelatedSettingsOverridesAndBackup()
    {
        const string original = """
            {"preferences":{"theme":"dark"},"mcpServers":{"other":{"command":"sentinel"},
            "engram-memory":{"enabled":false,"env":{"AGENT_ID":"existing-agent","MEMORY_SQLITE_PATH":"/durable/existing.db","MEMORY_GOVERNANCE_PATH":"/durable/governance"}}}}
            """;
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, original);
        var backup = ClientConfiguration.Commit(ClientConfiguration.Prepare(path, false, Options));
        Assert.Equal(original, File.ReadAllText(backup!));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("dark", (string?)json["preferences"]!["theme"]);
        Assert.Equal("sentinel", (string?)json["mcpServers"]!["other"]!["command"]);
        var entry = json["mcpServers"]!["engram-memory"]!;
        Assert.False((bool)entry["enabled"]!);
        Assert.Equal("existing-agent", (string?)entry["env"]!["AGENT_ID"]);
        Assert.Equal("/durable/existing.db", (string?)entry["env"]!["MEMORY_SQLITE_PATH"]);
        Assert.Equal("/durable/governance", (string?)entry["env"]!["MEMORY_GOVERNANCE_PATH"]);
        Assert.Equal("full", (string?)entry["env"]!["MEMORY_TOOL_PROFILE"]);
        Assert.Equal(typeof(ClientConfiguration).Assembly.Location, (string?)entry["args"]![0]);
    }

    [Theory]
    [InlineData("mcp_servers.engram-memory")]
    [InlineData("mcp_servers.\"engram-memory\"")]
    [InlineData("mcp_servers.'engram-memory'")]
    public void TomlUpdate_UsesExactKeysAndPreservesCommentsAndMultilineValues(string header)
    {
        var original = """
            # keep this preference
            model = "sentinel"
            prompt = '''
            [mcp_servers.engram-memory]
            this is text, not a table
            '''
            [mcp_servers.engram-memory-helper]
            command = "preserve-me"
            """ + $"\n[{header}]\ncommand = \"old\"\nenabled = false\n[{header}.env]\nMEMORY_SQLITE_PATH = '/durable/existing.db'\nAGENT_ID = 'existing'\n";
        var updated = ClientConfiguration.UpdateToml(original, Options);
        Assert.Contains("# keep this preference", updated);
        var root = TomlSerializer.Deserialize<TomlTable>(updated)!;
        Assert.Equal("sentinel", root["model"]);
        Assert.Contains("this is text, not a table", (string)root["prompt"]!);
        var servers = (TomlTable)root["mcp_servers"]!;
        Assert.Equal("preserve-me", ((TomlTable)servers["engram-memory-helper"]!)["command"]);
        var entry = (TomlTable)servers["engram-memory"]!;
        Assert.Equal(false, entry["enabled"]);
        Assert.Equal("/durable/existing.db", ((TomlTable)entry["env"]!)["MEMORY_SQLITE_PATH"]);
        Assert.Equal("existing", ((TomlTable)entry["env"]!)["AGENT_ID"]);
        var repeated = ClientConfiguration.UpdateToml(updated, Options);
        Assert.Equal(updated, repeated);
    }

    [Theory]
    [InlineData("mcp_servers = { other = { command = 'keep' }, engram-memory = { command = 'old' } }")]
    [InlineData("mcp_servers.other.command = 'keep'\nmcp_servers.engram-memory.command = 'old'")]
    [InlineData("[mcp_servers]\nother = { command = 'keep' }\nengram-memory = { command = 'old' }")]
    public void InlineAndDottedToml_PreserveOtherServers(string original)
    {
        var updated = ClientConfiguration.UpdateToml(original, Options);
        var servers = (TomlTable)TomlSerializer.Deserialize<TomlTable>(updated)!["mcp_servers"]!;
        Assert.Equal("keep", ((TomlTable)servers["other"]!)["command"]);
        Assert.Equal("dotnet", ((TomlTable)servers["engram-memory"]!)["command"]);
    }

    [Fact]
    public void ConcurrentConfigChange_IsNotOverwritten()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "{}");
        var edit = ClientConfiguration.Prepare(path, false, Options);
        File.WriteAllText(path, "{\"new\":true}");
        Assert.Throws<IOException>(() => ClientConfiguration.Commit(edit));
        Assert.Equal("{\"new\":true}", File.ReadAllText(path));
    }

    [Fact]
    public void OmittedSelections_PreserveExistingProfileBackendAndDataPath()
    {
        var options = Options with { Profile = null, Storage = null };
        const string json = """
            {"mcpServers":{"engram-memory":{"env":{"MEMORY_TOOL_PROFILE":"standard","MEMORY_STORAGE":"sqlite","MEMORY_DATA_PATH":"/custom/data"}}}}
            """;
        var updated = JsonNode.Parse(ClientConfiguration.UpdateJson(json, options))!;
        var environment = updated["mcpServers"]!["engram-memory"]!["env"]!;
        Assert.Equal("standard", (string?)environment["MEMORY_TOOL_PROFILE"]);
        Assert.Equal("sqlite", (string?)environment["MEMORY_STORAGE"]);
        Assert.Equal("/custom/data", (string?)environment["MEMORY_DATA_PATH"]);
        const string toml = "[mcp_servers.engram-memory.env]\nMEMORY_TOOL_PROFILE='standard'\nMEMORY_STORAGE='sqlite'\nMEMORY_DATA_PATH='/custom/data'";
        var table = (TomlTable)((TomlTable)((TomlTable)TomlSerializer.Deserialize<TomlTable>(ClientConfiguration.UpdateToml(toml, options))!["mcp_servers"]!)["engram-memory"]!)["env"]!;
        Assert.Equal("/custom/data", table["MEMORY_DATA_PATH"]);
        Assert.Equal("standard", table["MEMORY_TOOL_PROFILE"]);
        Assert.Equal("sqlite", table["MEMORY_STORAGE"]);
    }

    [Fact]
    public void AllClientConfigs_AreParsedBeforeTheFirstWrite()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini"));
        File.WriteAllText(Path.Combine(_root, ".gemini", "settings.json"), "invalid");
        Assert.Equal(1, ClientConfiguration.Run(new[] { "configure-client", "--root", _root, "--for", "all", "--server", Options.Server, "--data-path", Options.DataPath }));
        Assert.False(File.Exists(Path.Combine(_root, ".claude.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".codex")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshConfigurationAndRepeatedUpdate_AreValidAndBackedUp(bool toml)
    {
        var path = Path.Combine(_root, "nested", "config");
        Assert.Null(ClientConfiguration.Commit(ClientConfiguration.Prepare(path, toml, Options)));
        var first = File.ReadAllText(path);
        var backup = ClientConfiguration.Commit(ClientConfiguration.Prepare(path, toml, Options));
        Assert.Equal(first, File.ReadAllText(backup!));
        Assert.Equal(first, File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}

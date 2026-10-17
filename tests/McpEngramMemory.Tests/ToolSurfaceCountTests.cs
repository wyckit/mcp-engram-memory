using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tests;

/// <summary>
/// Counts the SDK tool registrations used by Program.cs, then checks published claims.
/// No tool invocation, model initialization, network access, or installer execution is needed.
/// </summary>
public class ToolSurfaceCountTests
{
    [Theory]
    [InlineData("minimal", 17)]
    [InlineData("standard", 39)]
    [InlineData("full", 63)]
    public void Profile_exposes_documented_tool_count(string profile, int expectedCount)
    {
        var names = RegisteredToolNames(profile);
        Assert.Equal(expectedCount, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unrecognized_profile_retains_minimal_surface(string profile) =>
        Assert.Equal(RegisteredToolNames("minimal"), RegisteredToolNames(profile));

    [Fact]
    public void Every_tool_in_assembly_is_registered_in_full_profile()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(CoreMemoryTools).Assembly);
        using var provider = services.BuildServiceProvider();
        var discovered = provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(discovered);
        Assert.Equal(discovered, RegisteredToolNames("full"));
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("standard")]
    [InlineData("full")]
    public void Profile_tables_and_configuration_docs_match_registrations(string profile)
    {
        int count = RegisteredToolNames(profile).Length;
        string tablePattern = $@"^\| `{profile}` \| (?<count>\d+) \|";
        foreach (string path in new[] { "README.md", "docs/cheat-sheet.md", "GEMINI.md", "examples/AGENTS.md", "examples/CLAUDE.md" })
            AssertPublishedCount(path, tablePattern, count);
        string configPattern = $@"`{profile}` \((?<count>\d+)";
        AssertPublishedCount("README.md", configPattern, count);
        AssertPublishedCount("docs/services.md", configPattern, count);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("standard")]
    [InlineData("full")]
    public void Bash_installer_counts_match_registrations(string profile)
    {
        string pattern = profile == "full"
            ? @"^TOOL_COUNT=(?<count>\d+)\s*$"
            : $@"^\[\[ ""\$PROFILE"" == ""{profile}"" +\]\] && TOOL_COUNT=(?<count>\d+)\s*$";
        AssertPublishedCount("setup.sh", pattern, RegisteredToolNames(profile).Length);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("standard")]
    [InlineData("full")]
    public void PowerShell_installer_counts_match_registrations(string profile)
    {
        string label = profile == "full" ? "default" : $"\"{profile}\"";
        string pattern = @"^\$toolCount = switch \(\$Profile\).*" + Regex.Escape(label) + @" \{ (?<count>\d+) \}";
        AssertPublishedCount("setup.ps1", pattern, RegisteredToolNames(profile).Length);
    }

    [Theory]
    [InlineData("README.md", @"\| MCP tools \| (?<count>\d+)")]
    [InlineData("README.md", @"The (?<count>\d+) MCP tools")]
    [InlineData("README.md", @"Full documentation for all (?<count>\d+) tools")]
    [InlineData("docs/mcp-tools-reference.md", @"# MCP Tools Reference \((?<count>\d+) tools\)")]
    [InlineData("docs/cognitive-constitution.md", @"The (?<count>\d+) MCP tools")]
    [InlineData("docs/core-10.md", @"Engram Memory exposes (?<count>\d+) MCP tools")]
    [InlineData("images/banner.svg", @">(?<count>\d+) MCP tools<")]
    public void Full_surface_claims_match_registrations(string path, string pattern) =>
        AssertPublishedCount(path, pattern, RegisteredToolNames("full").Length);

    [Fact]
    public void Readme_profile_summary_and_program_comment_match_registrations()
    {
        var profiles = new[] { "minimal", "standard", "full" };
        var counts = profiles.Select(profile => RegisteredToolNames(profile).Length);
        Assert.Contains($"(profiles: {string.Join(" / ", counts)})", ReadRepositoryFile("README.md"));
        AssertPublishedCount("src/McpEngramMemory/Program.cs",
            "// +\"minimal\" +→ (?<count>\\d+) tools", RegisteredToolNames("minimal").Length);
        AssertPublishedCount("docs/core-10.md",
            @"`minimal` shows (?<count>\d+)", RegisteredToolNames("minimal").Length);
    }

    [Fact]
    public void Reference_lists_every_registered_tool_once()
    {
        var names = Regex.Matches(ReadRepositoryFile("docs/mcp-tools-reference.md"),
                @"^\| `(?<name>[a-z][a-z0-9_]*)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(RegisteredToolNames("full"), names);
    }

    private static string[] RegisteredToolNames(string profile)
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithStdioServerTransport().WithMemoryToolProfile(profile);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AssertPublishedCount(string path, string pattern, int expected)
    {
        var matches = Regex.Matches(ReadRepositoryFile(path), pattern, RegexOptions.Multiline);
        Assert.True(matches.Count > 0, $"No tool count found in {path} matching {pattern}");
        foreach (Match match in matches)
        {
            int actual = int.Parse(match.Groups["count"].Value);
            Assert.True(actual == expected, $"{path}: '{match.Value.Trim()}' claims {actual} tools; SDK registers {expected}.");
        }
    }

    private static string ReadRepositoryFile(string path, [CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "McpEngramMemory.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, path));
    }
}

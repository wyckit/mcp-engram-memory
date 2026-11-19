using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpEngramMemory;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tests;

[CollectionDefinition("SecurityEnvVarTests", DisableParallelization = true)]
public sealed class SecurityEnvVarTestsCollection;

/// <summary>
/// Regression tests for the 2.1.x security hardening pass:
///   1. NamespaceRegistry ACL hardening (JSON grant serialization + legacy parse,
///      co-grantee redaction, Share/Unshare legacy-default boundary, PermissionEntryId collision guard)
///   2. Exhaustive ConstitutionMcpFilter mapping across all 63 MCP tools + fail-closed unknown tools
///   3. Benchmark &amp; MRCR tool hardening (ClaudeCliModelClient resolver, CLI RCE, Ollama SSRF,
///      datasetId/artifact/dataset path traversal &amp; confinement)
///   4. LifecycleTools.DeepRecall vector-dimension guard
///   5. MemoryDiffusionTools.ComputeDiffusionBasis non-mutating read authorization
///   6. Visualizer DOM XSS escaping &amp; null/finite safety
/// </summary>
public sealed class SecurityFixesTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly StubEmbeddingService _embedding = new();
    private readonly NamespaceRegistry _registry;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private sealed class RecordingModelClientFactory : IAgentOutcomeModelClientFactory
    {
        public int CreateCalls { get; private set; }
        public string? LastProvider { get; private set; }
        public string? LastEndpoint { get; private set; }

        public IAgentOutcomeModelClient Create(string provider, string? endpoint = null)
        {
            CreateCalls++;
            LastProvider = provider;
            LastEndpoint = endpoint;
            return new StubModelClient();
        }
    }

    private sealed class StubModelClient : IAgentOutcomeModelClient
    {
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string?> GenerateAsync(
            string model,
            string prompt,
            int maxTokens = 320,
            float temperature = 0.1f,
            CancellationToken ct = default)
            => Task.FromResult<string?>("ALPHA-77");

        public void Dispose() { }
    }

    public SecurityFixesTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"security_fixes_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 1. NamespaceRegistry ACL Hardening
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NamespaceRegistry_ShareWithDelimiterInjectionInAgentId_CannotForgeOrCorruptGrants()
    {
        _registry.EnsureOwnership("work", "alice", tenantId: "");

        // Semicolon + colon injection attempting to smuggle a write grant for "mallory".
        var r1 = _registry.Share("work", "alice", "bob;mallory:write", "read", tenantId: "");
        Assert.Equal("shared", r1.Status);

        // Colon injection attempting to escalate "eve" to write via agentId parsing.
        var r2 = _registry.Share("work", "alice", "eve:write", "read", tenantId: "");
        Assert.Equal("shared", r2.Status);

        // Forged identities must have ZERO access.
        Assert.False(_registry.HasAccess("mallory", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("mallory", "work", "write", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("eve", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("eve", "work", "write", tenantId: ""));

        // Exact literal grantee strings retain only the granted read level.
        Assert.True(_registry.HasAccess("bob;mallory:write", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("bob;mallory:write", "work", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("eve:write", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("eve:write", "work", "write", tenantId: ""));

        // Verify JSON storage format and persistence round-trip across index reload.
        var rawPerm = _index.Get(NamespaceRegistry.PermissionEntryId("work", ""), NamespaceRegistry.SystemNamespace, tenantId: "");
        Assert.NotNull(rawPerm);
        var grantsJson = rawPerm!.Metadata["grants"];
        Assert.StartsWith("[", grantsJson);
        using var doc = JsonDocument.Parse(grantsJson);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        _persistence.Flush();
        using var reloadedPersistence = new PersistenceManager(_dataPath, debounceMs: 10);
        using var reloadedIndex = new CognitiveIndex(reloadedPersistence);
        var reloadedRegistry = new NamespaceRegistry(reloadedIndex, _embedding);

        Assert.False(reloadedRegistry.HasAccess("mallory", "work", "read", tenantId: ""));
        Assert.False(reloadedRegistry.HasAccess("eve", "work", "write", tenantId: ""));
        Assert.True(reloadedRegistry.HasAccess("bob;mallory:write", "work", "read", tenantId: ""));
        Assert.False(reloadedRegistry.HasAccess("bob;mallory:write", "work", "write", tenantId: ""));
    }

    [Fact]
    public void NamespaceRegistry_ParseGrants_BackwardCompatibleWithLegacyPersistedStrings()
    {
        // Seed a legacy pre-2.1.x permission record using semicolon/colon encoding.
        var legacyEntry = new CognitiveEntry(
            id: "perm_legacy-ns",
            vector: _embedding.Embed("namespace permission legacy-ns"),
            ns: NamespaceRegistry.SystemNamespace,
            text: "Namespace 'legacy-ns' owned by 'alice'",
            category: "ns-permission",
            metadata: new Dictionary<string, string>
            {
                ["ns"] = "legacy-ns",
                ["tenantId"] = "",
                ["owner"] = "alice",
                ["grants"] = "bob:read;carol:write;dave"
            },
            lifecycleState: "ltm");
        _index.Upsert(legacyEntry);

        Assert.True(_registry.HasAccess("alice", "legacy-ns", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("bob", "legacy-ns", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", "legacy-ns", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("carol", "legacy-ns", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("dave", "legacy-ns", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("dave", "legacy-ns", "write", tenantId: ""));

        // Edge case: legacy grant starting with '[' falls back cleanly instead of throwing.
        var bracketLegacy = NamespaceRegistry.ParseGrants("[legacy-agent]:write");
        var only = Assert.Single(bracketLegacy);
        Assert.Equal("[legacy-agent]", only.AgentId);
        Assert.Equal("write", only.AccessLevel);

        // Mutating the legacy permission record preserves existing grants and upgrades to JSON.
        Assert.Equal("shared", _registry.Share("legacy-ns", "alice", "erin", "read", tenantId: "").Status);
        Assert.True(_registry.HasAccess("bob", "legacy-ns", "read", tenantId: ""));
        Assert.True(_registry.HasAccess("carol", "legacy-ns", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("erin", "legacy-ns", "read", tenantId: ""));
        var upgraded = _index.Get("perm_legacy-ns", NamespaceRegistry.SystemNamespace, tenantId: "")!;
        Assert.StartsWith("[", upgraded.Metadata["grants"]);
    }

    [Fact]
    public void NamespaceRegistry_GetAccessibleNamespaces_RedactsCoGranteesForNonOwners()
    {
        _registry.EnsureOwnership("shared-proj", "alice", tenantId: "");
        _registry.Share("shared-proj", "alice", "bob", "read", tenantId: "");
        _registry.Share("shared-proj", "alice", "carol", "write", tenantId: "");
        _registry.Share("shared-proj", "alice", AgentIdentity.DefaultAgentId, "read", tenantId: "");

        // Bob (non-owner) must see only his own grant — never carol or default.
        var bobView = _registry.GetAccessibleNamespaces("bob", tenantId: "");
        var bobPerm = Assert.Single(bobView.SharedNamespaces, p => p.Namespace == "shared-proj");
        Assert.Equal("alice", bobPerm.Owner);
        var bobGrant = Assert.Single(bobPerm.SharedWith);
        Assert.Equal("bob", bobGrant.AgentId);
        Assert.Equal("read", bobGrant.AccessLevel);

        // Verify via MultiAgentTools.ListShared and WhoAmI wire surfaces too.
        var bobTools = new MultiAgentTools(_index, _embedding, new MetricsCollector(), _registry, new AgentIdentity("bob"));
        var listSharedJson = JsonSerializer.Serialize(bobTools.ListShared());
        Assert.DoesNotContain("carol", listSharedJson);
        Assert.Contains("bob", listSharedJson);

        // Legacy default agent in legacy tenant "" retains full co-grantee visibility for backward compat.
        var defaultLegacyView = _registry.GetAccessibleNamespaces(AgentIdentity.DefaultAgentId, tenantId: "");
        var defaultPerm = Assert.Single(defaultLegacyView.SharedNamespaces, p => p.Namespace == "shared-proj");
        Assert.Equal(3, defaultPerm.SharedWith.Count);

        // In a non-empty tenant, "default" is NOT legacy-unisolated and must also be redacted.
        _registry.EnsureOwnership("tenant-proj", "alice", tenantId: "tenant-a");
        _registry.Share("tenant-proj", "alice", AgentIdentity.DefaultAgentId, "read", tenantId: "tenant-a");
        _registry.Share("tenant-proj", "alice", "carol", "write", tenantId: "tenant-a");
        var defaultTenantView = _registry.GetAccessibleNamespaces(AgentIdentity.DefaultAgentId, tenantId: "tenant-a");
        var tenantPerm = Assert.Single(defaultTenantView.SharedNamespaces, p => p.Namespace == "tenant-proj");
        var onlyGrant = Assert.Single(tenantPerm.SharedWith);
        Assert.Equal(AgentIdentity.DefaultAgentId, onlyGrant.AgentId);
    }

    [Fact]
    public void NamespaceRegistry_ShareAndUnshare_ConsistentLegacyDefaultPolicyAndTenantBoundary()
    {
        // Legacy tenant (""): default agent has legacy-unisolated admin override on both Share and Unshare.
        _registry.EnsureOwnership("legacy-work", "alice", tenantId: "");
        Assert.Equal("shared", _registry.Share("legacy-work", AgentIdentity.DefaultAgentId, "bob", "read", tenantId: "").Status);
        Assert.True(_registry.HasAccess("bob", "legacy-work", "read", tenantId: ""));
        Assert.Equal("unshared", _registry.Unshare("legacy-work", AgentIdentity.DefaultAgentId, "bob", tenantId: "").Status);
        Assert.False(_registry.HasAccess("bob", "legacy-work", "read", tenantId: ""));

        // Non-empty tenant ("tenant-a"): "default" is NOT legacy-unisolated and cannot Share or Unshare Alice's namespace.
        _registry.EnsureOwnership("tenant-work", "alice", tenantId: "tenant-a");
        Assert.Equal("shared", _registry.Share("tenant-work", "alice", "bob", "read", tenantId: "tenant-a").Status);

        Assert.Equal("error_not_owner", _registry.Share("tenant-work", AgentIdentity.DefaultAgentId, "mallory", "write", tenantId: "tenant-a").Status);
        Assert.Equal("error_not_owner", _registry.Unshare("tenant-work", AgentIdentity.DefaultAgentId, "bob", tenantId: "tenant-a").Status);
        Assert.False(_registry.HasAccess("mallory", "tenant-work", "read", tenantId: "tenant-a"));
        Assert.True(_registry.HasAccess("bob", "tenant-work", "read", tenantId: "tenant-a"));
    }

    [Fact]
    public void NamespaceRegistry_PermissionEntryId_GuardsAgainstLegacyTenantPrefixCollision()
    {
        const string tenantId = "tenant-a";
        const string victimNs = "work";
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..16];
        var collidingLegacyNs = $"t_{tenantHash}_{victimNs}";

        // Existing persisted IDs remain byte-for-byte stable.
        Assert.Equal("perm_work", NamespaceRegistry.PermissionEntryId("work", ""));
        Assert.Equal($"perm_t_{tenantHash}_work", NamespaceRegistry.PermissionEntryId(victimNs, tenantId));

        // Colliding legacy namespace receives a disjoint entry ID.
        var collidingId = NamespaceRegistry.PermissionEntryId(collidingLegacyNs, "");
        Assert.NotEqual(NamespaceRegistry.PermissionEntryId(victimNs, tenantId), collidingId);
        Assert.StartsWith("legacy_perm_", collidingId);

        // End-to-end: legacy principal claiming collidingLegacyNs cannot overwrite or hijack tenant-a's ACL.
        _registry.EnsureOwnership(victimNs, "alice", tenantId: tenantId);
        _registry.EnsureOwnership(collidingLegacyNs, "mallory", tenantId: "");
        Assert.Equal("shared", _registry.Share(collidingLegacyNs, "mallory", "eve", "write", tenantId: "").Status);

        Assert.True(_registry.HasAccess("alice", victimNs, "write", tenantId: tenantId));
        Assert.False(_registry.HasAccess("mallory", victimNs, "read", tenantId: tenantId));
        Assert.False(_registry.HasAccess("eve", victimNs, "read", tenantId: tenantId));

        Assert.True(_registry.HasAccess("mallory", collidingLegacyNs, "write", tenantId: ""));
        Assert.True(_registry.HasAccess("eve", collidingLegacyNs, "write", tenantId: ""));
        Assert.False(_registry.HasAccess("alice", collidingLegacyNs, "read", tenantId: ""));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. Exhaustive ConstitutionMcpFilter Mapping & Fail-Closed Unknown Tools
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ConstitutionMcpFilter_ExhaustivelyMapsAll63RegisteredMcpTools()
    {
        var toolMethods = typeof(CoreMemoryTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        Assert.Equal(63, toolMethods.Length);
        Assert.Equal(63, ConstitutionMcpFilter.ToolOperationMap.Count);

        var registeredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in toolMethods)
        {
            var attr = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var name = attr.Name!;
            registeredNames.Add(name);

            Assert.True(ConstitutionMcpFilter.TryMapOperation(name, out var kind),
                $"MCP tool '{name}' must be explicitly mapped in ConstitutionMcpFilter.");
            Assert.Equal(kind, ConstitutionMcpFilter.MapOperation(name));

            // Every non-ReadOnly tool must map to a non-ReadMemory operation (recall/deep_recall map to Retrieve).
            if (!attr.ReadOnly)
            {
                Assert.NotEqual(CognitiveOperationKind.ReadMemory, kind);
            }
        }

        // No stale or misspelled keys in ToolOperationMap.
        Assert.Equal(registeredNames, ConstitutionMcpFilter.ToolOperationMap.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("merge_memories")]
    [InlineData("link_memories")]
    [InlineData("unlink_memories")]
    [InlineData("create_cluster")]
    [InlineData("update_cluster")]
    [InlineData("store_cluster_summary")]
    [InlineData("promote_memory")]
    [InlineData("memory_feedback")]
    [InlineData("uncollapse_cluster")]
    [InlineData("invalidate_diffusion")]
    [InlineData("collapse_cluster")]
    [InlineData("dismiss_collapse")]
    [InlineData("run_benchmark")]
    [InlineData("run_agent_outcome_benchmark")]
    [InlineData("run_live_agent_outcome_benchmark")]
    [InlineData("reset_metrics")]
    [InlineData("run_mrcr_benchmark")]
    [InlineData("consult_expert_panel")]
    [InlineData("map_debate_graph")]
    [InlineData("resolve_debate")]
    [InlineData("rebuild_embeddings")]
    [InlineData("create_expert")]
    [InlineData("link_to_parent")]
    public async Task ConstitutionMcpFilter_PreviouslyUnmappedMutatingTools_DeniedUnderReadOnlyOverlay(string toolName)
    {
        var kind = ConstitutionMcpFilter.MapOperation(toolName);
        Assert.Contains(kind, new[]
        {
            CognitiveOperationKind.WriteMemory,
            CognitiveOperationKind.DeleteMemory,
            CognitiveOperationKind.AdministerGovernance
        });

        using var provider = BuildConstitutionServices(readOnlyOverlay: true, out var audit);
        bool nextCalled = false;

        var result = await ConstitutionMcpFilter.InvokeAsync(
            provider,
            toolName,
            arguments: null,
            requestId: "req-1",
            next: _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult(new CallToolResult());
            });

        Assert.False(nextCalled);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("operation-not-allowed", text);
        var records = await audit.ReadAllAsync();
        Assert.Single(records);
        Assert.Equal(ConstitutionOutcome.Deny, records[0].Outcome);
    }

    [Theory]
    [InlineData("unknown_future_tool")]
    [InlineData("delete_everything")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConstitutionMcpFilter_UnknownTool_FailsClosedAndAuditsDenial(string unknownTool)
    {
        Assert.False(ConstitutionMcpFilter.TryMapOperation(unknownTool, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConstitutionMcpFilter.MapOperation(unknownTool));

        using var provider = BuildConstitutionServices(readOnlyOverlay: false, out var audit);
        bool nextCalled = false;

        var result = await ConstitutionMcpFilter.InvokeAsync(
            provider,
            unknownTool,
            arguments: null,
            requestId: "req-unknown",
            next: _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult(new CallToolResult());
            });

        Assert.False(nextCalled);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("unmapped-mcp-tool", text);

        var records = await audit.ReadAllAsync();
        var record = Assert.Single(records);
        Assert.Equal(ConstitutionOutcome.Deny, record.Outcome);
        Assert.Contains("unmapped-mcp-tool", record.FindingCodes);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. Benchmark & MRCR Tool Hardening (RCE, SSRF, Path Traversal)
    // ═════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("claude-cli", "/bin/sh")]
    [InlineData("claude-cli", "cmd.exe")]
    [InlineData("claude-cli", "powershell.exe")]
    [InlineData("claude-cli", "C:\\Windows\\System32\\cmd.exe")]
    [InlineData("claude-cli", "./claude")]
    [InlineData("claude-cli", "claude.cmd")]
    [InlineData("codex-cli", "/usr/bin/python3")]
    [InlineData("gemini-cli", "bash")]
    public async Task BenchmarkTools_And_Factory_RejectCustomCliExecutableEndpointsByDefault(
        string provider, string maliciousEndpoint)
    {
        // Core factory defense-in-depth rejects arbitrary CLI executables.
        var factory = new AgentOutcomeModelClientFactory();
        Assert.Throws<ArgumentException>(() => factory.Create(provider, maliciousEndpoint));

        // BenchmarkTools rejects BEFORE calling the model client factory.
        var recordingFactory = new RecordingModelClientFactory();
        var benchTools = CreateBenchmarkTools(recordingFactory);
        var liveResult = await benchTools.RunLiveAgentOutcomeBenchmark(
            model: "sonnet",
            provider: provider,
            endpoint: maliciousEndpoint,
            persistArtifact: false);

        Assert.Equal("error", liveResult.Status);
        Assert.Contains("not permitted", liveResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, recordingFactory.CreateCalls);

        // MrcrBenchmarkTools rejects BEFORE loading dataset or calling the model client factory.
        var mrcrTools = CreateMrcrBenchmarkTools(recordingFactory);
        var mrcrResult = await mrcrTools.RunMrcrBenchmark(
            model: "sonnet",
            provider: provider,
            endpoint: maliciousEndpoint,
            persistArtifact: false);

        Assert.Equal("error", mrcrResult.Status);
        Assert.Contains("not permitted", mrcrResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, recordingFactory.CreateCalls);
    }

    [Theory]
    [InlineData("claude-cli", null)]
    [InlineData("claude-cli", "claude")]
    [InlineData("codex-cli", "codex")]
    [InlineData("gemini-cli", "gemini")]
    public void AgentOutcomeModelClientFactory_AllowsDefaultCanonicalCliNames(string provider, string? endpoint)
    {
        var factory = new AgentOutcomeModelClientFactory();
        using var client = factory.Create(provider, endpoint);
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1:11434")]
    [InlineData("http://192.168.1.50:11434")]
    [InlineData("http://evil.example.com:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://[::ffff:169.254.169.254]:11434")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://localhost:11434")]
    [InlineData("http://localhost@169.254.169.254:11434")]
    [InlineData("not-a-valid-url")]
    public async Task BenchmarkTools_And_Factory_BlockOllamaSsrf(string maliciousEndpoint)
    {
        var factory = new AgentOutcomeModelClientFactory();
        Assert.Throws<ArgumentException>(() => factory.Create("ollama", maliciousEndpoint));

        var recordingFactory = new RecordingModelClientFactory();
        var benchTools = CreateBenchmarkTools(recordingFactory);
        var liveResult = await benchTools.RunLiveAgentOutcomeBenchmark(
            model: "qwen2.5:3b",
            provider: "ollama",
            endpoint: maliciousEndpoint,
            persistArtifact: false);

        Assert.Equal("error", liveResult.Status);
        Assert.Equal(0, recordingFactory.CreateCalls);

        var mrcrTools = CreateMrcrBenchmarkTools(recordingFactory);
        var mrcrResult = await mrcrTools.RunMrcrBenchmark(
            model: "qwen2.5:3b",
            provider: "ollama",
            endpoint: maliciousEndpoint,
            persistArtifact: false);

        Assert.Equal("error", mrcrResult.Status);
        Assert.Equal(0, recordingFactory.CreateCalls);
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("https://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void AgentOutcomeModelClientFactory_AllowsLoopbackOllamaEndpoints(string loopbackEndpoint)
    {
        var factory = new AgentOutcomeModelClientFactory();
        using var client = factory.Create("ollama", loopbackEndpoint);
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("../../pwn")]
    [InlineData("..\\..\\pwn")]
    [InlineData("sub/dataset")]
    [InlineData("sub\\dataset")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("evil:stream")]
    [InlineData("")]
    public async Task MrcrBenchmarkTools_RejectsPathTraversalInDatasetId(string badDatasetId)
    {
        var recordingFactory = new RecordingModelClientFactory();
        var mrcrTools = CreateMrcrBenchmarkTools(recordingFactory);
        string datasetPath = WriteSyntheticMrcrDataset("valid.jsonl");
        string artifactRoot = Path.Combine(_dataPath, "mrcr-out");

        var result = await mrcrTools.RunMrcrBenchmark(
            model: "sonnet",
            datasetId: badDatasetId,
            datasetPath: datasetPath,
            provider: "ollama",
            endpoint: "http://127.0.0.1:11434",
            artifactDirectory: artifactRoot);

        Assert.Equal("error", result.Status);
        Assert.Null(result.ArtifactPath);
        Assert.Equal(0, recordingFactory.CreateCalls);
    }

    [Fact]
    public async Task BenchmarkTools_And_MrcrTools_RejectTraversalAndUnconfinedPaths()
    {
        var recordingFactory = new RecordingModelClientFactory();
        var benchTools = CreateBenchmarkTools(recordingFactory);
        var mrcrTools = CreateMrcrBenchmarkTools(recordingFactory);

        // Compute a path guaranteed to be outside benchmarks and temp directories.
        string rootDir = Path.GetPathRoot(Environment.SystemDirectory) ?? "/";
        string outsideDir = Path.Combine(rootDir, "engram-unconfined-test-dir");
        string outsideFile = Path.Combine(outsideDir, "secret.json");

        // 1. artifactDirectory traversal and out-of-root checks.
        var aoTraversal = benchTools.RunAgentOutcomeBenchmark(artifactDirectory: "../../evil-out");
        Assert.Equal("error", aoTraversal.Status);
        Assert.Contains("..", aoTraversal.Message);

        var aoOutside = benchTools.RunAgentOutcomeBenchmark(artifactDirectory: outsideDir);
        Assert.Equal("error", aoOutside.Status);

        var liveTraversal = await benchTools.RunLiveAgentOutcomeBenchmark(
            model: "qwen2.5:3b",
            artifactDirectory: "../evil-live");
        Assert.Equal("error", liveTraversal.Status);
        Assert.Equal(0, recordingFactory.CreateCalls);

        // 2. CompareLiveAgentOutcomeArtifacts & CheckForRegression path checks.
        var validTempFile = Path.Combine(_dataPath, "valid.json");
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(validTempFile, "{}");

        Assert.Equal("error", benchTools.CompareLiveAgentOutcomeArtifacts("../../etc/passwd", validTempFile).Status);
        Assert.Equal("error", benchTools.CompareLiveAgentOutcomeArtifacts(validTempFile, outsideFile).Status);
        Assert.Equal("error", benchTools.CheckForRegression(outsideFile, validTempFile).Status);

        // 3. MrcrBenchmarkTools datasetPath, artifactDirectory, and CompareMrcrArtifacts checks.
        var mrcrBadDataset = await mrcrTools.RunMrcrBenchmark(
            model: "sonnet",
            datasetPath: "../../etc/passwd",
            provider: "ollama",
            persistArtifact: false);
        Assert.Equal("error", mrcrBadDataset.Status);
        Assert.Equal(0, recordingFactory.CreateCalls);

        var mrcrOutsideDataset = await mrcrTools.RunMrcrBenchmark(
            model: "sonnet",
            datasetPath: outsideFile,
            provider: "ollama",
            persistArtifact: false);
        Assert.Equal("error", mrcrOutsideDataset.Status);
        Assert.Equal(0, recordingFactory.CreateCalls);

        Assert.Equal("error", mrcrTools.CompareMrcrArtifacts("../../secret.json", validTempFile).Status);
        Assert.Equal("error", mrcrTools.CompareMrcrArtifacts(validTempFile, outsideFile).Status);

        // 4. Positive control: valid MRCR run with confined temp datasetPath + artifactDirectory succeeds.
        string datasetPath = WriteSyntheticMrcrDataset("good.jsonl");
        string artifactRoot = Path.Combine(_dataPath, "mrcr-artifacts");
        var okResult = await mrcrTools.RunMrcrBenchmark(
            model: "qwen2.5:3b",
            datasetId: "mrcr-safe-v1",
            datasetPath: datasetPath,
            provider: "ollama",
            endpoint: "http://localhost:11434",
            limit: 1,
            artifactDirectory: artifactRoot);

        Assert.Equal("completed", okResult.Status);
        Assert.NotNull(okResult.ArtifactPath);
        Assert.True(File.Exists(okResult.ArtifactPath));
        Assert.StartsWith(Path.GetFullPath(artifactRoot), okResult.ArtifactPath!, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. LifecycleTools.DeepRecall Vector-Dimension Guard
    // ═════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    public void LifecycleTools_DeepRecall_MismatchedVectorDimensions_ReturnsCleanError(int badDims)
    {
        _index.Upsert(new CognitiveEntry("a", [1f, 0f], "test", "archived entry", lifecycleState: "archived"));
        var access = new NamespaceAccess(_registry, AgentIdentity.Default);
        var tools = new LifecycleTools(_lifecycle, _embedding, _index, access);

        var mismatchedVector = Enumerable.Repeat(0.5f, badDims).ToArray();
        var result = tools.DeepRecall("test", vector: mismatchedVector, minScore: 0f);

        var error = Assert.IsType<string>(result);
        Assert.StartsWith("Error:", error);
        Assert.Contains("2 dimensions", error);
        Assert.Contains($"got {badDims}", error);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. MemoryDiffusionTools.ComputeDiffusionBasis Non-Mutating Read Auth
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MemoryDiffusionTools_ComputeDiffusionBasis_DoesNotClaimEmptyNamespace()
    {
        var kernel = new MemoryDiffusionKernel(_index, _graph);
        var bobAccess = new NamespaceAccess(_registry, new AgentIdentity("bob"));
        var bobTools = new MemoryDiffusionTools(kernel, bobAccess);

        // Calling a ReadOnly tool on an unclaimed empty namespace must return null and MUST NOT
        // write an ownership record into _system_sharing.
        Assert.Null(bobTools.ComputeDiffusionBasis("unclaimed-empty-ns", force: false));
        Assert.Null(bobTools.ComputeDiffusionBasis("unclaimed-empty-ns", force: true));

        Assert.Null(_index.Get(
            NamespaceRegistry.PermissionEntryId("unclaimed-empty-ns", ""),
            NamespaceRegistry.SystemNamespace,
            tenantId: ""));
        Assert.False(_registry.HasAccess("bob", "unclaimed-empty-ns", "read", tenantId: ""));

        // Alice can still legitimately claim the untouched empty namespace via a write.
        Assert.True(_registry.HasAccess("alice", "unclaimed-empty-ns", "write", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", "unclaimed-empty-ns", "write", tenantId: ""));
    }

    [Fact]
    public void MemoryDiffusionTools_ComputeDiffusionBasis_ReadGranteeCanComputeButNotForceInvalidate()
    {
        const string ns = "alice-spectral";
        SeedQualifyingDiffusionGraph(ns);
        _registry.EnsureOwnership(ns, "alice", tenantId: "");
        _registry.Share(ns, "alice", "bob", "read", tenantId: "");

        var kernel = new MemoryDiffusionKernel(_index, _graph);
        var aliceTools = new MemoryDiffusionTools(kernel, new NamespaceAccess(_registry, new AgentIdentity("alice")));
        var bobTools = new MemoryDiffusionTools(kernel, new NamespaceAccess(_registry, new AgentIdentity("bob")));
        var eveTools = new MemoryDiffusionTools(kernel, new NamespaceAccess(_registry, new AgentIdentity("eve")));

        // Unauthorized agent gets null for both force=false and force=true.
        Assert.Null(eveTools.ComputeDiffusionBasis(ns, force: false));
        Assert.Null(eveTools.ComputeDiffusionBasis(ns, force: true));

        // Read-only grantee can compute the cached diffusion basis without force.
        var bobStats = bobTools.ComputeDiffusionBasis(ns, topK: 8, force: false);
        Assert.NotNull(bobStats);
        Assert.True(bobStats!.NodeCount >= 32);

        // Read-only grantee CANNOT force-invalidate the cached basis; owner's cached stats remain intact.
        Assert.Null(bobTools.ComputeDiffusionBasis(ns, topK: 8, force: true));
        Assert.NotNull(aliceTools.DiffusionStats(ns));

        // Owner CAN force-recompute.
        var aliceForced = aliceTools.ComputeDiffusionBasis(ns, topK: 8, force: true);
        Assert.NotNull(aliceForced);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 6. Visualizer DOM XSS & Null Safety
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MemoryGraphHtml_EscapesAllTooltipFieldsAndGuardsNullProperties()
    {
        var html = ReadRepositoryFile("visualization/memory-graph.html");

        // Extract showNodeTip and showEdgeTip bodies.
        var nodeTipMatch = Regex.Match(html, @"function showNodeTip\(event, d\)\s*\{[\s\S]*?\n\}");
        var edgeTipMatch = Regex.Match(html, @"function showEdgeTip\(event, d\)\s*\{[\s\S]*?\n\}");
        Assert.True(nodeTipMatch.Success, "showNodeTip function not found.");
        Assert.True(edgeTipMatch.Success, "showEdgeTip function not found.");

        string nodeTip = nodeTipMatch.Value;
        string edgeTip = edgeTipMatch.Value;

        // Every attacker-controllable field in showNodeTip is escaped via escHtml(...)
        Assert.Contains("escHtml(d?.id)", nodeTip);
        Assert.Contains("escHtml(d?.ns)", nodeTip);
        Assert.Contains("escHtml(d.category)", nodeTip);
        Assert.Contains("escHtml(state.toUpperCase())", nodeTip);
        Assert.DoesNotContain("${d.id}", nodeTip);
        Assert.DoesNotContain("${d.ns}", nodeTip);
        Assert.DoesNotContain("${d.category}", nodeTip);
        Assert.DoesNotContain("${d.lifecycleState", nodeTip);

        // Every attacker-controllable field in showEdgeTip is escaped via escHtml(...)
        Assert.Contains("escHtml(sourceId)", edgeTip);
        Assert.Contains("escHtml(targetId)", edgeTip);
        Assert.Contains("escHtml(relation)", edgeTip);
        Assert.DoesNotContain("${d.source.id}", edgeTip);
        Assert.DoesNotContain("${d.target.id}", edgeTip);
        Assert.DoesNotContain("${d.relation}", edgeTip);

        // escHtml handles null/undefined and escapes single quotes too.
        Assert.Contains("String(s ?? '')", html);
        Assert.Contains("&#39;", html);

        // Null/fallback guards for activationEnergy, weight, lifecycleState, and data.clusters.
        Assert.Contains("Array.isArray(data?.clusters)", html);
        Assert.Contains("safeFiniteNumber(d?.activationEnergy", nodeTip);
        Assert.Contains("safeFiniteNumber(d?.weight", edgeTip);
        Assert.Contains("d?.lifecycleState ?? 'unknown'", nodeTip);
        Assert.Contains("Object.prototype.hasOwnProperty.call(STATE_COLOR", html);
        Assert.Contains("Object.prototype.hasOwnProperty.call(RELATION_COLOR", html);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static ServiceProvider BuildConstitutionServices(bool readOnlyOverlay, out InMemoryConstitutionAuditStore audit)
    {
        var provider = new InMemoryConstitutionProvider();
        if (readOnlyOverlay)
        {
            var constraints = new ConstitutionConstraints(
                preserveProvenance: true,
                requireEvidenceForKnowledge: true,
                preserveContradictions: true,
                requireDeterministicVerificationFirst: true,
                requireExplainability: true,
                requireAudit: true,
                minimumEvidenceCount: 1,
                allowedOperations:
                [
                    CognitiveOperationKind.ReadMemory,
                    CognitiveOperationKind.Retrieve,
                    CognitiveOperationKind.CompileContext
                ]);
            var overlay = ConstitutionCanonicalizer.Publish(new ConstitutionDefinition(
                "readonly-overlay",
                "Read-only policy",
                ConstitutionLayerKind.Overlay,
                constraints,
                ["Allow only read and retrieval operations."],
                Array.Empty<ConstitutionRuleDefinition>(),
                RootConstitution.Version.ContentHash), "1.0.0", DateTimeOffset.UnixEpoch);
            provider.PublishOverlay(overlay);
        }

        audit = new InMemoryConstitutionAuditStore();
        var services = new ServiceCollection();
        services.AddSingleton<IConstitutionProvider>(provider);
        services.AddSingleton<IConstitutionAuditStore>(audit);
        services.AddSingleton<IConstitutionRule, AuditEnvelopeConstitutionRule>();
        services.AddSingleton<IConstitutionEvaluator>(sp =>
            new DeterministicConstitutionEvaluator(sp.GetServices<IConstitutionRule>()));
        services.AddSingleton<ConstitutionKernel>();
        services.AddSingleton<IPrincipalContext>(new PrincipalContext("tenant-a", "alice"));
        return services.BuildServiceProvider();
    }

    private BenchmarkTools CreateBenchmarkTools(IAgentOutcomeModelClientFactory factory)
    {
        var metrics = new MetricsCollector();
        return new BenchmarkTools(
            new BenchmarkRunner(_index, _embedding),
            new AgentOutcomeBenchmarkRunner(_index, _embedding, _graph, _lifecycle),
            new LiveAgentOutcomeBenchmarkRunner(_index, _embedding, _graph, _lifecycle),
            factory,
            metrics);
    }

    private MrcrBenchmarkTools CreateMrcrBenchmarkTools(IAgentOutcomeModelClientFactory factory)
    {
        var scorer = new MrcrScorer(_embedding, passThreshold: 0.9f);
        var runner = new MrcrBenchmarkRunner(_index, _embedding, scorer);
        return new MrcrBenchmarkTools(runner, factory);
    }

    private string WriteSyntheticMrcrDataset(string fileName)
    {
        Directory.CreateDirectory(_dataPath);
        string path = Path.Combine(_dataPath, fileName);
        var task = new MrcrTask(
            TaskId: "t1",
            ContextTokens: 32,
            Turns: [new MrcrTurn("user", "token ALPHA-77"), new MrcrTurn("assistant", "ok")],
            Probe: "what is the token?",
            GoldAnswer: "ALPHA-77",
            NeedleIndex: 0,
            Bucket: "tiny");
        File.WriteAllText(path, JsonSerializer.Serialize(task) + Environment.NewLine);
        return path;
    }

    private void SeedQualifyingDiffusionGraph(string ns)
    {
        for (int i = 0; i < 36; i++)
        {
            float angle = i * 0.1f;
            _index.Upsert(new CognitiveEntry(
                $"node-{i}",
                [MathF.Cos(angle), MathF.Sin(angle)],
                ns,
                $"spectral node {i}"));
        }

        for (int i = 0; i < 35; i++)
        {
            _graph.AddEdge(new GraphEdge($"node-{i}", $"node-{i + 1}", "similar_to", 0.9f));
        }
    }

    private static string ReadRepositoryFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpEngramMemory.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}

/// <summary>
/// Environment-variable-sensitive security tests isolated from parallel execution so
/// process-wide env mutations cannot race with other test fixtures.
/// </summary>
[Collection("SecurityEnvVarTests")]
public sealed class SecurityFixesEnvVarTests : IDisposable
{
    private readonly string _tempDir;

    public SecurityFixesEnvVarTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sec_env_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void AgentOutcomeModelClientFactory_RespectsOperatorOllamaUrlAndOptInEnvVar()
    {
        string? origOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        string? origAllowCustom = Environment.GetEnvironmentVariable("ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT");

        try
        {
            Environment.SetEnvironmentVariable("ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OLLAMA_URL", "http://ollama.corp.internal:11434");

            var factory = new AgentOutcomeModelClientFactory();

            // Operator-configured OLLAMA_URL host is permitted (both implicitly and explicitly).
            using (var implicitClient = factory.Create("ollama", null))
                Assert.NotNull(implicitClient);
            using (var explicitClient = factory.Create("ollama", "http://ollama.corp.internal:11434"))
                Assert.NotNull(explicitClient);

            // Non-matching remote host is still blocked.
            Assert.Throws<ArgumentException>(() => factory.Create("ollama", "http://169.254.169.254/latest/meta-data"));

            // Opt-in env var enables custom Ollama host AND custom CLI executable.
            Environment.SetEnvironmentVariable("ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT", "1");
            using (var customOllama = factory.Create("ollama", "http://10.20.30.40:11434"))
                Assert.NotNull(customOllama);
            using (var customCli = factory.Create("claude-cli", Path.Combine(_tempDir, "custom-claude")))
                Assert.NotNull(customCli);

            // Even with opt-in, non-http schemes remain blocked for Ollama.
            Assert.Throws<ArgumentException>(() => factory.Create("ollama", "file:///etc/passwd"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_URL", origOllamaUrl);
            Environment.SetEnvironmentVariable("ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT", origAllowCustom);
        }
    }

    [Fact]
    public void ClaudeCliModelClient_ResolvesExecutableViaCliExecutableResolver()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On non-Windows CliExecutableResolver returns the input unchanged; verify construction preserves it.
            using var nonWin = new ClaudeCliModelClient("claude");
            var field = typeof(ClaudeCliModelClient).GetField("_executable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("claude", field.GetValue(nonWin));
            return;
        }

        string shimName = $"engram_test_claude_{Guid.NewGuid():N}";
        string shimPath = Path.Combine(_tempDir, shimName + ".cmd");
        File.WriteAllText(shimPath, "@echo off\r\necho 1.0.0\r\n");

        string? origPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _tempDir + ";" + origPath);
            using var client = new ClaudeCliModelClient(shimName);
            var field = typeof(ClaudeCliModelClient).GetField("_executable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(shimPath, field.GetValue(client));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", origPath);
        }
    }
}

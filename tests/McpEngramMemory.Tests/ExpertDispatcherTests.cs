using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class ExpertDispatcherTests : IDisposable
{
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;
    private readonly ExpertDispatcher _dispatcher;

    public ExpertDispatcherTests()
    {
        var persistence = new InMemoryStorageProvider();
        _index = new CognitiveIndex(persistence);
        _embedding = new HashEmbeddingService(dimensions: 384);
        _dispatcher = new ExpertDispatcher(_index, _embedding);
    }

    public void Dispose() => _index.Dispose();

    [Fact]
    public void CreateExpert_StoresInSystemNamespace()
    {
        var result = _dispatcher.CreateExpert("security_engineer", "A cybersecurity specialist focused on application security.");

        Assert.Equal("security_engineer", result.ExpertId);
        Assert.Equal("expert_security_engineer", result.TargetNamespace);

        var entry = _index.Get("security_engineer", ExpertDispatcher.SystemNamespace);
        Assert.NotNull(entry);
        Assert.Equal("ltm", entry!.LifecycleState);
        Assert.True(entry.IsSummaryNode);
        Assert.Equal("expert-profile", entry.Category);
        Assert.Equal("expert_security_engineer", entry.Metadata["targetNamespace"]);
    }

    [Fact]
    public void CreateExpert_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => _dispatcher.CreateExpert("", "description"));
    }

    [Fact]
    public void CreateExpert_EmptyDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() => _dispatcher.CreateExpert("test", ""));
    }

    [Fact]
    public void ExpertExists_ReturnsTrueAfterCreation()
    {
        Assert.False(_dispatcher.ExpertExists("new_expert"));
        _dispatcher.CreateExpert("new_expert", "A new expert for testing.");
        Assert.True(_dispatcher.ExpertExists("new_expert"));
    }

    [Fact]
    public void GetExpert_ReturnsProfile()
    {
        _dispatcher.CreateExpert("db_admin", "A database administrator specializing in PostgreSQL.");

        var expert = _dispatcher.GetExpert("db_admin");
        Assert.NotNull(expert);
        Assert.Equal("db_admin", expert!.ExpertId);
        Assert.Equal("expert_db_admin", expert.TargetNamespace);
        Assert.Contains("PostgreSQL", expert.PersonaDescription);
    }

    [Fact]
    public void GetExpert_NotFound_ReturnsNull()
    {
        Assert.Null(_dispatcher.GetExpert("nonexistent"));
    }

    [Fact]
    public void ListExperts_ReturnsAllRegistered()
    {
        _dispatcher.CreateExpert("expert_a", "Expert in domain A.");
        _dispatcher.CreateExpert("expert_b", "Expert in domain B.");

        var experts = _dispatcher.ListExperts();
        Assert.Equal(2, experts.Count);
        Assert.Contains(experts, e => e.ExpertId == "expert_a");
        Assert.Contains(experts, e => e.ExpertId == "expert_b");
    }

    [Fact]
    public void ListExperts_Empty_ReturnsEmptyList()
    {
        var experts = _dispatcher.ListExperts();
        Assert.Empty(experts);
    }

    [Fact]
    public void Route_NoExperts_ReturnsNeedsExpert()
    {
        var queryVector = _embedding.Embed("How do I secure a REST API?");
        var (status, experts) = _dispatcher.Route(queryVector);

        Assert.Equal("needs_expert", status);
        Assert.Empty(experts);
    }

    [Fact]
    public void Route_ExpertExists_SameText_ReturnsRouted()
    {
        // Create an expert with a specific persona
        string persona = "A cybersecurity specialist focused on application security and REST API hardening.";
        _dispatcher.CreateExpert("api_security", persona);

        // Query with the exact same text — HashEmbeddingService produces identical vectors
        var queryVector = _embedding.Embed(persona);
        var (status, experts) = _dispatcher.Route(queryVector, threshold: 0.5f);

        Assert.Equal("routed", status);
        Assert.NotEmpty(experts);
        Assert.Equal("api_security", experts[0].ExpertId);
        Assert.Equal("expert_api_security", experts[0].TargetNamespace);
    }

    [Fact]
    public void Route_BelowThreshold_ReturnsNeedsExpert()
    {
        _dispatcher.CreateExpert("quantum_physics", "Quantum mechanics and entanglement specialist.");

        // Query something unrelated
        var queryVector = _embedding.Embed("How to bake chocolate chip cookies?");
        var (status, _) = _dispatcher.Route(queryVector, threshold: 0.99f);

        Assert.Equal("needs_expert", status);
    }

    [Fact]
    public void Route_MultipleExperts_ReturnsWithinMargin()
    {
        // Create multiple experts with similar domains
        _dispatcher.CreateExpert("frontend_dev", "A frontend developer specializing in React and TypeScript.");
        _dispatcher.CreateExpert("react_expert", "A React framework specialist with deep component architecture knowledge.");
        _dispatcher.CreateExpert("backend_dev", "A backend developer specializing in Go microservices.");

        // Query about React — both frontend_dev and react_expert may match
        var queryVector = _embedding.Embed("A frontend developer specializing in React and TypeScript.");
        var (status, experts) = _dispatcher.Route(queryVector, topK: 3, threshold: 0.3f);

        // At minimum the exact match should be found
        Assert.Equal("routed", status);
        Assert.NotEmpty(experts);
    }

    [Fact]
    public void RecordDispatch_IncrementsAccessCount()
    {
        _dispatcher.CreateExpert("ml_engineer", "Machine learning and deep learning specialist.");

        var before = _index.Get("ml_engineer", ExpertDispatcher.SystemNamespace);
        int initialCount = before!.AccessCount;

        _dispatcher.RecordDispatch("ml_engineer");

        var after = _index.Get("ml_engineer", ExpertDispatcher.SystemNamespace);
        Assert.Equal(initialCount + 1, after!.AccessCount);
    }

    [Fact]
    public void SystemNamespace_HasUnderscorePrefix()
    {
        // Verify the namespace uses underscore prefix for background service exemption
        Assert.StartsWith("_", ExpertDispatcher.SystemNamespace);
    }

    [Fact]
    public void CreateExpert_EntryHasCorrectMetadata()
    {
        _dispatcher.CreateExpert("data_scientist", "A data scientist specializing in statistical modeling.");

        var entry = _index.Get("data_scientist", ExpertDispatcher.SystemNamespace);
        Assert.NotNull(entry);
        Assert.Equal("expert_data_scientist", entry!.Metadata["targetNamespace"]);
    }
}

/// <summary>
/// Minimal in-memory storage provider for testing.
/// </summary>
file sealed class InMemoryStorageProvider : IStorageProvider
{
    private readonly Dictionary<string, NamespaceData> _data = new();

    public NamespaceData LoadNamespace(string ns)
        => _data.TryGetValue(ns, out var d) ? d : new NamespaceData();

    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        => _data[ns] = dataProvider();

    public void SaveNamespaceSync(string ns, NamespaceData data)
        => _data[ns] = data;

    public IReadOnlyList<string> GetPersistedNamespaces()
        => _data.Keys.Where(k => !k.StartsWith("_")).ToList();

    public List<GraphEdge> LoadGlobalEdges() => new();
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) { }
    public List<SemanticCluster> LoadClusters() => new();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) { }
    public List<CollapseRecord> LoadCollapseHistory() => new();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) { }
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => new();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) { }
    public bool SupportsIncrementalWrites => false;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }
    public void ScheduleDeleteEntry(string ns, string entryId) { }
    public void Flush() { }
    public void Dispose() { }
}

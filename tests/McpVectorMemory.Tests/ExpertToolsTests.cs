using McpVectorMemory.Core.Models;
using McpVectorMemory.Core.Services;
using McpVectorMemory.Tools;

namespace McpVectorMemory.Tests;

public class ExpertToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;
    private readonly ExpertDispatcher _dispatcher;
    private readonly ExpertTools _tools;

    public ExpertToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"expert_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _embedding = new HashEmbeddingService(dimensions: 384);
        _dispatcher = new ExpertDispatcher(_index, _embedding);
        _tools = new ExpertTools(_dispatcher, _index, _embedding, new MetricsCollector());
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── create_expert ──

    [Fact]
    public void CreateExpert_ReturnsSuccess()
    {
        var result = _tools.CreateExpert("rust_engineer", "A Rust systems engineer specializing in memory safety.");
        var typed = Assert.IsType<CreateExpertResult>(result);

        Assert.Equal("created", typed.Status);
        Assert.Equal("rust_engineer", typed.ExpertId);
        Assert.Equal("expert_rust_engineer", typed.TargetNamespace);
    }

    [Fact]
    public void CreateExpert_DuplicateId_ReturnsError()
    {
        _tools.CreateExpert("dup_expert", "First expert.");
        var result = _tools.CreateExpert("dup_expert", "Second expert with same ID.");

        var error = Assert.IsType<string>(result);
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void CreateExpert_EmptyId_ReturnsError()
    {
        var result = _tools.CreateExpert("", "Some description.");
        var error = Assert.IsType<string>(result);
        Assert.Contains("Error", error);
    }

    [Fact]
    public void CreateExpert_EmptyDescription_ReturnsError()
    {
        var result = _tools.CreateExpert("test_expert", "");
        var error = Assert.IsType<string>(result);
        Assert.Contains("Error", error);
    }

    [Fact]
    public void CreateExpert_StoresLtmWithSummaryNode()
    {
        _tools.CreateExpert("secure_dev", "Application security specialist.");

        var entry = _index.Get("secure_dev", ExpertDispatcher.SystemNamespace);
        Assert.NotNull(entry);
        Assert.Equal("ltm", entry!.LifecycleState);
        Assert.True(entry.IsSummaryNode);
    }

    // ── dispatch_task ──

    [Fact]
    public void DispatchTask_NoExperts_ReturnsNeedsExpert()
    {
        var result = _tools.DispatchTask("How do I optimize database queries?");
        var typed = Assert.IsType<DispatchMissResult>(result);

        Assert.Equal("needs_expert", typed.Status);
        Assert.Contains("create_expert", typed.Suggestion);
    }

    [Fact]
    public void DispatchTask_EmptyDescription_ReturnsError()
    {
        var result = _tools.DispatchTask("");
        var error = Assert.IsType<string>(result);
        Assert.Contains("Error", error);
    }

    [Fact]
    public void DispatchTask_ExactMatch_ReturnsRouted()
    {
        // Create expert and query with same text for deterministic hash match
        string persona = "A database performance specialist focused on query optimization and indexing strategies.";
        _tools.CreateExpert("db_perf", persona);

        var result = _tools.DispatchTask(persona, threshold: 0.5f);
        var typed = Assert.IsType<DispatchRoutedResult>(result);

        Assert.Equal("routed", typed.Status);
        Assert.Equal("db_perf", typed.Expert.ExpertId);
        Assert.Equal("expert_db_perf", typed.Expert.TargetNamespace);
    }

    [Fact]
    public void DispatchTask_Routed_ReturnsContextFromExpertNamespace()
    {
        string persona = "A cloud architect specializing in AWS infrastructure.";
        _tools.CreateExpert("cloud_arch", persona);

        // Seed the expert namespace with the same text so hash embeddings produce matching vectors
        var v = _embedding.Embed(persona);
        _index.Upsert(new CognitiveEntry("mem-1", v, "expert_cloud_arch", persona));

        var result = _tools.DispatchTask(persona, autoSearchK: 3, threshold: 0.5f);
        var typed = Assert.IsType<DispatchRoutedResult>(result);

        Assert.Equal("routed", typed.Status);
        Assert.NotEmpty(typed.Context);
    }

    [Fact]
    public void DispatchTask_Routed_IncrementsAccessCount()
    {
        string persona = "A DevOps engineer specializing in CI/CD pipelines.";
        _tools.CreateExpert("devops_eng", persona);

        var before = _index.Get("devops_eng", ExpertDispatcher.SystemNamespace);
        int initialCount = before!.AccessCount;

        _tools.DispatchTask(persona, threshold: 0.5f);

        var after = _index.Get("devops_eng", ExpertDispatcher.SystemNamespace);
        Assert.True(after!.AccessCount > initialCount);
    }

    [Fact]
    public void DispatchTask_HighThreshold_ReturnsNeedsExpert()
    {
        _tools.CreateExpert("general_dev", "A general software developer.");

        // Use unrelated query text with very high threshold
        var result = _tools.DispatchTask("Quantum entanglement in photonic circuits", threshold: 0.999f);
        var typed = Assert.IsType<DispatchMissResult>(result);

        Assert.Equal("needs_expert", typed.Status);
    }

    [Fact]
    public void DispatchTask_CandidateExperts_IncludedInResult()
    {
        string persona = "A frontend React developer.";
        _tools.CreateExpert("react_dev", persona);

        var result = _tools.DispatchTask(persona, threshold: 0.5f);
        var typed = Assert.IsType<DispatchRoutedResult>(result);

        Assert.NotEmpty(typed.CandidateExperts);
        Assert.Contains(typed.CandidateExperts, e => e.ExpertId == "react_dev");
    }

    // ── Full pipeline ──

    [Fact]
    public void FullPipeline_CreateThenDispatch_WorksEndToEnd()
    {
        // Step 1: Create expert
        string persona = "A machine learning engineer specializing in transformer architectures and attention mechanisms.";
        var createResult = _tools.CreateExpert("ml_transformers", persona);
        var created = Assert.IsType<CreateExpertResult>(createResult);
        Assert.Equal("created", created.Status);

        // Step 2: Seed expert namespace with domain knowledge
        var v1 = _embedding.Embed("Multi-head attention computes scaled dot-product attention in parallel.");
        var v2 = _embedding.Embed("Layer normalization stabilizes training in deep transformer models.");
        _index.Upsert(new CognitiveEntry("tf-1", v1, "expert_ml_transformers",
            "Multi-head attention computes scaled dot-product attention in parallel."));
        _index.Upsert(new CognitiveEntry("tf-2", v2, "expert_ml_transformers",
            "Layer normalization stabilizes training in deep transformer models."));

        // Step 3: Dispatch a query using exact persona text (deterministic with HashEmbedding)
        var dispatchResult = _tools.DispatchTask(persona, autoSearchK: 5, threshold: 0.5f);
        var routed = Assert.IsType<DispatchRoutedResult>(dispatchResult);

        Assert.Equal("routed", routed.Status);
        Assert.Equal("ml_transformers", routed.Expert.ExpertId);
        Assert.Equal("expert_ml_transformers", routed.Expert.TargetNamespace);
        Assert.NotEmpty(routed.Context);
    }

    [Fact]
    public void FullPipeline_MissThenCreate_WorksEndToEnd()
    {
        // Step 1: Dispatch with no experts — miss
        var missResult = _tools.DispatchTask("How to design a quantum error correction circuit?");
        var miss = Assert.IsType<DispatchMissResult>(missResult);
        Assert.Equal("needs_expert", miss.Status);

        // Step 2: Follow the suggestion — create expert
        string persona = "A quantum computing researcher specializing in error correction codes and fault-tolerant computation.";
        var createResult = _tools.CreateExpert("quantum_ecc", persona);
        var created = Assert.IsType<CreateExpertResult>(createResult);
        Assert.Equal("created", created.Status);

        // Step 3: Dispatch again with the exact persona text — now it routes
        var hitResult = _tools.DispatchTask(persona, threshold: 0.5f);
        var routed = Assert.IsType<DispatchRoutedResult>(hitResult);
        Assert.Equal("routed", routed.Status);
        Assert.Equal("quantum_ecc", routed.Expert.ExpertId);
    }

    [Fact]
    public void ExpertEntries_ProtectedFromDecayAndAccretion()
    {
        _tools.CreateExpert("hidden_expert", "An expert that should be protected from decay and accretion.");

        // Expert entries use IsSummaryNode=true + LTM, which exempts them from:
        // - LifecycleEngine.RunDecayCycle (skips IsSummaryNode entries)
        // - AccretionScanner.ScanNamespace (skips IsSummaryNode entries)
        var entry = _index.Get("hidden_expert", ExpertDispatcher.SystemNamespace);
        Assert.NotNull(entry);
        Assert.True(entry!.IsSummaryNode);
        Assert.Equal("ltm", entry.LifecycleState);
    }
}

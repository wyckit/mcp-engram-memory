using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for Expert Instantiation and Semantic Routing.
/// dispatch_task routes queries to specialized expert namespaces via semantic similarity.
/// create_expert registers new expert personas in the meta-index.
/// </summary>
[McpServerToolType]
public sealed class ExpertTools
{
    private readonly ExpertDispatcher _dispatcher;
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;

    public ExpertTools(
        ExpertDispatcher dispatcher,
        CognitiveIndex index,
        IEmbeddingService embedding,
        MetricsCollector metrics)
    {
        _dispatcher = dispatcher;
        _index = index;
        _embedding = embedding;
        _metrics = metrics;
    }

    [McpServerTool(Name = "dispatch_task")]
    [Description("Route a query to the most relevant expert namespace via semantic similarity against the meta-index. " +
        "If a qualified expert is found (cosine similarity >= threshold), returns the expert profile and top memories " +
        "from that expert's namespace as context. If no expert qualifies, returns 'needs_expert' status — " +
        "use create_expert to instantiate a specialist for the domain.")]
    public object DispatchTask(
        [Description("The core problem, question, or task to route to an expert.")] string taskDescription,
        [Description("How many memories to retrieve from the matched expert's namespace (default: 3).")] int autoSearchK = 3,
        [Description("Minimum cosine similarity threshold for a routing hit (default: 0.75). " +
            "Lower values match more broadly, higher values require stronger domain alignment.")] float threshold = 0.75f)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
            return "Error: taskDescription must not be empty.";
        if (autoSearchK < 1) autoSearchK = 1;
        if (threshold <= 0f || threshold > 1f) threshold = ExpertDispatcher.DefaultThreshold;

        using var timer = _metrics.StartTimer("dispatch_task");

        var queryVector = _embedding.Embed(taskDescription);
        var (status, experts) = _dispatcher.Route(queryVector, topK: 3, threshold: threshold);

        if (status == "needs_expert")
        {
            return new DispatchMissResult(
                "needs_expert",
                experts,
                "No qualified expert found for this domain. Use create_expert to instantiate a specialist.");
        }

        // Routed — search the best expert's namespace for context
        var bestExpert = experts[0];
        _dispatcher.RecordDispatch(bestExpert.ExpertId);

        var context = _index.Search(
            queryVector, bestExpert.TargetNamespace, k: autoSearchK);

        return new DispatchRoutedResult("routed", bestExpert, experts, context);
    }

    [McpServerTool(Name = "create_expert")]
    [Description("Instantiate a new expert namespace and register it in the semantic routing meta-index. " +
        "The persona description is embedded and used for future query routing. " +
        "Provide a detailed paragraph outlining the expert's domain, principles, and perspective.")]
    public object CreateExpert(
        [Description("Snake_case identifier for the expert (e.g., 'rust_systems_engineer', 'quantum_physicist').")] string expertId,
        [Description("Detailed paragraph describing the expert's domain expertise, specialization, and perspective. " +
            "This text is embedded and used for semantic matching during dispatch.")] string personaDescription)
    {
        if (string.IsNullOrWhiteSpace(expertId))
            return "Error: expertId must not be empty.";
        if (string.IsNullOrWhiteSpace(personaDescription))
            return "Error: personaDescription must not be empty.";

        using var timer = _metrics.StartTimer("create_expert");

        if (_dispatcher.ExpertExists(expertId))
            return $"Error: Expert '{expertId}' already exists. Use a different ID or update the existing expert.";

        var result = _dispatcher.CreateExpert(expertId, personaDescription);
        return new CreateExpertResult("created", result.ExpertId, result.TargetNamespace);
    }
}

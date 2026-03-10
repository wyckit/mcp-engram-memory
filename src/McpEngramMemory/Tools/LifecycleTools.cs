using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for cognitive lifecycle management.
/// </summary>
[McpServerToolType]
public sealed class LifecycleTools
{
    private readonly LifecycleEngine _lifecycle;
    private readonly IEmbeddingService _embedding;

    public LifecycleTools(LifecycleEngine lifecycle, IEmbeddingService embedding)
    {
        _lifecycle = lifecycle;
        _embedding = embedding;
    }

    [McpServerTool(Name = "promote_memory")]
    [Description("Manually change an entry's lifecycle state (e.g. archive, consolidate, resurrect).")]
    public string PromoteMemory(
        [Description("Entry ID.")] string id,
        [Description("Target state: 'stm', 'ltm', or 'archived'.")] string targetState)
    {
        return _lifecycle.PromoteMemory(id, targetState);
    }

    [McpServerTool(Name = "deep_recall")]
    [Description("Search ALL states including archived. Auto-resurrects high-scoring archived entries to STM.")]
    public object DeepRecall(
        [Description("Namespace to search.")] string ns,
        [Description("The original text to search for.")] string? text = null,
        [Description("Query embedding vector.")] float[]? vector = null,
        [Description("Max results (default: 10).")] int k = 10,
        [Description("Min similarity (default: 0.3).")] float minScore = 0.3f,
        [Description("Score above which archived entries auto-resurrect to STM (default: 0.7).")] float resurrectionThreshold = 0.7f)
    {
        float[] resolved;
        try
        {
            resolved = ResolveVector(vector, text);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        return _lifecycle.DeepRecall(resolved, ns, k, minScore, resurrectionThreshold);
    }

    [McpServerTool(Name = "decay_cycle")]
    [Description("Trigger activation energy recomputation and state transitions. Formula: ActivationEnergy = (accessCount * reinforcementWeight) - (hoursSinceLastAccess * decayRate)")]
    public DecayCycleResult DecayCycle(
        [Description("Namespace ('*' for all).")] string ns,
        [Description("Decay per hour (default: 0.1).")] float decayRate = 0.1f,
        [Description("Weight per access (default: 1.0).")] float reinforcementWeight = 1.0f,
        [Description("Below this, STM demotes to LTM (default: 2.0).")] float stmThreshold = 2.0f,
        [Description("Below this, LTM archives (default: -5.0).")] float archiveThreshold = -5.0f)
    {
        return _lifecycle.RunDecayCycle(ns, decayRate, reinforcementWeight, stmThreshold, archiveThreshold);
    }

    [McpServerTool(Name = "configure_decay")]
    [Description("Set per-namespace decay parameters. These are used by the background decay service and when decay_cycle is called with useStoredConfig=true.")]
    public object ConfigureDecay(
        [Description("Namespace to configure.")] string ns,
        [Description("Decay per hour (default: 0.1).")] float? decayRate = null,
        [Description("Weight per access (default: 1.0).")] float? reinforcementWeight = null,
        [Description("Below this, STM demotes to LTM (default: 2.0).")] float? stmThreshold = null,
        [Description("Below this, LTM archives (default: -5.0).")] float? archiveThreshold = null)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return "Error: Namespace must not be empty.";

        var config = _lifecycle.SetDecayConfig(ns, decayRate, reinforcementWeight, stmThreshold, archiveThreshold);
        return config;
    }

    private float[] ResolveVector(float[]? vector, string? text)
    {
        if (vector is not null && vector.Length > 0)
            return vector;

        if (!string.IsNullOrWhiteSpace(text))
            return _embedding.Embed(text);

        throw new ArgumentException("Either 'vector' or 'text' must be provided.");
    }
}

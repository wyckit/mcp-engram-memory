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
    [Description("Change an entry's lifecycle state. Use to archive, consolidate to LTM, or resurrect to STM.")]
    public string PromoteMemory(
        [Description("Entry ID.")] string id,
        [Description("Target state: 'stm', 'ltm', or 'archived'.")] string targetState)
    {
        return _lifecycle.PromoteMemory(id, targetState);
    }

    [McpServerTool(Name = "deep_recall")]
    [Description("Search ALL lifecycle states including archived. Auto-resurrects high-scoring archived entries to STM. Use when specifically recovering forgotten memories — recall handles this automatically for most cases.")]
    public object DeepRecall(
        [Description("Namespace to search.")] string ns,
        [Description("The original text to search for.")] string? text = null,
        [Description("Query embedding vector.")] float[]? vector = null,
        [Description("Max results (default: 10).")] int k = 10,
        [Description("Min similarity (default: 0.3).")] float minScore = 0.3f,
        [Description("Score above which archived entries auto-resurrect to STM (default: 0.7).")] float resurrectionThreshold = 0.7f,
        [Description("Use hybrid BM25+vector search for better keyword recall (default: false).")] bool hybrid = false,
        [Description("Apply token-level reranking to improve precision (default: false).")] bool rerank = false)
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

        return _lifecycle.DeepRecall(resolved, ns, k, minScore, resurrectionThreshold,
            queryText: text, hybrid: hybrid, rerank: rerank);
    }

    [McpServerTool(Name = "memory_feedback")]
    [Description("Reinforce or suppress a memory. Call after recall: positive delta boosts activation energy, negative suppresses. Drives lifecycle transitions via threshold crossing.")]
    public object MemoryFeedback(
        [Description("Entry ID to provide feedback on.")] string id,
        [Description("Feedback delta: positive reinforces (e.g. 1.0-3.0 for helpful), negative suppresses (e.g. -1.0 to -3.0 for unhelpful). Clamped to [-10, 10].")] float delta,
        [Description("Optional namespace for threshold config lookup.")] string? ns = null)
    {
        var result = _lifecycle.ApplyFeedback(id, delta, ns);
        if (result is null)
            return $"Error: Entry '{id}' not found.";
        return result;
    }

    [McpServerTool(Name = "decay_cycle")]
    [Description("Run activation energy decay and state transitions for a namespace. Demotes stale STM to LTM and LTM to archived based on configurable thresholds.")]
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
    [Description("Set per-namespace decay parameters. Applied by background decay service and decay_cycle with useStoredConfig=true.")]
    public object ConfigureDecay(
        [Description("Namespace to configure.")] string ns,
        [Description("Decay per hour (default: 0.1).")] float? decayRate = null,
        [Description("Weight per access (default: 1.0).")] float? reinforcementWeight = null,
        [Description("Below this, STM demotes to LTM (default: 2.0).")] float? stmThreshold = null,
        [Description("Below this, LTM archives (default: -5.0).")] float? archiveThreshold = null,
        [Description("Opt in to graph-Laplacian spectral diffusion of decay debt: tightly-linked clusters share forgetting pressure, isolated entries fade alone. Off by default; namespace must have >=32 nodes and >=8 positive-relation edges to qualify.")] bool? useSpectralDecay = null,
        [Description("Fractional-Laplacian exponent alpha for the heat kernel filter exp(-lambda^alpha). Default 1.0 = standard heat kernel. Values <1 are subdiffusive, >1 superdiffusive.")] float? subdiffusiveExponent = null)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return "Error: Namespace must not be empty.";

        var config = _lifecycle.SetDecayConfig(ns, decayRate, reinforcementWeight, stmThreshold, archiveThreshold,
            useSpectralDecay, subdiffusiveExponent);
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

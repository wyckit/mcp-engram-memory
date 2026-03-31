using System.ComponentModel;
using McpEngramMemory.Core.Services.Synthesis;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for local SLM-powered memory synthesis.
/// Uses Ollama to run map-reduce summarization over large memory sets
/// without expanding the calling LLM's context window.
/// </summary>
[McpServerToolType]
public sealed class SynthesisTools
{
    private readonly SynthesisEngine _synthesis;

    public SynthesisTools(SynthesisEngine synthesis)
    {
        _synthesis = synthesis;
    }

    [McpServerTool(Name = "synthesize_memories")]
    [Description("Synthesize all memories in a namespace into a dense summary using a local SLM (via Ollama). " +
        "Uses map-reduce: chunks memories by cluster boundaries, summarizes each chunk locally, " +
        "then reduces into a final synthesis. Requires Ollama running locally. " +
        "Use when you need to reason over more memories than fit in your context window.")]
    public async Task<object> SynthesizeMemories(
        [Description("Namespace to synthesize.")] string ns,
        [Description("Optional focus query — synthesis will prioritize aspects relevant to this question.")] string? query = null,
        [Description("Maximum number of memories to include in synthesis (default: 200).")] int maxEntries = 200,
        CancellationToken cancellationToken = default)
    {
        var result = await _synthesis.SynthesizeNamespaceAsync(ns, query, maxEntries, cancellationToken);
        return result;
    }
}

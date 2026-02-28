using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpVectorMemory;

/// <summary>
/// MCP tool class exposing vector-memory operations to an LLM.
/// </summary>
[McpServerToolType]
public sealed class VectorMemoryTools
{
    private readonly VectorIndex _index;

    public VectorMemoryTools(VectorIndex index)
    {
        _index = index;
    }

    /// <summary>
    /// Stores (or replaces) a vector in the memory index together with its
    /// source text and optional metadata key-value pairs.
    /// </summary>
    [McpServerTool(Name = "store_memory")]
    [Description("Store a vector embedding together with its text and optional metadata.")]
    public string StoreMemory(
        [Description("Unique identifier for this memory entry.")] string id,
        [Description("The float vector embedding as an array of numbers.")] float[] vector,
        [Description("The original text the vector was derived from.")] string? text = null,
        [Description("Optional metadata as a JSON object with string keys and values.")] Dictionary<string, string>? metadata = null)
    {
        try
        {
            var entry = new VectorEntry(id, vector, text, metadata);
            _index.Upsert(entry);
            return $"Stored entry '{id}' ({vector.Length}-dim vector).";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Searches for the nearest neighbors of a query vector.
    /// </summary>
    [McpServerTool(Name = "search_memory")]
    [Description("Find the most similar stored memories for a query vector.")]
    public IReadOnlyList<SearchResult> SearchMemory(
        [Description("The query vector embedding as an array of numbers.")] float[] vector,
        [Description("Maximum number of results to return (default: 5).")] int k = 5,
        [Description("Minimum cosine-similarity score threshold between -1 and 1 (default: 0).")] float minScore = 0f)
    {
        return _index.Search(vector, k, minScore);
    }

    /// <summary>
    /// Deletes a stored memory entry by its identifier.
    /// </summary>
    [McpServerTool(Name = "delete_memory")]
    [Description("Delete a stored memory entry by its unique identifier.")]
    public string DeleteMemory(
        [Description("The identifier of the entry to delete.")] string id)
    {
        bool removed = _index.Delete(id);
        return removed
            ? $"Deleted entry '{id}'."
            : $"Entry '{id}' not found.";
    }
}

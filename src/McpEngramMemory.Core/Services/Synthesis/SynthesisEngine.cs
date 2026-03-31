using System.Text;
using System.Threading.Channels;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;

namespace McpEngramMemory.Core.Services.Synthesis;

/// <summary>
/// Map-reduce synthesis engine that uses a local SLM (via Ollama) to synthesize
/// large memory sets into dense summaries. Closes the MSA dense-reasoning gap
/// by processing arbitrarily many memories without expanding the LLM's context window.
///
/// Pipeline: Chunker → Channel → MapWorker(s) → Channel → ReduceWorker → Output
/// </summary>
public sealed class SynthesisEngine
{
    private const int DefaultChunkSize = 15;
    private const int MaxMapWorkers = 2;
    private const int ChannelCapacity = 4;

    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly OllamaClient _ollama;
    private readonly string _mapModel;
    private readonly string _reduceModel;

    public SynthesisEngine(CognitiveIndex index, ClusterManager clusters,
        string mapModel = "qwen2.5:0.5b", string reduceModel = "qwen2.5:0.5b",
        string? ollamaUrl = null)
    {
        _index = index;
        _clusters = clusters;
        _ollama = new OllamaClient(ollamaUrl ?? "http://localhost:11434");
        _mapModel = mapModel;
        _reduceModel = reduceModel;
    }

    /// <summary>
    /// Synthesize all memories in a namespace into a dense summary.
    /// Uses map-reduce: chunks memories → maps each to a summary → reduces summaries into synthesis.
    /// </summary>
    public async Task<SynthesisResult> SynthesizeNamespaceAsync(
        string ns, string? query = null, int maxEntries = 200,
        CancellationToken ct = default)
    {
        // 1. Check Ollama availability
        bool available = await _ollama.IsAvailableAsync(_mapModel, ct);
        if (!available)
        {
            return new SynthesisResult(
                Status: "error",
                Synthesis: null,
                EntriesProcessed: 0,
                ChunksProcessed: 0,
                MapModel: _mapModel,
                ReduceModel: _reduceModel,
                Error: $"Ollama not available or model '{_mapModel}' not found. " +
                       "Ensure Ollama is running (ollama serve) and the model is pulled (ollama pull " + _mapModel + ").");
        }

        // 2. Gather memories
        var entries = _index.GetAllInNamespace(ns)
            .Where(e => !e.IsSummaryNode && e.LifecycleState is "stm" or "ltm")
            .OrderByDescending(e => e.AccessCount)
            .ThenByDescending(e => e.ActivationEnergy)
            .Take(maxEntries)
            .ToList();

        if (entries.Count == 0)
            return new SynthesisResult("empty", null, 0, 0, _mapModel, _reduceModel,
                Error: "No active memories found in namespace.");

        // 3. Chunk memories (prefer cluster boundaries)
        var chunks = ChunkMemories(entries, ns);

        // 4. Map phase: summarize each chunk in parallel
        var mapChannel = Channel.CreateBounded<MemoryChunk>(ChannelCapacity);
        var mapResults = Channel.CreateBounded<MapResult>(ChannelCapacity);

        // Producer: feed chunks
        var producer = Task.Run(async () =>
        {
            foreach (var chunk in chunks)
                await mapChannel.Writer.WriteAsync(chunk, ct);
            mapChannel.Writer.Complete();
        }, ct);

        // Map workers
        var workers = Enumerable.Range(0, Math.Min(MaxMapWorkers, chunks.Count))
            .Select(_ => MapWorkerAsync(mapChannel.Reader, mapResults.Writer, query, ct))
            .ToArray();

        // Collector
        var summaries = new List<MapResult>();
        var collector = Task.Run(async () =>
        {
            await foreach (var result in mapResults.Reader.ReadAllAsync(ct))
                summaries.Add(result);
        }, ct);

        // Wait for pipeline
        await producer;
        await Task.WhenAll(workers);
        mapResults.Writer.Complete();
        await collector;

        if (summaries.Count == 0)
            return new SynthesisResult("map_failed", null, entries.Count, 0,
                _mapModel, _reduceModel, Error: "All map workers failed.");

        // 5. Reduce phase: synthesize all chunk summaries
        var synthesis = await ReduceAsync(summaries, query, ct);

        return new SynthesisResult(
            Status: synthesis is not null ? "synthesized" : "reduce_failed",
            Synthesis: synthesis,
            EntriesProcessed: entries.Count,
            ChunksProcessed: summaries.Count,
            MapModel: _mapModel,
            ReduceModel: _reduceModel,
            ChunkSummaries: summaries.Select(s => s.Summary).ToList());
    }

    /// <summary>Chunk memories respecting cluster boundaries where possible.</summary>
    private List<MemoryChunk> ChunkMemories(List<CognitiveEntry> entries, string ns)
    {
        var chunks = new List<MemoryChunk>();
        var assigned = new HashSet<string>();

        // First pass: group by cluster membership
        var clusterList = _clusters.ListClusters(ns);
        foreach (var clusterInfo in clusterList)
        {
            var cluster = _clusters.GetCluster(clusterInfo.ClusterId);
            if (cluster is null) continue;

            var clusterEntries = entries
                .Where(e => cluster.Members.Any(m => m.Id == e.Id) && assigned.Add(e.Id))
                .ToList();

            if (clusterEntries.Count > 0)
            {
                // Split large clusters
                for (int i = 0; i < clusterEntries.Count; i += DefaultChunkSize)
                {
                    var slice = clusterEntries.Skip(i).Take(DefaultChunkSize).ToList();
                    chunks.Add(new MemoryChunk(slice, clusterInfo.Label));
                }
            }
        }

        // Second pass: chunk remaining unassigned entries
        var remaining = entries.Where(e => !assigned.Contains(e.Id)).ToList();
        for (int i = 0; i < remaining.Count; i += DefaultChunkSize)
        {
            var slice = remaining.Skip(i).Take(DefaultChunkSize).ToList();
            chunks.Add(new MemoryChunk(slice, null));
        }

        return chunks;
    }

    /// <summary>Map worker: reads chunks from channel, summarizes via SLM, writes results.</summary>
    private async Task MapWorkerAsync(ChannelReader<MemoryChunk> input,
        ChannelWriter<MapResult> output, string? query, CancellationToken ct)
    {
        await foreach (var chunk in input.ReadAllAsync(ct))
        {
            try
            {
                var prompt = BuildMapPrompt(chunk, query);
                var summary = await _ollama.GenerateAsync(_mapModel, prompt, maxTokens: 300, ct: ct);

                if (!string.IsNullOrWhiteSpace(summary))
                    await output.WriteAsync(new MapResult(summary, chunk.Entries.Count, chunk.ClusterLabel), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Graceful degradation: skip failed chunks
            }
        }
    }

    /// <summary>Reduce phase: synthesize all chunk summaries into a final output.</summary>
    private async Task<string?> ReduceAsync(List<MapResult> mapResults, string? query,
        CancellationToken ct)
    {
        var prompt = BuildReducePrompt(mapResults, query);
        return await _ollama.GenerateAsync(_reduceModel, prompt, maxTokens: 500,
            temperature: 0.1f, ct: ct);
    }

    private static string BuildMapPrompt(MemoryChunk chunk, string? query)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize the following memory entries. Extract key themes, decisions, patterns, and relationships.");
        if (query is not null)
            sb.AppendLine($"Focus on aspects relevant to: {query}");
        if (chunk.ClusterLabel is not null)
            sb.AppendLine($"These memories belong to cluster: {chunk.ClusterLabel}");
        sb.AppendLine();

        foreach (var entry in chunk.Entries)
        {
            sb.Append($"[{entry.Id}]");
            if (entry.Category is not null) sb.Append($" ({entry.Category})");
            sb.AppendLine($": {entry.Text}");
        }

        sb.AppendLine();
        sb.AppendLine("Summary:");
        return sb.ToString();
    }

    private static string BuildReducePrompt(List<MapResult> mapResults, string? query)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synthesize these chunk summaries into a coherent overview. Note contradictions, evolution of thinking, and key patterns.");
        if (query is not null)
            sb.AppendLine($"Focus on: {query}");
        sb.AppendLine();

        for (int i = 0; i < mapResults.Count; i++)
        {
            sb.Append($"Chunk {i + 1}");
            if (mapResults[i].ClusterLabel is not null)
                sb.Append($" ({mapResults[i].ClusterLabel})");
            sb.AppendLine($" [{mapResults[i].EntryCount} entries]:");
            sb.AppendLine(mapResults[i].Summary);
            sb.AppendLine();
        }

        sb.AppendLine("Synthesis:");
        return sb.ToString();
    }
}

// ── Synthesis pipeline models ──

internal sealed record MemoryChunk(List<CognitiveEntry> Entries, string? ClusterLabel);
internal sealed record MapResult(string Summary, int EntryCount, string? ClusterLabel);

/// <summary>Result of a synthesis operation.</summary>
public sealed record SynthesisResult(
    string Status,
    string? Synthesis,
    int EntriesProcessed,
    int ChunksProcessed,
    string MapModel,
    string ReduceModel,
    string? Error = null,
    IReadOnlyList<string>? ChunkSummaries = null);

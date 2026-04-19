using System.Diagnostics;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Runs MRCR v2 (8-needle) long-context benchmark tasks under two memory conditions:
///
/// - <c>full_context</c>: baseline — the entire conversation is stuffed into the prompt.
///   Replicates the published llm-stats numbers (e.g. Opus 4.6 = 0.930 mean similarity).
///
/// - <c>engram_retrieval</c>: every conversation turn is ingested into a scratch namespace,
///   the probe drives a hybrid BM25+vector search, and only the top-K chunks are sent to
///   the model. Tests whether hybrid retrieval can match long-context accuracy at a fraction
///   of the prompt-token cost.
/// </summary>
public sealed class MrcrBenchmarkRunner
{
    public const string FullContextArm = "full_context";
    public const string EngramRetrievalArm = "engram_retrieval";

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly MrcrScorer _scorer;

    public MrcrBenchmarkRunner(CognitiveIndex index, IEmbeddingService embedding, MrcrScorer scorer)
    {
        _index = index;
        _embedding = embedding;
        _scorer = scorer;
    }

    public async Task<MrcrBenchmarkResult> RunAsync(
        string datasetId,
        IReadOnlyList<MrcrTask> tasks,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct = default)
    {
        var selected = options.Limit > 0 ? tasks.Take(options.Limit).ToList() : tasks.ToList();

        MrcrArmResult? fullContext = null;
        MrcrArmResult? engram = null;

        if (options.RunFullContextArm)
            fullContext = await RunFullContextAsync(selected, options, client, ct);

        if (options.RunEngramArm)
            engram = await RunEngramRetrievalAsync(selected, options, client, ct);

        float similarityDelta = (engram?.MeanSimilarity ?? 0f) - (fullContext?.MeanSimilarity ?? 0f);
        float promptTokenReduction = ComputePromptTokenReduction(fullContext, engram);

        return new MrcrBenchmarkResult(
            datasetId,
            DateTimeOffset.UtcNow,
            options.Provider,
            options.Model,
            options.Endpoint,
            selected.Count,
            options.TopK,
            fullContext,
            engram,
            similarityDelta,
            promptTokenReduction,
            "MRCR v2 (8-needle) A/B — full-context baseline vs. engram hybrid retrieval. Scoring: mean cosine similarity via local ONNX embeddings.");
    }

    private async Task<MrcrArmResult> RunFullContextAsync(
        IReadOnlyList<MrcrTask> tasks,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        var results = new List<MrcrTaskResult>(tasks.Count);

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EvaluateFullContextAsync(task, options, client, ct));
        }

        return Aggregate(FullContextArm, results);
    }

    private async Task<MrcrTaskResult> EvaluateFullContextAsync(
        MrcrTask task,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        string prompt = BuildFullContextPrompt(task);
        int approxTokens = ApproximateTokens(prompt);

        if (approxTokens > options.MaxContextTokens)
        {
            return new MrcrTaskResult(
                task.TaskId, task.ContextTokens, task.Bucket,
                approxTokens, 0f, false, 0,
                Answer: null, task.GoldAnswer,
                Error: $"Prompt ~{approxTokens} tokens exceeds maxContextTokens {options.MaxContextTokens}; skipped.");
        }

        return await GenerateAndScoreAsync(task, prompt, approxTokens, options, client, ct);
    }

    private async Task<MrcrArmResult> RunEngramRetrievalAsync(
        IReadOnlyList<MrcrTask> tasks,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        var results = new List<MrcrTaskResult>(tasks.Count);

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EvaluateEngramAsync(task, options, client, ct));
        }

        return Aggregate(EngramRetrievalArm, results);
    }

    private async Task<MrcrTaskResult> EvaluateEngramAsync(
        MrcrTask task,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        string ns = $"__mrcr_{task.TaskId}_{Guid.NewGuid():N}";

        try
        {
            // 1. Ingest every turn as an individual memory in the scratch namespace.
            for (int i = 0; i < task.Turns.Count; i++)
            {
                var turn = task.Turns[i];
                string id = $"{ns}:turn-{i:D4}";
                string text = $"[{turn.Role}] {turn.Content}";

                var vector = _embedding.Embed(text);
                _index.Upsert(new CognitiveEntry(id, vector, ns, text, "mrcr-turn", lifecycleState: "ltm"));
            }

            // 2. Hybrid search for the probe.
            var queryVector = _embedding.Embed(task.Probe);
            var retrieved = _index.Search(new SearchRequest
            {
                Query = queryVector,
                QueryText = task.Probe,
                Namespace = ns,
                K = options.TopK,
                Hybrid = true,
                Rerank = true
            });

            // 3. Build prompt with only retrieved chunks.
            string prompt = BuildEngramPrompt(task, retrieved);
            int approxTokens = ApproximateTokens(prompt);
            return await GenerateAndScoreAsync(task, prompt, approxTokens, options, client, ct);
        }
        finally
        {
            _index.DeleteAllInNamespace(ns);
        }
    }

    private async Task<MrcrTaskResult> GenerateAndScoreAsync(
        MrcrTask task,
        string prompt,
        int approxTokens,
        MrcrGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? answer;
        string? error = null;

        try
        {
            answer = await client.GenerateAsync(options.Model, prompt, options.MaxTokens, options.Temperature, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = null;
            error = ex.Message;
        }
        sw.Stop();

        var (similarity, passed) = _scorer.Score(answer, task.GoldAnswer);

        return new MrcrTaskResult(
            task.TaskId, task.ContextTokens, task.Bucket,
            approxTokens, similarity, passed, sw.Elapsed.TotalMilliseconds,
            answer, task.GoldAnswer, error);
    }

    private static string BuildFullContextPrompt(MrcrTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are answering a long-context recall question. Read the conversation below, then answer the probe.");
        sb.AppendLine("Answer with the minimum necessary text — no preamble, no restating the question, no markdown.");
        sb.AppendLine();
        sb.AppendLine("=== CONVERSATION ===");
        foreach (var turn in task.Turns)
        {
            sb.Append('[').Append(turn.Role).Append("] ").AppendLine(turn.Content);
        }
        sb.AppendLine("=== END CONVERSATION ===");
        sb.AppendLine();
        sb.Append("Probe: ").AppendLine(task.Probe);
        sb.Append("Answer: ");
        return sb.ToString();
    }

    private static string BuildEngramPrompt(MrcrTask task, IReadOnlyList<CognitiveSearchResult> retrieved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are answering a question using only the retrieved conversation snippets below.");
        sb.AppendLine("Answer with the minimum necessary text — no preamble, no restating the question, no markdown.");
        sb.AppendLine();
        sb.AppendLine("=== RETRIEVED SNIPPETS ===");
        if (retrieved.Count == 0)
        {
            sb.AppendLine("(no snippets retrieved)");
        }
        else
        {
            for (int i = 0; i < retrieved.Count; i++)
                sb.Append('(').Append(i + 1).Append(") ").AppendLine(retrieved[i].Text);
        }
        sb.AppendLine("=== END SNIPPETS ===");
        sb.AppendLine();
        sb.Append("Probe: ").AppendLine(task.Probe);
        sb.Append("Answer: ");
        return sb.ToString();
    }

    /// <summary>
    /// Rough token count — 1 token ≈ 4 characters. Used for reporting prompt-cost deltas
    /// between arms, not for hard context-window enforcement against a specific model.
    /// </summary>
    private static int ApproximateTokens(string text) => (text.Length + 3) / 4;

    private static float ComputePromptTokenReduction(MrcrArmResult? full, MrcrArmResult? engram)
    {
        if (full is null || engram is null) return 0f;
        if (full.TotalPromptTokens <= 0) return 0f;
        return 1f - (float)engram.TotalPromptTokens / full.TotalPromptTokens;
    }

    private static MrcrArmResult Aggregate(string arm, IReadOnlyList<MrcrTaskResult> taskResults)
    {
        if (taskResults.Count == 0)
            return new MrcrArmResult(arm, taskResults, 0f, 0f, 0, 0f, 0, 0,
                new Dictionary<string, float>());

        float meanSim = taskResults.Average(t => t.Similarity);
        float passRate = (float)taskResults.Count(t => t.Passed) / taskResults.Count;
        double meanLatency = taskResults.Average(t => t.LatencyMs);
        float meanPromptTokens = (float)taskResults.Average(t => (double)t.PromptTokens);
        long totalPromptTokens = taskResults.Sum(t => (long)t.PromptTokens);
        int errors = taskResults.Count(t => t.Error is not null);

        var bucketMeans = taskResults
            .Where(t => !string.IsNullOrEmpty(t.Bucket))
            .GroupBy(t => t.Bucket!)
            .ToDictionary(g => g.Key, g => g.Average(t => t.Similarity));

        return new MrcrArmResult(arm, taskResults, meanSim, passRate, meanLatency,
            meanPromptTokens, totalPromptTokens, errors, bucketMeans);
    }
}

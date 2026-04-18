using System.Diagnostics;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Runs agent-outcome datasets through a real generation model under multiple memory conditions.
/// The model sees condition-specific memory context and must return structured JSON with cited memory IDs.
/// Scoring stays deterministic so benchmark deltas reflect memory policy and model behavior rather than judge drift.
/// </summary>
public sealed class LiveAgentOutcomeBenchmarkRunner
{
    public const string NoMemoryCondition = AgentOutcomeBenchmarkRunner.NoMemoryCondition;
    public const string TranscriptReplayCondition = AgentOutcomeBenchmarkRunner.TranscriptReplayCondition;
    public const string VectorMemoryCondition = AgentOutcomeBenchmarkRunner.VectorMemoryCondition;
    public const string FullEngramCondition = AgentOutcomeBenchmarkRunner.FullEngramCondition;
    public const string FullEngramNoGraphCondition = AgentOutcomeBenchmarkRunner.FullEngramNoGraphCondition;
    public const string FullEngramNoLifecycleCondition = AgentOutcomeBenchmarkRunner.FullEngramNoLifecycleCondition;
    public const string FullEngramNoHybridCondition = AgentOutcomeBenchmarkRunner.FullEngramNoHybridCondition;

    private static readonly string[] DefaultComparisonConditions =
        [TranscriptReplayCondition, VectorMemoryCondition, FullEngramCondition];

    private static readonly string[] AblationConditions =
    [
        FullEngramNoGraphCondition,
        FullEngramNoLifecycleCondition,
        FullEngramNoHybridCondition
    ];

    private readonly record struct FullEngramPolicy(bool UseGraph, bool UseLifecycle, bool UseHybrid)
    {
        public static FullEngramPolicy Full => new(true, true, true);
        public static FullEngramPolicy NoGraph => new(false, true, true);
        public static FullEngramPolicy NoLifecycle => new(true, false, true);
        public static FullEngramPolicy NoHybrid => new(true, true, false);
    }

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;

    public LiveAgentOutcomeBenchmarkRunner(
        CognitiveIndex index,
        IEmbeddingService embedding,
        KnowledgeGraph graph,
        LifecycleEngine lifecycle)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        _lifecycle = lifecycle;
    }

    public async Task<LiveAgentOutcomeBenchmarkResult> RunAsync(
        AgentOutcomeDataset dataset,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct = default)
    {
        var baseline = await RunConditionAsync(dataset, options, client, NoMemoryCondition, ct);
        var conditions = options.RunAblations
            ? DefaultComparisonConditions.Concat(AblationConditions).ToArray()
            : DefaultComparisonConditions;

        var comparisons = new List<LiveAgentOutcomeConditionComparison>(conditions.Length);

        foreach (var condition in conditions)
        {
            ct.ThrowIfCancellationRequested();

            var result = await RunConditionAsync(dataset, options, client, condition, ct);
            comparisons.Add(new LiveAgentOutcomeConditionComparison(
                condition,
                result,
                result.MeanSuccessScore - baseline.MeanSuccessScore,
                result.PassRate - baseline.PassRate,
                result.MeanRequiredCoverage - baseline.MeanRequiredCoverage,
                result.MeanConflictRate - baseline.MeanConflictRate,
                result.MeanLatencyMs - baseline.MeanLatencyMs,
                result.FormatValidityRate - baseline.FormatValidityRate));
        }

        return new LiveAgentOutcomeBenchmarkResult(
            dataset.DatasetId,
            DateTimeOffset.UtcNow,
            options.Provider,
            options.Model,
            options.Endpoint,
            NoMemoryCondition,
            baseline,
            comparisons,
            "Live model benchmark with condition-specific memory context, structured JSON output, and deterministic grading.");
    }

    private Task<LiveAgentOutcomeConditionResult> RunConditionAsync(
        AgentOutcomeDataset dataset,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        string condition,
        CancellationToken ct)
    {
        return condition switch
        {
            NoMemoryCondition => RunNoMemoryAsync(dataset, options, client, ct),
            TranscriptReplayCondition => RunTranscriptReplayAsync(dataset, options, client, ct),
            VectorMemoryCondition => RunIndexedConditionAsync(dataset, options, client, VectorMemoryCondition, policy: null, ct),
            FullEngramCondition => RunIndexedConditionAsync(dataset, options, client, FullEngramCondition, FullEngramPolicy.Full, ct),
            FullEngramNoGraphCondition => RunIndexedConditionAsync(dataset, options, client, FullEngramNoGraphCondition, FullEngramPolicy.NoGraph, ct),
            FullEngramNoLifecycleCondition => RunIndexedConditionAsync(dataset, options, client, FullEngramNoLifecycleCondition, FullEngramPolicy.NoLifecycle, ct),
            FullEngramNoHybridCondition => RunIndexedConditionAsync(dataset, options, client, FullEngramNoHybridCondition, FullEngramPolicy.NoHybrid, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), $"Unknown memory condition '{condition}'.")
        };
    }

    private async Task<LiveAgentOutcomeConditionResult> RunNoMemoryAsync(
        AgentOutcomeDataset dataset,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        var results = new List<LiveAgentOutcomeTaskResult>(dataset.Tasks.Count);

        foreach (var task in dataset.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EvaluateTaskAsync(task, PromptContext.Empty, options, client, dataset.Edges, ct));
        }

        return Aggregate(NoMemoryCondition, results);
    }

    private async Task<LiveAgentOutcomeConditionResult> RunTranscriptReplayAsync(
        AgentOutcomeDataset dataset,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        CancellationToken ct)
    {
        var seedById = dataset.SeedEntries.ToDictionary(seed => seed.Id, StringComparer.Ordinal);
        var chunks = BuildTranscriptChunks(dataset);
        var results = new List<LiveAgentOutcomeTaskResult>(dataset.Tasks.Count);

        foreach (var task in dataset.Tasks)
        {
            ct.ThrowIfCancellationRequested();

            var contextIds = SearchTranscript(task, chunks);
            var context = BuildPromptContext(contextIds, seedById);
            results.Add(await EvaluateTaskAsync(task, context, options, client, dataset.Edges, ct));
        }

        return Aggregate(TranscriptReplayCondition, results);
    }

    private async Task<LiveAgentOutcomeConditionResult> RunIndexedConditionAsync(
        AgentOutcomeDataset dataset,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        string condition,
        FullEngramPolicy? policy,
        CancellationToken ct)
    {
        var seeded = SeedConditionNamespace(dataset, condition, options.ContextualPrefix);
        var seedById = dataset.SeedEntries.ToDictionary(seed => seed.Id, StringComparer.Ordinal);

        try
        {
            var results = new List<LiveAgentOutcomeTaskResult>(dataset.Tasks.Count);

            foreach (var task in dataset.Tasks)
            {
                ct.ThrowIfCancellationRequested();

                var queryVector = _embedding.Embed(task.QueryText);
                var context = policy is FullEngramPolicy p
                    ? BuildFullEngramContext(seeded, seedById, task, queryVector, p, dataset)
                    : BuildVectorContext(seeded, seedById, task, queryVector);

                results.Add(await EvaluateTaskAsync(task, context, options, client, dataset.Edges, ct));
            }

            return Aggregate(condition, results);
        }
        finally
        {
            CleanupSeededNamespace(seeded);
        }
    }

    private PromptContext BuildVectorContext(
        SeededNamespace seeded,
        IReadOnlyDictionary<string, BenchmarkSeedEntry> seedById,
        AgentOutcomeTask task,
        float[] queryVector)
    {
        var results = _index.Search(queryVector, seeded.Namespace, task.K, minScore: task.MinScore);
        var contextIds = ToCanonicalIds(results.Select(r => r.Id), seeded.LocalToCanonical);
        return BuildPromptContext(contextIds, seedById);
    }

    private PromptContext BuildFullEngramContext(
        SeededNamespace seeded,
        IReadOnlyDictionary<string, BenchmarkSeedEntry> seedById,
        AgentOutcomeTask task,
        float[] queryVector,
        FullEngramPolicy policy,
        AgentOutcomeDataset dataset)
    {
        bool useHybrid = policy.UseHybrid && BenchmarkPolicyPatches.ShouldUseHybrid(dataset);

        IReadOnlyList<CognitiveSearchResult> results = _index.Search(new SearchRequest
        {
            Query = queryVector,
            QueryText = task.QueryText,
            Namespace = seeded.Namespace,
            K = task.K,
            MinScore = task.MinScore,
            Hybrid = useHybrid,
            Rerank = true
        });

        if (policy.UseLifecycle && (results.Count == 0 || results[0].Score < 0.50f))
        {
            results = _lifecycle.DeepRecall(
                queryVector,
                seeded.Namespace,
                k: task.K,
                minScore: task.MinScore,
                resurrectionThreshold: 0.7f,
                queryText: task.QueryText,
                hybrid: useHybrid,
                rerank: true);
        }

        var contextIds = policy.UseGraph
            ? ExpandWithGraph(results, seeded.LocalToCanonical)
            : ToCanonicalIds(results.Select(r => r.Id), seeded.LocalToCanonical);

        if (policy.UseLifecycle)
            contextIds = BenchmarkPolicyPatches.ResolveLifecycleContradictions(contextIds, dataset);

        return BuildPromptContext(contextIds, seedById);
    }

    private async Task<LiveAgentOutcomeTaskResult> EvaluateTaskAsync(
        AgentOutcomeTask task,
        PromptContext context,
        LiveAgentOutcomeGenerationOptions options,
        IAgentOutcomeModelClient client,
        IReadOnlyList<OutcomeGraphEdgeSeed> edges,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(task, context);

        var sw = Stopwatch.StartNew();
        var rawResponse = await client.GenerateAsync(
            options.Model,
            prompt,
            options.MaxTokens,
            options.Temperature,
            ct);
        sw.Stop();

        var parsed = ParseResponse(rawResponse, context.MemoryIds);
        return ScoreTask(
            task,
            context.MemoryIds,
            parsed.CitedMemoryIds,
            parsed.Answer,
            rawResponse,
            parsed.IsValid,
            parsed.InsufficientContext,
            sw.Elapsed.TotalMilliseconds,
            edges);
    }

    private SeededNamespace SeedConditionNamespace(
        AgentOutcomeDataset dataset,
        string condition,
        bool useContextualPrefix)
    {
        string ns = $"__live_agent_outcome_{condition}_{Guid.NewGuid():N}";
        var canonicalToLocal = new Dictionary<string, string>(dataset.SeedEntries.Count, StringComparer.Ordinal);
        var localToCanonical = new Dictionary<string, string>(dataset.SeedEntries.Count, StringComparer.Ordinal);
        var localIds = new List<string>(dataset.SeedEntries.Count);

        foreach (var seed in dataset.SeedEntries)
        {
            string localId = $"{condition}:{seed.Id}";
            canonicalToLocal[seed.Id] = localId;
            localToCanonical[localId] = seed.Id;
            localIds.Add(localId);

            var textToEmbed = useContextualPrefix
                ? BenchmarkRunner.BuildContextualPrefix(category: seed.Category) + seed.Text
                : seed.Text;
            var vector = _embedding.Embed(textToEmbed);

            var entry = new CognitiveEntry(
                localId,
                vector,
                ns,
                seed.Text,
                seed.Category,
                lifecycleState: seed.LifecycleState ?? "ltm");

            if (seed.AccessCount is int accessCount)
                entry.AccessCount = accessCount;
            if (seed.IsSummaryNode == true)
                entry.IsSummaryNode = true;
            if (seed.SourceClusterId is not null)
                entry.SourceClusterId = seed.SourceClusterId;

            _index.Upsert(entry);
        }

        foreach (var edge in dataset.Edges)
        {
            if (!canonicalToLocal.TryGetValue(edge.SourceId, out var localSource)) continue;
            if (!canonicalToLocal.TryGetValue(edge.TargetId, out var localTarget)) continue;

            _graph.AddEdge(new GraphEdge(localSource, localTarget, edge.Relation, edge.Weight));
        }

        return new SeededNamespace(ns, canonicalToLocal, localToCanonical, localIds);
    }

    private void CleanupSeededNamespace(SeededNamespace seeded)
    {
        foreach (var localId in seeded.LocalIds)
            _graph.RemoveAllEdgesForEntry(localId);

        _index.DeleteAllInNamespace(seeded.Namespace);
    }

    private static PromptContext BuildPromptContext(
        IEnumerable<string> contextIds,
        IReadOnlyDictionary<string, BenchmarkSeedEntry> seedById)
    {
        var memories = new List<PromptContextMemory>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in contextIds)
        {
            if (!seen.Add(id) || !seedById.TryGetValue(id, out var seed))
                continue;

            memories.Add(new PromptContextMemory(id, seed.Text));
        }

        return memories.Count == 0
            ? PromptContext.Empty
            : new PromptContext(memories.Select(m => m.Id).ToList(), memories);
    }

    private static string BuildPrompt(AgentOutcomeTask task, PromptContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are running a memory benchmark for an AI coding assistant.");
        builder.AppendLine("Use only the memory context below. Do not use outside knowledge.");
        builder.AppendLine("Return exactly one JSON object on one line with this exact schema:");
        builder.AppendLine("{\"answer\":\"string\",\"evidence_ids\":[\"memory-id\"],\"insufficient_context\":false}");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Cite only memory IDs that appear in the provided context.");
        builder.AppendLine("- If the context is insufficient, set insufficient_context to true.");
        builder.AppendLine("- If multiple context memories are needed, cite all of them in evidence_ids.");
        builder.AppendLine("- Keep the answer concise and grounded in the cited memory.");
        builder.AppendLine("- Do not emit markdown, bullet points, code fences, or multiple JSON objects.");
        builder.AppendLine("- Every field must appear in the same JSON object.");
        builder.AppendLine();
        builder.AppendLine("Valid example with evidence:");
        builder.AppendLine("{\"answer\":\"Use the stored workaround.\",\"evidence_ids\":[\"memory-a\",\"memory-b\"],\"insufficient_context\":false}");
        builder.AppendLine("Valid example when context is insufficient:");
        builder.AppendLine("{\"answer\":\"Insufficient context.\",\"evidence_ids\":[],\"insufficient_context\":true}");
        builder.AppendLine();
        builder.AppendLine($"Task: {task.QueryText}");
        if (!string.IsNullOrWhiteSpace(task.Notes))
            builder.AppendLine($"Task note: {task.Notes}");
        builder.AppendLine();
        builder.AppendLine("Memory context:");

        if (context.Memories.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            builder.AppendLine($"Allowed evidence ids: {string.Join(", ", context.MemoryIds)}");
            foreach (var memory in context.Memories)
                builder.AppendLine($"[{memory.Id}] {memory.Text}");
        }

        return builder.ToString();
    }

    private static ParsedResponse ParseResponse(string? rawResponse, IReadOnlyCollection<string> allowedIds)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return ParsedResponse.Invalid;

        if (TryParseResponse(rawResponse, allowedIds, out var parsed))
            return parsed;

        var fragments = ExtractJsonObjectFragments(rawResponse);
        if (TryParseMergedFragments(fragments, rawResponse, allowedIds, out parsed))
            return parsed;

        if (TryExtractLooseFields(rawResponse, allowedIds, out parsed))
            return parsed;

        int start = rawResponse.IndexOf('{');
        int end = rawResponse.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var candidate = rawResponse[start..(end + 1)];
            if (TryParseResponse(candidate, allowedIds, out parsed))
                return parsed;
        }

        return ParsedResponse.Invalid with { RawResponse = rawResponse };
    }

    private static bool TryParseResponse(
        string json,
        IReadOnlyCollection<string> allowedIds,
        out ParsedResponse parsed)
    {
        try
        {
            var response = JsonSerializer.Deserialize<LiveAgentOutcomeModelResponse>(json, ResponseJsonOptions);
            if (response is null)
            {
                parsed = ParsedResponse.Invalid;
                return false;
            }

            var citedIds = NormalizeIds(response.EvidenceIds, allowedIds);
            parsed = new ParsedResponse(
                response.Answer,
                citedIds,
                response.InsufficientContext,
                IsValid: true,
                RawResponse: json);
            return true;
        }
        catch
        {
            parsed = ParsedResponse.Invalid;
            return false;
        }
    }

    private static bool TryParseMergedFragments(
        IReadOnlyList<string> fragments,
        string rawResponse,
        IReadOnlyCollection<string> allowedIds,
        out ParsedResponse parsed)
    {
        if (fragments.Count == 0)
        {
            parsed = ParsedResponse.Invalid;
            return false;
        }

        string? answer = null;
        bool? insufficientContext = null;
        var evidence = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fragment in fragments)
        {
            if (!TryParsePartialResponse(fragment, allowedIds, out var partial))
                continue;

            answer ??= partial.Answer;
            if (partial.InsufficientContext.HasValue)
                insufficientContext = partial.InsufficientContext.Value;

            foreach (var id in partial.CitedMemoryIds)
            {
                if (seen.Add(id))
                    evidence.Add(id);
            }
        }

        if (TryExtractLooseFields(rawResponse, allowedIds, out var loose))
        {
            answer ??= loose.Answer;
            if (!insufficientContext.HasValue)
                insufficientContext = loose.InsufficientContext;

            foreach (var id in loose.CitedMemoryIds)
            {
                if (seen.Add(id))
                    evidence.Add(id);
            }
        }

        if (answer is null && evidence.Count == 0 && !insufficientContext.HasValue)
        {
            parsed = ParsedResponse.Invalid;
            return false;
        }

        parsed = new ParsedResponse(
            answer,
            evidence,
            insufficientContext ?? false,
            IsValid: true,
            RawResponse: rawResponse);
        return true;
    }

    private static bool TryParsePartialResponse(
        string json,
        IReadOnlyCollection<string> allowedIds,
        out PartialParsedResponse partial)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                partial = PartialParsedResponse.Invalid;
                return false;
            }

            string? answer = null;
            bool? insufficientContext = null;
            IReadOnlyList<string> citedIds = Array.Empty<string>();

            if (doc.RootElement.TryGetProperty("answer", out var answerProperty) &&
                answerProperty.ValueKind == JsonValueKind.String)
            {
                answer = answerProperty.GetString();
            }

            if (doc.RootElement.TryGetProperty("insufficient_context", out var insufficientProperty) &&
                (insufficientProperty.ValueKind == JsonValueKind.True || insufficientProperty.ValueKind == JsonValueKind.False))
            {
                insufficientContext = insufficientProperty.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("evidence_ids", out var evidenceProperty) &&
                evidenceProperty.ValueKind == JsonValueKind.Array)
            {
                var rawIds = evidenceProperty.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString() ?? string.Empty)
                    .ToList();
                citedIds = NormalizeIds(rawIds, allowedIds);
            }

            partial = new PartialParsedResponse(
                answer,
                citedIds,
                insufficientContext);
            return true;
        }
        catch
        {
            partial = PartialParsedResponse.Invalid;
            return false;
        }
    }

    private static bool TryExtractLooseFields(
        string rawResponse,
        IReadOnlyCollection<string> allowedIds,
        out ParsedResponse parsed)
    {
        string? answer = ExtractQuotedString(rawResponse, "\"answer\"");
        IReadOnlyList<string> evidenceIds = ExtractStringArray(rawResponse, "\"evidence_ids\"", allowedIds);
        bool? insufficientContext = ExtractBoolean(rawResponse, "\"insufficient_context\"");

        if (answer is null && evidenceIds.Count == 0 && !insufficientContext.HasValue)
        {
            parsed = ParsedResponse.Invalid;
            return false;
        }

        parsed = new ParsedResponse(
            answer,
            evidenceIds,
            insufficientContext ?? false,
            IsValid: true,
            RawResponse: rawResponse);
        return true;
    }

    private static IReadOnlyList<string> ExtractJsonObjectFragments(string rawResponse)
    {
        var fragments = new List<string>();
        int depth = 0;
        int start = -1;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < rawResponse.Length; i++)
        {
            char ch = rawResponse[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (ch == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    fragments.Add(rawResponse[start..(i + 1)]);
                    start = -1;
                }
            }
        }

        return fragments;
    }

    private static string? ExtractQuotedString(string rawResponse, string propertyName)
    {
        string marker = propertyName + ":";
        int propertyIndex = rawResponse.IndexOf(marker, StringComparison.Ordinal);
        if (propertyIndex < 0)
            return null;

        int colonIndex = rawResponse.IndexOf(':', propertyIndex);
        if (colonIndex < 0)
            return null;

        int stringStart = rawResponse.IndexOf('"', colonIndex + 1);
        if (stringStart < 0)
            return null;

        var builder = new StringBuilder();
        bool escaped = false;
        for (int i = stringStart + 1; i < rawResponse.Length; i++)
        {
            char ch = rawResponse[i];
            if (escaped)
            {
                builder.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                string encoded = "\"" + builder.ToString().Replace("\"", "\\\"") + "\"";
                try
                {
                    return JsonSerializer.Deserialize<string>(encoded);
                }
                catch
                {
                    return builder.ToString();
                }
            }

            builder.Append(ch);
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractStringArray(
        string rawResponse,
        string propertyName,
        IReadOnlyCollection<string> allowedIds)
    {
        string marker = propertyName + ":";
        int propertyIndex = rawResponse.IndexOf(marker, StringComparison.Ordinal);
        if (propertyIndex < 0)
            return Array.Empty<string>();

        int arrayStart = rawResponse.IndexOf('[', propertyIndex);
        int arrayEnd = rawResponse.IndexOf(']', arrayStart >= 0 ? arrayStart : propertyIndex);
        if (arrayStart < 0 || arrayEnd <= arrayStart)
            return Array.Empty<string>();

        var arrayText = rawResponse[arrayStart..(arrayEnd + 1)];
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(arrayText) ?? new List<string>();
            return NormalizeIds(values, allowedIds);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool? ExtractBoolean(string rawResponse, string propertyName)
    {
        string marker = propertyName + ":";
        int propertyIndex = rawResponse.IndexOf(marker, StringComparison.Ordinal);
        if (propertyIndex < 0)
            return null;

        int colonIndex = rawResponse.IndexOf(':', propertyIndex);
        if (colonIndex < 0)
            return null;

        var tail = rawResponse[(colonIndex + 1)..].TrimStart();
        if (tail.StartsWith("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (tail.StartsWith("false", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    private static IReadOnlyList<string> NormalizeIds(
        IReadOnlyList<string>? evidenceIds,
        IReadOnlyCollection<string> allowedIds)
    {
        if (evidenceIds is null || evidenceIds.Count == 0)
            return Array.Empty<string>();

        var allowed = allowedIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(evidenceIds.Count);

        foreach (var id in evidenceIds)
        {
            var candidate = id?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (!allowed.Contains(candidate) || !seen.Add(candidate))
                continue;

            normalized.Add(candidate);
        }

        return normalized;
    }

    private static LiveAgentOutcomeTaskResult ScoreTask(
        AgentOutcomeTask task,
        IReadOnlyList<string> contextMemoryIds,
        IReadOnlyCollection<string> citedMemoryIds,
        string? answer,
        string? rawResponse,
        bool responseFormatValid,
        bool insufficientContext,
        double latencyMs,
        IReadOnlyList<OutcomeGraphEdgeSeed> edges)
    {
        var required = task.RequiredMemoryIds;
        var helpful = task.HelpfulMemoryIds ?? Array.Empty<string>();
        var forbidden = task.ForbiddenMemoryIds ?? Array.Empty<string>();

        int requiredHits = required.Count(citedMemoryIds.Contains);
        int helpfulHits = helpful.Count(citedMemoryIds.Contains);
        int forbiddenHits = forbidden.Count(citedMemoryIds.Contains);

        float requiredCoverage = required.Count == 0 ? 1f : (float)requiredHits / required.Count;
        float helpfulCoverage = helpful.Count == 0 ? 1f : (float)helpfulHits / helpful.Count;
        float conflictRate = forbidden.Count == 0 ? 0f : (float)forbiddenHits / forbidden.Count;

        float helpfulWeight = helpful.Count > 0 ? 0.20f : 0f;
        float requiredWeight = 1.0f - helpfulWeight;
        float baseSuccess = Math.Clamp(
            (requiredCoverage * requiredWeight) +
            (helpfulCoverage * helpfulWeight) -
            (conflictRate * 0.50f),
            0f,
            1f);

        var intelligence = IntelligenceScoring.Compute(task, contextMemoryIds, citedMemoryIds, edges);
        float successScore = IntelligenceScoring.AdjustSuccessScore(baseSuccess, intelligence, task);

        bool passed = responseFormatValid
            && requiredCoverage >= 0.999f
            && conflictRate == 0f
            && intelligence.StaleMemoryPenalty == 0f;

        return new LiveAgentOutcomeTaskResult(
            task.TaskId,
            requiredCoverage,
            helpfulCoverage,
            conflictRate,
            successScore,
            passed,
            latencyMs,
            responseFormatValid,
            insufficientContext,
            contextMemoryIds,
            citedMemoryIds.ToList(),
            answer,
            rawResponse,
            intelligence.ReasoningPathValidity,
            intelligence.DependencyCompletionScore,
            intelligence.StaleMemoryPenalty,
            intelligence.MinimalEvidenceScore,
            intelligence.NoiseResistanceScore,
            intelligence.NoiseResistanceScoreRanked,
            intelligence.ContradictionHandlingScore);
    }

    private static LiveAgentOutcomeConditionResult Aggregate(
        string condition,
        IReadOnlyList<LiveAgentOutcomeTaskResult> taskResults)
    {
        if (taskResults.Count == 0)
        {
            return new LiveAgentOutcomeConditionResult(condition, taskResults, 0f, 0f, 0f, 0f, 0, 0f);
        }

        return new LiveAgentOutcomeConditionResult(
            condition,
            taskResults,
            taskResults.Average(result => result.SuccessScore),
            (float)taskResults.Count(result => result.Passed) / taskResults.Count,
            taskResults.Average(result => result.RequiredCoverage),
            taskResults.Average(result => result.ConflictRate),
            taskResults.Average(result => result.LatencyMs),
            (float)taskResults.Count(result => result.ResponseFormatValid) / taskResults.Count,
            taskResults.Average(result => result.ReasoningPathValidity),
            taskResults.Average(result => result.DependencyCompletionScore),
            taskResults.Average(result => result.StaleMemoryPenalty),
            taskResults.Average(result => result.MinimalEvidenceScore),
            taskResults.Average(result => result.NoiseResistanceScore),
            taskResults.Average(result => result.NoiseResistanceScoreRanked),
            taskResults.Average(result => result.ContradictionHandlingScore));
    }

    private IReadOnlyList<string> ExpandWithGraph(
        IReadOnlyList<CognitiveSearchResult> results,
        IReadOnlyDictionary<string, string> localToCanonical)
    {
        var canonicalIds = new List<string>();
        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            if (localToCanonical.TryGetValue(result.Id, out var canonical) && seenCanonical.Add(canonical))
                canonicalIds.Add(canonical);

            var neighbors = _graph.GetNeighbors(result.Id);
            foreach (var neighbor in neighbors.Neighbors)
            {
                if (neighbor.Entry.LifecycleState == "archived") continue;
                if (localToCanonical.TryGetValue(neighbor.Entry.Id, out var neighborCanonical) &&
                    seenCanonical.Add(neighborCanonical))
                {
                    canonicalIds.Add(neighborCanonical);
                }
            }
        }

        return canonicalIds;
    }

    private static IReadOnlyList<string> ToCanonicalIds(
        IEnumerable<string> localIds,
        IReadOnlyDictionary<string, string> localToCanonical)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var localId in localIds)
        {
            if (localToCanonical.TryGetValue(localId, out var canonical) && seen.Add(canonical))
                ids.Add(canonical);
        }

        return ids;
    }

    private static IReadOnlyList<TranscriptChunk> BuildTranscriptChunks(AgentOutcomeDataset dataset)
    {
        int chunkSize = Math.Max(dataset.TranscriptChunkSize, 1);
        var chunks = new List<TranscriptChunk>();

        for (int i = 0; i < dataset.SeedEntries.Count; i += chunkSize)
        {
            var slice = dataset.SeedEntries.Skip(i).Take(chunkSize).ToList();
            var builder = new StringBuilder();
            foreach (var seed in slice)
                builder.AppendLine(seed.Text);

            chunks.Add(new TranscriptChunk(
                $"chunk-{(i / chunkSize) + 1}",
                builder.ToString(),
                slice.Select(s => s.Id).ToList()));
        }

        return chunks;
    }

    private static IReadOnlyList<string> SearchTranscript(
        AgentOutcomeTask task,
        IReadOnlyList<TranscriptChunk> chunks)
    {
        var queryTokens = BM25Index.Tokenize(task.QueryText).Distinct().ToHashSet();
        if (queryTokens.Count == 0)
            return Array.Empty<string>();

        var ranked = chunks
            .Select(chunk => (Chunk: chunk, Score: ScoreTranscriptChunk(queryTokens, chunk.Text)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChunkId, StringComparer.Ordinal)
            .Take(Math.Max(task.K, 1))
            .ToList();

        var retrieved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (chunk, _) in ranked)
        {
            foreach (var id in chunk.MemoryIds)
            {
                if (seen.Add(id))
                    retrieved.Add(id);
            }
        }

        return retrieved;
    }

    private static int ScoreTranscriptChunk(HashSet<string> queryTokens, string text)
    {
        var chunkTokens = BM25Index.Tokenize(text).Distinct();
        int overlap = 0;

        foreach (var token in chunkTokens)
        {
            if (queryTokens.Contains(token))
                overlap++;
        }

        return overlap;
    }

    private sealed record PromptContext(
        IReadOnlyList<string> MemoryIds,
        IReadOnlyList<PromptContextMemory> Memories)
    {
        public static readonly PromptContext Empty = new(Array.Empty<string>(), Array.Empty<PromptContextMemory>());
    }

    private sealed record PromptContextMemory(string Id, string Text);

    private sealed record TranscriptChunk(string ChunkId, string Text, IReadOnlyList<string> MemoryIds);

    private sealed record SeededNamespace(
        string Namespace,
        IReadOnlyDictionary<string, string> CanonicalToLocal,
        IReadOnlyDictionary<string, string> LocalToCanonical,
        IReadOnlyList<string> LocalIds);

    private sealed record ParsedResponse(
        string? Answer,
        IReadOnlyList<string> CitedMemoryIds,
        bool InsufficientContext,
        bool IsValid,
        string? RawResponse)
    {
        public static readonly ParsedResponse Invalid =
            new(null, Array.Empty<string>(), false, false, null);
    }

    private sealed record PartialParsedResponse(
        string? Answer,
        IReadOnlyList<string> CitedMemoryIds,
        bool? InsufficientContext)
    {
        public static readonly PartialParsedResponse Invalid =
            new(null, Array.Empty<string>(), null);
    }
}

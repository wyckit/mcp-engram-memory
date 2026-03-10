using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Experts;

/// <summary>
/// Semantic routing engine that maps incoming queries to specialized expert namespaces.
/// Maintains a hidden meta-index (_system_experts) of expert persona embeddings.
/// When no matching expert exists, signals the host LLM to instantiate one via create_expert.
/// </summary>
public sealed class ExpertDispatcher
{
    /// <summary>
    /// Hidden system namespace for expert profiles. The underscore prefix exempts it
    /// from PersistenceManager.GetPersistedNamespaces(), making it invisible to
    /// background services (decay, accretion) while still persisting to disk.
    /// </summary>
    public const string SystemNamespace = "_system_experts";

    private const string ExpertCategory = "expert-profile";

    /// <summary>
    /// Default cosine similarity threshold for considering a route "hit".
    /// Tuned for bge-micro-v2 384-dimensional embeddings.
    /// </summary>
    public const float DefaultThreshold = 0.75f;

    /// <summary>
    /// Experts within this percentage of the top score are included as candidates.
    /// </summary>
    private const float MarginPercent = 0.05f;

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;

    public ExpertDispatcher(CognitiveIndex index, IEmbeddingService embedding)
    {
        _index = index;
        _embedding = embedding;
    }

    /// <summary>
    /// Route a query to the best matching expert namespace(s) via semantic similarity.
    /// Returns "routed" with matched experts, or "needs_expert" when confidence is below threshold.
    /// </summary>
    /// <param name="queryVector">Pre-embedded query vector.</param>
    /// <param name="topK">Maximum number of expert candidates to return.</param>
    /// <param name="threshold">Minimum cosine similarity for a routing "hit".</param>
    /// <returns>Status ("routed" or "needs_expert") and matched expert profiles.</returns>
    public (string Status, IReadOnlyList<ExpertMatch> Experts) Route(
        float[] queryVector, int topK = 3, float threshold = DefaultThreshold)
    {
        var results = _index.Search(
            queryVector, SystemNamespace, k: topK, minScore: 0f,
            includeStates: new HashSet<string> { "ltm" });

        if (results.Count == 0 || results[0].Score < threshold)
        {
            // Miss — return closest matches for context even though they're below threshold
            return ("needs_expert", results.Select(ToExpertMatch).ToList());
        }

        // Hit — return all experts within the margin of the top score
        float topScore = results[0].Score;
        float floor = topScore - (topScore * MarginPercent);
        var matched = results
            .Where(r => r.Score >= floor)
            .Select(ToExpertMatch)
            .ToList();

        return ("routed", matched);
    }

    /// <summary>
    /// Register a new expert in the meta-index and initialize its target namespace.
    /// The persona description is embedded and stored in _system_experts for future routing.
    /// Expert entries use LTM lifecycle state with IsSummaryNode=true for stability.
    /// </summary>
    public ExpertMatch CreateExpert(string expertId, string personaDescription)
    {
        if (string.IsNullOrWhiteSpace(expertId))
            throw new ArgumentException("Expert ID must not be empty.", nameof(expertId));
        if (string.IsNullOrWhiteSpace(personaDescription))
            throw new ArgumentException("Persona description must not be empty.", nameof(personaDescription));

        string targetNamespace = $"expert_{expertId}";
        var vector = _embedding.Embed(personaDescription);

        var entry = new CognitiveEntry(
            id: expertId,
            vector: vector,
            ns: SystemNamespace,
            text: personaDescription,
            category: ExpertCategory,
            metadata: new Dictionary<string, string>
            {
                ["targetNamespace"] = targetNamespace
            },
            lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };

        _index.Upsert(entry);
        return new ExpertMatch(expertId, personaDescription, targetNamespace, 0f, 1);
    }

    /// <summary>
    /// Retrieve an expert profile by ID from the meta-index.
    /// </summary>
    public ExpertMatch? GetExpert(string expertId)
    {
        var entry = _index.Get(expertId, SystemNamespace);
        if (entry is null) return null;

        string targetNamespace = entry.Metadata.GetValueOrDefault("targetNamespace") ?? $"expert_{expertId}";
        return new ExpertMatch(entry.Id, entry.Text ?? "", targetNamespace, 0f, entry.AccessCount);
    }

    /// <summary>
    /// Check if an expert already exists in the meta-index.
    /// </summary>
    public bool ExpertExists(string expertId)
        => _index.Get(expertId, SystemNamespace) is not null;

    /// <summary>
    /// List all registered experts in the meta-index.
    /// </summary>
    public IReadOnlyList<ExpertMatch> ListExperts()
    {
        var entries = _index.GetAllInNamespace(SystemNamespace);
        return entries
            .Where(e => e.Category == ExpertCategory)
            .Select(e => new ExpertMatch(
                e.Id,
                e.Text ?? "",
                e.Metadata.GetValueOrDefault("targetNamespace") ?? $"expert_{e.Id}",
                0f,
                e.AccessCount))
            .ToList();
    }

    /// <summary>
    /// Record a dispatch hit for an expert, incrementing its access count.
    /// </summary>
    public void RecordDispatch(string expertId)
        => _index.RecordAccess(expertId, SystemNamespace);

    private static ExpertMatch ToExpertMatch(CognitiveSearchResult r)
    {
        string targetNamespace = r.Metadata?.GetValueOrDefault("targetNamespace") ?? "";
        return new ExpertMatch(r.Id, r.Text ?? "", targetNamespace, r.Score, r.AccessCount);
    }
}

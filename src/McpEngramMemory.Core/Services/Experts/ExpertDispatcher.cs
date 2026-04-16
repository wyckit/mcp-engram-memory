using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

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
    /// Auto-classification threshold: confidently auto-link to parent at this similarity or above.
    /// </summary>
    public const float AutoLinkThreshold = 0.82f;

    /// <summary>
    /// Auto-classification threshold: suggest (but still link) parent at this similarity or above.
    /// Below this, the expert is left unclassified.
    /// </summary>
    public const float SuggestedThreshold = 0.60f;

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

    /// <summary>
    /// Create a domain node (root or branch) in the hierarchical routing tree.
    /// Domain nodes are stored as expert entries with additional level metadata.
    /// </summary>
    /// <param name="nodeId">Unique identifier for the domain node.</param>
    /// <param name="description">Description of this domain — embedded for routing.</param>
    /// <param name="level">Must be "root" or "branch".</param>
    /// <param name="parentNodeId">Parent node ID (null for root nodes).</param>
    /// <returns>The created domain node as an ExpertMatch.</returns>
    public ExpertMatch CreateDomainNode(string nodeId, string description, string level, string? parentNodeId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("Node ID must not be empty.", nameof(nodeId));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description must not be empty.", nameof(description));
        if (level != "root" && level != "branch")
            throw new ArgumentException("Level must be 'root' or 'branch'.", nameof(level));
        if (level == "root" && parentNodeId is not null)
            throw new ArgumentException("Root nodes must not have a parent.", nameof(parentNodeId));
        if (level == "branch" && string.IsNullOrWhiteSpace(parentNodeId))
            throw new ArgumentException("Branch nodes must have a parent.", nameof(parentNodeId));

        // Verify parent exists if specified
        if (parentNodeId is not null)
        {
            var parentEntry = _index.Get(parentNodeId, SystemNamespace);
            if (parentEntry is null)
                throw new ArgumentException($"Parent node '{parentNodeId}' does not exist.", nameof(parentNodeId));
        }

        string targetNamespace = $"domain_{nodeId}";
        var vector = _embedding.Embed(description);

        var metadata = new Dictionary<string, string>
        {
            ["targetNamespace"] = targetNamespace,
            ["level"] = level,
            ["childNodeIds"] = ""
        };
        if (parentNodeId is not null)
            metadata["parentNodeId"] = parentNodeId;

        var entry = new CognitiveEntry(
            id: nodeId,
            vector: vector,
            ns: SystemNamespace,
            text: description,
            category: ExpertCategory,
            metadata: metadata,
            lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };

        _index.Upsert(entry);

        // Update parent's childNodeIds to include this new node
        if (parentNodeId is not null)
            AddChildToParent(parentNodeId, nodeId);

        return new ExpertMatch(nodeId, description, targetNamespace, 0f, 1);
    }

    /// <summary>
    /// Hierarchical routing: coarse-to-fine tree walk from roots → branches → leaves.
    /// Falls back to flat Route() if no tree structure exists.
    /// </summary>
    /// <param name="queryVector">Pre-embedded query vector.</param>
    /// <param name="topK">Maximum number of leaf expert candidates to return.</param>
    /// <param name="threshold">Minimum cosine similarity for a routing "hit" at each level.</param>
    /// <returns>Hierarchical route result with tree path, matched experts, and context.</returns>
    public HierarchicalRouteResult RouteHierarchical(float[] queryVector, int topK = 3, float threshold = DefaultThreshold)
    {
        // Cache all expert entries once — avoids O(n²) repeated GetAllInNamespace scans
        var cachedEntries = _index.GetAllInNamespace(SystemNamespace);
        var roots = cachedEntries
            .Where(e => e.Category == ExpertCategory && GetLevel(e) == "root")
            .ToList();

        // FALLBACK: If no root domain nodes exist, fall back to flat routing
        if (roots.Count == 0)
        {
            var (status, experts) = Route(queryVector, topK, threshold);
            if (status == "needs_expert")
            {
                return new HierarchicalRouteResult(
                    "needs_expert",
                    Array.Empty<DomainNode>(),
                    experts,
                    Array.Empty<CognitiveSearchResult>());
            }

            // Search the best expert's namespace for context
            var context = experts.Count > 0
                ? _index.Search(queryVector, experts[0].TargetNamespace, k: topK)
                : Array.Empty<CognitiveSearchResult>();

            return new HierarchicalRouteResult("routed", Array.Empty<DomainNode>(), experts, context);
        }

        float queryNorm = VectorMath.Norm(queryVector);
        var path = new List<DomainNode>();

        // Step 1: Score all roots and select top-2 above threshold
        var scoredRoots = ScoreEntries(roots, queryVector, queryNorm);
        var matchedRoots = scoredRoots
            .Where(s => s.Score >= threshold)
            .OrderByDescending(s => s.Score)
            .Take(2)
            .ToList();

        if (matchedRoots.Count == 0)
        {
            // No root matched — fall back to flat routing
            var (status, experts) = Route(queryVector, topK, threshold);
            if (status == "needs_expert")
            {
                return new HierarchicalRouteResult(
                    "needs_expert",
                    Array.Empty<DomainNode>(),
                    experts,
                    Array.Empty<CognitiveSearchResult>());
            }

            var context = experts.Count > 0
                ? _index.Search(queryVector, experts[0].TargetNamespace, k: topK)
                : Array.Empty<CognitiveSearchResult>();

            return new HierarchicalRouteResult("routed", Array.Empty<DomainNode>(), experts, context);
        }

        // Add matched roots to path
        foreach (var root in matchedRoots)
            path.Add(EntryToDomainNode(root.Entry, root.Score));

        // Step 2: Get children of matched roots — separate branches from direct leaves
        var allBranches = new List<CognitiveEntry>();
        var directLeaves = new List<CognitiveEntry>();
        foreach (var root in matchedRoots)
        {
            var children = GetChildren(root.Entry.Id, cachedEntries);
            foreach (var child in children)
            {
                if (GetLevel(child) == "branch")
                    allBranches.Add(child);
                else
                    directLeaves.Add(child); // Leaf directly under root (2-level tree)
            }
        }

        // Step 3: Score branches and select top-2 above threshold
        var matchedBranches = new List<(CognitiveEntry Entry, float Score)>();
        if (allBranches.Count > 0)
        {
            var scoredBranches = ScoreEntries(allBranches, queryVector, queryNorm);
            matchedBranches = scoredBranches
                .Where(s => s.Score >= threshold)
                .OrderByDescending(s => s.Score)
                .Take(2)
                .ToList();

            foreach (var branch in matchedBranches)
                path.Add(EntryToDomainNode(branch.Entry, branch.Score));
        }

        // Step 4: Get leaf children of matched branches + direct leaves from roots
        var allLeaves = new List<CognitiveEntry>(directLeaves);
        if (matchedBranches.Count > 0)
        {
            foreach (var branch in matchedBranches)
            {
                var children = GetChildren(branch.Entry.Id, cachedEntries);
                allLeaves.AddRange(children);
            }
        }
        else if (directLeaves.Count == 0)
        {
            // No branches and no direct leaves — try roots' children as fallback
            foreach (var root in matchedRoots)
            {
                var children = GetChildren(root.Entry.Id, cachedEntries);
                allLeaves.AddRange(children);
            }
        }

        // Step 5: Score leaves and select top-K above threshold
        var matchedLeafExperts = new List<ExpertMatch>();
        if (allLeaves.Count > 0)
        {
            var scoredLeaves = ScoreEntries(allLeaves, queryVector, queryNorm);
            matchedLeafExperts = scoredLeaves
                .Where(s => s.Score >= threshold)
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .Select(s => new ExpertMatch(
                    s.Entry.Id,
                    s.Entry.Text ?? "",
                    s.Entry.Metadata.GetValueOrDefault("targetNamespace") ?? $"expert_{s.Entry.Id}",
                    s.Score,
                    s.Entry.AccessCount))
                .ToList();

            foreach (var leaf in scoredLeaves.Where(s => s.Score >= threshold).Take(topK))
                path.Add(EntryToDomainNode(leaf.Entry, leaf.Score));
        }

        if (matchedLeafExperts.Count == 0)
        {
            // Tree walk didn't find leaves — return path taken with needs_expert
            return new HierarchicalRouteResult(
                "needs_expert",
                path,
                Array.Empty<ExpertMatch>(),
                Array.Empty<CognitiveSearchResult>());
        }

        // Step 6: Search matched leaf expert namespaces for context
        var contextResults = new List<CognitiveSearchResult>();
        foreach (var expert in matchedLeafExperts)
        {
            var results = _index.Search(queryVector, expert.TargetNamespace, k: topK);
            contextResults.AddRange(results);
        }

        return new HierarchicalRouteResult("routed", path, matchedLeafExperts, contextResults);
    }

    /// <summary>
    /// Get the full domain tree structure showing roots, branches, and leaves.
    /// </summary>
    public DomainTreeResult GetDomainTree()
    {
        var allEntries = _index.GetAllInNamespace(SystemNamespace)
            .Where(e => e.Category == ExpertCategory)
            .ToList();

        if (allEntries.Count == 0)
            return new DomainTreeResult(Array.Empty<DomainNode>(), 0, 0);

        var nodeMap = new Dictionary<string, CognitiveEntry>();
        foreach (var entry in allEntries)
            nodeMap[entry.Id] = entry;

        // Build the tree starting from roots
        var roots = allEntries
            .Where(e => GetLevel(e) == "root")
            .Select(e => BuildTreeNode(e, nodeMap))
            .ToList();

        // Calculate max depth
        int maxDepth = roots.Count > 0 ? roots.Max(r => CalculateDepth(r, nodeMap)) : 0;

        // If there are no explicit roots but there are entries, they're all flat leaves
        if (roots.Count == 0)
        {
            var flatLeaves = allEntries
                .Where(e => GetLevel(e) == "leaf")
                .Select(e => EntryToDomainNode(e, 0f, truncateDescription: true))
                .ToList();
            return new DomainTreeResult(flatLeaves, flatLeaves.Count, flatLeaves.Count > 0 ? 1 : 0);
        }

        return new DomainTreeResult(roots, allEntries.Count, maxDepth);
    }

    /// <summary>
    /// Link an existing expert to a parent node in the domain tree.
    /// Sets the expert's parentNodeId and level metadata, and updates the parent's childNodeIds.
    /// </summary>
    public bool LinkToParent(string expertId, string parentNodeId)
    {
        var entry = _index.Get(expertId, SystemNamespace);
        if (entry is null) return false;

        var parentEntry = _index.Get(parentNodeId, SystemNamespace);
        if (parentEntry is null) return false;

        // Unlink from previous parent if any
        var oldParent = entry.Metadata.GetValueOrDefault("parentNodeId");
        if (oldParent is not null && oldParent != parentNodeId)
            RemoveChildFromParent(oldParent, expertId);

        entry.Metadata["parentNodeId"] = parentNodeId;
        entry.Metadata["level"] = "leaf";
        _index.Upsert(entry);

        AddChildToParent(parentNodeId, expertId);
        return true;
    }

    /// <summary>
    /// Classify a new expert into the domain tree by scoring its persona vector against
    /// all root and branch nodes. Returns a <see cref="PlacementInfo"/> indicating
    /// "auto_linked" (>= AutoLinkThreshold), "suggested" (>= SuggestedThreshold),
    /// or "unclassified" (below SuggestedThreshold). Prefers deeper nodes (branch over root)
    /// when scores are within 5%.
    /// </summary>
    /// <param name="personaVector">Pre-embedded persona description vector.</param>
    /// <param name="autoLinkThreshold">Override for auto-link threshold (default: 0.82).</param>
    /// <param name="suggestedThreshold">Override for suggested threshold (default: 0.60).</param>
    public PlacementInfo ClassifyExpert(
        float[] personaVector,
        float autoLinkThreshold = AutoLinkThreshold,
        float suggestedThreshold = SuggestedThreshold)
    {
        var roots = GetNodesByLevel("root");
        if (roots.Count == 0)
            return new PlacementInfo("unclassified", null, 0f, Array.Empty<PlacementCandidate>());

        float queryNorm = VectorMath.Norm(personaVector);
        if (queryNorm == 0f)
            return new PlacementInfo("unclassified", null, 0f, Array.Empty<PlacementCandidate>());

        // Cache all expert entries once — avoids repeated GetAllInNamespace scans
        var cachedEntries = _index.GetAllInNamespace(SystemNamespace);

        // Score all roots
        var scoredRoots = ScoreEntries(roots, personaVector, queryNorm);
        if (scoredRoots.Count == 0)
            return new PlacementInfo("unclassified", null, 0f, Array.Empty<PlacementCandidate>());

        scoredRoots.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Collect all candidates (roots + branches of top-matching roots)
        var allCandidates = new List<(CognitiveEntry Entry, float Score)>(scoredRoots);

        // Get branches of top-2 roots above suggestedThreshold
        var matchedRoots = scoredRoots
            .Where(r => r.Score >= suggestedThreshold)
            .Take(2)
            .ToList();

        foreach (var root in matchedRoots)
        {
            var children = GetChildren(root.Entry.Id, cachedEntries);
            var branches = children.Where(c => GetLevel(c) == "branch").ToList();
            if (branches.Count > 0)
            {
                var scoredBranches = ScoreEntries(branches, personaVector, queryNorm);
                allCandidates.AddRange(scoredBranches);
            }
        }

        allCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (allCandidates.Count == 0)
            return new PlacementInfo("unclassified", null, 0f, Array.Empty<PlacementCandidate>());

        // Pick best parent — prefer branch over root when scores are within 5%
        var best = allCandidates[0];
        if (GetLevel(best.Entry) == "root")
        {
            foreach (var candidate in allCandidates)
            {
                if (GetLevel(candidate.Entry) == "branch" &&
                    candidate.Score >= best.Score * 0.95f)
                {
                    best = candidate;
                    break;
                }
            }
        }

        // Build top-3 candidates for the response
        var topCandidates = allCandidates
            .Take(3)
            .Select(c => new PlacementCandidate(
                c.Entry.Id,
                GetLevel(c.Entry),
                TruncateText(c.Entry.Text ?? "", 120),
                c.Score))
            .ToList();

        float bestScore = best.Score;

        if (bestScore >= autoLinkThreshold)
            return new PlacementInfo("auto_linked", best.Entry.Id, bestScore, topCandidates);

        if (bestScore >= suggestedThreshold)
            return new PlacementInfo("suggested", best.Entry.Id, bestScore, topCandidates);

        return new PlacementInfo("unclassified", null, bestScore, topCandidates);
    }

    private static string TruncateText(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 3), "...");

    /// <summary>
    /// Remove a child node ID from a parent's childNodeIds metadata.
    /// </summary>
    private void RemoveChildFromParent(string parentNodeId, string childNodeId)
    {
        var parentEntry = _index.Get(parentNodeId, SystemNamespace);
        if (parentEntry is null) return;

        string existing = parentEntry.Metadata.GetValueOrDefault("childNodeIds") ?? "";
        var childIds = existing.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (childIds.Remove(childNodeId))
        {
            parentEntry.Metadata["childNodeIds"] = string.Join(",", childIds);
            _index.Upsert(parentEntry);
        }
    }

    /// <summary>
    /// Get children from a pre-fetched entry list (avoids repeated GetAllInNamespace scans).
    /// </summary>
    private static IReadOnlyList<CognitiveEntry> GetChildren(
        string parentNodeId, IReadOnlyList<CognitiveEntry> allEntries)
    {
        return allEntries
            .Where(e => e.Category == ExpertCategory &&
                        e.Metadata.GetValueOrDefault("parentNodeId") == parentNodeId)
            .ToList();
    }

    /// <summary>
    /// Get all nodes at a specific level ("root", "branch", or "leaf") from the meta-index.
    /// Entries without a level metadata field default to "leaf" for backward compatibility.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetNodesByLevel(string level)
    {
        var allEntries = _index.GetAllInNamespace(SystemNamespace);
        return allEntries
            .Where(e => e.Category == ExpertCategory && GetLevel(e) == level)
            .ToList();
    }

    private static ExpertMatch ToExpertMatch(CognitiveSearchResult r)
    {
        string targetNamespace = r.Metadata?.GetValueOrDefault("targetNamespace") ?? "";
        return new ExpertMatch(r.Id, r.Text ?? "", targetNamespace, r.Score, r.AccessCount);
    }

    /// <summary>
    /// Get the level of an expert entry, defaulting to "leaf" for backward compatibility.
    /// </summary>
    private static string GetLevel(CognitiveEntry entry)
        => entry.Metadata.GetValueOrDefault("level") ?? "leaf";

    /// <summary>
    /// Score a list of entries against a query vector using cosine similarity.
    /// </summary>
    private static List<(CognitiveEntry Entry, float Score)> ScoreEntries(
        IReadOnlyList<CognitiveEntry> entries, float[] queryVector, float queryNorm)
    {
        var scored = new List<(CognitiveEntry Entry, float Score)>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Vector.Length != queryVector.Length) continue;
            float entryNorm = VectorMath.Norm(entry.Vector);
            if (entryNorm == 0f) continue;

            float dot = VectorMath.Dot(queryVector, entry.Vector);
            float score = dot / (queryNorm * entryNorm);
            scored.Add((entry, score));
        }
        return scored;
    }

    /// <summary>
    /// Add a child node ID to a parent's childNodeIds metadata.
    /// </summary>
    private void AddChildToParent(string parentNodeId, string childNodeId)
    {
        var parentEntry = _index.Get(parentNodeId, SystemNamespace);
        if (parentEntry is null) return;

        string existing = parentEntry.Metadata.GetValueOrDefault("childNodeIds") ?? "";
        var childIds = string.IsNullOrEmpty(existing)
            ? new List<string>()
            : existing.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (!childIds.Contains(childNodeId))
        {
            childIds.Add(childNodeId);
            parentEntry.Metadata["childNodeIds"] = string.Join(",", childIds);
            _index.Upsert(parentEntry);
        }
    }

    /// <summary>
    /// Convert a CognitiveEntry to a DomainNode.
    /// </summary>
    private static DomainNode EntryToDomainNode(CognitiveEntry entry, float score, bool truncateDescription = false)
    {
        string childNodeIdsStr = entry.Metadata.GetValueOrDefault("childNodeIds") ?? "";
        var childNodeIds = string.IsNullOrEmpty(childNodeIdsStr)
            ? Array.Empty<string>()
            : childNodeIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

        string description = entry.Text ?? "";
        if (truncateDescription && description.Length > 120)
            description = string.Concat(description.AsSpan(0, 117), "...");

        return new DomainNode(
            entry.Id,
            description,
            entry.Metadata.GetValueOrDefault("targetNamespace") ?? $"expert_{entry.Id}",
            GetLevel(entry),
            entry.Metadata.GetValueOrDefault("parentNodeId"),
            childNodeIds,
            score,
            entry.AccessCount);
    }

    /// <summary>
    /// Build a DomainNode for GetDomainTree(), filtering ChildNodeIds to only those
    /// that exist in nodeMap so stale or orphaned ID references are excluded.
    /// </summary>
    private static DomainNode BuildTreeNode(CognitiveEntry entry, Dictionary<string, CognitiveEntry> nodeMap)
    {
        string childNodeIdsStr = entry.Metadata.GetValueOrDefault("childNodeIds") ?? "";
        var childNodeIds = string.IsNullOrEmpty(childNodeIdsStr)
            ? Array.Empty<string>()
            : childNodeIdsStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => nodeMap.ContainsKey(id))
                .ToArray();

        string description = entry.Text ?? "";
        if (description.Length > 120)
            description = string.Concat(description.AsSpan(0, 117), "...");

        return new DomainNode(
            entry.Id,
            description,
            entry.Metadata.GetValueOrDefault("targetNamespace") ?? $"expert_{entry.Id}",
            GetLevel(entry),
            entry.Metadata.GetValueOrDefault("parentNodeId"),
            childNodeIds,
            0f,
            entry.AccessCount);
    }

    /// <summary>
    /// Calculate depth of a node in the tree.
    /// </summary>
    private int CalculateDepth(DomainNode node, Dictionary<string, CognitiveEntry> nodeMap)
    {
        if (node.ChildNodeIds.Count == 0)
            return 1;

        int maxChildDepth = 0;
        foreach (var childId in node.ChildNodeIds)
        {
            if (nodeMap.TryGetValue(childId, out var childEntry))
            {
                var childNode = BuildTreeNode(childEntry, nodeMap);
                int childDepth = CalculateDepth(childNode, nodeMap);
                if (childDepth > maxChildDepth)
                    maxChildDepth = childDepth;
            }
        }
        return 1 + maxChildDepth;
    }
}

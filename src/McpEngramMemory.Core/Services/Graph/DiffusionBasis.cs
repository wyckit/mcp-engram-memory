namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Cached top-K eigenbasis of the memory-graph normalized Laplacian
/// L_norm = I - D^(-1/2) W D^(-1/2), held by <see cref="MemoryDiffusionKernel"/>
/// and used to apply spectral filters (heat-kernel diffusion of decay debt and
/// other per-entry signals).
///
/// W is built from positive-relation edges only (parent_child, cross_reference,
/// similar_to, elaborates, depends_on). Contradicts edges are excluded to keep L
/// positive semi-definite, so the heat kernel exp(-tL) stays a contraction.
///
/// Row order in <see cref="Eigenvectors"/> follows <see cref="EntryIds"/>; the
/// kernel returns a stable id-&gt;index map for callers that need to project signals.
/// </summary>
public sealed class DiffusionBasis
{
    /// <summary>Namespace this basis was computed for.</summary>
    public string Namespace { get; }

    /// <summary>
    /// Stable row order. <c>entryIds[i]</c> is the id of row i in <see cref="Eigenvectors"/>
    /// and the lookup <see cref="IndexOf"/>.
    /// </summary>
    public IReadOnlyList<string> EntryIds { get; }

    /// <summary>
    /// Eigenvalues sorted ascending. Length equals <see cref="TopK"/>.
    /// For the normalized Laplacian these lie in [0, 2]; the smallest is 0 with
    /// multiplicity equal to the number of connected components.
    /// </summary>
    public IReadOnlyList<float> Eigenvalues { get; }

    /// <summary>
    /// Eigenvectors as a row-major dense matrix of shape [N, K]. Column j holds
    /// the eigenvector for <see cref="Eigenvalues"/>[j]. Columns are orthonormal.
    /// </summary>
    public float[,] Eigenvectors { get; }

    /// <summary>Number of eigenpairs cached.</summary>
    public int TopK { get; }

    /// <summary>Number of nodes in the namespace at the time of computation.</summary>
    public int NodeCount => EntryIds.Count;

    /// <summary>Number of edges (positive-relation only) used to build the Laplacian.</summary>
    public int EdgeCount { get; }

    /// <summary>UTC time the basis was computed.</summary>
    public DateTime ComputedAt { get; }

    /// <summary>
    /// <see cref="KnowledgeGraph.Revision"/> at the time of computation. The kernel
    /// recomputes when the live revision diverges.
    /// </summary>
    public long GraphRevision { get; }

    private readonly Dictionary<string, int> _indexOf;

    public DiffusionBasis(
        string ns,
        IReadOnlyList<string> entryIds,
        float[] eigenvalues,
        float[,] eigenvectors,
        int edgeCount,
        long graphRevision)
    {
        if (eigenvectors.GetLength(0) != entryIds.Count)
            throw new ArgumentException("Eigenvectors row count must match entryIds length.", nameof(eigenvectors));
        if (eigenvectors.GetLength(1) != eigenvalues.Length)
            throw new ArgumentException("Eigenvectors column count must match eigenvalues length.", nameof(eigenvectors));

        Namespace = ns;
        EntryIds = entryIds;
        Eigenvalues = eigenvalues;
        Eigenvectors = eigenvectors;
        TopK = eigenvalues.Length;
        EdgeCount = edgeCount;
        ComputedAt = DateTime.UtcNow;
        GraphRevision = graphRevision;

        _indexOf = new Dictionary<string, int>(entryIds.Count);
        for (int i = 0; i < entryIds.Count; i++)
            _indexOf[entryIds[i]] = i;
    }

    /// <summary>Row index for the given entry id, or -1 if absent.</summary>
    public int IndexOf(string entryId) =>
        _indexOf.TryGetValue(entryId, out var idx) ? idx : -1;
}

/// <summary>Lightweight diagnostics view of a diffusion basis without exposing the dense matrix.</summary>
public sealed record DiffusionStats(
    string Namespace,
    int NodeCount,
    int EdgeCount,
    int TopK,
    float SmallestEigenvalue,
    float LargestEigenvalue,
    long GraphRevision,
    DateTime ComputedAt,
    bool Stale);

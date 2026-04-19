namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// MRCR v2 scoring: mean cosine similarity between the model's answer and the gold answer,
/// computed against the local ONNX embedding model. Matches the paper's "mean similarity"
/// metric while keeping scoring deterministic and API-cost-free.
/// </summary>
public sealed class MrcrScorer
{
    /// <summary>
    /// Similarity threshold for the per-task "passed" flag. 0.85 is conservative —
    /// semantically correct answers with minor phrasing drift still pass.
    /// </summary>
    public const float DefaultPassThreshold = 0.85f;

    private readonly IEmbeddingService _embedding;
    private readonly float _passThreshold;

    public MrcrScorer(IEmbeddingService embedding, float passThreshold = DefaultPassThreshold)
    {
        _embedding = embedding;
        _passThreshold = passThreshold;
    }

    public (float Similarity, bool Passed) Score(string? answer, string goldAnswer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (0f, false);
        if (string.IsNullOrWhiteSpace(goldAnswer)) return (0f, false);

        var answerVec = _embedding.Embed(answer);
        var goldVec = _embedding.Embed(goldAnswer);
        float sim = CosineSimilarity(answerVec, goldVec);
        return (sim, sim >= _passThreshold);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0f;

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}

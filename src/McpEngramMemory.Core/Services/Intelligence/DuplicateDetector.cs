using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Detects near-duplicate entries by pairwise cosine similarity.
/// Stateless — operates on data snapshots passed by the caller.
/// </summary>
public sealed class DuplicateDetector
{
    /// <summary>
    /// Find near-duplicates for a single entry within a namespace (O(N) scan).
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string entryId,
        (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)? target,
        IEnumerable<KeyValuePair<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> nsEntries,
        float threshold = 0.95f)
    {
        if (target is null)
            return Array.Empty<(string, string, float)>();

        var t = target.Value;
        if (t.Norm == 0f)
            return Array.Empty<(string, string, float)>();

        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        foreach (var (id, (entry, norm, _)) in nsEntries)
        {
            if (id == entryId || norm == 0f) continue;
            if (entry.Vector.Length != t.Entry.Vector.Length) continue;

            float dot = VectorMath.Dot(t.Entry.Vector, entry.Vector);
            float sim = dot / (t.Norm * norm);
            if (sim >= threshold)
                duplicates.Add((entryId, id, sim));
        }

        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    /// <summary>
    /// Find near-duplicate entries by pairwise cosine similarity scan (O(N²)).
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold = 0.95f,
        int maxResults = 100)
    {
        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        for (int i = 0; i < candidates.Count && duplicates.Count < maxResults; i++)
        {
            for (int j = i + 1; j < candidates.Count && duplicates.Count < maxResults; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.Norm == 0f || b.Norm == 0f) continue;
                if (a.Entry.Vector.Length != b.Entry.Vector.Length) continue;

                float dot = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector);
                float sim = dot / (a.Norm * b.Norm);

                if (sim >= threshold)
                    duplicates.Add((a.Entry.Id, b.Entry.Id, sim));
            }
        }

        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }
}

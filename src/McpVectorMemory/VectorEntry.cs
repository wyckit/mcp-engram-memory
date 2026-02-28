namespace McpVectorMemory;

/// <summary>
/// Represents a stored vector with an identifier and optional metadata.
/// </summary>
public sealed class VectorEntry
{
    public string Id { get; init; }
    public float[] Vector { get; init; }
    public string? Text { get; init; }
    public Dictionary<string, string> Metadata { get; init; }

    public VectorEntry(string id, float[] vector, string? text = null, Dictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id must not be empty.", nameof(id));
        if (vector is null || vector.Length == 0)
            throw new ArgumentException("Vector must not be null or empty.", nameof(vector));

        Id = id;
        Vector = vector;
        Text = text;
        Metadata = metadata ?? new Dictionary<string, string>();
    }
}

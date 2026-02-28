using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpVectorMemory;

/// <summary>
/// Handles saving and loading vector entries to/from a JSON file on disk.
/// Only entry data is persisted — the HNSW graph is rebuilt on load.
/// </summary>
internal static class IndexPersistence
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Persists entries to disk as a JSON array. Writes to a temp file first,
    /// then atomically renames to avoid corruption on crash.
    /// </summary>
    public static void Save(string path, IEnumerable<VectorEntry> entries)
    {
        var dtos = entries.Select(e => new VectorEntryDto
        {
            Id = e.Id,
            Vector = e.Vector,
            Text = e.Text,
            Metadata = e.Metadata.Count > 0
                ? e.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null
        }).ToArray();

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, dtos, _jsonOptions);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads entries from a JSON file on disk. Returns an empty list if the file
    /// does not exist.
    /// </summary>
    public static List<VectorEntry> Load(string path)
    {
        if (!File.Exists(path))
            return new List<VectorEntry>();

        using var stream = File.OpenRead(path);
        var dtos = JsonSerializer.Deserialize<VectorEntryDto[]>(stream, _jsonOptions);
        if (dtos is null)
            return new List<VectorEntry>();

        var entries = new List<VectorEntry>(dtos.Length);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || dto.Vector is null || dto.Vector.Length == 0)
                continue; // skip corrupt entries

            entries.Add(new VectorEntry(dto.Id, dto.Vector, dto.Text, dto.Metadata));
        }
        return entries;
    }

    /// <summary>
    /// DTO for JSON serialization of VectorEntry (includes the vector array
    /// that is normally excluded from MCP JSON output).
    /// </summary>
    private sealed class VectorEntryDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = Array.Empty<float>();

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }
    }
}

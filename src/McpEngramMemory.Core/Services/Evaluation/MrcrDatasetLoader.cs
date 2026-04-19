using System.Text.Json;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Loads MRCR v2 (8-needle) tasks from JSONL files on disk.
///
/// Expected file layout (one JSON object per line):
///   { "taskId": "...", "contextTokens": 32768, "bucket": "32k-64k",
///     "turns": [ { "role": "user", "content": "..." }, ... ],
///     "probe": "...", "goldAnswer": "...", "needleIndex": 3 }
///
/// Dataset source: Google MRCR v2 — https://huggingface.co/datasets/google/mrcr-v2
/// Download instructions live in benchmarks/datasets/mrcr-v2/README.md so we never
/// check dataset bytes into the repo.
/// </summary>
public static class MrcrDatasetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Default on-disk location relative to the repo root.
    /// </summary>
    public const string DefaultRelativePath = "benchmarks/datasets/mrcr-v2";

    public static IReadOnlyList<MrcrTask> Load(string jsonlPath, int? limit = null)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException(
                $"MRCR dataset not found at '{jsonlPath}'. " +
                $"See benchmarks/datasets/mrcr-v2/README.md for download instructions.",
                jsonlPath);

        var tasks = new List<MrcrTask>();
        using var reader = new StreamReader(jsonlPath);
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            MrcrTask? task;
            try
            {
                task = JsonSerializer.Deserialize<MrcrTask>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"MRCR dataset parse error at {jsonlPath}:{lineNumber}: {ex.Message}", ex);
            }

            if (task is null) continue;
            if (task.Turns.Count == 0)
                throw new InvalidDataException(
                    $"MRCR task '{task.TaskId}' has no turns at {jsonlPath}:{lineNumber}.");
            if (string.IsNullOrWhiteSpace(task.Probe))
                throw new InvalidDataException(
                    $"MRCR task '{task.TaskId}' has empty probe at {jsonlPath}:{lineNumber}.");
            if (string.IsNullOrWhiteSpace(task.GoldAnswer))
                throw new InvalidDataException(
                    $"MRCR task '{task.TaskId}' has empty goldAnswer at {jsonlPath}:{lineNumber}.");

            tasks.Add(task);
            if (limit is int max && tasks.Count >= max) break;
        }

        return tasks;
    }

    /// <summary>
    /// Resolve the dataset path: explicit override → MRCR_DATASET_PATH env var → default relative path.
    /// </summary>
    public static string ResolveDefaultPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var env = Environment.GetEnvironmentVariable("MRCR_DATASET_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        return Path.Combine(Directory.GetCurrentDirectory(), DefaultRelativePath, "mrcr_v2_8needle.jsonl");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Services.Synthesis;

/// <summary>
/// Lightweight Ollama API client for local SLM inference.
/// Communicates with the Ollama REST API at localhost:11434.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    /// <summary>Check if Ollama is running and a model is available.</summary>
    public async Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: ct);
            return result?.Models?.Any(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Generate a completion from a local model (non-streaming).</summary>
    public async Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
        float temperature = 0.1f, CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = prompt,
            Stream = false,
            Options = new OllamaOptions { NumPredict = maxTokens, Temperature = temperature }
        };

        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/generate", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
        return result?.Response;
    }

    public void Dispose() => _http.Dispose();
}

// ── Ollama API models ──

internal sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaOptions
{
    [JsonPropertyName("num_predict")] public int NumPredict { get; set; } = 512;
    [JsonPropertyName("temperature")] public float Temperature { get; set; } = 0.1f;
}

internal sealed class OllamaGenerateResponse
{
    [JsonPropertyName("response")] public string? Response { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("total_duration")] public long TotalDuration { get; set; }
    [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
}

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaModelInfo>? Models { get; set; }
}

internal sealed class OllamaModelInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Optional keep_alive string applied to every /api/generate request. Set to "24h" by agent-side
    /// wiring so Ollama doesn't unload the model after 5 min idle (the default). Pass null / unset to
    /// use Ollama's default.
    /// </summary>
    public string? KeepAlive { get; set; }

    /// <summary>Generate a completion from a local model (non-streaming).</summary>
    public async Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
        float temperature = 0.1f, CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = prompt,
            Stream = false,
            Options = new OllamaOptions { NumPredict = maxTokens, Temperature = temperature },
            KeepAlive = KeepAlive,
        };

        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/generate", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
        return result?.Response;
    }

    /// <summary>
    /// Stream a completion token-by-token. Each yielded string is a delta the model just produced —
    /// callers should concatenate or write directly to the console as they arrive. Consumes Ollama's
    /// NDJSON streaming format (one JSON object per line with a "response" chunk and terminal "done").
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string model, string prompt, int maxTokens = 512, float temperature = 0.1f,
        string[]? stop = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = prompt,
            Stream = true,
            Options = new OllamaOptions { NumPredict = maxTokens, Temperature = temperature, Stop = stop },
            KeepAlive = KeepAlive,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) continue;
            OllamaGenerateResponse? chunk;
            try { chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line); }
            catch (JsonException) { continue; }
            if (chunk is null) continue;
            if (!string.IsNullOrEmpty(chunk.Response)) yield return chunk.Response!;
            if (chunk.Done) yield break;
        }
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
    // Ollama defaults keep_alive to "5m" — after 5 minutes of inactivity, the model is unloaded
    // and the next call pays a 10-40s re-load cost. "24h" keeps models resident for a full session.
    // Serialized as a string because Ollama accepts either duration ("24h") or seconds int.
    [JsonPropertyName("keep_alive")] public string? KeepAlive { get; set; }
}

internal sealed class OllamaOptions
{
    [JsonPropertyName("num_predict")] public int NumPredict { get; set; } = 512;
    [JsonPropertyName("temperature")] public float Temperature { get; set; } = 0.1f;
    [JsonPropertyName("stop")] public string[]? Stop { get; set; }
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

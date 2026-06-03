namespace McpEngramMemory.Core.Services.Synthesis;

/// <summary>
/// Abstraction over a local small-language-model text generator used by <see cref="SynthesisEngine"/>.
/// Lets the synthesis map-reduce pipeline run against any backend without code changes.
/// Implementations:
/// <list type="bullet">
///   <item><see cref="OllamaClient"/> — HTTP to a local Ollama daemon (external dependency).</item>
///   <item><see cref="OnnxGenAiTextGenerator"/> — fully in-process via ONNX Runtime GenAI (no daemon).</item>
/// </list>
/// </summary>
public interface ITextGenerator : IDisposable
{
    /// <summary>
    /// Backend availability check. MUST NOT throw — return false (not an exception) when the
    /// backend or the requested model is unavailable, so <see cref="SynthesisEngine"/> can degrade gracefully.
    /// </summary>
    Task<bool> IsAvailableAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// Generate a non-streaming completion for <paramref name="prompt"/>.
    /// Returns null when the model produced no output.
    /// </summary>
    Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
        float temperature = 0.1f, CancellationToken ct = default);
}

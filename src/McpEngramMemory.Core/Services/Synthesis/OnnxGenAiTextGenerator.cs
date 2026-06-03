using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace McpEngramMemory.Core.Services.Synthesis;

/// <summary>
/// Fully in-process text generator backed by ONNX Runtime GenAI. Loads a local
/// Qwen2.5-Instruct ONNX model and runs map-reduce synthesis without any external
/// daemon — mirroring how <c>OnnxEmbeddingService</c> already hosts the embedding model in-process.
///
/// The model is loaded lazily on first use and reused for the process lifetime. Generation is
/// serialized through a semaphore because a GenAI <see cref="Generator"/> session is not safe for
/// concurrent token generation; the two synthesis map workers therefore take turns on one model.
///
/// Model location resolves to (in order): the <c>modelDir</c> ctor arg, the
/// <c>SYNTHESIS_ONNX_MODEL_DIR</c> env var, then <c>{BaseDirectory}/LocalSynthesisModel/qwen2.5-1.5b</c>.
/// Qwen2.5-1.5B-Instruct is the default — best quality-vs-speed for this batch synthesis workload per
/// the 2026-06-03 benchmark (it even finished the full map-reduce faster than 0.5B by being more concise).
/// If the model is absent, <see cref="IsAvailableAsync"/> returns false (never throws) so synthesis
/// degrades gracefully exactly like the Ollama-unavailable path.
/// </summary>
public sealed class OnnxGenAiTextGenerator : ITextGenerator
{
    private readonly string _modelDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();

    private Model? _model;
    private Tokenizer? _tokenizer;
    private bool _initFailed;
    private string? _initError;
    private bool _disposed;

    public OnnxGenAiTextGenerator(string? modelDir = null)
    {
        _modelDir = !string.IsNullOrWhiteSpace(modelDir)
            ? modelDir!
            : Environment.GetEnvironmentVariable("SYNTHESIS_ONNX_MODEL_DIR") is { Length: > 0 } envDir
                ? envDir
                : Path.Combine(AppContext.BaseDirectory, "LocalSynthesisModel", "qwen2.5-1.5b");
    }

    /// <summary>Lazily loads the model + tokenizer once. Returns false (with a reason) instead of throwing.</summary>
    private bool EnsureLoaded(out string? error)
    {
        if (_model is not null && _tokenizer is not null) { error = null; return true; }
        if (_initFailed) { error = _initError; return false; }

        lock (_initLock)
        {
            if (_model is not null && _tokenizer is not null) { error = null; return true; }
            if (_initFailed) { error = _initError; return false; }

            try
            {
                // A valid ONNX GenAI model directory always contains genai_config.json.
                if (!Directory.Exists(_modelDir) || !File.Exists(Path.Combine(_modelDir, "genai_config.json")))
                {
                    _initFailed = true;
                    _initError =
                        $"ONNX synthesis model not found at '{_modelDir}'. Stage a Qwen2.5-Instruct ONNX GenAI " +
                        "model there (genai_config.json + model.onnx[.data] + tokenizer files), or set " +
                        "SYNTHESIS_ONNX_MODEL_DIR. See scripts/fetch-synthesis-model.ps1.";
                    error = _initError;
                    return false;
                }

                _model = new Model(_modelDir);
                _tokenizer = new Tokenizer(_model);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _initError = $"Failed to load ONNX synthesis model from '{_modelDir}': {ex.GetType().Name}: {ex.Message}";
                error = _initError;
                _tokenizer?.Dispose(); _tokenizer = null;
                _model?.Dispose(); _model = null;
                return false;
            }
        }
    }

    public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
        => Task.FromResult(EnsureLoaded(out _));

    public async Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
        float temperature = 0.1f, CancellationToken ct = default)
    {
        if (!EnsureLoaded(out var error))
            throw new InvalidOperationException(error ?? "ONNX synthesis model unavailable.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Token generation is CPU-bound and synchronous; offload so we don't block the caller's thread.
            return await Task.Run(() => GenerateCore(prompt, maxTokens, temperature, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GenerateCore(string prompt, int maxTokens, float temperature, CancellationToken ct)
    {
        var model = _model!;
        var tokenizer = _tokenizer!;

        // Qwen2.5 chat template — wrap the synthesis instruction as the user turn.
        var chat =
            "<|im_start|>system\nYou are a concise assistant that summarizes and synthesizes notes.<|im_end|>\n" +
            $"<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n";

        using var sequences = tokenizer.Encode(chat);
        int inputLength = sequences[0].Length;

        using var generatorParams = new GeneratorParams(model);
        generatorParams.SetSearchOption("max_length", inputLength + maxTokens);
        generatorParams.SetSearchOption("temperature", temperature);
        generatorParams.SetSearchOption("do_sample", temperature > 0.0f);

        using var generator = new Generator(model, generatorParams);
        generator.AppendTokenSequences(sequences);

        using var stream = tokenizer.CreateStream();
        var sb = new StringBuilder();
        while (!generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();

            var seq = generator.GetSequence(0);
            int nextToken = seq[seq.Length - 1];
            sb.Append(stream.Decode(nextToken));
        }

        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenizer?.Dispose();
        _model?.Dispose();
        _gate.Dispose();
    }
}

using System.Buffers;
using System.Numerics.Tensors;
using System.Text;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;

namespace McpEngramMemory.Core.Services;

public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private static readonly RunOptions s_runOptions = new();
    private static readonly string[] s_inputNames = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly long[] _tokenTypeIds;
    private readonly long[] _shape = [1L, 0];
    private readonly OrtValue[] _inputValues = new OrtValue[3];

    public int Dimensions { get; }

    public OnnxEmbeddingService(string? modelDir = null, int maximumTokens = 512)
    {
        _tokenTypeIds = new long[maximumTokens];

        modelDir ??= Path.Combine(AppContext.BaseDirectory, "LocalEmbeddingsModel", "default");

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        _session = new InferenceSession(modelPath);
        Dimensions = _session.OutputMetadata.First().Value.Dimensions.Last();

        // Case-insensitive tokenization (matching bge-micro-v2 defaults)
        _tokenizer = new BertTokenizer();
        using var vocabReader = new StreamReader(vocabPath, Encoding.UTF8);
        _tokenizer.LoadVocabulary(vocabReader, convertInputToLowercase: true);
    }

    public float[] Embed(string text)
    {
        _lock.Wait();
        try
        {
            int maxTokens = _tokenTypeIds.Length;
            long[] scratch = ArrayPool<long>.Shared.Rent(maxTokens * 2);
            try
            {
                int tokenCount = _tokenizer.Encode(
                    text,
                    scratch.AsSpan(0, maxTokens),
                    scratch.AsSpan(maxTokens, maxTokens));
                _shape[1] = tokenCount;

                var info = OrtMemoryInfo.DefaultInstance;
                using var inputIdsOrt = OrtValue.CreateTensorValueFromMemory(
                    info, scratch.AsMemory(0, tokenCount), _shape);
                using var attMaskOrt = OrtValue.CreateTensorValueFromMemory(
                    info, scratch.AsMemory(maxTokens, tokenCount), _shape);
                using var typeIdsOrt = OrtValue.CreateTensorValueFromMemory(
                    info, _tokenTypeIds.AsMemory(0, tokenCount), _shape);

                _inputValues[0] = inputIdsOrt;
                _inputValues[1] = attMaskOrt;
                _inputValues[2] = typeIdsOrt;
                using var outputs = _session.Run(
                    s_runOptions, s_inputNames, _inputValues, _session.OutputNames);

                return MeanPool(outputs[0].GetTensorDataAsSpan<float>());
            }
            finally
            {
                ArrayPool<long>.Shared.Return(scratch);
            }
        }
        finally { _lock.Release(); }
    }

    private float[] MeanPool(ReadOnlySpan<float> modelOutput)
    {
        int dims = Dimensions;
        int tokenCount = Math.DivRem(modelOutput.Length, dims, out int leftover);
        if (leftover != 0)
            throw new InvalidOperationException(
                $"Model output length {modelOutput.Length} is not a multiple of {dims} dimensions.");

        var result = new float[dims];

        if (tokenCount <= 1)
        {
            modelOutput.CopyTo(result);
        }
        else
        {
            TensorPrimitives.Add(
                modelOutput.Slice(0, dims),
                modelOutput.Slice(dims, dims),
                result);
            for (int pos = dims * 2; pos < modelOutput.Length; pos += dims)
            {
                TensorPrimitives.Add(result, modelOutput.Slice(pos, dims), result);
            }
            TensorPrimitives.Divide(result, tokenCount, result);
        }

        // No L2 normalization — bge-micro-v2 embeddings are used unnormalized
        return result;
    }

    public void Dispose()
    {
        _lock.Dispose();
        _session.Dispose();
    }
}

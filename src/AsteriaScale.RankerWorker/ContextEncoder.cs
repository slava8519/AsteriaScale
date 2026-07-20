using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AsteriaScale.RankerWorker;

/// <summary>
/// Single-text MiniLM encoder for the bi-encoder prefilter (roadmap task #7):
/// mean pooling over the attention mask, L2-normalized, CPU-only — the model
/// is 22M parameters and encodes one marked context per request.
/// </summary>
internal sealed class ContextEncoder : IDisposable
{
    private const int SequenceLength = 64;

    private readonly InferenceSession _session;
    private readonly PairTokenizer _tokenizer;
    private readonly int _dimensions;

    public ContextEncoder(string modelPath, string tokenizerPath, int dimensions)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1
        };
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);
        _tokenizer = new PairTokenizer(tokenizerPath);
        _dimensions = dimensions;
    }

    public float[] Encode(string text)
    {
        EncodedBatch encoded = _tokenizer.EncodeSingle(text, SequenceLength);
        var dimensions = new[] { 1, encoded.SequenceLength };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                "input_ids",
                new DenseTensor<long>(encoded.InputIds, dimensions)),
            NamedOnnxValue.CreateFromTensor(
                "attention_mask",
                new DenseTensor<long>(encoded.AttentionMask, dimensions))
        };
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids",
                new DenseTensor<long>(
                    new long[encoded.InputIds.Length],
                    dimensions)));
        }

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First().AsTensor<float>();
        int sequence = hidden.Dimensions[1];
        int width = hidden.Dimensions[2];
        if (width != _dimensions)
        {
            throw new RankerException(
                "invalid_embedding_model",
                $"Encoder produced {width}-dim states, index expects {_dimensions}.");
        }

        float[] pooled = new float[width];
        int counted = 0;
        for (int token = 0; token < sequence; token++)
        {
            if (encoded.AttentionMask[token] == 0)
            {
                continue;
            }

            counted++;
            for (int channel = 0; channel < width; channel++)
            {
                pooled[channel] += hidden[0, token, channel];
            }
        }

        double norm = 0;
        for (int channel = 0; channel < width; channel++)
        {
            pooled[channel] /= Math.Max(counted, 1);
            norm += pooled[channel] * pooled[channel];
        }

        float inverse = (float)(1.0 / Math.Max(Math.Sqrt(norm), 1e-9));
        for (int channel = 0; channel < width; channel++)
        {
            pooled[channel] *= inverse;
        }

        return pooled;
    }

    public void Dispose()
    {
        _session.Dispose();
        _tokenizer.Dispose();
    }
}

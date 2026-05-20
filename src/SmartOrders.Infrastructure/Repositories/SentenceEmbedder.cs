using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// Generates sentence embeddings using all-MiniLM-L6-v2 via ONNX Runtime.
///
/// Equivalent of sentence_transformers.SentenceTransformer('all-MiniLM-L6-v2') in Python.
/// Produces 384-dimensional cosine-compatible vectors, identical to Python output.
///
/// Requires the ONNX model export (run once from the Python project):
///   pip install optimum
///   optimum-cli export onnx --model sentence-transformers/all-MiniLM-L6-v2 data/all-MiniLM-L6-v2-onnx/
///
/// This creates:
///   data/all-MiniLM-L6-v2-onnx/model.onnx   ← set in SmartOrders:OnnxModelPath
///   data/all-MiniLM-L6-v2-onnx/vocab.txt    ← set in SmartOrders:OnnxVocabPath
/// </summary>
public sealed class SentenceEmbedder : IDisposable
{
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;

    public const int EmbeddingDim = 384;

    public SentenceEmbedder(string vocabPath, string modelPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"BERT vocab not found at '{vocabPath}'. See class summary.");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found at '{modelPath}'. See class summary.");

        // all-MiniLM-L6-v2 uses lowercase WordPiece tokenization
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
        _session = new InferenceSession(modelPath);
    }

    /// <summary>
    /// Generates a normalized 384-dim embedding for <paramref name="text"/>.
    /// Mirrors _embed() in QdrantOrderCatalogRepository (Python) + sentence_transformers pipeline:
    /// tokenize → ONNX inference → mean pooling → L2 normalize.
    /// </summary>
    public float[] Embed(string text)
    {
        // EncodeToIds with addSpecialTokens=true adds [CLS] and [SEP]
        var inputIds = _tokenizer.EncodeToIds(text, addSpecialTokens: true, considerNormalization: true);
        var seqLen = Math.Min(inputIds.Count, 512);

        var ids   = new long[seqLen];
        var mask  = new long[seqLen];
        var types = new long[seqLen]; // all zeros = single sentence

        for (var i = 0; i < seqLen; i++)
        {
            ids[i]  = inputIds[i];
            mask[i] = 1L;
        }

        var dims = new[] { 1, seqLen };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",      new DenseTensor<long>(ids,   dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask,  dims)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, dims)),
        };

        using var outputs = _session.Run(inputs);

        // optimum export output name: "last_hidden_state" [1, seqLen, 384]
        var lastHiddenState = outputs
            .First(o => o.Name == "last_hidden_state")
            .AsEnumerable<float>()
            .ToArray();

        // Mean pooling weighted by attention mask (same as sentence_transformers Python)
        var pooled = MeanPool(lastHiddenState, mask, seqLen, EmbeddingDim);

        // L2 normalization (same as sentence_transformers Python)
        return L2Normalize(pooled);
    }

    private static float[] MeanPool(float[] tokenEmbeddings, long[] attentionMask, int seqLen, int dim)
    {
        var result = new float[dim];
        var maskSum = attentionMask.Sum(m => (float)m);

        for (var d = 0; d < dim; d++)
        {
            float sum = 0;
            for (var s = 0; s < seqLen; s++)
                sum += tokenEmbeddings[s * dim + d] * attentionMask[s];
            result[d] = sum / MathF.Max(maskSum, 1e-9f);
        }

        return result;
    }

    private static float[] L2Normalize(float[] vec)
    {
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm < 1e-9f) return vec;
        return vec.Select(v => v / norm).ToArray();
    }

    public void Dispose() => _session.Dispose();
}

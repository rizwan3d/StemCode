using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StemCode.Application.Utilities;

namespace StemCode.Infrastructure.Storage;

internal sealed class TinyE5OnnxCodebaseEmbeddingProvider : ICodebaseEmbeddingProvider
{
    private const string ModelId = "GrowBitLabs/tinye5";
    private const string ModelName = "TinyE5-L6-384";
    private const string ModelRevision = "75a45e2568c14df937ded5379b877c1b37b0b998";
    private const string ModelUrl = "https://huggingface.co/GrowBitLabs/tinye5/tree/int8-onnx";
    private const string Quantization = "int8-onnx";
    private const string QueryPrefix = "query: ";
    private const string PassagePrefix = "passage: ";
    private const string ModelFileName = "model_int8.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    private const int EmbeddingDimensions = 384;
    private const int MaxSequenceLength = 128;
    private const float Int8EmbeddingScale = 127f;

    private static readonly Uri ModelDownloadUri = new(
        "https://huggingface.co/GrowBitLabs/tinye5/resolve/int8-onnx/onnx/model_int8.onnx");
    private static readonly Uri TokenizerDownloadUri = new(
        "https://huggingface.co/GrowBitLabs/tinye5/resolve/int8-onnx/tokenizer.json");
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly SemaphoreSlim _resourcesGate = new(1, 1);
    private TinyE5OnnxResources? _resources;
    private bool _disposed;

    public CodebaseEmbeddingMetadata Metadata { get; } = new(
        ModelId,
        ModelName,
        ModelRevision,
        ModelUrl,
        Quantization,
        EmbeddingDimensions);

    public async Task<sbyte[]> CreateQueryEmbeddingAsync(
        string workspaceRoot,
        string query,
        CancellationToken cancellationToken)
    {
        TinyE5OnnxResources resources = await GetResourcesAsync(workspaceRoot, cancellationToken);
        return CreateEmbedding(resources, QueryPrefix + query);
    }

    public async Task<sbyte[]> CreatePassageEmbeddingAsync(
        string workspaceRoot,
        CodebaseEmbeddingPassage passage,
        CancellationToken cancellationToken)
    {
        TinyE5OnnxResources resources = await GetResourcesAsync(workspaceRoot, cancellationToken);
        return CreateEmbedding(resources, PassagePrefix + passage.ToModelInputText());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resourcesGate.Dispose();
        _resources?.Dispose();
    }

    private async Task<TinyE5OnnxResources> GetResourcesAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_resources is not null)
        {
            return _resources;
        }

        await _resourcesGate.WaitAsync(cancellationToken);
        try
        {
            if (_resources is not null)
            {
                return _resources;
            }

            string modelDirectory = GetModelDirectory(workspaceRoot);
            Directory.CreateDirectory(modelDirectory);

            string modelPath = Path.Combine(modelDirectory, ModelFileName);
            string tokenizerPath = Path.Combine(modelDirectory, TokenizerFileName);
            await EnsureFileAsync(ModelDownloadUri, modelPath, cancellationToken);
            await EnsureFileAsync(TokenizerDownloadUri, tokenizerPath, cancellationToken);

            BertWordPieceTokenizer tokenizer = await BertWordPieceTokenizer.LoadAsync(
                tokenizerPath,
                cancellationToken);
            InferenceSession session = new(modelPath);
            _resources = new TinyE5OnnxResources(session, tokenizer);
            return _resources;
        }
        finally
        {
            _resourcesGate.Release();
        }
    }

    private static sbyte[] CreateEmbedding(
        TinyE5OnnxResources resources,
        string text)
    {
        BertTokenizedInput tokenized = resources.Tokenizer.Encode(text, MaxSequenceLength);
        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor(
                "input_ids",
                new DenseTensor<long>(tokenized.InputIds, [1, MaxSequenceLength])),
            NamedOnnxValue.CreateFromTensor(
                "attention_mask",
                new DenseTensor<long>(tokenized.AttentionMask, [1, MaxSequenceLength]))
        ];
        if (resources.Session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids",
                new DenseTensor<long>(tokenized.TokenTypeIds, [1, MaxSequenceLength])));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = resources.Session.Run(inputs);

        Tensor<float> output = results.First().AsTensor<float>();
        float[] embedding = MeanPool(output, tokenized.AttentionMask);
        NormalizeEmbedding(embedding);
        return QuantizeEmbedding(embedding);
    }

    private static float[] MeanPool(
        Tensor<float> output,
        IReadOnlyList<long> attentionMask)
    {
        ReadOnlySpan<int> dimensions = output.Dimensions;
        if (dimensions.Length == 2 && dimensions[0] == 1 && dimensions[1] == EmbeddingDimensions)
        {
            return output.ToArray();
        }

        if (dimensions.Length != 3 || dimensions[0] != 1)
        {
            throw new InvalidOperationException(
                $"Unexpected TinyE5 ONNX output shape: {string.Join("x", dimensions.ToArray())}.");
        }

        int sequenceLength = dimensions[1];
        int hiddenSize = dimensions[2];
        if (hiddenSize != EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Unexpected TinyE5 embedding dimension {hiddenSize}; expected {EmbeddingDimensions}.");
        }

        float[] values = output.ToArray();
        float[] embedding = new float[hiddenSize];
        int tokenCount = 0;
        for (int tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Count; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            int offset = tokenIndex * hiddenSize;
            for (int dimension = 0; dimension < hiddenSize; dimension++)
            {
                embedding[dimension] += values[offset + dimension];
            }

            tokenCount++;
        }

        if (tokenCount == 0)
        {
            return embedding;
        }

        for (int dimension = 0; dimension < embedding.Length; dimension++)
        {
            embedding[dimension] /= tokenCount;
        }

        return embedding;
    }

    private static void NormalizeEmbedding(float[] embedding)
    {
        double sumSquares = 0;
        foreach (float component in embedding)
        {
            sumSquares += component * component;
        }

        if (sumSquares <= double.Epsilon)
        {
            return;
        }

        float scale = (float)(1d / Math.Sqrt(sumSquares));
        for (int index = 0; index < embedding.Length; index++)
        {
            embedding[index] *= scale;
        }
    }

    private static sbyte[] QuantizeEmbedding(float[] embedding)
    {
        sbyte[] quantized = new sbyte[embedding.Length];
        for (int index = 0; index < embedding.Length; index++)
        {
            quantized[index] = (sbyte)Math.Clamp(
                (int)MathF.Round(embedding[index] * Int8EmbeddingScale),
                sbyte.MinValue,
                sbyte.MaxValue);
        }

        return quantized;
    }

    private static string GetModelDirectory(string workspaceRoot)
    {
        return WorkspacePath.Resolve(
            workspaceRoot,
            Path.Combine(".stemcode", "cache", "embedding-models", "tinye5-int8-onnx"));
    }

    private static async Task EnsureFileAsync(
        Uri uri,
        string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        string? parentDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        string tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using HttpResponseMessage response = await SharedHttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream destination = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (File.Exists(path))
            {
                return;
            }

            try
            {
                File.Move(tempPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StemCode/1.0");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private sealed class TinyE5OnnxResources : IDisposable
    {
        public TinyE5OnnxResources(
            InferenceSession session,
            BertWordPieceTokenizer tokenizer)
        {
            Session = session;
            Tokenizer = tokenizer;
        }

        public InferenceSession Session { get; }

        public BertWordPieceTokenizer Tokenizer { get; }

        public void Dispose()
        {
            Session.Dispose();
        }
    }
}

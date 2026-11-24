using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Takam.Core.Embeddings;

/// <summary>
/// Embeddings locaux avec le modèle intfloat/multilingual-e5-small (ONNX, licence MIT).
/// Convention E5 : préfixe "query: " pour une question, "passage: " pour un texte indexé ;
/// vecteur = moyenne des états cachés pondérée par le masque d'attention, puis normalisation L2.
/// </summary>
public sealed class E5OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    // Identifiants XLM-RoBERTa (fairseq) : <s>=0, <pad>=1, </s>=2, <unk>=3 ; les autres = id SentencePiece + 1.
    private const long BosId = 0, EosId = 2, UnkId = 3; // <pad>=1 inutile : une seule phrase par appel
    private const int FairseqOffset = 1;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly bool _needsTokenTypeIds;
    private readonly object _lock = new();

    public string ModelId => "intfloat/multilingual-e5-small";
    public int Dimensions { get; }

    public E5OnnxEmbeddingService(string modelPath, string sentencePiecePath, int maxTokens = 512)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Modèle ONNX introuvable (voir scripts/download-model.ps1).", modelPath);
        if (!File.Exists(sentencePiecePath)) throw new FileNotFoundException("Tokenizer SentencePiece introuvable.", sentencePiecePath);

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        using (var spm = File.OpenRead(sentencePiecePath))
            _tokenizer = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        _maxTokens = maxTokens;
        _needsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        Dimensions = (int)_session.OutputMetadata.First().Value.Dimensions.Last();
    }

    public float[] EmbedQuery(string query) => Embed("query: " + query);
    public float[] EmbedPassage(string passage) => Embed("passage: " + passage);

    /// <summary>Identifiants de jetons au format XLM-R (exposé pour les tests de conformité).</summary>
    public long[] TokenIds(string text)
    {
        var spmIds = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = new List<long>(spmIds.Count + 2) { BosId };
        foreach (var id in spmIds.Take(_maxTokens - 2))
            ids.Add(id == 0 ? UnkId : id + FairseqOffset); // id 0 de SentencePiece = <unk>
        ids.Add(EosId);
        return ids.ToArray();
    }

    private float[] Embed(string text)
    {
        var ids = TokenIds(text);
        var n = ids.Length;
        var inputIds = new DenseTensor<long>(ids, [1, n]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), [1, n]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask)
        };
        if (_needsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], [1, n])));

        float[] pooled;
        lock (_lock) // InferenceSession.Run est thread-safe, mais on limite la mémoire en sérialisant
        {
            using var results = _session.Run(inputs);
            var hidden = results.First().AsTensor<float>(); // [1, n, dim]
            var dim = hidden.Dimensions[2];
            pooled = new float[dim];
            for (var t = 0; t < n; t++)
                for (var d = 0; d < dim; d++)
                    pooled[d] += hidden[0, t, d];
            for (var d = 0; d < dim; d++) pooled[d] /= n;
        }
        VectorMath.NormalizeInPlace(pooled);
        return pooled;
    }

    public void Dispose() => _session.Dispose();
}

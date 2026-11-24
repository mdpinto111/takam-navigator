using System.Text.Json;
using Takam.Core.Embeddings;

namespace Takam.Tests;

/// <summary>
/// Le tokenizer et le calcul des vecteurs en .NET doivent donner le même résultat que la référence Python
/// (bibliothèque tokenizers de Hugging Face + onnxruntime), enregistrée dans Fixtures/e5-reference.json.
/// Ignoré si le modèle n'est pas téléchargé (scripts/download-model.ps1).
/// </summary>
public class E5ConformanceTests
{
    private sealed record Reference(string Text, long[] Ids, float[] Vector);

    private static readonly Lazy<E5OnnxEmbeddingService?> Service = new(() =>
        File.Exists(TestPaths.Model) && File.Exists(TestPaths.SentencePiece)
            ? new E5OnnxEmbeddingService(TestPaths.Model, TestPaths.SentencePiece) : null);

    private static List<Reference> References() =>
        JsonSerializer.Deserialize<List<Reference>>(File.ReadAllText(TestPaths.Fixture("e5-reference.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [SkippableFact]
    public void Token_ids_match_huggingface()
    {
        Skip.If(Service.Value is null, "Modèle absent");
        foreach (var r in References())
            Assert.Equal(r.Ids, Service.Value!.TokenIds(r.Text));
    }

    [SkippableFact]
    public void Vectors_match_python_reference()
    {
        Skip.If(Service.Value is null, "Modèle absent");
        foreach (var r in References())
        {
            var v = r.Text.StartsWith("query: ") ? Service.Value!.EmbedQuery(r.Text["query: ".Length..])
                                                 : Service.Value!.EmbedPassage(r.Text["passage: ".Length..]);
            Assert.True(VectorMath.Dot(v, r.Vector) > 0.9999, $"Vecteur différent pour : {r.Text}");
        }
    }

    [SkippableFact]
    public void Synonyms_are_closer_than_unrelated_text()
    {
        Skip.If(Service.Value is null, "Modèle absent");
        var s = Service.Value!;
        var q = s.EmbedQuery("איחור בתשלום לספק");
        var related = s.EmbedPassage("עיכוב בתשלום: יש לבצע בדיקה בגין מועדי ביצוע התשלום לספקים");
        var unrelated = s.EmbedPassage("קבוצות מורשי החתימה בחשבון הבנק של המשרד");
        Assert.True(VectorMath.Dot(q, related) > VectorMath.Dot(q, unrelated));
    }
}

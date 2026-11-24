namespace Takam.Core.Embeddings;

/// <summary>Transforme un texte en vecteur normalisé (norme L2 = 1), qui représente son sens.</summary>
public interface IEmbeddingService
{
    /// <summary>Identifiant du modèle, stocké avec l'index pour détecter un changement de modèle.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    float[] EmbedQuery(string query);
    float[] EmbedPassage(string passage);
}

public static class VectorMath
{
    /// <summary>Produit scalaire = similarité cosinus quand les vecteurs sont normalisés.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Dimensions différentes.");
        return System.Numerics.Tensors.TensorPrimitives.Dot(a, b);
    }

    public static void NormalizeInPlace(Span<float> v)
    {
        var norm = System.Numerics.Tensors.TensorPrimitives.Norm(v);
        if (norm <= 0) return;
        System.Numerics.Tensors.TensorPrimitives.Divide(v, norm, v);
    }

    public static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }
}

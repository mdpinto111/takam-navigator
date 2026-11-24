namespace Takam.Core.Embeddings;

/// <summary>Turns a text into a normalized vector (L2 norm = 1) that represents its meaning.</summary>
public interface IEmbeddingService
{
    /// <summary>Model identifier, stored with the index to detect a model change.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    float[] EmbedQuery(string query);
    float[] EmbedPassage(string passage);
}

public static class VectorMath
{
    /// <summary>Dot product = cosine similarity when the vectors are normalized.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector dimensions differ.");
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

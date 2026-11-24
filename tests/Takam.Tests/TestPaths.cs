using Takam.Core.Configuration;

namespace Takam.Tests;

internal static class TestPaths
{
    public static string Sample => RepoPaths.Resolve("data/sample");
    public static string Docx(string code) => Path.Combine(Sample, "docx", code + ".docx");
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string Model => RepoPaths.Resolve("models/multilingual-e5-small/model.onnx");
    public static string SentencePiece => RepoPaths.Resolve("models/multilingual-e5-small/sentencepiece.bpe.model");
}

namespace Takam.Core.Configuration;

/// <summary>Access to the site's public API (for ingestion).</summary>
public sealed class TakamApiOptions
{
    public const string SectionName = "TakamApi";
    public string BaseUrl { get; set; } = "https://takam.mof.gov.il";
    /// <summary>Main chapter to ingest (POC scope: 1 – ביצוע תקציב).</summary>
    public int MainChapter { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    /// <summary>Delay between two downloads, to avoid loading the site.</summary>
    public int DelayBetweenRequestsMs { get; set; } = 300;
}

/// <summary>Data locations. Relative paths are resolved from the repository root.</summary>
public sealed class DataOptions
{
    public const string SectionName = "Data";
    /// <summary>Versioned sample (works offline).</summary>
    public string SampleDir { get; set; } = "data/sample";
    /// <summary>Data downloaded by "fetch" (not versioned). Takes precedence over the sample when present.</summary>
    public string RawDir { get; set; } = "data/raw";
    public string DatabasePath { get; set; } = "data/takam.db";
    public string EvalQuestionsPath { get; set; } = "data/eval/questions.json";
}

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "models/multilingual-e5-small/model.onnx";
    public string SentencePiecePath { get; set; } = "models/multilingual-e5-small/sentencepiece.bpe.model";
}

public static class RepoPaths
{
    /// <summary>Repository root: first parent folder containing TakamNavigator.slnx (otherwise the current folder).</summary>
    public static string Root { get; } = FindRoot();

    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    private static string FindRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "TakamNavigator.slnx"))) return dir.FullName;
        }
        return Environment.CurrentDirectory;
    }
}

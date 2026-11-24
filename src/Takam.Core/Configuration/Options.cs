namespace Takam.Core.Configuration;

/// <summary>Accès à l'API publique du site (pour l'ingestion).</summary>
public sealed class TakamApiOptions
{
    public const string SectionName = "TakamApi";
    public string BaseUrl { get; set; } = "https://takam.mof.gov.il";
    /// <summary>Chapitre principal à ingérer (périmètre du POC : 1 – ביצוע תקציב).</summary>
    public int MainChapter { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    /// <summary>Pause entre deux téléchargements, pour ne pas charger le site.</summary>
    public int DelayBetweenRequestsMs { get; set; } = 300;
}

/// <summary>Emplacements des données. Les chemins relatifs sont résolus depuis la racine du dépôt.</summary>
public sealed class DataOptions
{
    public const string SectionName = "Data";
    /// <summary>Échantillon versionné (fonctionne hors ligne).</summary>
    public string SampleDir { get; set; } = "data/sample";
    /// <summary>Données téléchargées par "fetch" (non versionnées). Prioritaires sur l'échantillon si présentes.</summary>
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
    /// <summary>Racine du dépôt : premier dossier parent contenant TakamNavigator.slnx (sinon le dossier courant).</summary>
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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Takam.Core.Model;

/// <summary>
/// List of downloaded directives, with their metadata.
/// Same format for the versioned sample (data/sample) and for an online ingestion (data/raw).
/// </summary>
public sealed record Manifest
{
    public string Source { get; init; } = "";
    public string FetchedAt { get; init; } = "";
    public int MainChapter { get; init; }
    public List<Directive> Directives { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Manifest Load(string path) =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Manifeste illisible : {path}");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

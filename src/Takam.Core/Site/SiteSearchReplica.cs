using System.Text.Json;
using System.Text.Json.Serialization;

namespace Takam.Core.Site;

/// <summary>
/// Copie fidèle de la recherche libre du site takam.mof.gov.il (fonction filterByFreeTxt du bundle Angular,
/// relevée le 24/11/2026) : la requête est découpée sur les espaces, et un document est retenu si TOUS les mots
/// apparaissent, en sous-chaîne exacte, dans UN MÊME champ parmi : name, description, keywords, documentcodetext.
/// Sert de point de comparaison dans l'évaluation.
/// </summary>
public sealed class SiteSearchReplica(IReadOnlyList<SiteSearchReplica.SiteDocument> documents)
{
    public sealed record SiteDocument
    {
        [JsonPropertyName("sfid")] public string? Sfid { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("keywords")] public string? Keywords { get; init; }
        [JsonPropertyName("documentcodetext")] public string? DocumentCodeText { get; init; }
        [JsonPropertyName("namecode")] public string? NameCode { get; init; }
        [JsonPropertyName("documenttype")] public string? DocumentType { get; init; }
    }

    public static SiteSearchReplica Load(string path) =>
        new(JsonSerializer.Deserialize<List<SiteDocument>>(File.ReadAllText(path)) ?? []);

    public IReadOnlyList<SiteDocument> Search(string freeText)
    {
        var words = freeText.Split(' ').Where(w => w != "").ToArray();
        if (words.Length == 0) return documents;
        return documents.Where(d =>
            new[] { d.Name, d.Description, d.Keywords, d.DocumentCodeText }
                .Any(field => field is not null && words.All(field.Contains))).ToList();
    }

    /// <summary>Codes des directives (type Instruction) retournées.</summary>
    public IReadOnlyList<string> SearchDirectiveCodes(string freeText) =>
        Search(freeText).Where(d => d.DocumentType == "Instruction" && d.NameCode is not null)
            .Select(d => d.NameCode!).Distinct().ToList();
}

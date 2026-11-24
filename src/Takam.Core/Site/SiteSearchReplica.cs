using System.Text.Json;
using System.Text.Json.Serialization;

namespace Takam.Core.Site;

/// <summary>
/// Faithful copy of the free-text search of takam.mof.gov.il (filterByFreeTxt function in the Angular bundle,
/// read on 24/11/2026): the query is split on spaces, and a document matches if ALL the words
/// appear, as exact substrings, in ONE SAME field among: name, description, keywords, documentcodetext.
/// Used as the baseline in the evaluation.
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

    /// <summary>Codes of the returned directives (Instruction type).</summary>
    public IReadOnlyList<string> SearchDirectiveCodes(string freeText) =>
        Search(freeText).Where(d => d.DocumentType == "Instruction" && d.NameCode is not null)
            .Select(d => d.NameCode!).Distinct().ToList();
}

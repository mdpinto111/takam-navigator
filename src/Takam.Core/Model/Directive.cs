namespace Takam.Core.Model;

/// <summary>Une directive (הוראת תכ"ם) telle que décrite par le manifeste d'ingestion.</summary>
public sealed record Directive
{
    public required string Code { get; init; }            // ex. "1.4.3"
    public required string Name { get; init; }            // ex. "מועדי תשלום"
    public string? Sfid { get; init; }
    public string? CodeText { get; init; }                 // ex. "H.1.4.3"
    public string? MainChapter { get; init; }
    public string? SubChapter { get; init; }
    public string? Keywords { get; init; }
    public int Edition { get; init; }
    public string? ValidFrom { get; init; }                // yyyy-MM-dd
    public string? ContentVersionId { get; init; }
    public string? File { get; init; }                     // chemin relatif du .docx

    /// <summary>Lien vers la fiche officielle sur le site.</summary>
    public string OfficialUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/document/{CodeText ?? "H." + Code}";
}

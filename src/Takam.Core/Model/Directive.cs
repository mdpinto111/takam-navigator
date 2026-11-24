namespace Takam.Core.Model;

/// <summary>A directive (הוראת תכ"ם) as described by the ingestion manifest.</summary>
public sealed record Directive
{
    public required string Code { get; init; }            // ex. "1.4.3"
    public required string Name { get; init; }            // e.g. "מועדי תשלום"
    public string? Sfid { get; init; }
    public string? CodeText { get; init; }                 // ex. "H.1.4.3"
    public string? MainChapter { get; init; }
    public string? SubChapter { get; init; }
    public string? Keywords { get; init; }
    public int Edition { get; init; }
    public string? ValidFrom { get; init; }                // yyyy-MM-dd
    public string? ContentVersionId { get; init; }
    public string? File { get; init; }                     // relative path of the .docx

    /// <summary>Link to the official page on the site.</summary>
    public string OfficialUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/document/{CodeText ?? "H." + Code}";
}

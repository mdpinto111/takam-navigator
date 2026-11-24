using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Takam.Core.Configuration;
using Takam.Core.Model;

namespace Takam.Ingestion;

/// <summary>
/// Client for the site's public API (endpoints found in the site's Angular bundle):
/// GET /api/Document, GET /api/DocumentVersion/{sfid}, GET /api/DocumentSF/DownloadDocument/{contentVersionId}/{name}.
/// </summary>
public sealed class TakamApiClient(HttpClient http, TakamApiOptions options, ILogger<TakamApiClient> logger)
{
    public sealed record ApiDocument
    {
        [JsonPropertyName("sfid")] public string Sfid { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("documenttype")] public string? DocumentType { get; init; }
        [JsonPropertyName("namecode")] public string? NameCode { get; init; }
        [JsonPropertyName("documentcodetext")] public string? DocumentCodeText { get; init; }
        [JsonPropertyName("mainchaptercode")] public int? MainChapterCode { get; init; }
        [JsonPropertyName("firstnamechapter")] public string? MainChapterName { get; init; }
        [JsonPropertyName("secondnamechapter")] public string? SubChapterName { get; init; }
        [JsonPropertyName("keywords")] public string? Keywords { get; init; }
        [JsonPropertyName("datevalidto")] public DateTime? ValidTo { get; init; }
    }

    public sealed record ApiVersion
    {
        [JsonPropertyName("rtn_versionnumber__c")] public int VersionNumber { get; init; }
        [JsonPropertyName("rtn_validfrom__c")] public DateTime? ValidFrom { get; init; }
        [JsonPropertyName("rtn_validto__c")] public DateTime? ValidTo { get; init; }
        [JsonPropertyName("rtn_contentversionid__c")] public string? ContentVersionId { get; init; }
        [JsonPropertyName("rtn_publish__c")] public bool Published { get; init; }
    }

    public Task<List<ApiDocument>> GetDocumentsAsync(CancellationToken ct) =>
        WithRetry(async () => await http.GetFromJsonAsync<List<ApiDocument>>("api/Document", ct) ?? [], "api/Document", ct);

    public Task<List<ApiVersion>> GetVersionsAsync(string sfid, CancellationToken ct) =>
        WithRetry(async () => await http.GetFromJsonAsync<List<ApiVersion>>($"api/DocumentVersion/{sfid}", ct) ?? [], $"api/DocumentVersion/{sfid}", ct);

    public Task<byte[]> DownloadAsync(string contentVersionId, string fileName, CancellationToken ct) =>
        WithRetry(() => http.GetByteArrayAsync($"api/DocumentSF/DownloadDocument/{contentVersionId}/{Uri.EscapeDataString(fileName)}", ct),
            $"DownloadDocument/{contentVersionId}", ct);

    /// <summary>
    /// Downloads the current edition of each directive in the configured chapter and writes the manifest.
    /// Also returns the full document list (used by the replica of the site's search).
    /// </summary>
    public async Task<(Manifest Manifest, List<ApiDocument> AllDocuments)> FetchChapterAsync(string targetDir, CancellationToken ct)
    {
        var all = await GetDocumentsAsync(ct);
        var today = DateTime.UtcNow.Date;
        var directives = all
            .Where(d => d.DocumentType == "Instruction" && d.MainChapterCode == options.MainChapter && d.NameCode is not null)
            .Where(d => d.ValidTo is null || d.ValidTo >= today)
            .OrderBy(d => d.NameCode!.Split('.').Select(int.Parse).Aggregate(0L, (acc, n) => acc * 100 + n))
            .ToList();
        logger.LogInformation("{Count} directives in force in chapter {Chapter}", directives.Count, options.MainChapter);

        Directory.CreateDirectory(Path.Combine(targetDir, "docx"));
        var manifest = new Manifest
        {
            Source = options.BaseUrl, FetchedAt = DateTime.UtcNow.ToString("yyyy-MM-dd"), MainChapter = options.MainChapter
        };
        foreach (var d in directives)
        {
            var versions = await GetVersionsAsync(d.Sfid, ct);
            var current = versions.Where(v => v.Published && v.ContentVersionId is not null && (v.ValidTo is null || v.ValidTo >= today))
                .OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (current is null)
            {
                logger.LogWarning("Directive {Code}: no published edition in force, skipped.", d.NameCode);
                continue;
            }
            var file = $"docx/{d.NameCode}.docx";
            var bytes = await DownloadAsync(current.ContentVersionId!, d.NameCode!, ct);
            if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            {
                logger.LogWarning("Directive {Code}: the received file is not a .docx, skipped.", d.NameCode);
                continue;
            }
            await File.WriteAllBytesAsync(Path.Combine(targetDir, file), bytes, ct);
            manifest.Directives.Add(new Directive
            {
                Sfid = d.Sfid, Code = d.NameCode!, CodeText = d.DocumentCodeText, Name = d.Name, MainChapter = d.MainChapterName,
                SubChapter = d.SubChapterName, Keywords = d.Keywords, Edition = current.VersionNumber,
                ValidFrom = current.ValidFrom?.ToString("yyyy-MM-dd"), ContentVersionId = current.ContentVersionId, File = file
            });
            logger.LogInformation("  {Code,-7} edition {Edition}  {Size,7:N0} bytes", d.NameCode, current.VersionNumber, bytes.Length);
            await Task.Delay(options.DelayBetweenRequestsMs, ct);
        }
        return (manifest, all);
    }

    private async Task<T> WithRetry<T>(Func<Task<T>> action, string what, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (attempt < options.MaxRetries && ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning("{What} failed ({Error}), retrying in {Delay}s", what, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }
}

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Takam.Core.Configuration;
using Takam.Ingestion;

namespace Takam.Tests;

/// <summary>Client for the site's API, tested with a fake HTTP server (no network calls).</summary>
public class TakamApiClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"takam-fetch-{Guid.NewGuid():N}");
    private static readonly TakamApiOptions Options = new() { MainChapter = 1, MaxRetries = 1, DelayBetweenRequestsMs = 0 };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static TakamApiClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://takam.test/") }, Options, NullLogger<TakamApiClient>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Server_error_is_retried_then_reported()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).FetchChapterAsync(_dir, CancellationToken.None));
        Assert.Equal(2, handler.Calls.Count);                 // 1 attempt + 1 retry
        Assert.False(File.Exists(Path.Combine(_dir, "manifest.json")));   // nothing is overwritten
    }

    [Fact]
    public async Task Fetch_keeps_only_current_directives_of_the_chapter_and_skips_non_docx()
    {
        const string documents = """
            [
              {"sfid":"A","name":"מועדי תשלום","documenttype":"Instruction","namecode":"1.4.3","mainchaptercode":1,"keywords":"תשלום"},
              {"sfid":"B","name":"פרק אחר","documenttype":"Instruction","namecode":"2.1.1","mainchaptercode":2},
              {"sfid":"C","name":"פג תוקף","documenttype":"Instruction","namecode":"1.9.9","mainchaptercode":1,"datevalidto":"2020-01-01T00:00:00"},
              {"sfid":"D","name":"קובץ פגום","documenttype":"Instruction","namecode":"1.4.4","mainchaptercode":1},
              {"sfid":"E","name":"טופס","documenttype":"Form","namecode":"1.4.3","mainchaptercode":1}
            ]
            """;
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/Document" => Json(documents),
            var p when p.StartsWith("/api/DocumentVersion/") => Json(
                "[{\"rtn_versionnumber__c\":5,\"rtn_publish__c\":true,\"rtn_contentversionid__c\":\"OLD\",\"rtn_validto__c\":\"2021-01-01T00:00:00\"}," +
                $"{{\"rtn_versionnumber__c\":6,\"rtn_publish__c\":true,\"rtn_contentversionid__c\":\"CV-{p[^1]}\",\"rtn_validfrom__c\":\"2024-02-01T00:00:00\"}}]"),
            var p when p.Contains("/CV-A/") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("PK\u0003\u0004fake"u8.ToArray()) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>erreur</html>") }
        });

        var (manifest, all) = await Client(handler).FetchChapterAsync(_dir, CancellationToken.None);

        Assert.Equal(5, all.Count);
        var d = Assert.Single(manifest.Directives);           // 2.1.1 other chapter, 1.9.9 expired, 1.4.4 not a docx
        Assert.Equal("1.4.3", d.Code);
        Assert.Equal(6, d.Edition);                            // the current edition, not the old one
        Assert.Equal("CV-A", d.ContentVersionId);
        Assert.Equal("2024-02-01", d.ValidFrom);
        Assert.True(File.Exists(Path.Combine(_dir, "docx", "1.4.3.docx")));
        Assert.DoesNotContain(handler.Calls, c => c.Contains("OLD"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

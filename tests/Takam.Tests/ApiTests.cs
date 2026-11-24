using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Takam.Tests;

/// <summary>
/// L'API HTTP de bout en bout, sur la base de test (sans le vrai modèle : recherche par mots, état "Degraded").
/// </summary>
[Collection("search")]
public class ApiTests(SearchFixture fx)
{
    private HttpClient Client(string databasePath) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Data:DatabasePath", databasePath);
            b.UseSetting("Embeddings:Enabled", "false");
        })
        .CreateClient();

    private static string Q(string text) => Uri.EscapeDataString(text);

    [Fact]
    public async Task Health_reports_degraded_mode_without_model()
    {
        var r = await Client(fx.KeywordOnlyDatabasePath).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Degraded", body.GetProperty("status").GetString());
        Assert.Equal(SearchFixture.Codes.Length, body.GetProperty("index").GetProperty("directives").GetInt32());
        Assert.False(body.GetProperty("semantic").GetBoolean());
    }

    [Fact]
    public async Task Health_is_503_when_database_is_missing()
    {
        var client = Client(Path.Combine(Path.GetTempPath(), "absente.db"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/search?q={Q("תשלום")}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/search?q=")]
    [InlineData("/api/search?q=%20%20")]
    public async Task Empty_query_is_400(string url) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(fx.KeywordOnlyDatabasePath).GetAsync(url)).StatusCode);

    [Fact]
    public async Task Too_long_query_is_400() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client(fx.KeywordOnlyDatabasePath).GetAsync($"/api/search?q={new string('a', 301)}")).StatusCode);

    [Fact]
    public async Task Semantic_mode_without_model_is_503() =>
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await Client(fx.KeywordOnlyDatabasePath).GetAsync($"/api/search?mode=Semantic&q={Q("תשלום")}")).StatusCode);

    [Fact]
    public async Task Search_returns_hits_with_section_and_link()
    {
        var body = await Client(fx.KeywordOnlyDatabasePath).GetFromJsonAsync<JsonElement>($"/api/search?q={Q("עיכוב בתשלום")}");
        Assert.True(body.GetProperty("found").GetBoolean());
        Assert.Equal("Keyword", body.GetProperty("mode").GetString());
        var hit = body.GetProperty("results")[0];
        Assert.Equal("1.4.3", hit.GetProperty("directiveCode").GetString());
        Assert.StartsWith("https://takam.mof.gov.il/document/", hit.GetProperty("officialUrl").GetString());
        Assert.False(string.IsNullOrEmpty(hit.GetProperty("snippet").GetString()));
    }

    [Fact]
    public async Task Off_topic_search_is_200_with_not_found_message()
    {
        var body = await Client(fx.KeywordOnlyDatabasePath).GetFromJsonAsync<JsonElement>($"/api/search?q={Q("מתכון לעוגת שוקולד")}");
        Assert.False(body.GetProperty("found").GetBoolean());
        Assert.Equal(0, body.GetProperty("results").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Section_endpoint_returns_full_text_and_siblings()
    {
        var client = Client(fx.KeywordOnlyDatabasePath);
        var body = await client.GetFromJsonAsync<JsonElement>("/api/directives/1.4.3/sections/0");
        Assert.Equal("1.1", body.GetProperty("label").GetString());
        Assert.True(body.GetProperty("sections").GetArrayLength() > 10);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/directives/1.4.3/sections/9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/directives/9.9.9/sections/0")).StatusCode);
    }
}

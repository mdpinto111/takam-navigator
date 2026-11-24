using System.Text.Json.Serialization;
using Takam.Api;
using Takam.Core.Configuration;
using Takam.Core.Model;
using Takam.Core.Search;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("TAKAM_");
var config = builder.Configuration;
var searchOptions = config.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new();
builder.Services.AddSingleton(searchOptions);
builder.Services.AddSingleton(sp => new SearchServices(
    config.GetSection(DataOptions.SectionName).Get<DataOptions>() ?? new(),
    config.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new(),
    searchOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).WithMethods("GET")));
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
_ = app.Services.GetRequiredService<SearchServices>(); // chargement au démarrage, pas à la première requête

const string NotLoaded = "La base de recherche n'est pas chargée.";

// Recherche : q (obligatoire), top (1-20), mode (Hybrid|Keyword|Semantic), subChapter (filtre facultatif).
app.MapGet("/api/search", (string? q, int? top, SearchMode? mode, string? subChapter, SearchServices s, ILogger<Program> log) =>
{
    if (s.Engine is null)
        return Results.Problem(NotLoaded, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (string.IsNullOrWhiteSpace(q))
        return Results.Problem("Le paramètre q est obligatoire.", statusCode: StatusCodes.Status400BadRequest);
    if (q.Trim().Length > searchOptions.MaxQueryLength)
        return Results.Problem($"La requête dépasse {searchOptions.MaxQueryLength} caractères.", statusCode: StatusCodes.Status400BadRequest);
    if (mode == SearchMode.Semantic && !s.Engine.SemanticAvailable)
        return Results.Problem("La recherche par le sens est indisponible (modèle absent).", statusCode: StatusCodes.Status503ServiceUnavailable);

    var response = s.Engine.Search(new SearchRequest(q, top ?? 5, mode ?? SearchMode.Hybrid, subChapter));
    // Le texte de la requête n'est pas journalisé (il peut contenir des informations internes au ministère).
    log.LogInformation("search mode={Mode} found={Found} results={Count} ms={Ms}", response.Mode, response.Found, response.Results.Count, response.ElapsedMs);
    return Results.Ok(response);
});

// Section complète, pour la lecture dans l'interface.
app.MapGet("/api/directives/{code}/sections/{order:int}", (string code, int order, SearchServices s) =>
{
    if (s.Index is null) return Results.Problem(NotLoaded, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!s.Index.Sections.TryGetValue(Section.MakeId(code, order), out var stored)) return Results.NotFound();
    var siblings = s.Index.Sections.Values.Where(x => x.Directive.Code == code).OrderBy(x => x.Section.Order)
        .Select(x => new { x.Section.Order, x.Section.Label, x.Section.Heading }).ToList();
    var d = stored.Directive;
    return Results.Ok(new
    {
        stored.Section.Id, stored.Section.Order, stored.Section.Label, stored.Section.Heading, stored.Section.Text,
        Kind = stored.Section.Kind.ToString(),
        Directive = new { d.Code, d.Name, d.SubChapter, d.Edition, d.ValidFrom, OfficialUrl = d.OfficialUrl(searchOptions.OfficialBaseUrl) },
        Sections = siblings
    });
});

// Liste des directives indexées (sert au filtre par sous-chapitre).
app.MapGet("/api/directives", (SearchServices s) => s.Index is null
    ? Results.Problem(NotLoaded, statusCode: StatusCodes.Status503ServiceUnavailable)
    : Results.Ok(s.Index.Directives.Values.Select(d => new { d.Code, d.Name, d.SubChapter, d.Edition })));

// Santé : 200 si la base est chargée (même sans modèle : "Degraded"), 503 sinon.
app.MapGet("/api/health", (SearchServices s) =>
{
    var healthy = s.Index is not null;
    var body = new
    {
        Status = !healthy ? "Unhealthy" : s.Engine!.SemanticAvailable ? "Healthy" : "Degraded",
        Index = s.Index is null ? null : new
        {
            Directives = s.Index.Directives.Count, Sections = s.Index.Sections.Count, Vectors = s.Index.Vectors.Count,
            Source = s.Index.Meta.GetValueOrDefault("source"), FetchedAt = s.Index.Meta.GetValueOrDefault("fetched_at"),
            BuiltAt = s.Index.Meta.GetValueOrDefault("built_at"), EmbeddingModel = s.Index.EmbeddingModel
        },
        Semantic = s.Engine?.SemanticAvailable ?? false,
        Error = s.IndexError
    };
    return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;

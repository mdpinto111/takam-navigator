using Takam.Core.Configuration;
using Takam.Core.Embeddings;
using Takam.Core.Search;
using Takam.Core.Storage;

namespace Takam.Api;

/// <summary>
/// Loads the search database and the model at startup. If the database is missing, the API still starts
/// (health becomes "Unhealthy" and /api/search returns 503); if the model is missing, search falls back to keywords.
/// </summary>
public sealed class SearchServices : IDisposable
{
    public SearchIndex? Index { get; }
    public IEmbeddingService? Embeddings { get; }
    public HybridSearchEngine? Engine { get; }
    public string? IndexError { get; }
    public string DatabasePath { get; }

    public SearchServices(DataOptions data, EmbeddingOptions embeddingOptions, SearchOptions searchOptions, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger<SearchServices>();
        DatabasePath = RepoPaths.Resolve(data.DatabasePath);
        try
        {
            Index = SearchIndex.Open(DatabasePath);
            log.LogInformation("Database loaded: {Directives} directives, {Sections} sections, {Vectors} vectors ({Path})",
                Index.Directives.Count, Index.Sections.Count, Index.Vectors.Count, DatabasePath);
        }
        catch (Exception ex)
        {
            IndexError = ex.Message;
            log.LogError("Search database unavailable ({Path}): {Error}. Run: dotnet run --project src/Takam.Ingestion -- build",
                DatabasePath, ex.Message);
            return;
        }
        Embeddings = EmbeddingFactory.TryCreate(embeddingOptions, log);
        Engine = new HybridSearchEngine(Index, Embeddings, searchOptions, loggers.CreateLogger<HybridSearchEngine>());
        if (Embeddings is not null && !Engine.SemanticAvailable)
            log.LogWarning("The database was built with a different model ({IndexModel}) than the loaded one ({Model}): keyword search only.",
                Index.EmbeddingModel ?? "none", Embeddings.ModelId);
    }

    public void Dispose()
    {
        (Embeddings as IDisposable)?.Dispose();
        Index?.Dispose();
    }
}

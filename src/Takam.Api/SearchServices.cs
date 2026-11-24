using Takam.Core.Configuration;
using Takam.Core.Embeddings;
using Takam.Core.Search;
using Takam.Core.Storage;

namespace Takam.Api;

/// <summary>
/// Charge la base de recherche et le modèle au démarrage. Si la base manque, l'API démarre quand même
/// (la santé passe à "Unhealthy" et /api/search répond 503) ; si le modèle manque, la recherche continue par mots.
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
            log.LogInformation("Base chargée : {Directives} directives, {Sections} sections, {Vectors} vecteurs ({Path})",
                Index.Directives.Count, Index.Sections.Count, Index.Vectors.Count, DatabasePath);
        }
        catch (Exception ex)
        {
            IndexError = ex.Message;
            log.LogError("Base de recherche indisponible ({Path}) : {Error}. Lancer : dotnet run --project src/Takam.Ingestion -- build",
                DatabasePath, ex.Message);
            return;
        }
        Embeddings = EmbeddingFactory.TryCreate(embeddingOptions, log);
        Engine = new HybridSearchEngine(Index, Embeddings, searchOptions, loggers.CreateLogger<HybridSearchEngine>());
        if (Embeddings is not null && !Engine.SemanticAvailable)
            log.LogWarning("La base a été construite avec un autre modèle ({IndexModel}) que celui chargé ({Model}) : recherche par mots seulement.",
                Index.EmbeddingModel ?? "aucun", Embeddings.ModelId);
    }

    public void Dispose()
    {
        (Embeddings as IDisposable)?.Dispose();
        Index?.Dispose();
    }
}

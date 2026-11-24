using Takam.Core.Embeddings;
using Takam.Core.Model;
using Takam.Core.Parsing;
using Takam.Core.Storage;
using Takam.Core.Text;

namespace Takam.Tests;

/// <summary>
/// Faux modèle d'embeddings, déterministe et sans fichier : chaque mot (sans préfixe) est haché dans une dimension.
/// Deux textes qui partagent des mots sont proches. Suffisant pour tester la mécanique (fusion, seuils, regroupement)
/// sans dépendre du vrai modèle.
/// </summary>
public sealed class FakeEmbeddingService : IEmbeddingService
{
    public string ModelId => "fake-hash-128";
    public int Dimensions => 128;
    public float[] EmbedQuery(string query) => Embed(query);
    public float[] EmbedPassage(string passage) => Embed(passage);

    private float[] Embed(string text)
    {
        var v = new float[Dimensions];
        foreach (var t in HebrewText.QueryTerms(text))
            v[(int)((uint)HebrewText.Variants(t)[^1].GetHashCode(StringComparison.Ordinal) % Dimensions)] += 1;
        v[0] += 0.01f; // évite le vecteur nul
        VectorMath.NormalizeInPlace(v);
        return v;
    }
}

/// <summary>Base de test construite une fois à partir de quelques directives réelles de l'échantillon.</summary>
public sealed class SearchFixture : IDisposable
{
    public static readonly string[] Codes = ["1.4.3", "1.4.5", "1.6.5", "1.10.1", "1.3.3"];
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"takam-test-{Guid.NewGuid():N}.db");
    public string KeywordOnlyDatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"takam-test-kw-{Guid.NewGuid():N}.db");
    public FakeEmbeddingService Embeddings { get; } = new();
    public SearchIndex Index { get; }

    public SearchFixture()
    {
        var full = Manifest.Load(Path.Combine(TestPaths.Sample, "manifest.json"));
        var manifest = full with { Directives = full.Directives.Where(d => Codes.Contains(d.Code)).ToList() };
        var parser = new DocxSectionParser();
        var sections = manifest.Directives.ToDictionary(d => d.Code, d => parser.Parse(d.Code, TestPaths.Docx(d.Code)));
        new SearchIndexBuilder(Embeddings).Build(DatabasePath, manifest, sections);
        new SearchIndexBuilder(null).Build(KeywordOnlyDatabasePath, manifest, sections);
        Index = SearchIndex.Open(DatabasePath);
    }

    public void Dispose()
    {
        Index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { DatabasePath, KeywordOnlyDatabasePath })
            try { File.Delete(p); } catch (IOException) { }
    }
}

[CollectionDefinition("search")]
public sealed class SearchCollection : ICollectionFixture<SearchFixture>;

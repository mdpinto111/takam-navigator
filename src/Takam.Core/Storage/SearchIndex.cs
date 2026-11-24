using Microsoft.Data.Sqlite;
using Takam.Core.Embeddings;
using Takam.Core.Model;
using Takam.Core.Text;

namespace Takam.Core.Storage;

/// <summary>
/// Read access to the search database. Sections and vectors are loaded into memory at startup
/// (a few MB for one chapter); keyword search queries SQLite's FTS5 index.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    /// <param name="IndexTokens">Word forms of the section, plus the directive's title and keywords (used by the confidence threshold).</param>
    public sealed record StoredSection(Section Section, Directive Directive, IReadOnlySet<string> IndexTokens);
    public sealed record ChunkVector(string SectionId, float[] Vector);

    private readonly SqliteConnection _connection;
    private readonly object _ftsLock = new();

    public IReadOnlyDictionary<string, Directive> Directives { get; }
    public IReadOnlyDictionary<string, StoredSection> Sections { get; }
    public IReadOnlyList<ChunkVector> Vectors { get; }
    public IReadOnlyDictionary<string, string> Meta { get; }
    public string? EmbeddingModel => Meta.GetValueOrDefault("embedding_model") is { Length: > 0 } m ? m : null;

    private SearchIndex(SqliteConnection cn, Dictionary<string, Directive> directives,
        Dictionary<string, StoredSection> sections, List<ChunkVector> vectors, Dictionary<string, string> meta)
    {
        _connection = cn; Directives = directives; Sections = sections; Vectors = vectors; Meta = meta;
    }

    public static SearchIndex Open(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Search database not found: run the ingestion first (Takam.Ingestion build).", databasePath);

        var cn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        cn.Open();
        var meta = Query(cn, "SELECT key, value FROM meta", r => (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
        if (meta.GetValueOrDefault("schema_version") != SearchIndexBuilder.SchemaVersion.ToString())
            throw new InvalidDataException("Unexpected schema version: rebuild the index.");

        var directives = Query(cn, "SELECT code, name, sfid, code_text, main_chapter, sub_chapter, keywords, edition, valid_from FROM directives",
            r => new Directive
            {
                Code = r.GetString(0), Name = r.GetString(1), Sfid = Str(r, 2), CodeText = Str(r, 3), MainChapter = Str(r, 4),
                SubChapter = Str(r, 5), Keywords = Str(r, 6), Edition = r.IsDBNull(7) ? 0 : r.GetInt32(7), ValidFrom = Str(r, 8)
            }).ToDictionary(d => d.Code);

        var sections = Query(cn, "SELECT id, directive_code, ord, label, heading, text, kind FROM sections ORDER BY directive_code, ord",
            r => new Section
            {
                Id = r.GetString(0), DirectiveCode = r.GetString(1), Order = r.GetInt32(2), Label = r.GetString(3),
                Heading = r.GetString(4), Text = r.GetString(5), Kind = Enum.Parse<SectionKind>(r.GetString(6))
            })
            .ToDictionary(s => s.Id, s =>
            {
                var d = directives[s.DirectiveCode];
                return new StoredSection(s, d, Tokens(string.Join(' ', d.Name, d.Keywords, s.Heading, s.Text)));
            });

        var vectors = Query(cn, "SELECT section_id, vector FROM chunks",
            r => new ChunkVector(r.GetString(0), VectorMath.FromBytes((byte[])r[1]))).ToList();

        return new SearchIndex(cn, directives, sections, vectors, meta);
    }

    /// <summary>
    /// Full-text search. Each query word becomes (word OR its prefix-less variants), and the words are
    /// joined with OR: BM25 ranks first the sections that contain the most words, and the rarest ones.
    /// Column weights: directive title 8, keywords 4, chapter title 3, text 1.
    /// </summary>
    public IReadOnlyList<(string SectionId, double Score)> KeywordSearch(IReadOnlyList<string> terms, int limit)
    {
        if (terms.Count == 0) return [];
        var groups = terms.Select(t => "(" + string.Join(" OR ", HebrewText.Variants(t).Select(Quote)) + ")");
        var match = string.Join(" OR ", groups);
        lock (_ftsLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT section_id, bm25(sections_fts, 0.0, 8.0, 4.0, 3.0, 1.0) AS rank
                FROM sections_fts WHERE sections_fts MATCH $match ORDER BY rank LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$match", match);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, double)>();
            while (r.Read()) list.Add((r.GetString(0), -r.GetDouble(1))); // bm25(): lower = better
            return list;
        }
    }

    private static HashSet<string> Tokens(string text) =>
        HebrewText.ToIndexText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static string Quote(string token) => "\"" + token.Replace("\"", "\"\"") + "\"";
    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static IEnumerable<T> Query<T>(SqliteConnection cn, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public void Dispose() => _connection.Dispose();
}

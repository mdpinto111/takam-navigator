using Microsoft.Data.Sqlite;
using Takam.Core.Embeddings;
using Takam.Core.Model;
using Takam.Core.Text;

namespace Takam.Core.Storage;

/// <summary>
/// Builds the SQLite search database: directives, sections, FTS5 full-text index and vectors.
/// The database is fully rebuilt on each run (a few hundred sections: a few seconds
/// without the model, about 20 seconds with embeddings). It is written to a temporary file and then
/// renamed, so the API never reads a half-built database.
/// </summary>
public sealed class SearchIndexBuilder(IEmbeddingService? embeddings, Action<string>? log = null)
{
    public const int SchemaVersion = 2;

    /// <summary>
    /// Chapters that only contain lists of titles (other directives, laws, annexes): kept for
    /// display but excluded from search. Otherwise they come up whenever a question mentions another directive's name,
    /// even though they contain no rule (seen in the evaluation: 1.4.3§3.6 for "עבודות הנדסה בנאיות").
    /// </summary>
    public static readonly IReadOnlySet<string> ReferenceOnlyHeadings = new HashSet<string> { "מסמכים ישימים", "נספחים" };

    public static bool IsSearchable(Section s) =>
        !ReferenceOnlyHeadings.Contains(HebrewText.Normalize(s.Heading)) && !IsPointerOnly(s.Text);

    /// <summary>
    /// Cross-reference sentence with no content ("ראה הגדרות הוראה זו בנספח א"). Being so short, it would be judged only on
    /// the directive title prepended to each chunk, and would outrank the actual rule.
    /// </summary>
    public static bool IsPointerOnly(string text)
    {
        var t = HebrewText.Normalize(text);
        return t.Length < 80 && t.StartsWith("ראה ");
    }

    public void Build(string databasePath, Manifest manifest, IReadOnlyDictionary<string, IReadOnlyList<Section>> sectionsByDirective)
    {
        var tmp = databasePath + ".building";
        if (File.Exists(tmp)) File.Delete(tmp);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        using (var cn = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            cn.Open();
            Exec(cn, Schema);
            using var tx = cn.BeginTransaction();

            InsertMeta(cn, "schema_version", SchemaVersion.ToString());
            InsertMeta(cn, "source", manifest.Source);
            InsertMeta(cn, "fetched_at", manifest.FetchedAt);
            InsertMeta(cn, "main_chapter", manifest.MainChapter.ToString());
            InsertMeta(cn, "built_at", DateTime.UtcNow.ToString("O"));
            InsertMeta(cn, "embedding_model", embeddings?.ModelId ?? "");

            int sectionCount = 0, chunkCount = 0;
            foreach (var d in manifest.Directives)
            {
                InsertDirective(cn, d);
                if (!sectionsByDirective.TryGetValue(d.Code, out var sections)) continue;
                foreach (var s in sections)
                {
                    var searchable = IsSearchable(s);
                    InsertSection(cn, d, s, searchable);
                    sectionCount++;
                    if (embeddings is null || !searchable) continue;
                    var chunks = SectionChunker.Chunks(d, s);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        InsertChunk(cn, s.Id, i, chunks[i], embeddings.EmbedPassage(chunks[i]));
                        chunkCount++;
                    }
                }
                log?.Invoke($"  {d.Code,-7} {sections.Count,3} sections");
            }
            tx.Commit();
            log?.Invoke($"Index: {manifest.Directives.Count} directives, {sectionCount} sections, {chunkCount} vectors.");
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, databasePath, overwrite: true);
    }

    private const string Schema = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE directives(code TEXT PRIMARY KEY, name TEXT NOT NULL, sfid TEXT, code_text TEXT,
            main_chapter TEXT, sub_chapter TEXT, keywords TEXT, edition INTEGER, valid_from TEXT);
        CREATE TABLE sections(id TEXT PRIMARY KEY, directive_code TEXT NOT NULL REFERENCES directives(code),
            ord INTEGER NOT NULL, label TEXT NOT NULL, heading TEXT NOT NULL, text TEXT NOT NULL, kind TEXT NOT NULL);
        CREATE TABLE chunks(id INTEGER PRIMARY KEY, section_id TEXT NOT NULL REFERENCES sections(id),
            ord INTEGER NOT NULL, text TEXT NOT NULL, vector BLOB NOT NULL);
        -- Index plein texte : colonnes déjà normalisées (voir HebrewText.ToIndexText).
        CREATE VIRTUAL TABLE sections_fts USING fts5(section_id UNINDEXED, title, keywords, heading, body,
            tokenize = 'unicode61 remove_diacritics 0');
        """;

    private static void InsertMeta(SqliteConnection cn, string key, string value) =>
        Exec(cn, "INSERT INTO meta(key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));

    private static void InsertDirective(SqliteConnection cn, Directive d) =>
        Exec(cn, """
            INSERT INTO directives(code, name, sfid, code_text, main_chapter, sub_chapter, keywords, edition, valid_from)
            VALUES ($code, $name, $sfid, $codeText, $main, $sub, $kw, $ed, $vf)
            """,
            ("$code", d.Code), ("$name", d.Name), ("$sfid", d.Sfid), ("$codeText", d.CodeText), ("$main", d.MainChapter),
            ("$sub", d.SubChapter), ("$kw", d.Keywords), ("$ed", d.Edition), ("$vf", d.ValidFrom));

    private static void InsertSection(SqliteConnection cn, Directive d, Section s, bool searchable)
    {
        Exec(cn, """
            INSERT INTO sections(id, directive_code, ord, label, heading, text, kind)
            VALUES ($id, $code, $ord, $label, $heading, $text, $kind)
            """,
            ("$id", s.Id), ("$code", d.Code), ("$ord", s.Order), ("$label", s.Label), ("$heading", s.Heading),
            ("$text", s.Text), ("$kind", s.Kind.ToString()));
        if (!searchable) return;
        Exec(cn, """
            INSERT INTO sections_fts(section_id, title, keywords, heading, body)
            VALUES ($id, $title, $kw, $heading, $body)
            """,
            ("$id", s.Id), ("$title", HebrewText.ToIndexText(d.Name)), ("$kw", HebrewText.ToIndexText(d.Keywords)),
            ("$heading", HebrewText.ToIndexText(s.Heading)), ("$body", HebrewText.ToIndexText(s.Text)));
    }

    private static void InsertChunk(SqliteConnection cn, string sectionId, int ord, string text, float[] vector) =>
        Exec(cn, "INSERT INTO chunks(section_id, ord, text, vector) VALUES ($s, $o, $t, $v)",
            ("$s", sectionId), ("$o", ord), ("$t", text), ("$v", VectorMath.ToBytes(vector)));

    private static void Exec(SqliteConnection cn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

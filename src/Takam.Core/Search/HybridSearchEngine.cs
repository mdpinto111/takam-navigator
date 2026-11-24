using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Takam.Core.Embeddings;
using Takam.Core.Storage;
using Takam.Core.Text;

namespace Takam.Core.Search;

/// <summary>
/// Recherche hybride : par mots (BM25 sur FTS5) et par sens (similarité cosinus des embeddings),
/// fusionnées par Reciprocal Rank Fusion. Seules les places comptent dans la fusion, ce qui évite de comparer
/// des scores de nature différente. Les résultats sont regroupés par directive (meilleure section en tête).
/// </summary>
public sealed partial class HybridSearchEngine(
    SearchIndex index, IEmbeddingService? embeddings, SearchOptions options, ILogger<HybridSearchEngine>? logger = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Embeddings utilisables seulement si l'index a été construit avec le même modèle.</summary>
    public bool SemanticAvailable =>
        embeddings is not null && index.Vectors.Count > 0 && index.EmbeddingModel == embeddings.ModelId;

    public SearchResponse Search(SearchRequest request)
    {
        var sw = Stopwatch.StartNew();
        var query = (request.Query ?? "").Trim();
        if (query.Length == 0) throw new ArgumentException("La requête est vide.", nameof(request));
        if (query.Length > options.MaxQueryLength) throw new ArgumentException($"La requête dépasse {options.MaxQueryLength} caractères.", nameof(request));
        var top = Math.Clamp(request.Top, 1, options.MaxTop);
        var mode = request.Mode;
        if (mode != SearchMode.Keyword && !SemanticAvailable)
        {
            if (mode == SearchMode.Semantic)
                throw new InvalidOperationException("La recherche par le sens est indisponible (modèle ou vecteurs absents).");
            mode = SearchMode.Keyword; // dégradation : l'outil reste utilisable sans modèle
        }

        // 1) Numéro de directive : accès direct, sans recherche.
        if (DirectiveNumber().Match(HebrewText.Normalize(query)) is { Success: true } num)
            return Done(ByDirectiveNumber(query, num.Groups[1].Value, mode), sw);

        var terms = HebrewText.QueryTerms(query);
        var sectionFilter = BuildFilter(request.SubChapter);

        // 2) Recherche par mots.
        var keyword = mode == SearchMode.Semantic ? [] :
            index.KeywordSearch(terms, options.CandidatesPerRetriever * 3)
                .Where(k => sectionFilter(k.SectionId))
                .Take(options.CandidatesPerRetriever).ToList();

        // 3) Recherche par le sens : score d'une section = meilleur score de ses morceaux.
        List<(string SectionId, double Score)> semantic = [];
        double? bestSemantic = null;
        if (mode != SearchMode.Keyword)
        {
            var qv = embeddings!.EmbedQuery(query);
            semantic = index.Vectors
                .Where(v => sectionFilter(v.SectionId))
                .GroupBy(v => v.SectionId)
                .Select(g => (SectionId: g.Key, Score: (double)g.Max(v => VectorMath.Dot(qv, v.Vector))))
                .OrderByDescending(x => x.Score)
                .Take(options.CandidatesPerRetriever)
                .ToList();
            bestSemantic = semantic.Count > 0 ? semantic[0].Score : null;
        }

        // 4) Seuil de confiance : on préfère "rien trouvé" à des résultats sans rapport.
        var coverageBase = mode == SearchMode.Semantic
            ? index.KeywordSearch(terms, options.CandidatesPerRetriever).Where(k => sectionFilter(k.SectionId)).ToList() : keyword;
        var bestCoverage = coverageBase.Select(k => Coverage(k.SectionId, terms)).DefaultIfEmpty(0).Max();
        var relevant = IsConfident(bestSemantic, terms.Count > 0 ? bestCoverage : 0);
        if (!relevant)
        {
            _log.LogInformation("Aucun résultat pertinent pour {Query} (sens={Semantic:F3}, couverture={Coverage:P0})", query, bestSemantic, bestCoverage);
            return Done(new SearchResponse
            {
                Query = query, Mode = mode, Found = false, Message = "לא נמצאה הוראה שעוסקת בנושא זה",
                BestSemanticScore = bestSemantic, SemanticAvailable = SemanticAvailable
            }, sw);
        }

        // 5) Fusion (ou classement simple dans les modes à un seul moteur).
        var fused = mode switch
        {
            SearchMode.Keyword => keyword.Select(k => (k.SectionId, k.Score)).ToList(),
            SearchMode.Semantic => semantic,
            _ => ReciprocalRankFusion(keyword, semantic, options.RrfK)
        };
        var keywordIds = keyword.Select(k => k.SectionId).ToHashSet();
        var semanticIds = semantic.Where(s => s.Score >= options.MinSemanticScore).Select(s => s.SectionId).ToHashSet();

        // 6) Regroupement par directive : meilleure section + autres sections pertinentes.
        var results = new List<SearchHit>();
        var others = new Dictionary<string, List<SectionRef>>();
        foreach (var (sectionId, score) in fused)
        {
            var stored = index.Sections[sectionId];
            var code = stored.Directive.Code;
            var existing = results.FindIndex(r => r.DirectiveCode == code);
            if (existing >= 0)
            {
                if (others[code].Count < 3)
                    others[code].Add(new SectionRef(sectionId, stored.Section.Label, stored.Section.Heading));
                continue;
            }
            if (results.Count == top) continue;
            others[code] = [];
            var matchedBy = (keywordIds.Contains(sectionId), semanticIds.Contains(sectionId)) switch
            {
                (true, true) => MatchKind.Both,
                (true, false) => MatchKind.Keyword,
                _ => MatchKind.Semantic
            };
            results.Add(ToHit(stored, terms, matchedBy, score));
        }
        results = results.Select(r => r with { OtherSections = others[r.DirectiveCode] }).ToList();

        return Done(new SearchResponse
        {
            Query = query, Mode = mode, Found = results.Count > 0, Results = results,
            Message = results.Count > 0 ? null : "לא נמצאה הוראה שעוסקת בנושא זה",
            BestSemanticScore = bestSemantic, SemanticAvailable = SemanticAvailable
        }, sw);
    }

    public bool IsConfident(double? semantic, double coverage) =>
        (semantic ?? 0) >= options.StrongSemanticScore
        || ((semantic ?? 0) >= options.MinSemanticScore && coverage >= options.MinSupportingCoverage)
        || coverage >= options.MinKeywordCoverage;

    /// <summary>Signaux de confiance bruts d'une requête (pour le réglage des seuils) : meilleur cosinus, meilleure couverture des mots.</summary>
    public (double? BestSemantic, double BestCoverage) ConfidenceSignals(string query)
    {
        var terms = HebrewText.QueryTerms(query);
        double? sem = SemanticAvailable && index.Vectors.Count > 0
            ? index.Vectors.Max(v => (double)VectorMath.Dot(embeddings!.EmbedQuery(query), v.Vector)) : null;
        var cov = index.KeywordSearch(terms, options.CandidatesPerRetriever).Select(k => Coverage(k.SectionId, terms)).DefaultIfEmpty(0).Max();
        return (sem, cov);
    }

    /// <summary>RRF : chaque section gagne 1/(k + rang) dans chaque liste où elle apparaît.</summary>
    public static List<(string SectionId, double Score)> ReciprocalRankFusion(
        IReadOnlyList<(string SectionId, double Score)> a, IReadOnlyList<(string SectionId, double Score)> b, int k)
    {
        var scores = new Dictionary<string, double>();
        void Add(IReadOnlyList<(string SectionId, double Score)> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
                scores[list[rank].SectionId] = scores.GetValueOrDefault(list[rank].SectionId) + 1.0 / (k + rank + 1);
        }
        Add(a); Add(b);
        return scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>Part des mots de la requête présents (sous une de leurs formes) dans la section.</summary>
    public double Coverage(string sectionId, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        var tokens = index.Sections[sectionId].IndexTokens;
        return terms.Count(t => HebrewText.Variants(t).Any(tokens.Contains)) / (double)terms.Count;
    }

    private SearchResponse ByDirectiveNumber(string query, string code, SearchMode mode)
    {
        var sections = index.Sections.Values.Where(s => s.Directive.Code == code).OrderBy(s => s.Section.Order).ToList();
        if (sections.Count == 0)
            return new SearchResponse
            {
                Query = query, Mode = mode, Found = false, Message = $"הוראה {code} אינה קיימת במאגר",
                SemanticAvailable = SemanticAvailable
            };
        var first = sections.FirstOrDefault(s => s.Section.Label.StartsWith("2")) ?? sections[0]; // "הנחיות לביצוע" en priorité
        var hit = ToHit(first, [], MatchKind.DirectiveNumber, 1.0) with
        {
            OtherSections = sections.Where(s => s != first).Take(5).Select(s => new SectionRef(s.Section.Id, s.Section.Label, s.Section.Heading)).ToList()
        };
        return new SearchResponse { Query = query, Mode = mode, Found = true, Results = [hit], SemanticAvailable = SemanticAvailable };
    }

    private Func<string, bool> BuildFilter(string? subChapter) =>
        string.IsNullOrWhiteSpace(subChapter)
            ? _ => true
            : id => index.Sections.TryGetValue(id, out var s) && s.Directive.SubChapter == subChapter;

    private SearchHit ToHit(SearchIndex.StoredSection stored, IReadOnlyList<string> terms, MatchKind matchedBy, double score)
    {
        var (snippet, highlights) = SnippetBuilder.Build(stored.Section.Text, terms, options.SnippetLength);
        var d = stored.Directive;
        return new SearchHit
        {
            SectionId = stored.Section.Id, DirectiveCode = d.Code, DirectiveName = d.Name, SubChapter = d.SubChapter,
            SectionLabel = stored.Section.Label, SectionHeading = stored.Section.Heading, Snippet = snippet, Highlights = highlights,
            Edition = d.Edition, ValidFrom = d.ValidFrom, OfficialUrl = d.OfficialUrl(options.OfficialBaseUrl),
            MatchedBy = matchedBy, Score = Math.Round(score, 4)
        };
    }

    private static SearchResponse Done(SearchResponse r, Stopwatch sw) => r with { ElapsedMs = sw.ElapsedMilliseconds };

    [GeneratedRegex(@"^(?:[Hh]\.?\s*)?(\d{1,2}(?:\.\d{1,2}){1,3})$")]
    private static partial Regex DirectiveNumber();
}

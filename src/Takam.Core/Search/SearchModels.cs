namespace Takam.Core.Search;

public enum SearchMode
{
    /// <summary>Mots (BM25) + sens (embeddings), fusionnés par RRF. Mode par défaut.</summary>
    Hybrid,
    Keyword,
    Semantic
}

public enum MatchKind
{
    /// <summary>La requête est un numéro de directive (ex. 1.4.3).</summary>
    DirectiveNumber,
    Keyword,
    Semantic,
    Both
}

public sealed record SearchRequest(string Query, int Top = 5, SearchMode Mode = SearchMode.Hybrid, string? SubChapter = null);

public sealed record HighlightRange(int Start, int Length);

public sealed record SectionRef(string SectionId, string Label, string Heading);

public sealed record SearchHit
{
    public required string SectionId { get; init; }
    public required string DirectiveCode { get; init; }
    public required string DirectiveName { get; init; }
    public string? SubChapter { get; init; }
    public required string SectionLabel { get; init; }
    public required string SectionHeading { get; init; }
    public required string Snippet { get; init; }
    public required IReadOnlyList<HighlightRange> Highlights { get; init; }
    public int Edition { get; init; }
    public string? ValidFrom { get; init; }
    public required string OfficialUrl { get; init; }
    public required MatchKind MatchedBy { get; init; }
    public double Score { get; init; }
    /// <summary>Autres sections pertinentes de la même directive.</summary>
    public IReadOnlyList<SectionRef> OtherSections { get; init; } = [];
}

public sealed record SearchResponse
{
    public required string Query { get; init; }
    public required SearchMode Mode { get; init; }
    public required bool Found { get; init; }
    /// <summary>Message à afficher quand rien n'est assez pertinent.</summary>
    public string? Message { get; init; }
    public IReadOnlyList<SearchHit> Results { get; init; } = [];
    /// <summary>Diagnostic : meilleure similarité sémantique (null si embeddings indisponibles).</summary>
    public double? BestSemanticScore { get; init; }
    public bool SemanticAvailable { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class SearchOptions
{
    public const string SectionName = "Search";
    /// <summary>Constante k de la fusion RRF : score = Σ 1 / (k + rang).</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>Nombre de candidats retenus par chaque recherche avant fusion.</summary>
    public int CandidatesPerRetriever { get; set; } = 30;
    // Seuil de confiance (calibré sur data/eval, voir README) : un résultat est jugé pertinent si
    //   sens >= StrongSemanticScore, ou sens >= MinSemanticScore ET couverture >= MinSupportingCoverage,
    //   ou couverture >= MinKeywordCoverage.
    // Le cosinus seul ne suffit pas : avec e5 les scores sont tassés (0,77–0,93) et une question hors sujet
    // ("איך מחדשים דרכון") atteint 0,857, plus qu'une vraie question (0,845).
    /// <summary>Similarité cosinus suffisante à elle seule.</summary>
    public double StrongSemanticScore { get; set; } = 0.90;
    /// <summary>Similarité cosinus minimale quand au moins une partie des mots de la question est aussi présente.</summary>
    public double MinSemanticScore { get; set; } = 0.82;
    /// <summary>Part des mots de la question qui doit appuyer un résultat sémantique.</summary>
    public double MinSupportingCoverage { get; set; } = 0.30;
    /// <summary>Part des mots de la question suffisante à elle seule (recherche par mots).</summary>
    public double MinKeywordCoverage { get; set; } = 0.6;
    public int MaxTop { get; set; } = 20;
    public int MaxQueryLength { get; set; } = 300;
    public int SnippetLength { get; set; } = 240;
    public string OfficialBaseUrl { get; set; } = "https://takam.mof.gov.il";
}
